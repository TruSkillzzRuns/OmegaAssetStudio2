using OmegaAssetStudio.TextureManager;
using OmegaAssetStudio.WinUI.Services;
using UpkManager.Models.UpkFile;
using UpkManager.Models.UpkFile.Tables;
using UpkManager.Repository;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.Safety;

// Pre-flight read-only checks that flag the specific data states which
// produce the in-game failure modes users hit most:
//   - White textures   → TFC entry missing or unresolved texture imports
//   - Blue / flat shade → normal map slot empty
//   - Pink material    → compiled FMaterialResource missing, or shader
//                        cache GUID drift between MIC and parent Material
//
// Every check is read-only and self-contained — no UPK writes, no game
// launch. Run on demand from the Material Editor's Pre-flight panel and
// (optionally) before any Import / Save command lands a write.
public static class MaterialPreflightDiagnostics
{
    public enum Severity { Info, Warning, Error }

    public sealed record Finding(
        Severity Level,
        string CheckId,
        string Title,
        string Detail,
        string FixHint);

    public sealed record Report(
        string UpkPath,
        string MaterialExportPath,
        string MaterialClass,
        IReadOnlyList<Finding> Findings,
        DateTime RanAtUtc)
    {
        public int ErrorCount => Findings.Count(f => f.Level == Severity.Error);
        public int WarningCount => Findings.Count(f => f.Level == Severity.Warning);
        public int InfoCount => Findings.Count(f => f.Level == Severity.Info);
        public bool PassedClean => ErrorCount == 0 && WarningCount == 0;
    }

    public static async Task<Report> RunAsync(
        string upkPath, string materialExportPath, CancellationToken ct = default)
    {
        var findings = new List<Finding>();
        if (!File.Exists(upkPath))
        {
            findings.Add(new(Severity.Error, "load",
                "UPK not found", $"The path '{upkPath}' could not be opened.",
                "Re-load the UPK from disk and retry pre-flight."));
            return new(upkPath, materialExportPath, "", findings, DateTime.UtcNow);
        }

        UnrealHeader header;
        UnrealExportTableEntry export;
        try
        {
            var repo = new UpkFileRepository();
            header = await repo.LoadUpkFile(upkPath).ConfigureAwait(false);
            await header.ReadHeaderAsync(null).ConfigureAwait(false);
            var found = header.ExportTable.FirstOrDefault(e =>
                string.Equals(e.GetPathName(), materialExportPath, StringComparison.OrdinalIgnoreCase));
            if (found is null)
            {
                findings.Add(new(Severity.Error, "load",
                    "Export not found",
                    $"'{materialExportPath}' was not found in {Path.GetFileName(upkPath)}.",
                    "Reload the UPK; the editor's selection may be stale."));
                return new(upkPath, materialExportPath, "", findings, DateTime.UtcNow);
            }
            export = found;
        }
        catch (Exception ex)
        {
            findings.Add(new(Severity.Error, "load",
                "UPK parse failed",
                $"{ex.GetType().Name}: {ex.Message}",
                "Check the file isn't locked by another process; try Reload from disk."));
            return new(upkPath, materialExportPath, "", findings, DateTime.UtcNow);
        }

        string cls = export.ClassReferenceNameIndex?.Name ?? "Unknown";
        byte[] body = export.UnrealObjectReader?.GetBytes() ?? Array.Empty<byte>();

        // Run each check independently — one failing doesn't block the others.
        Try(() => CheckShaderCachePresent(body, header, cls, findings));
        Try(() => CheckBaseMaterialIdAlignment(body, header, export, cls, findings));
        Try(() => CheckNormalSlotPopulated(body, header, cls, findings));
        Try(() => CheckTextureImportsResolve(body, header, findings));
        Try(() => CheckTfcEntriesPresent(body, header, findings));

        return new(upkPath, materialExportPath, cls, findings, DateTime.UtcNow);
    }

    private static void Try(Action a)
    {
        try { a(); } catch { /* a failed check is silent — others still run */ }
    }

