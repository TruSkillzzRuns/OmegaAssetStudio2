using OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.Cloning;
using UpkManager.Models.UpkFile;
using UpkManager.Repository;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.Safety;

// Pre-import compatibility check that compares a donor Material/MIC against
// a destination UPK and predicts whether the compiled FMaterialResource
// shader cache will resolve correctly once copied across.
//
// Three signals, in order of strictness:
//   1. Parent material path identity — donor and dest share the same parent
//      → near-certain compatibility (the engine keys the shader-map on
//      parent identity).
//   2. BaseMaterialId GUID match — read from FStaticParameterSet's leading
//      FGuid. If identical, the donor's compiled cache will key the same
//      slot the dest expects.
//   3. Structural-shape match — UniformExpressionTextures.Count and
//      TextureLookups.Count line up between donor and dest. Catches the
//      "same-named parent, different version" case.
//
// Verdict ordering — Compatible > Likely > Unknown > Incompatible. The
// dialog surfaces the result as a colored chip so the user can decide
// before committing the write.
public static class ShaderCompatibilityCheck
{
    public enum Verdict { Compatible, Likely, Unknown, Incompatible }

    public sealed record Result(
        Verdict Verdict,
        string Headline,
        IReadOnlyList<string> Signals);

    // Donor export = the source MIC/Material in the donor UPK.
    // Dest UPK = where we plan to write. If destExportPath is provided, we
    // compare against that specific dest export (used by Import Shaders).
    // Otherwise we compare donor against the dest's "any base Material with
    // the same parent" (used by Import Material / Import Material Instance).
    public static async Task<Result> CheckAsync(
        string donorUpkPath, string donorExportPath,
        string destUpkPath, string? destExportPath = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(donorUpkPath))
            return new(Verdict.Unknown, "Donor UPK missing", new[] { "Cannot read donor file." });
        if (!File.Exists(destUpkPath))
            return new(Verdict.Unknown, "Destination UPK missing", new[] { "Cannot read destination file." });

