using UpkManager.Repository;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.Importing;

// Intra-UPK duplicate of a UMaterial export. Mirrors the same-UPK path in
// MicCloneService — copy the body verbatim, add NameTable entry, add a
// new export entry that reuses the source's class/super/outer/archetype
// refs. Refs inside the body all resolve in the same package so no
// rewriting is required (this is the simple case).
//
// For cross-UPK Material import, see ImportMaterialService (Phase B —
// needs the body walker extended to know about UMaterial's Expressions /
// ReferencedTextures arrays).
public sealed class CloneMaterialService
{
    private readonly UpkFileRepository _repo = new();

    public sealed record CloneResult(bool Ok, string Message, string? NewExportPath = null);

    public async Task<CloneResult> CloneAsync(
        string upkPath,
        string sourceExportPath,
        string newName,
        CancellationToken ct = default)
    {
        if (!File.Exists(upkPath))
            return new(false, $"UPK not found: {upkPath}");
        if (string.IsNullOrWhiteSpace(newName))
            return new(false, "New material name is required.");

        try
        {
            byte[] originalBytes = await File.ReadAllBytesAsync(upkPath, ct).ConfigureAwait(false);
            var header = await _repo.LoadUpkFile(upkPath).ConfigureAwait(false);
            await header.ReadHeaderAsync(null).ConfigureAwait(false);

            var source = header.ExportTable.FirstOrDefault(e =>
                string.Equals(e.GetPathName(), sourceExportPath, StringComparison.OrdinalIgnoreCase));
            if (source is null)
                return new(false, $"Source export not found: {sourceExportPath}");

            string sourceClass = source.ClassReferenceNameIndex?.Name ?? "";
            if (!string.Equals(sourceClass, "Material", StringComparison.OrdinalIgnoreCase))
                return new(false,
                    $"Source export is a '{sourceClass}', not a Material. " +
                    "Use Clone MIC for Material Instances.");

            string targetName = newName.Trim();
            string? targetGroup = ExtractGroup(sourceExportPath);
            string targetFullPath = string.IsNullOrWhiteSpace(targetGroup)
                ? targetName : $"{targetGroup}.{targetName}";
            if (header.ExportTable.Any(e => string.Equals(e.GetPathName(),
                targetFullPath, StringComparison.OrdinalIgnoreCase)))
            {
                return new(false,
                    $"UPK already has an export at '{targetFullPath}'. " +
                    "Choose a different name.");
            }

            byte[] materialBody = source.UnrealObjectReader.GetBytes();

            // NameTable: add new name if absent.
            int existingNameIndex = -1;
            for (int i = 0; i < header.NameTable.Count; i++)
                if (string.Equals(header.NameTable[i].Name?.String, targetName,
                                  StringComparison.OrdinalIgnoreCase))
                { existingNameIndex = i; break; }
            var addedNames = new List<string>();
            int newNameTableIndex = existingNameIndex;
            if (existingNameIndex < 0)
            {
                addedNames.Add(targetName);
                newNameTableIndex = header.NameTable.Count;
            }

            var existing = header.ExportTable
                .Select(e => new OmegaAssetStudio.UpkRepacker.ExportBuffer(
                    e.UnrealObjectReader.GetBytes(),
                    Array.Empty<OmegaAssetStudio.UpkRepacker.BulkDataPatch>()))
                .ToList();

            var spec = new OmegaAssetStudio.UpkRepacker.NewExportSpec(
                Data: materialBody,
                Patches: Array.Empty<OmegaAssetStudio.UpkRepacker.BulkDataPatch>(),
                ClassRef: source.ClassReference,
                SuperRef: source.SuperReference,
                OuterRef: source.OuterReference,
                ArchetypeRef: source.ArchetypeReference,
                ObjectNameTableIndex: newNameTableIndex,
                ObjectNameNumeric: 0,
                ObjectFlags: source.ObjectFlags,
                ExportFlags: source.ExportFlags,
                NetObjects: source.NetObjects.ToArray(),
                PackageGuid: source.PackageGuid ?? new byte[16],
                PackageFlags: source.PackageFlags);

            byte[] repacked = header.CompressedChunks.Count > 0
                ? OmegaAssetStudio.UpkRepacker.RepackCompressedWithAddedExports(
                    originalBytes, header, existing, new[] { spec }, addedNames,
                    out _, out _)
                : OmegaAssetStudio.UpkRepacker.RepackWithAddedExports(
                    originalBytes, header, existing, new[] { spec }, addedNames,
                    out _, out _);

            OmegaAssetStudio.BackupManager.BackupFileHelper.CreateBackup(upkPath);
            string tmp = upkPath + ".omtmp";
            await File.WriteAllBytesAsync(tmp, repacked, ct).ConfigureAwait(false);
            File.Move(tmp, upkPath, overwrite: true);

            return new(true,
                $"Cloned Material '{sourceExportPath}' as '{targetFullPath}'. Reload to see it.",
                targetFullPath);
        }
        catch (Exception ex)
        {
            return new(false, $"Clone failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string? ExtractGroup(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return null;
        int dot = fullPath.LastIndexOf('.');
        return dot > 0 ? fullPath[..dot] : null;
    }
}