    // ----------------------------------------------------------------
    // 1. Compiled FMaterialResource present and non-empty?
    //    Empty cache → engine can't bind a shader → renders pink in-game.
    // ----------------------------------------------------------------
    private static void CheckShaderCachePresent(
        byte[] body, UnrealHeader header, string cls, List<Finding> findings)
    {
        bool isMaterial = string.Equals(cls, "Material", StringComparison.OrdinalIgnoreCase);
        bool isMic = string.Equals(cls, "MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(cls, "MaterialInstanceTimeVarying", StringComparison.OrdinalIgnoreCase);
        if (!isMaterial && !isMic) return;

        var split = MaterialCore.Cloning.MaterialBodySplitter.Split(body, header);
        if (isMaterial && split.TailBytes.Length < 4)
        {
            findings.Add(new(Severity.Error, "shader-cache",
                "Compiled shader cache missing",
                "This UMaterial has no binary FMaterialResource tail at all (or only " +
                $"{split.TailBytes.Length} stray bytes). Renders pink in-game.",
                "Use 'Import Shaders' from a donor UPK whose Material is known to render correctly."));
            return;
        }
        if (isMic && !split.HasStaticPermutationResource)
        {
            findings.Add(new(Severity.Info, "shader-cache",
                "MIC has no static-permutation resource",
                "bHasStaticPermutationResource is false; the MIC relies entirely on " +
                "its parent's shader cache. That is fine unless the parent itself is broken.",
                "If renders pink, run pre-flight on the parent UMaterial too."));
            return;
        }
        if (split.TailBytes.Length < 4) return;

        // Quality mask = 0 ⇒ no resource was serialized for either quality slot.
        uint qualityMask = BitConverter.ToUInt32(split.TailBytes, 0);
        if (qualityMask == 0)
        {
            findings.Add(new(Severity.Error, "shader-cache",
                "Shader cache is stub (qualityMask = 0)",
                "Both quality slots are empty in the FMaterialResource[2] tail. " +
                "Renders pink in-game.",
                "Use 'Import Shaders' to populate the cache from a compatible donor Material."));
        }
    }

    // ----------------------------------------------------------------
    // 2. MIC's BaseMaterialId should equal its parent Material's
    //    FMaterial.Id. Drift = engine cache miss → pink.
    // ----------------------------------------------------------------
    private static void CheckBaseMaterialIdAlignment(
        byte[] body, UnrealHeader header, UnrealExportTableEntry export,
        string cls, List<Finding> findings)
    {
        bool isMic = string.Equals(cls, "MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(cls, "MaterialInstanceTimeVarying", StringComparison.OrdinalIgnoreCase);
        if (!isMic) return;

        // Read MIC's BaseMaterialId from the trailing FStaticParameterSet[2].
        var micSplit = MaterialCore.Cloning.MaterialBodySplitter.Split(body, header);
        if (!micSplit.HasStaticPermutationResource || micSplit.TailBytes.Length < 4) return;
        Guid? micBaseId = TryReadFirstStaticParameterSetGuid(micSplit.TailBytes);
        if (micBaseId is null) return;

        // Walk MIC's tagged properties to find Parent ref.
        int parentRef = TryReadObjectProperty(body, header, "Parent") ?? 0;
        if (parentRef <= 0) return; // parent is an import or null — skip cross-UPK chase

        int idx = parentRef - 1;
        if (idx < 0 || idx >= header.ExportTable.Count) return;
        var parentExport = header.ExportTable[idx];
        if (!string.Equals(parentExport.ClassReferenceNameIndex?.Name, "Material",
                           StringComparison.OrdinalIgnoreCase))
            return;

        byte[] parentBody = parentExport.UnrealObjectReader?.GetBytes() ?? Array.Empty<byte>();
        if (parentBody.Length < 5) return;
        var parentSplit = MaterialCore.Cloning.MaterialBodySplitter.Split(parentBody, header);
        Guid? parentMatId = TryReadFirstFMaterialResourceId(parentSplit.TailBytes);
        if (parentMatId is null) return;

        if (micBaseId.Value == parentMatId.Value) return;

        findings.Add(new(Severity.Warning, "base-material-id",
            "BaseMaterialId drift between MIC and parent",
            $"MIC's FStaticParameterSet.BaseMaterialId = {micBaseId.Value:N}, but " +
            $"parent Material's FMaterial.Id = {parentMatId.Value:N}. The engine's " +
            "shader-map cache keys on the parent's Id; a mismatch means the MIC " +
            "won't find a compiled shader and may render pink.",
            "Either re-import the MIC from a donor whose parent's Id matches yours, " +
            "or run 'Import Shaders' to bring across a compatible compiled cache."));
    }

    // ----------------------------------------------------------------
    // 3. Normal map slot empty?
    //    Engine substitutes a flat-blue default → flat-blue rendering.
    // ----------------------------------------------------------------
    private static void CheckNormalSlotPopulated(
        byte[] body, UnrealHeader header, string cls, List<Finding> findings)
    {
        bool isMic = string.Equals(cls, "MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(cls, "MaterialInstanceTimeVarying", StringComparison.OrdinalIgnoreCase);
        if (!isMic) return;

        // Walk TextureParameterValues looking for normal-shaped names.
        var entries = ReadTextureParameterValues(body, header);
        if (entries.Count == 0) return;
        foreach (var (name, value) in entries)
        {
            if (!LooksLikeNormalSlot(name)) continue;
            if (value != 0) continue;
            findings.Add(new(Severity.Warning, "normal-slot",
                $"Normal slot '{name}' is empty",
                $"The TextureParameterValue '{name}' has a null binding. The engine " +
                "will substitute its default normal (flat blue), causing the " +
                "lit surface to look flat / lose detail.",
                "Bind a normal map via the Textures section, or repoint the slot " +
                "to a known-good _N / _NRM texture from a sibling material."));
        }
    }

    // ----------------------------------------------------------------
    // 4. Texture import refs resolve?
    //    Import points at a UPK that doesn't exist → engine fails to
    //    bind that texture → white rendering on that slot.
    // ----------------------------------------------------------------
    private static void CheckTextureImportsResolve(
        byte[] body, UnrealHeader header, List<Finding> findings)
    {
        string? cooked = GameInstallService.GetCookedDataDir();
        if (string.IsNullOrWhiteSpace(cooked) || !Directory.Exists(cooked))
        {
            findings.Add(new(Severity.Info, "texture-imports",
                "Texture-import check skipped",
                "Game install root not configured, so the texture-import resolution " +
                "check could not run.",
                "Set the Game Install Root in Settings to enable this check."));
            return;
        }

        // Collect every texture ref the material references.
        var entries = ReadTextureParameterValues(body, header);
        // Also pull refs from the body's binary tail (UniformExpressionTextures).
        var tailRefs = MaterialCore.Cloning.MaterialBodyRefScanner.Scan(body, header,
            classHintForTail(header, body)).PositiveExportRefs;

        var refs = new HashSet<int>();
        foreach (var (_, r) in entries) refs.Add(r);
        foreach (var r in tailRefs) refs.Add(r);

        var unresolved = new List<string>();
        foreach (int r in refs)
        {
            if (r >= 0) continue; // exports are local — resolution irrelevant here
            int importIdx = -r - 1;
            if (importIdx < 0 || importIdx >= header.ImportTable.Count) continue;
            var import = header.ImportTable[importIdx];
            if (!string.Equals(import.ClassNameIndex?.Name, "Texture2D",
                               StringComparison.OrdinalIgnoreCase))
                continue;
            string packageName = TopLevelPackageName(import, header);
            if (string.IsNullOrWhiteSpace(packageName)) continue;
            // Look for <packageName>.upk somewhere under the cooked dir.
            string candidate = Path.Combine(cooked, packageName + ".upk");
            if (File.Exists(candidate)) continue;
            // Recursive fallback (cooked dir can have subfolders).
            bool found = Directory.EnumerateFiles(cooked, packageName + ".upk",
                SearchOption.AllDirectories).Any();
            if (!found) unresolved.Add($"{packageName}.{import.ObjectNameIndex?.Name ?? "?"}");
        }

        if (unresolved.Count > 0)
        {
            findings.Add(new(Severity.Error, "texture-imports",
                $"{unresolved.Count} texture import(s) don't resolve",
                "These Texture2D imports point at UPKs not found in your cooked-data " +
                "folder. The engine cannot bind them and the affected texture slot " +
                "renders white:\n  " + string.Join("\n  ", unresolved.Take(8)) +
                (unresolved.Count > 8 ? $"\n  ... and {unresolved.Count - 8} more" : ""),
                "Use 'Import Full Material' to copy missing textures locally, or " +
                "repoint the slot to an import whose package IS installed."));
        }

        static string classHintForTail(UnrealHeader header, byte[] body) => "Material";
    }

    // ----------------------------------------------------------------
    // 5. TFC entries present?
    //    Texture's GUID is not in the TFC manifest → engine can't find
    //    the pixel data → white slot.
    // ----------------------------------------------------------------
    private static void CheckTfcEntriesPresent(
        byte[] body, UnrealHeader header, List<Finding> findings)
    {
        var manifest = TextureManifest.Instance;
        if (manifest is null || manifest.Entries.Count == 0)
        {
            findings.Add(new(Severity.Info, "tfc-entries",
                "TFC manifest check skipped",
                "TextureFileCacheManifest.bin is not loaded.",
                "Load the manifest in Settings to enable TFC presence checks."));
            return;
        }

        // For each LOCAL Texture2D export referenced by the material, ask the
        // manifest whether its GUID is present.
        var refs = MaterialCore.Cloning.MaterialBodyRefScanner.Scan(body, header, "Material").PositiveExportRefs;
        var entries = ReadTextureParameterValues(body, header);
        var allRefs = new HashSet<int>(refs);
        foreach (var (_, r) in entries) allRefs.Add(r);

        var missing = new List<string>();
        foreach (int r in allRefs)
        {
            if (r <= 0) continue;
            int idx = r - 1;
            if (idx >= header.ExportTable.Count) continue;
            var tex = header.ExportTable[idx];
            if (!string.Equals(tex.ClassReferenceNameIndex?.Name, "Texture2D",
                               StringComparison.OrdinalIgnoreCase))
                continue;
            // Resolve the texture's GUID via the manifest lookup helper.
            try
            {
                if (tex.UnrealObject is null)
                    tex.ParseUnrealObject(false, false).GetAwaiter().GetResult();
                if (tex.UnrealObject is UpkManager.Models.UpkFile.Objects.IUnrealObject u
                    && u.UObject is UpkManager.Models.UpkFile.Engine.Texture.UTexture2D t2d)
                {
                    var entry = manifest.GetTextureEntry(t2d);
                    if (entry is null)
                        missing.Add(tex.ObjectNameIndex?.Name ?? "?");
                }
            }
            catch { /* unparseable texture — skip silently */ }
        }

        if (missing.Count > 0)
        {
            findings.Add(new(Severity.Warning, "tfc-entries",
                $"{missing.Count} local texture(s) not in the loaded TFC manifest",
                "These local Texture2D exports either reference TFC entries that have " +
                "been moved or the manifest is stale. Affected slots render white in-game:\n  " +
                string.Join(", ", missing.Take(12)) +
                (missing.Count > 12 ? $", ... +{missing.Count - 12} more" : ""),
                "Re-import the texture pixel data via Textures 2.0, or refresh the TFC " +
                "manifest in Settings."));
        }
    }

    // ----------------------------------------------------------------
    // Body-walking helpers (read-only, small).
    // ----------------------------------------------------------------
    private static List<(string Name, int ObjRef)> ReadTextureParameterValues(
        byte[] body, UnrealHeader header)
    {
        var result = new List<(string, int)>();
        if (body.Length <= 4) return result;
        var names = new string[header.NameTable.Count];
        for (int i = 0; i < names.Length; i++)
            names[i] = header.NameTable[i].Name?.String ?? "";

        using var br = new BinaryReader(new MemoryStream(body, writable: false));
        br.ReadInt32(); // NetIndex
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
                if (string.Equals(tn, "BoolProperty", StringComparison.OrdinalIgnoreCase))
                { br.ReadByte(); continue; }
                if (string.Equals(tn, "ByteProperty", StringComparison.OrdinalIgnoreCase))
                { br.ReadBytes(8); br.ReadBytes(size); continue; }
                if (string.Equals(tn, "StructProperty", StringComparison.OrdinalIgnoreCase))
                { br.ReadBytes(8); br.ReadBytes(size); continue; }
                if (string.Equals(tn, "ArrayProperty", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(pn, "TextureParameterValues", StringComparison.OrdinalIgnoreCase))
                {
                    int count = br.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        int pnIdx = br.ReadInt32(); br.ReadInt32();
                        int objRef = br.ReadInt32();
                        br.ReadBytes(16);
                        string paramName = (pnIdx >= 0 && pnIdx < names.Length) ? names[pnIdx] : "";
                        result.Add((paramName, objRef));
                    }
                    continue;
                }
                br.ReadBytes(size);
            }
        }
        catch { /* malformed — return what we have */ }
        return result;
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

    // First FMaterialResource (q=0 slot) begins right after the qualityMask
    // uint32. Its FGuid Id sits after the CompileErrors + TextureDependencyLengthMap
    // + MaxTextureDependencyLength sequence. We walk that prefix to land on Id.
    private static Guid? TryReadFirstFMaterialResourceId(byte[] tail)
    {
        try
        {
            using var br = new BinaryReader(new MemoryStream(tail, writable: false));
            uint mask = br.ReadUInt32();
            if (mask == 0) return null;
            // CompileErrors UArray<string>
            int sc = br.ReadInt32();
            for (int i = 0; i < sc; i++)
            {
                int len = br.ReadInt32();
                if (len == 0) continue;
                int bytes = len > 0 ? len : -len * 2;
                br.ReadBytes(bytes);
            }
            int mc = br.ReadInt32();
            br.ReadBytes(mc * 8); // FObject + int per entry
            br.ReadInt32();        // MaxTextureDependencyLength
            byte[] guid = br.ReadBytes(16);
            return new Guid(guid);
        }
        catch { return null; }
    }

    // FStaticParameterSet's first field is FGuid BaseMaterialId. We need to
    // walk past the FMaterialResource header to get there.
    private static Guid? TryReadFirstStaticParameterSetGuid(byte[] tail)
    {
        try
        {
            using var br = new BinaryReader(new MemoryStream(tail, writable: false));
            uint mask = br.ReadUInt32();
            if ((mask & 1) == 0) return null;
            if (!SkipOneFMaterialResource(br)) return null;
            // Now at the start of FStaticParameterSet → first 16 bytes = BaseMaterialId
            byte[] guid = br.ReadBytes(16);
            return new Guid(guid);
        }
        catch { return null; }
    }

    private static bool SkipOneFMaterialResource(BinaryReader br)
    {
        try
        {
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
            br.ReadInt32();
            br.ReadBytes(16);
            br.ReadInt32();
            int tc = br.ReadInt32();
            br.ReadBytes(tc * 4);
            br.ReadBytes(5 * 4 + 4);
            int lc = br.ReadInt32();
            if (lc > 0) br.ReadBytes(lc * 16);
            br.ReadUInt32();
            br.ReadBytes(12);
            return true;
        }
        catch { return false; }
    }

    private static bool LooksLikeNormalSlot(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string n = name.ToLowerInvariant();
        return n.Contains("normal") || n.Contains("_nrm") || n.Contains("_n_")
            || n.EndsWith("_n") || n.Contains("bump");
    }

    private static string TopLevelPackageName(UnrealImportTableEntry import, UnrealHeader header)
    {
        // Walk up the Outer chain until we hit the top-level package import
        // (the one whose ClassName is "Package").
        var cur = import;
        int guard = 0;
        while (cur is not null && guard++ < 10)
        {
            string cn = cur.ClassNameIndex?.Name ?? "";
            if (string.Equals(cn, "Package", StringComparison.OrdinalIgnoreCase))
                return cur.ObjectNameIndex?.Name ?? "";
            int outerRef = cur.OuterReference;
            if (outerRef >= 0) return cur.PackageNameIndex?.Name ?? "";
            int idx = -outerRef - 1;
            if (idx < 0 || idx >= header.ImportTable.Count) break;
            cur = header.ImportTable[idx];
        }
        return import.PackageNameIndex?.Name ?? "";
    }
}
