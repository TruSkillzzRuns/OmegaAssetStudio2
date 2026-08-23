using OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.Cloning;
using UpkManager.Repository;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.Importing;

// Cross-UPK import of a UMaterialInstanceConstant (MIC) or
// UMaterialInstanceTimeVarying (MITV) from a donor UPK into the currently
// open one. The export is copied (body + class/outer refs translated,
// missing names/imports queued) and lands in the dest UPK under its
// original name unless the caller passes a new one.
//
// Engine reuse: this delegates to the same CrossUpkReferenceTranslator
// + MicBodyRewriter + UpkRepacker.RepackWith… pipeline that MicCloneService
// uses for its cross-UPK path. The compiled FMaterialResource tail (when
// present) is rewritten through MaterialResourceTailRewriter so embedded
// FObject + FName refs translate to dest UPK coordinates — better than a
// verbatim copy of the shader cache which would leave stale source-UPK
// indices in the texture-dependency arrays.
public sealed class ImportMaterialInstanceService
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
            return new(false, "Source and destination are the same UPK. Use Clone instead of Import.");

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
            bool isMic = string.Equals(sourceClass, "MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase);
            bool isMitv = string.Equals(sourceClass, "MaterialInstanceTimeVarying", StringComparison.OrdinalIgnoreCase);
            if (!isMic && !isMitv)
                return new(false,
                    $"Source export is a '{sourceClass}', not a MaterialInstanceConstant or " +
                    "MaterialInstanceTimeVarying. Use Import Material for base materials.");

            // Decide the final export name. Default: source's leaf name.
            string targetName = string.IsNullOrWhiteSpace(newName)
                ? (sourceExport.ObjectNameIndex?.Name ?? "ImportedMIC")
                : newName.Trim();

            // Refuse if dest already has the same name at the same outer.
            string? targetGroup = ExtractGroup(sourceExportPath);
            string targetFullPath = string.IsNullOrWhiteSpace(targetGroup)
                ? targetName : $"{targetGroup}.{targetName}";
            if (destHeader.ExportTable.Any(e => string.Equals(e.GetPathName(),
                targetFullPath, StringComparison.OrdinalIgnoreCase)))
            {
                return new(false,
                    $"Destination UPK already has an export at '{targetFullPath}'. " +
                    $"Choose a different name to avoid overwriting.");
            }

            // Translate the source export's own class/super/outer/archetype refs
            // FIRST so the queued imports lead the name/import lists. Same
            // pattern as MicCloneService.CloneCrossUpkAsync.
            var translator = new CrossUpkReferenceTranslator(sourceHeader, destHeader);
            int classRef = translator.TranslateObjectRef(sourceExport.ClassReference);
            int superRef = translator.TranslateObjectRef(sourceExport.SuperReference);
            int outerRef = translator.TranslateObjectRef(sourceExport.OuterReference);
            int archetypeRef = translator.TranslateObjectRef(sourceExport.ArchetypeReference);

            // Walk + rewrite the export's serial bytes.
            byte[] sourceBody = sourceExport.UnrealObjectReader.GetBytes();
            var rewrite = MicBodyRewriter.Rewrite(sourceBody, sourceHeader, destHeader, translator);

            // Compute the new export's own name index — appended after all of
            // the body's queued names.
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

            // Existing-export buffers passed through unchanged.
            var existing = destHeader.ExportTable
                .Select(e => new OmegaAssetStudio.UpkRepacker.ExportBuffer(
                    e.UnrealObjectReader.GetBytes(),
                    Array.Empty<OmegaAssetStudio.UpkRepacker.BulkDataPatch>()))
                .ToList();

            // Step 1: add the imports (and any extra names they need).
            byte[] withImports = OmegaAssetStudio.UpkRepacker.RepackWithAddedImports(
                destBytes, destHeader, existing, addedImports, addedNames,
                out _, out _);

            // Re-parse so we have an up-to-date header for the next step.
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

            string kind = isMic ? "MIC" : "MITV";
            return new(true,
                $"Imported {kind} '{sourceExportPath}' as '{targetFullPath}' " +
                $"into {Path.GetFileName(destUpkPath)} " +
                $"(+{addedNames.Count} name(s), +{addedImports.Count} import(s)). " +
                $"Reload to see it.",
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
