using UpkManager.Models.UpkFile.Classes;
using UpkManager.Models.UpkFile.Objects;
using UpkManager.Models.UpkFile.Tables;
using UpkManager.Repository;

namespace OmegaAssetStudio.Calligraphy;

// CANONICAL anim-name resolver for an TargetClient power.
//
// Each power's UnrealScript class (e.g. "PowerThor_HammerKiller") ships in its own
// UPK file at  cooked-data folder/UC__<ClassName>_SF.upk  alongside the character
// UPKs. That UPK contains:
//   - one class definition export (class="", object="<classname>")
//   - one class default object   (class="<classname>", object="default__<classname>")
//   - PowerFXAnimation components attached to the default object, each holding an
//     `animname : Name` property that is the LITERAL AnimSequence name the game
//     plays for that activation slot.
//
// For combo / multi-swing powers the default object has multiple PowerFXAnimation
// children (anim1, anim2, ..., one per activation tick). For sustain / channeled
// powers (Ultimate, channeled DoTs) the default object uses PowerFXAnimationLooping
// instead â€” that variant lists 3 name refs (start, loop, end) in a packed `poweranims`
// array, which we resolve via the UPK's name table.
public static class PowerAnimResolver
{
    private static readonly Dictionary<string, IReadOnlyList<string>> _cache = new(StringComparer.OrdinalIgnoreCase);

    // Overrides for power classes whose default object carries no PowerFXAnimation
    // components â€” the animation reference lives in the class's compiled-script
    // parent (or the engine plays a hard-coded clip in response to the power
    // activating). Keys are PowerUnrealClass names (case-insensitive); values are
    // the literal AnimSequence names the game plays.
    //
    // Add entries here as you discover them. The "absattack_*" sequences live in
    // the hero's main AnimSet (e.g. thor_as), which our cross-UPK loader already
    // pulls in via the sibling-UPK search.
    private static readonly Dictionary<string, IReadOnlyList<string>> _overrides
        = new(StringComparer.OrdinalIgnoreCase)
    {
        // A shout whose animation the sibling search does not reach.
        ["PowerThor_Berserker"] = new[] { "absattack_berserkr" },
    };

    public sealed record ResolvedAnims(IReadOnlyList<string> Sequences, string Source);

    // Returns the literal AnimSequence names this power activates, in game order.
    // Returns an empty list if the class UPK or its anim components can't be located.
    public static async Task<ResolvedAnims?> ResolveAsync(string powerClassName, string canonicalCookedDir, UpkFileRepository repo)
    {
        if (string.IsNullOrWhiteSpace(powerClassName) || string.IsNullOrWhiteSpace(canonicalCookedDir))
            return null;

        if (_cache.TryGetValue(powerClassName, out var cached))
            return new ResolvedAnims(cached, $"cached:UC__{powerClassName}_SF.upk");

        // Manual override takes precedence â€” these are skills whose per-power UPK
        // has no PowerFXAnimation children (animation is parent-class / engine-driven).
        if (_overrides.TryGetValue(powerClassName, out var overrideAnims))
        {
            _cache[powerClassName] = overrideAnims;
            return new ResolvedAnims(overrideAnims, $"override:{powerClassName}");
        }

        string upkPath = Path.Combine(canonicalCookedDir, $"UC__{powerClassName}_SF.upk");
        if (!File.Exists(upkPath))
        {
            _cache[powerClassName] = Array.Empty<string>();
            return null;
        }

        try
        {
            var header = await repo.LoadUpkFile(upkPath).ConfigureAwait(false);
            await header.ReadHeaderAsync(null).ConfigureAwait(false);

            string classLow = powerClassName.ToLowerInvariant();
            List<string> sequences = new();

            foreach (UnrealExportTableEntry export in header.ExportTable)
            {
                string cls = (export.ClassReferenceNameIndex?.Name ?? string.Empty).ToLowerInvariant();
                if (cls != "powerfxanimation" && cls != "powerfxanimationlooping") continue;
                // Only count anim components that hang off THIS power's default object.
                string outer = (export.OuterReferenceNameIndex?.Name ?? string.Empty).ToLowerInvariant();
                if (outer != "default__" + classLow) continue;

                try
                {
                    if (export.UnrealObject is null) await export.ParseUnrealObject(false, false).ConfigureAwait(false);
                    if (export.UnrealObject is IUnrealObject uobj && uobj.UObject is UObject obj)
                    {
                        foreach (var prop in obj.Properties)
                        {
                            string pname = (prop.NameIndex?.Name ?? string.Empty).ToLowerInvariant();
                            if (pname == "animname")
                            {
                                // PowerFXAnimation: single Name property pointing to the
                                // AnimSequence name. The parser surfaces it as a string.
                                string? animName = ExtractInnerString(prop.Value);
                                if (!string.IsNullOrWhiteSpace(animName))
                                    sequences.Add(animName!);
                            }
                            else if (pname == "poweranims")
                            {
                                // PowerFXAnimationLooping: array of 8-byte name refs
                                // (4-byte name-table index + 4-byte instance number). Walk
                                // it in stride 8, resolve each index against the UPK name
                                // table. Each entry is a start / loop / end clip.
                                ExtractPackedNameRefs(prop.Value, header, sequences);
                            }
                        }
                    }
                }
                catch { /* skip this component on parse error, continue with the rest */ }
            }

            _cache[powerClassName] = sequences;
            return new ResolvedAnims(sequences, Path.GetFileName(upkPath));
        }
        catch
        {
            _cache[powerClassName] = Array.Empty<string>();
            return null;
        }
    }