        try
        {
            var repo = new UpkFileRepository();
            var donor = await repo.LoadUpkFile(donorUpkPath).ConfigureAwait(false);
            await donor.ReadHeaderAsync(null).ConfigureAwait(false);
            var dest = await repo.LoadUpkFile(destUpkPath).ConfigureAwait(false);
            await dest.ReadHeaderAsync(null).ConfigureAwait(false);

            var donorExp = donor.ExportTable.FirstOrDefault(e =>
                string.Equals(e.GetPathName(), donorExportPath, StringComparison.OrdinalIgnoreCase));
            if (donorExp is null)
                return new(Verdict.Unknown, "Donor export not found", new[] { "Selection is stale." });

            // Pull donor signals.
            byte[] donorBody = donorExp.UnrealObjectReader?.GetBytes() ?? Array.Empty<byte>();
            string donorCls = donorExp.ClassReferenceNameIndex?.Name ?? "";
            var donorSig = ExtractSignals(donorBody, donor, donorCls, donor.ExportTable.IndexOf(donorExp));

            // Pull dest signals — either a specific export or the best peer.
            (MaterialSignals destSig, string destLabel) destInfo;
            if (!string.IsNullOrWhiteSpace(destExportPath))
            {
                var destExp = dest.ExportTable.FirstOrDefault(e =>
                    string.Equals(e.GetPathName(), destExportPath, StringComparison.OrdinalIgnoreCase));
                if (destExp is null)
                    return new(Verdict.Unknown, "Destination export not found", new[] { "Selection is stale." });
                byte[] destBody = destExp.UnrealObjectReader?.GetBytes() ?? Array.Empty<byte>();
                string destCls = destExp.ClassReferenceNameIndex?.Name ?? "";
                destInfo = (ExtractSignals(destBody, dest, destCls, dest.ExportTable.IndexOf(destExp)),
                            destExp.GetPathName());
            }
            else
            {
                // Find the dest export that shares the donor's parent material
                // name, if any. Lets us compare apples-to-apples without the
                // caller picking a specific dest target.
                var peer = dest.ExportTable.FirstOrDefault(e =>
                    string.Equals(e.ClassReferenceNameIndex?.Name, donorCls, StringComparison.OrdinalIgnoreCase));
                if (peer is null)
                    return new(Verdict.Unknown,
                        "No comparable Material in destination",
                        new[] { $"Destination has no {donorCls} to compare against." });
                byte[] peerBody = peer.UnrealObjectReader?.GetBytes() ?? Array.Empty<byte>();
                destInfo = (ExtractSignals(peerBody, dest, donorCls, dest.ExportTable.IndexOf(peer)),
                            peer.GetPathName());
            }

            return BuildVerdict(donorSig, destInfo.destSig, destInfo.destLabel);
        }
        catch (Exception ex)
        {
            return new(Verdict.Unknown,
                "Compatibility check failed",
                new[] { $"{ex.GetType().Name}: {ex.Message}" });
        }
    }

    private static Result BuildVerdict(MaterialSignals donor, MaterialSignals dest, string destLabel)
    {
        var signals = new List<string>();
        int positive = 0, negative = 0;

        // 1. Parent material name.
        if (!string.IsNullOrEmpty(donor.ParentName) && !string.IsNullOrEmpty(dest.ParentName))
        {
            if (string.Equals(donor.ParentName, dest.ParentName, StringComparison.OrdinalIgnoreCase))
            { signals.Add($"✓ Same parent material name ({donor.ParentName})"); positive += 2; }
            else
            { signals.Add($"✗ Parent name drift: donor '{donor.ParentName}' vs dest '{dest.ParentName}'"); negative += 2; }
        }

        // 2. BaseMaterialId GUID.
        if (donor.BaseMaterialId is { } dGuid && dest.BaseMaterialId is { } eGuid)
        {
            if (dGuid == eGuid)
            { signals.Add($"✓ BaseMaterialId match ({dGuid.ToString("N").Substring(0, 8)}…)"); positive += 3; }
            else
            { signals.Add($"✗ BaseMaterialId drift: {dGuid.ToString("N").Substring(0, 8)}… vs {eGuid.ToString("N").Substring(0, 8)}…"); negative += 3; }
        }
        else
        {
            signals.Add("? BaseMaterialId unavailable on one side (no static parameter set?)");
        }

        // 3. Structural shape — UniformExpressionTextures count.
        if (donor.UniformExpressionTextureCount >= 0 && dest.UniformExpressionTextureCount >= 0)
        {
            if (donor.UniformExpressionTextureCount == dest.UniformExpressionTextureCount)
            { signals.Add($"✓ UniformExpressionTextures count matches ({donor.UniformExpressionTextureCount})"); positive++; }
            else
            { signals.Add($"✗ Texture slot count differs: donor {donor.UniformExpressionTextureCount} vs dest {dest.UniformExpressionTextureCount}"); negative++; }
        }

        // 4. Structural shape — TextureLookups count.
        if (donor.TextureLookupCount >= 0 && dest.TextureLookupCount >= 0)
        {
            if (donor.TextureLookupCount == dest.TextureLookupCount)
            { signals.Add($"✓ TextureLookups count matches ({donor.TextureLookupCount})"); positive++; }
            else
            { signals.Add($"✗ TextureLookups differ: donor {donor.TextureLookupCount} vs dest {dest.TextureLookupCount}"); negative++; }
        }

        // Choose a verdict.
        Verdict v;
        string headline;
        if (negative == 0 && positive >= 4)
        { v = Verdict.Compatible; headline = $"Compatible — donor and {Path.GetFileName(destLabel)} agree on parent + shader keys."; }
        else if (positive > negative && positive >= 2)
        { v = Verdict.Likely; headline = "Likely compatible — most signals line up but at least one differs."; }
        else if (negative >= 3 && negative > positive)
        { v = Verdict.Incompatible; headline = "Likely incompatible — shader cache will probably miss; expect pink rendering."; }
        else if (positive == 0 && negative == 0)
        { v = Verdict.Unknown; headline = "Inconclusive — not enough signals available on either side."; }
        else
        { v = Verdict.Unknown; headline = "Mixed signals — proceed with care, then run pre-flight diagnostics after Save."; }

        return new(v, headline, signals);
    }

    private sealed class MaterialSignals
    {
        public string ParentName = "";
        public Guid? BaseMaterialId;
        public int UniformExpressionTextureCount = -1;
        public int TextureLookupCount = -1;
    }

    private static MaterialSignals ExtractSignals(
        byte[] body, UnrealHeader header, string cls, int exportIdx)
    {
        var sig = new MaterialSignals();
        if (body.Length <= 4) return sig;

        // ParentName: for a MIC, look at the Parent ObjectProperty in tagged
        // props. For a base UMaterial, treat its own name as the parent.
        bool isMic = string.Equals(cls, "MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(cls, "MaterialInstanceTimeVarying", StringComparison.OrdinalIgnoreCase);
        if (isMic)
        {
            int parentRef = TryReadObjectProperty(body, header, "Parent") ?? 0;
            sig.ParentName = ResolveRefName(parentRef, header);
        }
        else if (exportIdx >= 0 && exportIdx < header.ExportTable.Count)
        {
            sig.ParentName = header.ExportTable[exportIdx].ObjectNameIndex?.Name ?? "";
        }

        // Binary tail walk: pull BaseMaterialId, UniformExpressionTextures
        // count, TextureLookups count from FMaterialResource[2].
        var split = MaterialBodySplitter.Split(body, header);
        if (split.TailBytes.Length >= 4)
            ReadTailSignals(split.TailBytes, isMic, sig);
        return sig;
    }

    private static void ReadTailSignals(byte[] tail, bool isMic, MaterialSignals sig)
    {
        try
        {
            using var br = new BinaryReader(new MemoryStream(tail, writable: false));
            uint mask = br.ReadUInt32();
            if ((mask & 1) == 0) return;

            // First FMaterialResource.
            int sc = br.ReadInt32();
            for (int i = 0; i < sc; i++)
            {
                int len = br.ReadInt32();
                if (len == 0) continue;
                int bytes = len > 0 ? len : -len * 2;
                br.ReadBytes(bytes);
            }
            int mc = br.ReadInt32();
            br.ReadBytes(mc * 8);
            br.ReadInt32();             // MaxTextureDependencyLength
            br.ReadBytes(16);           // Id
            br.ReadInt32();             // NumUserTexCoords
            int tc = br.ReadInt32();    // UniformExpressionTextures count
            sig.UniformExpressionTextureCount = tc;
            br.ReadBytes(tc * 4);
            br.ReadBytes(5 * 4 + 4);    // 5 bools + UsingTransforms
            int lc = br.ReadInt32();    // TextureLookups count
            sig.TextureLookupCount = lc;
            if (lc > 0) br.ReadBytes(lc * 16);
            br.ReadUInt32();            // DummyDroppedFallbackComponents
            br.ReadBytes(12);           // BlendModeOverride + 2 bools

            // For MICs only, an FStaticParameterSet follows — first 16 bytes
            // are the BaseMaterialId GUID we want.
            if (isMic && br.BaseStream.Position + 16 <= br.BaseStream.Length)
            {
                byte[] guid = br.ReadBytes(16);
                sig.BaseMaterialId = new Guid(guid);
            }
        }
        catch { /* malformed tail — leave fields at -1 / null */ }
    }

    private static int? TryReadObjectProperty(byte[] body, UnrealHeader header, string targetName)
    {
        if (body.Length <= 4) return null;
        var names = new string[header.NameTable.Count];
        for (int i = 0; i < names.Length; i++)
            names[i] = header.NameTable[i].Name?.String ?? "";
        using var br = new BinaryReader(new MemoryStream(body, writable: false));
        br.ReadInt32();
        try
        {
            while (br.BaseStream.Position < br.BaseStream.Length)
            {
                int nameIdx = br.ReadInt32(); br.ReadInt32();
                string pn = (nameIdx >= 0 && nameIdx < names.Length) ? names[nameIdx] : "";
                if (string.Equals(pn, "None", StringComparison.OrdinalIgnoreCase)) break;
                int typeIdx = br.ReadInt32(); br.ReadInt32();
                string tn = (typeIdx >= 0 && typeIdx < names.Length) ? names[typeIdx] : "";
                int size = br.ReadInt32(); br.ReadInt32();
                if (string.Equals(tn, "ObjectProperty", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(pn, targetName, StringComparison.OrdinalIgnoreCase))
                    return br.ReadInt32();
                if (string.Equals(tn, "BoolProperty", StringComparison.OrdinalIgnoreCase))
                { br.ReadByte(); continue; }
                if (string.Equals(tn, "ByteProperty", StringComparison.OrdinalIgnoreCase))
                { br.ReadBytes(8); br.ReadBytes(size); continue; }
                if (string.Equals(tn, "StructProperty", StringComparison.OrdinalIgnoreCase))
                { br.ReadBytes(8); br.ReadBytes(size); continue; }
                br.ReadBytes(size);
            }
        }
        catch { }
        return null;
    }

    private static string ResolveRefName(int objRef, UnrealHeader header)
    {
        if (objRef == 0) return "";
        if (objRef > 0)
        {
            int idx = objRef - 1;
            if (idx < 0 || idx >= header.ExportTable.Count) return "";
            return header.ExportTable[idx].ObjectNameIndex?.Name ?? "";
        }
        int iidx = -objRef - 1;
        if (iidx < 0 || iidx >= header.ImportTable.Count) return "";
        return header.ImportTable[iidx].ObjectNameIndex?.Name ?? "";
    }
}
