using OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.Cloning;
using UpkManager.Repository;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.Importing;

// Copies the compiled FMaterialResource[2] shader-cache tail from a donor
// UMaterial onto an existing UMaterial in the destination UPK, preserving
// the destination's tagged-property block (BlendMode, Expressions array,
// EmissiveColor, etc.). The tail's FObject + FName refs are translated to
// dest-UPK coordinates via the shared CrossUpkReferenceTranslator engine.
//
// Use this when:
//   - You ran Clone Material on an empty stub and need shaders back.
//   - You ran Import Material with a stripped shader cache and the result
//     renders pink because no compiled shader is bound.
//   - You want to swap a Material's compiled shader cache for a different
//     donor's without rewriting its property values.
public sealed class ImportShadersService
{
    private readonly UpkFileRepository _repo = new();

    public sealed record ImportResult(bool Ok, string Message);

    public async Task<ImportResult> ImportAsync(
        string donorUpkPath,
        string donorMaterialExportPath,
        string destUpkPath,
        string destMaterialExportPath,
        CancellationToken ct = default)
    {
        if (!File.Exists(donorUpkPath))
            return new(false, $"Donor UPK not found: {donorUpkPath}");
        if (!File.Exists(destUpkPath))
            return new(false, $"Destination UPK not found: {destUpkPath}");
        if (string.Equals(donorUpkPath, destUpkPath, StringComparison.OrdinalIgnoreCase))
            return new(false, "Donor and destination are the same UPK.");

        try
        {
            var donorHeader = await _repo.LoadUpkFile(donorUpkPath).ConfigureAwait(false);
            await donorHeader.ReadHeaderAsync(null).ConfigureAwait(false);
            byte[] destBytes = await File.ReadAllBytesAsync(destUpkPath, ct).ConfigureAwait(false);
            var destHeader = await _repo.LoadUpkFile(destUpkPath).ConfigureAwait(false);
            await destHeader.ReadHeaderAsync(null).ConfigureAwait(false);

            var donorExport = donorHeader.ExportTable.FirstOrDefault(e =>
                string.Equals(e.GetPathName(), donorMaterialExportPath,
                              StringComparison.OrdinalIgnoreCase));
            if (donorExport is null)
                return new(false, $"Donor Material not found: {donorMaterialExportPath}");
            if (!string.Equals(donorExport.ClassReferenceNameIndex?.Name, "Material",
                               StringComparison.OrdinalIgnoreCase))
                return new(false,
                    $"Donor export is a '{donorExport.ClassReferenceNameIndex?.Name}', " +
                    "not a Material. Import Shaders only operates on base Materials.");

            var destExport = destHeader.ExportTable.FirstOrDefault(e =>
                string.Equals(e.GetPathName(), destMaterialExportPath,
                              StringComparison.OrdinalIgnoreCase));
            if (destExport is null)
                return new(false, $"Destination Material not found: {destMaterialExportPath}");
            if (!string.Equals(destExport.ClassReferenceNameIndex?.Name, "Material",
                               StringComparison.OrdinalIgnoreCase))
                return new(false,
                    $"Destination export is a '{destExport.ClassReferenceNameIndex?.Name}', " +
                    "not a Material. Import Shaders only operates on base Materials.");

            byte[] donorBody = donorExport.UnrealObjectReader.GetBytes();
            byte[] destBody = destExport.UnrealObjectReader.GetBytes();

            // Split donor body: keep only the tail (FMaterialResource[2]).
            var donorSplit = MaterialBodySplitter.Split(donorBody, donorHeader);
            if (donorSplit.TailBytes.Length < 4)
                return new(false,
                    $"Donor Material '{donorMaterialExportPath}' has no compiled " +
                    "shader cache — nothing to import.");

            // Split dest body: keep the properties section as-is.
            var destSplit = MaterialBodySplitter.Split(destBody, destHeader);

            // Rewrite donor tail to dest-UPK ref coordinates.
            var translator = new CrossUpkReferenceTranslator(donorHeader, destHeader);
            var rewritten = MaterialResourceTailRewriter.RewriteUMaterialTail(
                donorSplit.TailBytes, translator);

            // Build replacement body = dest's properties + rewritten donor tail.
            byte[] newDestBody = new byte[destSplit.PropertiesBytes.Length + rewritten.RewrittenBytes.Length];
            Buffer.BlockCopy(destSplit.PropertiesBytes, 0, newDestBody, 0, destSplit.PropertiesBytes.Length);
            Buffer.BlockCopy(rewritten.RewrittenBytes, 0,
                newDestBody, destSplit.PropertiesBytes.Length, rewritten.RewrittenBytes.Length);

            // Repack dest: replace the dest export's body bytes; add any
            // names/imports the tail walker queued.
            var existing = destHeader.ExportTable.Select((e, i) =>
                new OmegaAssetStudio.UpkRepacker.ExportBuffer(
                    i == destHeader.ExportTable.IndexOf(destExport) ? newDestBody : e.UnrealObjectReader.GetBytes(),
                    Array.Empty<OmegaAssetStudio.UpkRepacker.BulkDataPatch>()))
                .ToList();

            byte[] withImports = OmegaAssetStudio.UpkRepacker.RepackWithAddedImports(
                destBytes, destHeader, existing,
                translator.AddedImports, translator.AddedNames,
                out _, out _);

            OmegaAssetStudio.BackupManager.BackupFileHelper.CreateBackup(destUpkPath);
            string tmp = destUpkPath + ".omtmp";
            await File.WriteAllBytesAsync(tmp, withImports, ct).ConfigureAwait(false);
            File.Move(tmp, destUpkPath, overwrite: true);

            return new(true,
                $"Imported shaders from '{donorMaterialExportPath}' onto " +
                $"'{destMaterialExportPath}' in {Path.GetFileName(destUpkPath)} " +
                $"({rewritten.ResourcesFound} resource(s); +{translator.AddedNames.Count} name(s), " +
                $"+{translator.AddedImports.Count} import(s)). Reload to see the change.");
        }
        catch (Exception ex)
        {
            return new(false, $"Import shaders failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
