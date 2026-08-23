using OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.Cloning;
using UpkManager.Models.UpkFile;
using UpkManager.Repository;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.Importing;

// "Full" Material import: copies the Material plus any Texture2D source-UPK
// exports its body references, so the destination UPK is self-contained
// rather than holding Imports back to the donor.
//
// Texture2D refs that are already IMPORTS in the donor (typical: textures
// living in shared TFC-backed texture packages) are translated to matching
// dest imports — the actual texture data isn't duplicated, only the import
// bookkeeping. Only Texture2D refs that are LOCAL EXPORTS in the donor UPK
// get copied across as new local dest exports.
//
// Process:
//   1. Scan source Material body for positive (source-EXPORT) FObject refs.
//   2. Filter to Texture2D class.
//   3. Sequentially copy each Texture2D to dest as a local export
//      (each copy is its own backup + atomic write).
//   4. Reload dest, look up the new dest-export ref for each copied texture.
//   5. Import the Material with the texture ref overrides registered so the
//      Material's body points at the new local copies, not at imports back
//      to the donor.
//
// MaterialExpression* exports are NOT auto-copied in this version — they
// stay as Imports back to the donor. That keeps this scoped to the
// shipping-textures-only case which is the common ask. (MaterialExpression
// chain copy ships next.)
public sealed class ImportFullMaterialService
{
    private readonly UpkFileRepository _repo = new();

    public sealed record ImportResult(
        bool Ok, string Message, string? NewMaterialPath = null,
        int CopiedTextures = 0);

    public async Task<ImportResult> ImportAsync(
        string sourceUpkPath,
        string sourceMaterialExportPath,
        string destUpkPath,
        string? newName = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(sourceUpkPath))
            return new(false, $"Source UPK not found: {sourceUpkPath}");
        if (!File.Exists(destUpkPath))
            return new(false, $"Destination UPK not found: {destUpkPath}");
        if (string.Equals(sourceUpkPath, destUpkPath, StringComparison.OrdinalIgnoreCase))
            return new(false,
                "Source and destination are the same UPK. Use Clone Material for intra-UPK duplicates.");

