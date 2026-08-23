namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.Packaging;

// Move or rename a material across UPKs, with every referencing package's
// import-table updated to point at the new location.
//
// Why it's scaffolded, not implemented: this requires (a) UpkRepacker's
// add-export path (same prerequisite as MicCloneService), AND (b) batch
// rewrite of every UPK in the corpus whose import table mentions the
// source path. The corpus rewrite is high-risk — one stale ref leaves
// a broken package — so it deserves a dedicated session with extensive
// dry-run + verification.
public interface IPackageMoveService
{
    Task<MoveResult> MoveMaterialAsync(
        string sourceUpkPath,
        string materialExportPath,
        string destUpkPath,
        string? newName = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default);

    Task<MoveResult> RenameMaterialAsync(
        string upkPath,
        string materialExportPath,
        string newName,
        IProgress<string>? progress = null,
        CancellationToken ct = default);

    public sealed record MoveResult(bool Ok, string Message, IReadOnlyList<string> ReferencingUpksUpdated);
}

public sealed class PackageMoveService : IPackageMoveService
{
    private readonly UpkManager.Repository.UpkFileRepository _repo = new();

    public async Task<IPackageMoveService.MoveResult> MoveMaterialAsync(
        string sourceUpkPath, string materialExportPath, string destUpkPath,
        string? newName, IProgress<string>? progress, CancellationToken ct)
    {
        // Cross-UPK move = clone into dest + delete from source + rewrite
        // every referencing UPK's import table. Blocks on the tagged-
        // property reference rewriter (same prerequisite as cross-UPK
        // clone) since the cloned bytes need their references translated.
        return await Task.FromResult(new IPackageMoveService.MoveResult(false,
            "Cross-UPK material move = MIC Clone (cross-UPK, supported today) into the destination + corpus-wide rewrite of every UPK whose " +
            "import table references the old export path. The clone half works; the corpus rewriter is high-risk (one stale ref bricks a package) " +
            "and deserves its own dry-run + verification pass before shipping. Same-UPK rename is supported today.",
            Array.Empty<string>())).ConfigureAwait(false);
    }

    // In-UPK rename: change the export's ObjectNameIndex to point at a new
    // NameTable entry. No body changes; no referencer updates needed unless
    // a cross-package import references this name (caller can use
    // CrossPackageMaterialSearch to find those manually).
    public async Task<IPackageMoveService.MoveResult> RenameMaterialAsync(
        string upkPath, string materialExportPath, string newName,
        IProgress<string>? progress, CancellationToken ct)
    {
        if (!File.Exists(upkPath))
            return new(false, $"UPK not found: {upkPath}", Array.Empty<string>());
        if (string.IsNullOrWhiteSpace(newName))
            return new(false, "new name is required", Array.Empty<string>());

        try
        {
            byte[] originalBytes = await File.ReadAllBytesAsync(upkPath, ct).ConfigureAwait(false);
            var header = await _repo.LoadUpkFile(upkPath).ConfigureAwait(false);
            await header.ReadHeaderAsync(null).ConfigureAwait(false);

            var export = header.ExportTable.FirstOrDefault(e =>
                string.Equals(e.GetPathName(), materialExportPath, StringComparison.OrdinalIgnoreCase));
            if (export is null)
                return new(false, $"export not found: {materialExportPath}", Array.Empty<string>());

            // Find or add the new name.
            int newNameIndex = -1;
            for (int i = 0; i < header.NameTable.Count; i++)
                if (string.Equals(header.NameTable[i].Name?.String, newName, StringComparison.OrdinalIgnoreCase))
                { newNameIndex = i; break; }
            var addedNames = new List<string>();
            if (newNameIndex < 0)
            {
                addedNames.Add(newName);
                newNameIndex = header.NameTable.Count; // first added
            }

            // Build the existing export buffers + override the renamed
            // export's entry by writing a new export-table entry block.
            // Simpler approach: rewrite ALL existing exports unchanged
            // via RepackWithAddedNames + then surgically patch the renamed
            // export's ObjectName int32 in the resulting bytes.
            var existing = header.ExportTable
                .Select(e => new OmegaAssetStudio.UpkRepacker.ExportBuffer(
                    e.UnrealObjectReader.GetBytes(),
                    Array.Empty<OmegaAssetStudio.UpkRepacker.BulkDataPatch>()))
                .ToList();
            byte[] repacked = OmegaAssetStudio.UpkRepacker.RepackWithAddedNames(
                originalBytes, header, existing, addedNames, out _);

            // Patch the renamed export's ObjectNameIndex in the new bytes.
            // Each export entry's ObjectName FName sits at offset 12 from
            // entry start (after 3*int32 Class/Super/Outer). Layout per the
            // read code: 4 + 4 + 4 + 8 (FName) + ... = ObjectName starts at +12.
            int newExportTableOffset = header.ExportTableOffset + (addedNames.Count > 0 ? CalcAddedNamesSize(addedNames) : 0);
            int entryOffset = newExportTableOffset;
            for (int i = 0; i < header.ExportTable.Count; i++)
            {
                if (i == export.TableIndex)
                {
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                        repacked.AsSpan(entryOffset + 12, 4), newNameIndex);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                        repacked.AsSpan(entryOffset + 16, 4), 0); // numeric
                    break;
                }
                entryOffset += 68 + 4 * header.ExportTable[i].NetObjects.Count;
            }

            // Snapshot + stage-and-rename.
            try { OmegaAssetStudio.BackupManager.BackupFileHelper.CreateBackup(upkPath); } catch { }
            string tmp = upkPath + ".omtmp";
            await File.WriteAllBytesAsync(tmp, repacked, ct).ConfigureAwait(false);
            File.Move(tmp, upkPath, overwrite: true);

            return new(true, $"Renamed {materialExportPath} → {newName}. Reload the UPK to see the change.", Array.Empty<string>());
        }
        catch (Exception ex)
        {
            return new(false, $"{ex.GetType().Name}: {ex.Message}", Array.Empty<string>());
        }
    }

    private static int CalcAddedNamesSize(IReadOnlyList<string> names)
    {
        int total = 0;
        foreach (var s in names) total += 4 + (System.Text.Encoding.ASCII.GetByteCount(s) + 1) + 8;
        return total;
    }
}
