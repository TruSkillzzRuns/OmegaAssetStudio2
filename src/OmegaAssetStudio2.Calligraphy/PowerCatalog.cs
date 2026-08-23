using UpkManager.Models.UpkFile.Engine.Anim;

namespace OmegaAssetStudio.Calligraphy;

// One playable power on a character (e.g. Ultimate, BasicMelee, Signature).
// Phase 1: prototype metadata + name-heuristic-matched AnimSequence refs in the
// loaded character's AnimSets. Phase 2 will decode the prototype graph for
// definitive refs.
public sealed class PowerEntry
{
    // Power's path inside the Calligraphy archive, e.g. "Calligraphy/Powers/Player/<Hero>/Ultimate.prototype".
    public required string PrototypePath { get; init; }
    // Final path segment without extension, e.g. "Ultimate".
    public required string PowerName { get; init; }
    // Character-token folder before the file, e.g. "<Hero>".
    public required string CharacterToken { get; init; }
    // Display name resolved from the prototype (or PowerName if not resolvable).
    public string DisplayName { get; set; } = string.Empty;
    // Loco hash for the localized DisplayName (the 'S' DisplayName field on the
    // power prototype). 0 means no DisplayName field was present.
    public ulong DisplayNameHash { get; set; }
    // Prototype.directory asset ID for the IconPath ('A' IconPath field). 0 means
    // no icon was specified.
    public ulong IconAssetId { get; set; }
    // Resolved IconPath after looking IconAssetId up in Prototype.directory.
    // Example: "UI/Powers/<Hero>/Power_<Hero>_<Skill>.unr". Empty when unresolved.
    public string IconAssetPath { get; set; } = string.Empty;
    // The 'A PowerUnrealClass' field â€” asset ID of the UnrealScript class that
    // implements this power in the game (drives its animation, VFX, etc.).
    public ulong PowerUnrealClassId { get; set; }
    // Resolved class name from Powers/Types/PowerUnrealClass.type.
    // Example: "PowerThor_ThunderHammer" (the ACTUAL token used by animation names
    // like absattack_thunderhammer_rework_NN). Empty when unresolved.
    public string PowerUnrealClassName { get; set; } = string.Empty;
    // CANONICAL animation sequence names this power plays, read from
    // UC__<ClassName>_SF.upk's PowerFXAnimation / PowerFXAnimationLooping components.
    // This is the ground truth â€” what the game actually plays in-game.
    public List<string> CanonicalAnimNames { get; } = new();
    // Channel timing in seconds (from prototype L fields), 0 if unknown.
    public float ChannelStartSec { get; set; }
    public float ChannelLoopSec { get; set; }
    public float ChannelEndSec { get; set; }
    public float ChannelMinSec { get; set; }
    public float CooldownSec { get; set; }
    // True for Ultimate / signature etc. (from IsUltimate bool).
    public bool IsUltimate { get; set; }
    // Subfolder under Powers/Player/<token>/ â€” empty for top-level, "Rework" for reworked
    // skills, "Talents", "Traits", "MappedPowers", etc.
    public string Subfolder { get; set; } = string.Empty;
    // True when the prototype carries BOTH a DisplayName and an IconPath â€” the signature of a
    // user-visible skill (Master of Mjolnir / God of Thunder tree slots). Buffs, passives,
    // mechanic effects, summon-children etc. lack one or both. Used to filter the in-game
    // skill set out of the ~100+ Calligraphy prototypes per character.
    public bool IsVisibleSkill { get; set; }
    // True for prototypes that are passive stat boosts and have no in-game animation
    // by design (Defensive/Offensive Traits, Boons, MechanicTrait passives without
    // an active trigger). Set in LoadSkillTreeForCharacter based on the prototype's
    // filename. The matcher won't expect an anim and the panel reports them
    // explicitly so the "(N with anims)" total is honest.
    public bool IsPassive { get; set; }
    // Animation sequence refs matched against the loaded character's AnimSets,
    // populated by PowerCatalog.MatchAnimationsForPower. Lists are start/loop/end/hit.
    public List<UAnimSequence> StartSequences { get; } = new();
    public List<UAnimSequence> LoopSequences { get; } = new();
    public List<UAnimSequence> EndSequences { get; } = new();
    public List<UAnimSequence> OtherSequences { get; } = new();
    // Diagnostic: how the sequences were resolved.
    public string ResolutionSource { get; set; } = "none";

    public int TotalMatchedSequences =>
        StartSequences.Count + LoopSequences.Count + EndSequences.Count + OtherSequences.Count;
}