        try
        {
            var sourceHeader = await _repo.LoadUpkFile(sourceUpkPath).ConfigureAwait(false);
            await sourceHeader.ReadHeaderAsync(null).ConfigureAwait(false);

            var sourceMaterial = sourceHeader.ExportTable.FirstOrDefault(e =>
                string.Equals(e.GetPathName(), sourceMaterialExportPath,
                              StringComparison.OrdinalIgnoreCase));
            if (sourceMaterial is null)
                return new(false, $"Source Material not found: {sourceMaterialExportPath}");
            if (!string.Equals(sourceMaterial.ClassReferenceNameIndex?.Name, "Material",
                               StringComparison.OrdinalIgnoreCase))
                return new(false,
                    $"Source export is a '{sourceMaterial.ClassReferenceNameIndex?.Name}', " +
                    "not a Material. Use Import Material Instance for MIC/MITV.");

            // Step 1+2: scan and filter to Texture2D source-exports.
            byte[] sourceBody = sourceMaterial.UnrealObjectReader.GetBytes();
            var scan = MaterialBodyRefScanner.Scan(sourceBody, sourceHeader, "Material");

            var textureRefsToCopy = new List<int>();
            var textureExports = new List<UpkManager.Models.UpkFile.Tables.UnrealExportTableEntry>();
            foreach (int srcRef in scan.PositiveExportRefs)
            {
                int idx = srcRef - 1;
                if (idx < 0 || idx >= sourceHeader.ExportTable.Count) continue;
                var ex = sourceHeader.ExportTable[idx];
                string cls = ex.ClassReferenceNameIndex?.Name ?? "";
                if (string.Equals(cls, "Texture2D", StringComparison.OrdinalIgnoreCase))
                {
                    textureRefsToCopy.Add(srcRef);
                    textureExports.Add(ex);
                }
            }

            // Step 3: copy each Texture2D to dest as local export.
            int copiedCount = 0;
            var copiedTexturePaths = new List<string>();
            foreach (var texExport in textureExports)
            {
                string texPath = texExport.GetPathName();
                // Skip if dest already has it under the same path.
                var destHeader = await _repo.LoadUpkFile(destUpkPath).ConfigureAwait(false);
                await destHeader.ReadHeaderAsync(null).ConfigureAwait(false);
                if (destHeader.ExportTable.Any(e =>
                    string.Equals(e.GetPathName(), texPath, StringComparison.OrdinalIgnoreCase)))
                {
                    copiedTexturePaths.Add(texPath);   // already there — count toward override
                    continue;
                }
                var copyResult = await CopySingleExportAsync(
                    sourceUpkPath, texPath, destUpkPath, ct).ConfigureAwait(false);
                if (!copyResult.Ok)
                    return new(false,
                        $"Texture copy failed for '{texPath}': {copyResult.Message}",
                        CopiedTextures: copiedCount);
                copiedCount++;
                copiedTexturePaths.Add(texPath);
            }

            // Step 4: reload dest and locate each copied texture's new dest ref.
            var destHeader2 = await _repo.LoadUpkFile(destUpkPath).ConfigureAwait(false);
            await destHeader2.ReadHeaderAsync(null).ConfigureAwait(false);
            var texOverrides = new Dictionary<int, int>(); // sourceRef → destRef
            for (int i = 0; i < textureRefsToCopy.Count; i++)
            {
                int srcRef = textureRefsToCopy[i];
                string texPath = copiedTexturePaths[i];
                int destIdx = destHeader2.ExportTable.FindIndex(e =>
                    string.Equals(e.GetPathName(), texPath, StringComparison.OrdinalIgnoreCase));
                if (destIdx >= 0)
                    texOverrides[srcRef] = destIdx + 1;    // positive ref = idx+1
            }

            // Step 5: import the Material with overrides registered.
            var materialResult = await ImportMaterialWithOverridesAsync(
                sourceUpkPath, sourceMaterialExportPath, destUpkPath,
                newName, texOverrides, ct).ConfigureAwait(false);

            if (!materialResult.Ok)
                return new(false,
                    $"Material import failed: {materialResult.Message}",
                    CopiedTextures: copiedCount);

            return new(true,
                $"Imported Material '{sourceMaterialExportPath}' " +
                $"({copiedCount} new texture export(s) copied; " +
                $"{texOverrides.Count} ref override(s) applied). " +
                $"Reload to see changes.",
                materialResult.NewExportPath, copiedCount);
        }
        catch (Exception ex)
        {
            return new(false, $"Import Full Material failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Cross-UPK copy of a single export (Texture2D, MaterialExpression, etc.)
    // as a local dest export. Body refs translated; tail copied verbatim
    // (textures have no FObject refs in their binary mip tail).
    private async Task<(bool Ok, string Message)> CopySingleExportAsync(
        string sourceUpkPath, string sourceExportPath, string destUpkPath,
        CancellationToken ct)
    {
        try
        {
            var sourceHeader = await _repo.LoadUpkFile(sourceUpkPath).ConfigureAwait(false);
            await sourceHeader.ReadHeaderAsync(null).ConfigureAwait(false);
            var sourceExport = sourceHeader.ExportTable.FirstOrDefault(e =>
                string.Equals(e.GetPathName(), sourceExportPath, StringComparison.OrdinalIgnoreCase));
            if (sourceExport is null) return (false, "source export not found");

            byte[] destBytes = await File.ReadAllBytesAsync(destUpkPath, ct).ConfigureAwait(false);
            var destHeader = await _repo.LoadUpkFile(destUpkPath).ConfigureAwait(false);
            await destHeader.ReadHeaderAsync(null).ConfigureAwait(false);

            var translator = new CrossUpkReferenceTranslator(sourceHeader, destHeader);
            int classRef = translator.TranslateObjectRef(sourceExport.ClassReference);
            int superRef = translator.TranslateObjectRef(sourceExport.SuperReference);
            int outerRef = translator.TranslateObjectRef(sourceExport.OuterReference);
            int archetypeRef = translator.TranslateObjectRef(sourceExport.ArchetypeReference);

            byte[] sourceBody = sourceExport.UnrealObjectReader.GetBytes();
            var rewrite = MicBodyRewriter.Rewrite(sourceBody, sourceHeader, destHeader, translator);

            var addedNames = new List<string>(translator.AddedNames);
            string targetName = sourceExport.ObjectNameIndex?.Name ?? "Imported";
            int existingNameIndex = -1;
            for (int i = 0; i < destHeader.NameTable.Count; i++)
                if (string.Equals(destHeader.NameTable[i].Name?.String, targetName,
                                  StringComparison.OrdinalIgnoreCase))
                { existingNameIndex = i; break; }
            int newNameIndex;
            if (existingNameIndex >= 0) newNameIndex = existingNameIndex;
            else { newNameIndex = destHeader.NameTable.Count + addedNames.Count; addedNames.Add(targetName); }
            var addedImports = new List<OmegaAssetStudio.UpkRepacker.NewImportSpec>(translator.AddedImports);

            var existing = destHeader.ExportTable
                .Select(e => new OmegaAssetStudio.UpkRepacker.ExportBuffer(
                    e.UnrealObjectReader.GetBytes(),
                    Array.Empty<OmegaAssetStudio.UpkRepacker.BulkDataPatch>()))
                .ToList();

            byte[] withImports = OmegaAssetStudio.UpkRepacker.RepackWithAddedImports(
                destBytes, destHeader, existing, addedImports, addedNames, out _, out _);

            string tmpImports = destUpkPath + ".omtmp_imports";
            await File.WriteAllBytesAsync(tmpImports, withImports, ct).ConfigureAwait(false);
            var newDestHeader = await _repo.LoadUpkFile(tmpImports).ConfigureAwait(false);
            await newDestHeader.ReadHeaderAsync(null).ConfigureAwait(false);

            var existingAfterImports = newDestHeader.ExportTable
                .Select(e => new OmegaAssetStudio.UpkRepacker.ExportBuffer(
                    e.UnrealObjectReader.GetBytes(),
                    Array.Empty<OmegaAssetStudio.UpkRepacker.BulkDataPatch>()))
                .ToList();

            var spec = new OmegaAssetStudio.UpkRepacker.NewExportSpec(
                Data: rewrite.RewrittenBytes,
                Patches: Array.Empty<OmegaAssetStudio.UpkRepacker.BulkDataPatch>(),
                ClassRef: classRef, SuperRef: superRef, OuterRef: outerRef, ArchetypeRef: archetypeRef,
                ObjectNameTableIndex: newNameIndex, ObjectNameNumeric: 0,
                ObjectFlags: sourceExport.ObjectFlags, ExportFlags: sourceExport.ExportFlags,
                NetObjects: sourceExport.NetObjects.ToArray(),
                PackageGuid: sourceExport.PackageGuid ?? new byte[16],
                PackageFlags: sourceExport.PackageFlags);

            byte[] finalBytes = newDestHeader.CompressedChunks.Count > 0
                ? OmegaAssetStudio.UpkRepacker.RepackCompressedWithAddedExports(
                    withImports, newDestHeader, existingAfterImports, new[] { spec }, Array.Empty<string>(),
                    out _, out _)
                : OmegaAssetStudio.UpkRepacker.RepackWithAddedExports(
                    withImports, newDestHeader, existingAfterImports, new[] { spec }, Array.Empty<string>(),
                    out _, out _);

            OmegaAssetStudio.BackupManager.BackupFileHelper.CreateBackup(destUpkPath);
            string tmp = destUpkPath + ".omtmp";
            await File.WriteAllBytesAsync(tmp, finalBytes, ct).ConfigureAwait(false);
            File.Move(tmp, destUpkPath, overwrite: true);
            try { File.Delete(tmpImports); } catch { }
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // Import the Material with caller-supplied refs overrides (sourceRef →
    // destRef). Lets the Material's translated body point at local dest
    // exports we already copied rather than at fresh Imports.
    private async Task<(bool Ok, string Message, string? NewExportPath)> ImportMaterialWithOverridesAsync(
        string sourceUpkPath, string sourceExportPath, string destUpkPath,
        string? newName, Dictionary<int, int> overrides, CancellationToken ct)
    {
        try
        {
            var sourceHeader = await _repo.LoadUpkFile(sourceUpkPath).ConfigureAwait(false);
            await sourceHeader.ReadHeaderAsync(null).ConfigureAwait(false);
            var sourceMaterial = sourceHeader.ExportTable.FirstOrDefault(e =>
                string.Equals(e.GetPathName(), sourceExportPath, StringComparison.OrdinalIgnoreCase));
            if (sourceMaterial is null) return (false, "source not found", null);

            byte[] destBytes = await File.ReadAllBytesAsync(destUpkPath, ct).ConfigureAwait(false);
            var destHeader = await _repo.LoadUpkFile(destUpkPath).ConfigureAwait(false);
            await destHeader.ReadHeaderAsync(null).ConfigureAwait(false);

            string targetName = string.IsNullOrWhiteSpace(newName)
                ? (sourceMaterial.ObjectNameIndex?.Name ?? "ImportedMaterial")
                : newName.Trim();
            string? targetGroup = sourceExportPath.LastIndexOf('.') > 0
                ? sourceExportPath[..sourceExportPath.LastIndexOf('.')] : null;
            string targetFullPath = string.IsNullOrWhiteSpace(targetGroup)
                ? targetName : $"{targetGroup}.{targetName}";
            if (destHeader.ExportTable.Any(e =>
                string.Equals(e.GetPathName(), targetFullPath, StringComparison.OrdinalIgnoreCase)))
                return (false, $"dest already has '{targetFullPath}'", null);

            var translator = new CrossUpkReferenceTranslator(sourceHeader, destHeader);
            foreach (var (sourceRef, destRef) in overrides)
                translator.RegisterRefOverride(sourceRef, destRef);

            int classRef = translator.TranslateObjectRef(sourceMaterial.ClassReference);
            int superRef = translator.TranslateObjectRef(sourceMaterial.SuperReference);
            int outerRef = translator.TranslateObjectRef(sourceMaterial.OuterReference);
            int archetypeRef = translator.TranslateObjectRef(sourceMaterial.ArchetypeReference);

            byte[] sourceBody = sourceMaterial.UnrealObjectReader.GetBytes();
            var rewrite = MicBodyRewriter.Rewrite(sourceBody, sourceHeader, destHeader, translator, "Material");

            var addedNames = new List<string>(translator.AddedNames);
            int existingNameIndex = -1;
            for (int i = 0; i < destHeader.NameTable.Count; i++)
                if (string.Equals(destHeader.NameTable[i].Name?.String, targetName,
                                  StringComparison.OrdinalIgnoreCase))
                { existingNameIndex = i; break; }
            int newNameIndex;
            if (existingNameIndex >= 0) newNameIndex = existingNameIndex;
            else { newNameIndex = destHeader.NameTable.Count + addedNames.Count; addedNames.Add(targetName); }
            var addedImports = new List<OmegaAssetStudio.UpkRepacker.NewImportSpec>(translator.AddedImports);

            var existing = destHeader.ExportTable
                .Select(e => new OmegaAssetStudio.UpkRepacker.ExportBuffer(
                    e.UnrealObjectReader.GetBytes(),
                    Array.Empty<OmegaAssetStudio.UpkRepacker.BulkDataPatch>()))
                .ToList();

            byte[] withImports = OmegaAssetStudio.UpkRepacker.RepackWithAddedImports(
                destBytes, destHeader, existing, addedImports, addedNames, out _, out _);

            string tmpImports = destUpkPath + ".omtmp_imports";
            await File.WriteAllBytesAsync(tmpImports, withImports, ct).ConfigureAwait(false);
            var newDestHeader = await _repo.LoadUpkFile(tmpImports).ConfigureAwait(false);
            await newDestHeader.ReadHeaderAsync(null).ConfigureAwait(false);

            var existingAfterImports = newDestHeader.ExportTable
                .Select(e => new OmegaAssetStudio.UpkRepacker.ExportBuffer(
                    e.UnrealObjectReader.GetBytes(),
                    Array.Empty<OmegaAssetStudio.UpkRepacker.BulkDataPatch>()))
                .ToList();

            var spec = new OmegaAssetStudio.UpkRepacker.NewExportSpec(
                Data: rewrite.RewrittenBytes,
                Patches: Array.Empty<OmegaAssetStudio.UpkRepacker.BulkDataPatch>(),
                ClassRef: classRef, SuperRef: superRef, OuterRef: outerRef, ArchetypeRef: archetypeRef,
                ObjectNameTableIndex: newNameIndex, ObjectNameNumeric: 0,
                ObjectFlags: sourceMaterial.ObjectFlags, ExportFlags: sourceMaterial.ExportFlags,
                NetObjects: sourceMaterial.NetObjects.ToArray(),
                PackageGuid: sourceMaterial.PackageGuid ?? new byte[16],
                PackageFlags: sourceMaterial.PackageFlags);

            byte[] finalBytes = newDestHeader.CompressedChunks.Count > 0
                ? OmegaAssetStudio.UpkRepacker.RepackCompressedWithAddedExports(
                    withImports, newDestHeader, existingAfterImports, new[] { spec }, Array.Empty<string>(),
                    out _, out _)
                : OmegaAssetStudio.UpkRepacker.RepackWithAddedExports(
                    withImports, newDestHeader, existingAfterImports, new[] { spec }, Array.Empty<string>(),
                    out _, out _);

            OmegaAssetStudio.BackupManager.BackupFileHelper.CreateBackup(destUpkPath);
            string tmp = destUpkPath + ".omtmp";
            await File.WriteAllBytesAsync(tmp, finalBytes, ct).ConfigureAwait(false);
            File.Move(tmp, destUpkPath, overwrite: true);
            try { File.Delete(tmpImports); } catch { }
            return (true, "", targetFullPath);
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}", null);
        }
    }
}
