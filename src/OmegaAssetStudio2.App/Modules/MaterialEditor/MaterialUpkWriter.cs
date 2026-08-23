using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using OmegaAssetStudio;
using OmegaAssetStudio.BackupManager;
using UpkManager.Models.UpkFile;
using UpkManager.Models.UpkFile.Engine.Material;
using UpkManager.Models.UpkFile.Objects;
using UpkManager.Models.UpkFile.Tables;
using UpkManager.Repository;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor;

/// <summary>
/// Applies in-place size-preserving byte patches to a UMaterialInstanceConstant export and
/// rewrites the host UPK via <see cref="UpkRepacker"/>. The patcher only touches the bytes of
/// values that the caller actually changed; every other byte (and every other export) is left
/// bit-for-bit identical. This is the same write strategy the existing texture injectors use,
/// and is the only known-safe path for TargetClient modding because it requires no header rebuild, no
/// import-table churn, and no shader-permutation recompile.
/// </summary>
public sealed class MaterialUpkWriter
{
    private readonly UpkFileRepository upkRepository = new();

    public event Action<string>? LogMessage;

    public sealed class PatchSet
    {
        /// <summary>Map of parameter name -> new float value.</summary>
        public Dictionary<string, float> Scalars { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Map of parameter name -> new FLinearColor (R, G, B, A) value.</summary>
        public Dictionary<string, Vector4> Vectors { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Map of parameter name -> new texture path. Resolution rules:
        ///   * Empty string -> write null (FObject ref = 0).
        ///   * Matches an export in the same UPK -> write +TableIndex.
        ///   * Matches an existing import in the same UPK -> write -TableIndex.
        ///   * Anything else is rejected (we never add new imports â€” that requires header rebuild).
        /// </summary>
        public Dictionary<string, string> Textures { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool IsEmpty => Scalars.Count == 0 && Vectors.Count == 0 && Textures.Count == 0;
    }

    public sealed class PatchResult
    {
        public int ScalarsWritten { get; init; }
        public int VectorsWritten { get; init; }
        public int TexturesWritten { get; init; }
        public List<string> Skipped { get; init; } = [];
        public string BackupPath { get; init; } = string.Empty;
    }

    public async Task<PatchResult> ApplyAsync(string upkPath, string materialExportPath, PatchSet patches)
    {
        ArgumentNullException.ThrowIfNull(upkPath);
        ArgumentNullException.ThrowIfNull(materialExportPath);
        ArgumentNullException.ThrowIfNull(patches);

        if (!File.Exists(upkPath))
            throw new FileNotFoundException("UPK file not found.", upkPath);

        if (patches.IsEmpty)
            return new PatchResult();

        Log($"Loading {Path.GetFileName(upkPath)} for material patch.");

        byte[] originalBytes = await File.ReadAllBytesAsync(upkPath).ConfigureAwait(true);
        UnrealHeader header = await upkRepository.LoadUpkFile(upkPath).ConfigureAwait(true);
        await header.ReadHeaderAsync(null).ConfigureAwait(true);

        UnrealExportTableEntry export = FindMaterialExport(header, materialExportPath);

        if (export.UnrealObject is null)
        {
            await header.ReadExportObjectAsync(export, null).ConfigureAwait(true);
            await export.ParseUnrealObject(false, false).ConfigureAwait(true);
        }

        if (export.UnrealObject is not IUnrealObject unrealObject || unrealObject.UObject is not UMaterialInstanceConstant)
            throw new InvalidOperationException($"Export '{materialExportPath}' is not a MaterialInstanceConstant.");

        if (export.UnrealObjectReader is null)
            throw new InvalidOperationException($"Export '{materialExportPath}' has no raw byte buffer; cannot patch.");

        byte[] sourceBytes = export.UnrealObjectReader.GetBytes();
        byte[] patchedBytes = new byte[sourceBytes.Length];
        Buffer.BlockCopy(sourceBytes, 0, patchedBytes, 0, sourceBytes.Length);

        MaterialBytePatcher patcher = new();
        MaterialBytePatcher.ParameterOffsets offsets = patcher.Locate(patchedBytes, header);

        List<string> skipped = [];
        int scalarsWritten = ApplyScalars(patchedBytes, offsets, patches.Scalars, skipped);
        int vectorsWritten = ApplyVectors(patchedBytes, offsets, patches.Vectors, skipped);
        int texturesWritten = ApplyTextureRefs(patchedBytes, offsets, patches.Textures, header, skipped);

        if (scalarsWritten + vectorsWritten + texturesWritten == 0)
        {
            Log("No parameter offsets matched; nothing to write.");
            return new PatchResult { Skipped = skipped };
        }

        Log($"Repacking UPK with {scalarsWritten} scalar / {vectorsWritten} vector / {texturesWritten} texture patch(es).");

        byte[] repacked = header.CompressedChunks.Count > 0
            ? UpkRepacker.RepackCompressed(originalBytes, header, export.TableIndex - 1, patchedBytes, [])
            : UpkRepacker.Repack(originalBytes, header, export.TableIndex - 1, patchedBytes, []);

        string backupPath = BackupFileHelper.CreateBackup(upkPath);
        // Rolling per-edit snapshot (separate from one-shot pristine .bak) so
        // the Edit History page can scrub through every material commit.
        try { OmegaAssetStudio.WinUI.Services.EditHistoryService.Snapshot(upkPath, "MaterialWriter"); }
        catch { }
        // Material-Editor-scoped snapshot — captured BEFORE the new bytes
        // write so Restore can revert this exact edit. The previous two
        // backup paths cover "ever-modified" and "edit history" but neither
        // is browsable per-material; this one is.
        try
        {
            new MaterialCore.Snapshots.MaterialAutoSnapshotStore()
                .Capture(upkPath, materialExportPath ?? "", $"pre-{DateTime.UtcNow:HH:mm:ss}", "MaterialWriter save");
        }
        catch { }
        // Crash-safe stage-and-rename: half-written UPKs on power loss would
        // brick the material; staging to .omtmp then atomic File.Move means
        // the on-disk file is always either pristine-or-fully-new.
        string tmpPath = upkPath + ".omtmp";
        await File.WriteAllBytesAsync(tmpPath, repacked).ConfigureAwait(true);
        File.Move(tmpPath, upkPath, overwrite: true);

        Log($"Material patch committed. Backup: {Path.GetFileName(backupPath)}.");

        return new PatchResult
        {
            ScalarsWritten = scalarsWritten,
            VectorsWritten = vectorsWritten,
            TexturesWritten = texturesWritten,
            Skipped = skipped,
            BackupPath = backupPath
        };
    }

    private static UnrealExportTableEntry FindMaterialExport(UnrealHeader header, string materialExportPath)
    {
        foreach (UnrealExportTableEntry candidate in header.ExportTable)
        {
            if (string.Equals(candidate.GetPathName(), materialExportPath, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        throw new InvalidOperationException($"Material export '{materialExportPath}' was not found.");
    }

    private static int ApplyScalars(byte[] buffer, MaterialBytePatcher.ParameterOffsets offsets, IReadOnlyDictionary<string, float> scalars, List<string> skipped)
    {
        int written = 0;
        foreach (KeyValuePair<string, float> kvp in scalars)
        {
            if (!offsets.Scalars.TryGetValue(kvp.Key, out MaterialBytePatcher.ScalarOffset? offset))
            {
                skipped.Add($"Scalar '{kvp.Key}' not located in MIC; left unchanged.");
                continue;
            }

            BitConverter.GetBytes(kvp.Value).CopyTo(buffer, offset.ValueOffset);
            written++;
        }
        return written;
    }

    private static int ApplyVectors(byte[] buffer, MaterialBytePatcher.ParameterOffsets offsets, IReadOnlyDictionary<string, Vector4> vectors, List<string> skipped)
    {
        int written = 0;
        foreach (KeyValuePair<string, Vector4> kvp in vectors)
        {
            if (!offsets.Vectors.TryGetValue(kvp.Key, out MaterialBytePatcher.VectorOffset? offset))
            {
                skipped.Add($"Vector '{kvp.Key}' not located in MIC; left unchanged.");
                continue;
            }

            int o = offset.ValueOffset;
            BitConverter.GetBytes(kvp.Value.X).CopyTo(buffer, o);       // R
            BitConverter.GetBytes(kvp.Value.Y).CopyTo(buffer, o + 4);   // G
            BitConverter.GetBytes(kvp.Value.Z).CopyTo(buffer, o + 8);   // B
            BitConverter.GetBytes(kvp.Value.W).CopyTo(buffer, o + 12);  // A
            written++;
        }
        return written;
    }

    private static int ApplyTextureRefs(byte[] buffer, MaterialBytePatcher.ParameterOffsets offsets, IReadOnlyDictionary<string, string> textures, UnrealHeader header, List<string> skipped)
    {
        int written = 0;
        foreach (KeyValuePair<string, string> kvp in textures)
        {
            if (!offsets.Textures.TryGetValue(kvp.Key, out MaterialBytePatcher.TextureOffset? offset))
            {
                skipped.Add($"Texture slot '{kvp.Key}' not located in MIC; left unchanged.");
                continue;
            }

            int reference;
            if (string.IsNullOrWhiteSpace(kvp.Value))
            {
                reference = 0;
            }
            else if (TryResolveExportReference(header, kvp.Value, out int exportRef))
            {
                reference = exportRef;
            }
            else if (TryResolveImportReference(header, kvp.Value, out int importRef))
            {
                reference = importRef;
            }
            else
            {
                skipped.Add($"Texture slot '{kvp.Key}' -> '{kvp.Value}' could not be resolved as an existing export or import; left unchanged.");
                continue;
            }

            BitConverter.GetBytes(reference).CopyTo(buffer, offset.ValueOffset);
            written++;
        }
        return written;
    }

    private static bool TryResolveExportReference(UnrealHeader header, string path, out int reference)
    {
        foreach (UnrealExportTableEntry candidate in header.ExportTable)
        {
            if (string.Equals(candidate.GetPathName(), path, StringComparison.OrdinalIgnoreCase))
            {
                reference = candidate.TableIndex;
                return true;
            }
        }

        reference = 0;
        return false;
    }

    private static bool TryResolveImportReference(UnrealHeader header, string path, out int reference)
    {
        for (int i = 0; i < header.ImportTable.Count; i++)
        {
            UnrealImportTableEntry candidate = header.ImportTable[i];
            if (string.Equals(candidate.GetPathName(), path, StringComparison.OrdinalIgnoreCase))
            {
                reference = -candidate.TableIndex;
                return true;
            }
        }

        reference = 0;
        return false;
    }

    private void Log(string message) => LogMessage?.Invoke(message);
}