// Loads + caches every Calligraphy power prototype for a given character.
public static class PowerCatalog
{
    // AUTHORITATIVE skill tree loader. Reads
    //   Calligraphy/Entity/Characters/Avatars/Shipping/<Hero>.prototype
    // which lists each character's PowerProgressionTables -> PowerProgressionEntries,
    // each entry containing a PowerAssignment > Ability ref to the actual skill power.
    // This matches what the in-game Powers UI shows (Master of Mjolnir / God of Thunder
    // / Ultimate trees). Excludes passives, talents, buffs, mechanic effects, etc.
    public static List<PowerEntry> LoadSkillTreeForCharacter(KapgArchiveReader archive, BlueprintRegistry registry, string characterToken, Action<string>? log = null)
    {
        List<PowerEntry> result = new();
        if (string.IsNullOrWhiteSpace(characterToken)) return result;

        // Playable heroes live at Avatars/Shipping/<Hero>.prototype, but companions
        // (team-ups), bosses, enemies and NPCs live elsewhere under
        // Entity/Characters/ (Bosses/, Mobs/, NPCs/, PetsAndSummons/, Avatars/...).
        // Resolve the character's prototype across all of those, not just Shipping.
        if (!TryResolveCharacterPrototype(archive, characterToken, out var avatarEntry, out string matchedPath))
        {
            log?.Invoke($"[skills] no prototype found for token '{characterToken}'");
            return result;
        }
        log?.Invoke($"[skills] prototype matched for '{characterToken}': {matchedPath}");

        byte[] data;
        try { data = archive.ExtractEntry(avatarEntry); }
        catch { return result; }

        var parser = new PrototypeParser(data);
        if (!parser.TryParse(out _)) return result;

        // Build a hash -> entry-path map for resolving the Ability prototype refs.
        // The 'P' (prototype) field values are PROTOTYPE.DIRECTORY asset IDs â€” NOT the
        // KAPG entry-table FileHash. Using FileHash returns nothing because those are a
        // different hash function. Use the directory parser.
        Dictionary<ulong, string> pathByHash = new();
        var dirReader = PrototypeDirectoryReader.LoadFromArchive(archive);
        if (dirReader is not null)
        {
            foreach (var kv in dirReader.IdToPath)
            {
                // Directory paths use forward slashes already (LoadFromArchive normalised).
                // Prefix with "Calligraphy/" since archive entries are "Calligraphy/<path>"
                // â€” most other callers already use the full archive path. Storing both
                // forms keeps lookups simple downstream.
                pathByHash[kv.Key] = "Calligraphy/" + kv.Value;
            }
        }
        // Also overlay the archive's FileHash table as a fallback â€” some references may
        // still use that hash (early-bound asset refs, copies of CDOs).
        foreach (var e in archive.Entries)
            if (!pathByHash.ContainsKey(e.FileHash)) pathByHash[e.FileHash] = e.Name;

        HashSet<ulong> seenAbilities = new();
        int tablesSeen = 0;
        // Each table = one skill tab in the in-game UI.
        foreach (var group in parser.Result.Groups)
        {
            foreach (var listField in group.ListFields)
            {
                if (!registry.TryGetMemberById(listField.FieldId, out var member)) continue;
                if (!string.Equals(member.Name, "PowerProgressionTables", StringComparison.OrdinalIgnoreCase)) continue;

                foreach (var tableObj in listField.Values)
                {
                    if (tableObj is not PrototypeBody tableBody) continue;
                    tablesSeen++;
                    HarvestAbilitiesFromTable(tableBody, registry, pathByHash, archive, characterToken, result, seenAbilities);
                }
            }
        }
        log?.Invoke($"[skills] '{characterToken}' @ {matchedPath}: PowerProgressionTables={tablesSeen}, abilities={result.Count}");

        // Probe: when the prototype carries no hero-style skill tree (bosses,
        // companions, enemies use a different power-listing field), dump the
        // top-level member names so we can see the real field name instead of
        // guessing it. Remove once the non-hero power format is supported.
        if (tablesSeen == 0 && log is not null)
        {
            var memberNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in parser.Result.Groups)
            {
                foreach (var lf in group.ListFields)
                    if (registry.TryGetMemberById(lf.FieldId, out var m)) memberNames.Add(m.Name);
                foreach (var sf in group.SimpleFields)
                    if (registry.TryGetMemberById(sf.FieldId, out var m)) memberNames.Add(m.Name);
            }
            log($"[skills] '{characterToken}' top-level members ({memberNames.Count}): {string.Join(", ", memberNames)}");
        }

