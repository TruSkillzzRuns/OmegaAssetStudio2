using OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.Cloning;
using UpkManager.Repository;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.Importing;

// Cross-UPK import of a base UMaterial (NOT a MIC/MITV). Walks the body's
// tagged-property block via the same engine MicBodyRewriter uses, then walks
// the binary FMaterialResource[2] tail via MaterialResourceTailRewriter so
// embedded FObject/FName refs (UniformExpressionTextures, etc.) get
// translated rather than left pointing at the donor UPK's tables.
//
// Refs to source-UPK exports become Imports in the destination pointing back
// at the donor UPK. That preserves rendering as long as the donor UPK stays
// installed alongside the destination. For a self-contained copy that pulls
// the referenced Texture2D exports across too, use Import Full Material.
public sealed class ImportMaterialService
{
    private readonly UpkFileRepository _repo = new();

    public sealed record ImportResult(bool Ok, string Message, string? NewExportPath = null);

    public async Task<ImportResult> ImportAsync(
        string sourceUpkPath,
        string sourceExportPath,
        string destUpkPath,
        string? newName = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(sourceUpkPath))
            return new(false, $"Source UPK not found: {sourceUpkPath}");
        if (!File.Exists(destUpkPath))
            return new(false, $"Destination UPK not found: {destUpkPath}");
        if (string.Equals(sourceUpkPath, destUpkPath, StringComparison.OrdinalIgnoreCase))
            return new(false, "Source and destination are the same UPK. Use Clone Material instead.");

        try
        {
            byte[] sourceBytes = await File.ReadAllBytesAsync(sourceUpkPath, ct).ConfigureAwait(false);
            var sourceHeader = await _repo.LoadUpkFile(sourceUpkPath).ConfigureAwait(false);
            await sourceHeader.ReadHeaderAsync(null).ConfigureAwait(false);

            byte[] destBytes = await File.ReadAllBytesAsync(destUpkPath, ct).ConfigureAwait(false);
            var destHeader = await _repo.LoadUpkFile(destUpkPath).ConfigureAwait(false);
            await destHeader.ReadHeaderAsync(null).ConfigureAwait(false);

            var sourceExport = sourceHeader.ExportTable.FirstOrDefault(e =>
                string.Equals(e.GetPathName(), sourceExportPath, StringComparison.OrdinalIgnoreCase));
            if (sourceExport is null)
                return new(false, $"Source export not found: {sourceExportPath}");

            string sourceClass = sourceExport.ClassReferenceNameIndex?.Name ?? "";
            if (!string.Equals(sourceClass, "Material", StringComparison.OrdinalIgnoreCase))
                return new(false,
                    $"Source export is a '{sourceClass}', not a Material. " +
                    "Use Import Material Instance for MIC/MITV exports.");

            string targetName = string.IsNullOrWhiteSpace(newName)
                ? (sourceExport.ObjectNameIndex?.Name ?? "ImportedMaterial")
                : newName.Trim();

            string? targetGroup = ExtractGroup(sourceExportPath);
            string targetFullPath = string.IsNullOrWhiteSpace(targetGroup)
                ? targetName : $"{targetGroup}.{targetName}";
            if (destHeader.ExportTable.Any(e => string.Equals(e.GetPathName(),
                targetFullPath, StringComparison.OrdinalIgnoreCase)))
            {
                return new(false,
                    $"Destination UPK already has an export at '{targetFullPath}'. " +
                    "Choose a different name to avoid overwriting.");
            }

            var translator = new CrossUpkReferenceTranslator(sourceHeader, destHeader);
            int classRef = translator.TranslateObjectRef(sourceExport.ClassReference);
            int superRef = translator.TranslateObjectRef(sourceExport.SuperReference);
            int outerRef = translator.TranslateObjectRef(sourceExport.OuterReference);
            int archetypeRef = translator.TranslateObjectRef(sourceExport.ArchetypeReference);

            byte[] sourceBody = sourceExport.UnrealObjectReader.GetBytes();
            // Class-aware overload walks the FMaterialResource[2] tail too.
            var rewrite = MicBodyRewriter.Rewrite(sourceBody, sourceHeader, destHeader, translator, "Material");

            var addedNames = new List<string>(translator.AddedNames);
            int existingNameIndex = -1;
            for (int i = 0; i < destHeader.NameTable.Count; i++)
                if (string.Equals(destHeader.NameTable[i].Name?.String, targetName,
                                  StringComparison.OrdinalIgnoreCase))
                { existingNameIndex = i; break; }
            int newNameIndex;
            if (existingNameIndex >= 0) newNameIndex = existingNameIndex;
            else
            {
                newNameIndex = destHeader.NameTable.Count + addedNames.Count;
                addedNames.Add(targetName);
            }
            var addedImports = new List<OmegaAssetStudio.UpkRepacker.NewImportSpec>(translator.AddedImports);

            var existing = destHeader.ExportTable
                .Select(e => new OmegaAssetStudio.UpkRepacker.ExportBuffer(
                    e.UnrealObjectReader.GetBytes(),
                    Array.Empty<OmegaAssetStudio.UpkRepacker.BulkDataPatch>()))
                .ToList();

            byte[] withImports = OmegaAssetStudio.UpkRepacker.RepackWithAddedImports(
                destBytes, destHeader, existing, addedImports, addedNames,
                out _, out _);

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
                ClassRef: classRef,
                SuperRef: superRef,
                OuterRef: outerRef,
                ArchetypeRef: archetypeRef,
                ObjectNameTableIndex: newNameIndex,
                ObjectNameNumeric: 0,
                ObjectFlags: sourceExport.ObjectFlags,
                ExportFlags: sourceExport.ExportFlags,
                NetObjects: sourceExport.NetObjects.ToArray(),
                PackageGuid: sourceExport.PackageGuid ?? new byte[16],
                PackageFlags: sourceExport.PackageFlags);

            byte[] finalBytes = newDestHeader.CompressedChunks.Count > 0
                ? OmegaAssetStudio.UpkRepacker.RepackCompressedWithAddedExports(
                    withImports, newDestHeader, existingAfterImports, new[] { spec }, Array.Empty<string>(),
                    out _, out var newExportIndices)
                : OmegaAssetStudio.UpkRepacker.RepackWithAddedExports(
                    withImports, newDestHeader, existingAfterImports, new[] { spec }, Array.Empty<string>(),
                    out _, out newExportIndices);

            OmegaAssetStudio.BackupManager.BackupFileHelper.CreateBackup(destUpkPath);
            string tmp = destUpkPath + ".omtmp";
            await File.WriteAllBytesAsync(tmp, finalBytes, ct).ConfigureAwait(false);
            File.Move(tmp, destUpkPath, overwrite: true);
            try { File.Delete(tmpImports); } catch { }

            return new(true,
                $"Imported Material '{sourceExportPath}' as '{targetFullPath}' " +
                $"into {Path.GetFileName(destUpkPath)} " +
                $"(+{addedNames.Count} name(s), +{addedImports.Count} import(s)). " +
                "Compiled shaders copied with refs translated — may still need 'Import Shaders' " +
                "if rendering shows pink in destination.",
                targetFullPath);
        }
        catch (Exception ex)
        {
            return new(false, $"Import failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string? ExtractGroup(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return null;
        int dot = fullPath.LastIndexOf('.');
        return dot > 0 ? fullPath[..dot] : null;
    }
}