    public static void ClearCache() => _cache.Clear();

    // Synchronous cache lookup. Returns the canonical anim names for a class if a
    // previous ResolveAsync call cached them, otherwise null. Lets the UI-thread skill
    // matcher pick up canonical names without waiting on disk IO.
    public static IReadOnlyList<string>? TryGetCached(string powerClassName)
    {
        if (string.IsNullOrWhiteSpace(powerClassName)) return null;
        if (_cache.TryGetValue(powerClassName, out var v) && v.Count > 0) return v;
        // Overrides are available immediately on the first lookup too, so the
        // initial sync skill load can pick them up without waiting for the bg pass.
        if (_overrides.TryGetValue(powerClassName, out var ov) && ov.Count > 0) return ov;
        return null;
    }

    private static string? ExtractInnerString(object? v)
    {
        if (v is null) return null;
        var t = v.GetType();
        var pv = t.GetProperty("PropertyValue");
        var inner = pv?.GetValue(v);
        if (inner is null) return null;
        // UNameProperty often wraps the value as UnrealString { String=... } rather than
        // a plain string. Unwrap that explicitly; fall back to ToString for raw strings.
        var stringProp = inner.GetType().GetProperty("String");
        if (stringProp is not null)
        {
            var s = stringProp.GetValue(inner) as string;
            if (!string.IsNullOrEmpty(s)) return s;
        }
        return inner is string str ? str : inner.ToString();
    }

    private static void ExtractPackedNameRefs(object? value, UpkManager.Models.UpkFile.UnrealHeader header, List<string> sink)
    {
        if (value is null) return;
        var t = value.GetType();
        var pv = t.GetProperty("PropertyValue");
        var inner = pv?.GetValue(value);
        // The parser exposes the raw 8-bytes-per-entry blob as a byte array.
        if (inner is not byte[] bytes || bytes.Length < 8) return;

        var names = header.NameTable;
        if (names is null || names.Count == 0) return;

        for (int i = 0; i + 8 <= bytes.Length; i += 8)
        {
            int nameIndex = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i, 4));
            if (nameIndex < 0 || nameIndex >= names.Count) continue;
            // NameTableEntry.Name is an UnrealString; its .String property holds the
            // actual text. ToString() returns the type name, not the value.
            string n = names[nameIndex].Name?.String ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(n)) sink.Add(n);
        }
    }
}