        // Sort: ultimates last, then by power name.
        result = result.OrderBy(p => p.IsUltimate ? 1 : 0).ThenBy(p => p.PowerName, StringComparer.OrdinalIgnoreCase).ToList();
        return result;
    }

    // Resolves a character token to its prototype entry. Tries the playable-hero
    // Shipping path first (exact), then a normalised search across every
    // *.prototype under Entity/Characters/ so companions, bosses, enemies and NPCs
    // (which live in Bosses/, Mobs/, NPCs/, PetsAndSummons/, Avatars/...) resolve too.
    private static bool TryResolveCharacterPrototype(KapgArchiveReader archive, string token, out KapgEntry entry, out string matchedPath)
    {
        entry = default;
        matchedPath = string.Empty;

        string shipping = $"Calligraphy/Entity/Characters/Avatars/Shipping/{token}.prototype";
        if (archive.TryFindByName(shipping, out entry)) { matchedPath = shipping; return true; }

        string normToken = NormalizeToken(token);
        if (normToken.Length == 0) return false;

        // Tiered match, best first. Tiers 3 & 5 handle the case where the prototype
        // leaf is SHORTER than the token (e.g. token "skrullelektraboss" vs leaf
        // "ElektraBoss") — without them those bosses report "no prototype found".
        KapgEntry exactHit = default, prefixHit = default, revPrefixHit = default, containsHit = default, revContainsHit = default;
        bool haveExact = false, havePrefix = false, haveRevPrefix = false, haveContains = false, haveRevContains = false;

        foreach (var e in archive.Entries)
        {
            string name = e.Name;
            if (string.IsNullOrEmpty(name)) continue;
            if (name.IndexOf("Entity/Characters/", StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (!name.EndsWith(".prototype", StringComparison.OrdinalIgnoreCase)) continue;

            string leaf = System.IO.Path.GetFileNameWithoutExtension(name);
            string normLeaf = NormalizeToken(leaf);
            if (normLeaf.Length == 0) continue;

            if (normLeaf == normToken) { exactHit = e; haveExact = true; break; }                                  // 1
            if (!havePrefix && normLeaf.StartsWith(normToken, StringComparison.Ordinal)) { prefixHit = e; havePrefix = true; }                 // 2
            else if (!haveRevPrefix && normLeaf.Length >= 5 && normToken.StartsWith(normLeaf, StringComparison.Ordinal)) { revPrefixHit = e; haveRevPrefix = true; } // 3
            else if (!haveContains && normLeaf.Contains(normToken, StringComparison.Ordinal)) { containsHit = e; haveContains = true; }        // 4
            else if (!haveRevContains && normLeaf.Length >= 5 && normToken.Contains(normLeaf, StringComparison.Ordinal)) { revContainsHit = e; haveRevContains = true; } // 5
        }

        if (haveExact) { entry = exactHit; matchedPath = exactHit.Name; return true; }
        if (havePrefix) { entry = prefixHit; matchedPath = prefixHit.Name; return true; }
        if (haveRevPrefix) { entry = revPrefixHit; matchedPath = revPrefixHit.Name; return true; }
        if (haveContains) { entry = containsHit; matchedPath = containsHit.Name; return true; }
        if (haveRevContains) { entry = revContainsHit; matchedPath = revContainsHit.Name; return true; }
        return false;
    }

    // Lower-cases and strips every non-alphanumeric character so "<Hero>_<Variant>",
    // "<Variant>" and "<variant>" all compare on the same normalised key.
    private static string NormalizeToken(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static void HarvestAbilitiesFromTable(
        PrototypeBody tableBody,
        BlueprintRegistry registry,
        Dictionary<ulong, string> pathByHash,
        KapgArchiveReader archive,
        string characterToken,
        List<PowerEntry> sink,
        HashSet<ulong> seenAbilities)
    {
        foreach (var group in tableBody.Groups)
        {
            foreach (var listField in group.ListFields)
            {
                if (!registry.TryGetMemberById(listField.FieldId, out var member)) continue;
                if (!string.Equals(member.Name, "PowerProgressionEntries", StringComparison.OrdinalIgnoreCase)) continue;

                foreach (var entryObj in listField.Values)
                {
                    if (entryObj is not PrototypeBody entryBody) continue;
                    foreach (var eg in entryBody.Groups)
                    {
                        // Recurse into PowerAssignment (R field) to find Ability (P field).
                        foreach (var ef in eg.SimpleFields)
                        {
                            if (!registry.TryGetMemberById(ef.FieldId, out var em)) continue;
                            if (string.Equals(em.Name, "PowerAssignment", StringComparison.OrdinalIgnoreCase) &&
                                ef.Values.Count > 0 && ef.Values[0] is PrototypeBody paBody)
                            {
                                ExtractAbilityFromAssignment(paBody, registry, pathByHash, archive, characterToken, sink, seenAbilities);
                            }
                        }
                    }
                }
            }
        }
    }

    private static void ExtractAbilityFromAssignment(
        PrototypeBody paBody,
        BlueprintRegistry registry,
        Dictionary<ulong, string> pathByHash,
        KapgArchiveReader archive,
        string characterToken,
        List<PowerEntry> sink,
        HashSet<ulong> seenAbilities)
    {
        foreach (var g in paBody.Groups)
        {
            foreach (var f in g.SimpleFields)
            {
                if (!registry.TryGetMemberById(f.FieldId, out var m)) continue;
                if (!string.Equals(m.Name, "Ability", StringComparison.OrdinalIgnoreCase)) continue;
                if (f.Values.Count == 0 || f.Values[0] is not ulong abilityId) continue;
                if (abilityId == 0UL || !seenAbilities.Add(abilityId)) continue;
                if (!pathByHash.TryGetValue(abilityId, out string? abilityPath) || string.IsNullOrEmpty(abilityPath)) continue;

                // Build the PowerEntry from the resolved power prototype.
                string powerName = Path.GetFileNameWithoutExtension(abilityPath);
                string subfolder = string.Empty;
                int playerIdx = abilityPath.IndexOf($"/{characterToken}/", StringComparison.OrdinalIgnoreCase);
                if (playerIdx >= 0)
                {
                    string after = abilityPath.Substring(playerIdx + characterToken.Length + 2);
                    int slash = after.IndexOf('/');
                    if (slash > 0) subfolder = after.Substring(0, slash);
                }

                // Passive detection by prototype filename. Prototypes whose name
                // ends with "Trait", "Boon", or contains "PassiveOnly" are stat
                // bonuses with no in-game animation. Tag them so the matcher
                // doesn't try to find anims and the UI can report them honestly.
                bool isPassive =
                    powerName.EndsWith("Trait", StringComparison.OrdinalIgnoreCase) ||
                    powerName.EndsWith("Boon", StringComparison.OrdinalIgnoreCase) ||
                    powerName.IndexOf("PassiveOnly", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    powerName.IndexOf("HiddenPassive", StringComparison.OrdinalIgnoreCase) >= 0;

                var entry = new PowerEntry
                {
                    PrototypePath = abilityPath,
                    PowerName = powerName,
                    CharacterToken = characterToken,
                    DisplayName = powerName,
                    Subfolder = subfolder,
                    IsVisibleSkill = true,
                    IsPassive = isPassive,
                };

                // Decode the power prototype for timing + ultimate flag.
                try
                {
                    if (archive.TryFindByName(abilityPath, out var powerEntry))
                    {
                        byte[] powerData = archive.ExtractEntry(powerEntry);
                        var pp = new PrototypeParser(powerData);
                        if (pp.TryParse(out _))
                            FillFromPrototypeWithRegistry(entry, pp.Result, registry);
                    }
                }
                catch { /* keep stub */ }

                sink.Add(entry);
            }
        }
    }

    // Enumerates power prototypes under Calligraphy/Powers/Player/<token>/ for the
    // given character token (e.g. "hero"). Returns a list of PowerEntry stubs with
    // metadata extracted from each prototype. Animation matching is a separate step
    // (call MatchAnimationsForPower after AnimSets are loaded).
    public static List<PowerEntry> LoadPowersForCharacter(KapgArchiveReader archive, string characterToken)
    {
        List<PowerEntry> powers = new();
        if (string.IsNullOrWhiteSpace(characterToken)) return powers;

        string prefix = $"Calligraphy/Powers/Player/{characterToken}/";
        // Include top-level + one level of subfolder. Subfolders like Rework/ contain the
        // ACTUAL currently-active skills for reworked characters.
        // Skip MappedPowers (keybind mappings), Talents (passive perks), Traits, and
        // deep tooltip/curve directories â€” those aren't user-visible skill animations.
        string[] skipSubfolders = { "MappedPowers", "Talents", "Traits", "Tooltips", "Curves" };

        foreach (var entry in archive.Entries)
        {
            if (!entry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!entry.Name.EndsWith(".prototype", StringComparison.OrdinalIgnoreCase)) continue;

            string relative = entry.Name.Substring(prefix.Length);
            int slash = relative.IndexOf('/');
            string subfolder = slash > 0 ? relative.Substring(0, slash) : string.Empty;
            // Reject deeper-than-one-level nests AND known non-skill subfolders.
            if (subfolder.Length > 0)
            {
                if (relative.Substring(slash + 1).Contains('/')) continue;
                if (skipSubfolders.Contains(subfolder, StringComparer.OrdinalIgnoreCase)) continue;
            }

            string powerName = Path.GetFileNameWithoutExtension(relative);
            PowerEntry power = new()
            {
                PrototypePath = entry.Name,
                PowerName = powerName,
                CharacterToken = characterToken,
                DisplayName = powerName,
                Subfolder = subfolder
            };

            try
            {
                byte[] data = archive.ExtractEntry(entry);
                var parser = new PrototypeParser(data);
                if (parser.TryParse(out _))
                    FillFromPrototype(power, parser.Result);
            }
            catch
            {
                // Best-effort: keep the stub even if the prototype decode fails.
            }

            powers.Add(power);
        }

        return powers.OrderByDescending(p => p.IsUltimate).ThenBy(p => p.PowerName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void FillFromPrototype(PowerEntry power, PrototypeBody body)
    {
        // Walk every field group; pick out the timing fields we care about by their
        // resolved member name. Field IDs vary by blueprint so name-keyed extraction is
        // the right path here.
        foreach (var group in body.Groups)
        {
            foreach (var f in group.SimpleFields)
            {
                if (f.Values.Count == 0) continue;
                // We can only resolve field names if we have a registry; the parser doesn't
                // do name resolution itself. Caller can pass a registry for richer data,
                // but the timing fields can also be found by their L (int64) values being
                // in a known plausible range (e.g., timing in ms is positive, <60000).
                // For now we leave the field name resolution to the future schema-aware pass.
            }
        }
        // Timing extraction with the schema-aware path lives in FillFromPrototypeWithRegistry.
    }

    public static void FillFromPrototypeWithRegistry(PowerEntry power, PrototypeBody body, BlueprintRegistry registry)
    {
        bool hasDisplayName = false;
        bool hasIconPath = false;

        foreach (var group in body.Groups)
        {
            foreach (var f in group.SimpleFields)
            {
                if (!registry.TryGetMemberById(f.FieldId, out var member)) continue;
                if (f.Values.Count == 0) continue;
                object v = f.Values[0];
                switch (member.Name)
                {
                    case "ChannelStartTimeMS" when v is long l1: power.ChannelStartSec = l1 / 1000.0f; break;
                    case "ChannelEndTimeMS" when v is long l2: power.ChannelEndSec = l2 / 1000.0f; break;
                    case "ChannelMinTimeMS" when v is long l3: power.ChannelMinSec = l3 / 1000.0f; break;
                    case "IsUltimate" when v is bool b1: power.IsUltimate = b1; break;
                    case "DisplayName" when v is ulong dnId && dnId != 0UL: hasDisplayName = true; power.DisplayNameHash = dnId; break;
                    case "IconPath" when v is ulong ipId && ipId != 0UL: hasIconPath = true; power.IconAssetId = ipId; break;
                    case "PowerUnrealClass" when v is ulong pucId && pucId != 0UL: power.PowerUnrealClassId = pucId; break;
                }
            }
        }

        power.IsVisibleSkill = hasDisplayName && hasIconPath;
    }

    // Replace each power's heuristic CamelCase DisplayName with the actual localized
    // text from the game's Loco database, resolve IconPath asset IDs to UPK paths via
    // Prototype.directory, and resolve PowerUnrealClass asset IDs to class names via
    // Powers/Types/PowerUnrealClass.type. The class name (e.g. "PowerThor_ThunderHammer")
    // is the strongest signal for matching animation sequences.
    public static void ResolveDisplayNamesAndIcons(
        IEnumerable<PowerEntry> powers,
        LocoIndex? loco,
        PrototypeDirectoryReader? directory,
        TypeDirectoryReader? powerUnrealClasses = null,
        TypeDirectoryReader? powerIconPaths = null)
    {
        foreach (var p in powers)
        {
            if (loco is not null && p.DisplayNameHash != 0UL &&
                loco.TryResolveString(p.DisplayNameHash, out string localized) &&
                !string.IsNullOrWhiteSpace(localized))
            {
                p.DisplayName = localized;
            }
            // PRIMARY icon source: PowerIconPathType.type maps IconPath hashes to UPK
            // asset paths like "MarvelUIIcons.Power_Thor_Ultimate". Falls back to
            // Prototype.directory for any holdout entries.
            if (powerIconPaths is not null && p.IconAssetId != 0UL &&
                powerIconPaths.IdToName.TryGetValue(p.IconAssetId, out string? iconNameFromType) &&
                !string.IsNullOrWhiteSpace(iconNameFromType))
            {
                p.IconAssetPath = iconNameFromType;
            }
            else if (directory is not null && p.IconAssetId != 0UL &&
                directory.IdToPath.TryGetValue(p.IconAssetId, out string? iconPath) &&
                !string.IsNullOrWhiteSpace(iconPath))
            {
                p.IconAssetPath = iconPath;
            }
            if (powerUnrealClasses is not null && p.PowerUnrealClassId != 0UL &&
                powerUnrealClasses.IdToName.TryGetValue(p.PowerUnrealClassId, out string? className) &&
                !string.IsNullOrWhiteSpace(className))
            {
                p.PowerUnrealClassName = className;
            }
        }
    }

    // Populates each power's CanonicalAnimNames by loading its UC__<ClassName>_SF.upk
    // and reading the PowerFXAnimation / PowerFXAnimationLooping component animname
    // properties. This is the ground-truth mapping straight from the game data.
    public static async Task ResolveCanonicalAnimsAsync(
        IEnumerable<PowerEntry> powers,
        string canonicalCookedDir,
        UpkManager.Repository.UpkFileRepository repo,
        Action<string>? log = null)
    {
        foreach (var p in powers)
        {
            // PRIMARY path: PowerUnrealClass field on the prototype gave us the class.
            string? cls = string.IsNullOrWhiteSpace(p.PowerUnrealClassName) ? null : p.PowerUnrealClassName;

            // FALLBACK 1: wrapper powers like "MiraculousAssaultStart" have an empty
            // PowerUnrealClass field on their prototype (they reference the real power
            // via an indirect TriggeredPower field we don't yet decode). Their actual
            // implementation usually lives under the base power name's class â€”
            // strip trailing "Start"/"End"/"Effect" suffix and try
            // UC__Power<Character>_<base>_SF.upk.
            //
            // FALLBACK 2: even when there's no suffix, the convention
            // UC__Power<Character>_<PowerName>_SF.upk holds for most powers â€” try it.
            if (cls is null && !string.IsNullOrWhiteSpace(p.CharacterToken) && !string.IsNullOrWhiteSpace(p.PowerName))
            {
                foreach (string guess in BuildClassNameGuesses(p.CharacterToken, p.PowerName))
                {
                    string upkPath = System.IO.Path.Combine(canonicalCookedDir, $"UC__{guess}_SF.upk");
                    if (System.IO.File.Exists(upkPath)) { cls = guess; break; }
                }
                if (cls is not null) log?.Invoke($"[power-resolve] {p.DisplayName} ({p.PowerName}) â€” class inferred from UPK: {cls}");
            }
            if (cls is null) continue;

            var resolved = await PowerAnimResolver.ResolveAsync(cls, canonicalCookedDir, repo).ConfigureAwait(false);
            if (resolved is null || resolved.Sequences.Count == 0) continue;
            p.CanonicalAnimNames.Clear();
            p.CanonicalAnimNames.AddRange(resolved.Sequences);
            // Persist the discovered class so the in-game lookup logs reflect the real source.
            if (string.IsNullOrWhiteSpace(p.PowerUnrealClassName)) p.PowerUnrealClassName = cls;
            log?.Invoke($"[power-resolve] {p.DisplayName} ({cls}) -> {string.Join(", ", resolved.Sequences)}");
        }
    }

    private static IEnumerable<string> BuildClassNameGuesses(string character, string powerName)
    {
        // Strip common wrapper suffixes that don't appear in the per-power UPK name.
        string[] suffixes = { "Start", "End", "Effect", "Cone", "Proc", "Wrapper" };
        var bases = new List<string> { powerName };
        foreach (string sfx in suffixes)
        {
            if (powerName.EndsWith(sfx, StringComparison.Ordinal) && powerName.Length > sfx.Length)
                bases.Add(powerName.Substring(0, powerName.Length - sfx.Length));
        }
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string b in bases)
        {
            foreach (string candidate in new[] {
                $"Power{character}_{b}",
                $"Power{b}_{character}",
                $"Power{b}",
            })
            {
                if (emitted.Add(candidate)) yield return candidate;
            }
        }
    }

    // Match the power's animations from a flat list of loaded AnimSequences. Builds a
    // ranked list of candidate prefixes from both the prototype filename AND the
    // localized DisplayName, and tries each in priority order.
    //
    // Real TargetClient sequence names live in the character.s *_anim package, e.g. <hero>_as:
    //   absattack_<ultimateskill>_rework_start/loop/end   <- canonical Ultimate animation
    //   absattack_<skillA>_rework_start/loop/end          <- a variant skill
    //   absattack_<skillB>_start/end                      <- a signature skill
    //   absattack_<melee>_rework_NN                       <- reworked basic-melee combo
    //
    // The matching strategy:
    //   1. Build a set of name-tokens from the prototype filename, the DisplayName, and
    //      common "ultimate<X>" / "<X>_rework" variants.
    //   2. For each token, look for sequences named "absattack_<token>" / "power_<token>"
    //      / "<token>". First token to produce hits wins.
    public static void MatchAnimationsForPower(PowerEntry power, IEnumerable<UAnimSequence> allSequences)
        => MatchAnimationsForPower(power, allSequences, crossUpkResolver: null);

    // Cross-UPK overload. When canonical anim names exist but aren't found in
    // the local sequence pool, the page can pass a resolver (typically backed
    // by the global AnimSet name index + UpkFileRepository) that pulls the
    // sequence from whichever UPK actually owns it. Without this overload,
    // skills whose anims live in shared / power-specific UPKs show as
    // 0/0/0/0 in the panel — the user reported this for many heroes.
    public static void MatchAnimationsForPower(PowerEntry power, IEnumerable<UAnimSequence> allSequences, Func<string, UAnimSequence?>? crossUpkResolver)
    {
        power.StartSequences.Clear();
        power.LoopSequences.Clear();
        power.EndSequences.Clear();
        power.OtherSequences.Clear();

        // GROUND TRUTH PATH: if the per-power UPK gave us canonical AnimSequence names
        // (via PowerFXAnimation / PowerFXAnimationLooping components on the class default
        // object), match those directly. No guessing — these are the literal names the
        // game plays in-game.
        if (power.CanonicalAnimNames.Count > 0)
        {
            var seqByName = allSequences
                .Where(s => !string.IsNullOrEmpty(s.SequenceName?.Name))
                .GroupBy(s => s.SequenceName!.Name!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            int canonicalHits = 0;
            int crossUpkHits = 0;
            foreach (string name in power.CanonicalAnimNames)
            {
                UAnimSequence? seq;
                bool fromCrossUpk = false;
                if (!seqByName.TryGetValue(name, out seq))
                {
                    // Local pool didn't have it — try the cross-UPK resolver
                    // before giving up. This is how skills whose anims live
                    // in shared/power-specific UPKs get their full segment
                    // list instead of showing 0/0/0/0.
                    if (crossUpkResolver is not null)
                    {
                        seq = crossUpkResolver(name);
                        if (seq is not null) { fromCrossUpk = true; crossUpkHits++; }
                    }
                    if (seq is null) continue;
                }
                string tail = string.Empty;
                int us = name.LastIndexOf('_');
                if (us >= 0) tail = "_" + name.Substring(us + 1).ToLowerInvariant();
                ClassifyAndAdd(power, seq, tail);
                canonicalHits++;
            }
            if (canonicalHits > 0)
            {
                power.ResolutionSource = crossUpkHits > 0
                    ? $"canonical:{canonicalHits}/{power.CanonicalAnimNames.Count} (+{crossUpkHits} cross-upk)"
                    : $"canonical:{canonicalHits}/{power.CanonicalAnimNames.Count}";
                return;
            }
        }

        string powerLow = (power.PowerName ?? string.Empty).ToLowerInvariant();
        string displayLow = (power.DisplayName ?? string.Empty).ToLowerInvariant();
        // Strip spaces and non-alnum from the display name: "Foo Bar" -> "foobar",
        // "Crack the Sky" -> "crackthesky". TargetClient sequence names are letters+digits only.
        string displayToken = new(displayLow.Where(c => char.IsLetterOrDigit(c)).ToArray());

        // Strongest signal: PowerUnrealClass name from Powers/Types/PowerUnrealClass.type.
        // TargetClient uses several class-naming conventions:
        //   "Power<Hero>_<Skill>"  -> token "<skill>" (strip "Power" + "<Hero>_")
        //   "PowerGroundSmash_Thor"       -> token "groundsmash"          (strip "Power" + "_Thor")
        //   "PowerKnockdownCharge_Thor"   -> token "knockdowncharge"      (strip "Power" + "_Thor")
        //   "PowerFlyMjolnir"             -> token "flymjolnir"           (strip "Power" only)
        //   "PowerElementalStorm_Thor"    -> token "elementalstorm"       (strip "Power" + "_Thor")
        // We strip the "Power" prefix and any occurrence of "<CharacterToken>" with its
        // adjacent underscores, then lowercase + alnum.
        string classToken = string.Empty;
        string classRaw = power.PowerUnrealClassName ?? string.Empty;
        if (classRaw.Length > 0 && classRaw.StartsWith("Power", StringComparison.OrdinalIgnoreCase))
        {
            string body = classRaw.Substring("Power".Length);
            string hero = power.CharacterToken ?? string.Empty;
            if (hero.Length > 0)
            {
                // Remove "<hero>_" prefix or "_<hero>" suffix or stand-alone "_<hero>_".
                if (body.StartsWith(hero + "_", StringComparison.OrdinalIgnoreCase))
                    body = body.Substring(hero.Length + 1);
                else if (body.EndsWith("_" + hero, StringComparison.OrdinalIgnoreCase))
                    body = body.Substring(0, body.Length - hero.Length - 1);
                else
                {
                    int mid = body.IndexOf("_" + hero + "_", StringComparison.OrdinalIgnoreCase);
                    if (mid > 0)
                        body = body.Substring(0, mid) + body.Substring(mid + hero.Length + 1);
                }
            }
            classToken = new(body.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c)).ToArray());
        }

        var tokens = new List<string>();
        void AddToken(string t) { if (!string.IsNullOrEmpty(t) && !tokens.Contains(t, StringComparer.OrdinalIgnoreCase)) tokens.Add(t); }

        // PowerUnrealClass-derived tokens â€” highest priority because they're the game's
        // own naming convention for this skill.
        if (classToken.Length > 0)
        {
            AddToken(classToken + "_rework");
            AddToken(classToken);
        }
        // Display-name-derived tokens (catches reworked / ultimate powers where the
        // prototype filename is generic).
        if (displayToken.Length > 0)
        {
            AddToken(displayToken + "_rework");                 // <skill>_rework
            AddToken(displayToken);                              // <skill>
            AddToken("ultimate" + displayToken + "_rework");    // <ultimateskill>_rework
            AddToken("ultimate" + displayToken);                // <ultimateskill>
        }
        // Prototype-filename tokens (catches powers whose filename IS the game name).
        AddToken(powerLow + "_rework");                          // boltspray_rework
        AddToken(powerLow);                                      // boltspray
        if (power.IsUltimate)
        {
            AddToken("ultimate" + powerLow + "_rework");        // ultimateodinforce_rework
            AddToken("ultimate" + powerLow);                    // ultimateodinforce
        }

        // For each token, try the three prefix conventions in order.
        string[] prefixForms = { "absattack_", "power_", "" };
        int matched = 0;
        string? hitPrefix = null;
        foreach (string token in tokens)
        {
            if (matched > 0) break;
            foreach (string prefix in prefixForms)
            {
                string fullPrefix = prefix + token;
                foreach (var seq in allSequences)
                {
                    string seqName = seq.SequenceName?.Name ?? string.Empty;
                    if (string.IsNullOrEmpty(seqName)) continue;
                    if (!seqName.StartsWith(fullPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    // Require word-boundary after the prefix so "boltspray" doesn't
                    // accidentally also match "boltsprayhotspot".
                    if (seqName.Length > fullPrefix.Length)
                    {
                        char next = seqName[fullPrefix.Length];
                        if (next != '_' && !char.IsDigit(next)) continue;
                    }
                    ClassifyAndAdd(power, seq, seqName.Substring(fullPrefix.Length).ToLowerInvariant());
                    matched++;
                }
                if (matched > 0) { hitPrefix = fullPrefix; break; }
            }
        }

        // Fuzzy fallback intentionally removed: per the user's request, the Skills panel
        // should only play CORRECT animations driven by the power's actual prototype data
        // (PowerUnrealClass / displayname / prototype filename). A skill with no
        // confident match simply shows "no anim matched" rather than guessing a
        // plausibly-named clip that may not be what the game plays.

        power.ResolutionSource = hitPrefix is null ? "no-match" : $"prefix:{hitPrefix}";
    }

    // Pull the "<token>" out of "<token>_start" / "<token>_loop" / "<token>_rework_NN" /
    // "<token>" â€” anything before the first trailing modifier. Used when grouping
    // animation sequences by their power token.
    private static string? ExtractSeqToken(string afterPrefix)
    {
        if (string.IsNullOrEmpty(afterPrefix)) return null;
        // Treat _start/_loop/_end/_in/_out/_channel/_rework/_NN as modifiers.
        string[] mods = { "_start", "_loop", "_end", "_in", "_out", "_channel", "_rework", "_combo" };
        int firstMod = afterPrefix.Length;
        foreach (string m in mods)
        {
            int idx = afterPrefix.IndexOf(m, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && idx < firstMod) firstMod = idx;
        }
        // Trailing numeric variant like "_01".
        int us = afterPrefix.IndexOf('_');
        if (us >= 0 && us < firstMod)
        {
            // Check if what's after _ is purely digits.
            string tail = afterPrefix.Substring(us + 1);
            if (tail.Length > 0 && tail.All(c => char.IsDigit(c) || c == '_')) firstMod = us;
        }
        string token = afterPrefix.Substring(0, firstMod);
        return token.Length >= 3 ? token : null;
    }

    private static void ClassifyAndAdd(PowerEntry power, UAnimSequence seq, string tail)
    {
        if (tail.StartsWith("_start") || tail == "_in") power.StartSequences.Add(seq);
        else if (tail.StartsWith("_loop") || tail.StartsWith("_channel")) power.LoopSequences.Add(seq);
        else if (tail.StartsWith("_end") || tail == "_out") power.EndSequences.Add(seq);
        else power.OtherSequences.Add(seq);
    }
}

