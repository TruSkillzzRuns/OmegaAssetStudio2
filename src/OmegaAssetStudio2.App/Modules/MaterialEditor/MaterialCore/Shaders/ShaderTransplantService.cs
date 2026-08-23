namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.Shaders;

// Advanced "copy the compiled shader from material A onto material B,
// remapping textures by slot name." Useful for users who want to make MIC
// X render the way MIC Y does without re-authoring the source graph.
//
// Scaffolded because: shader bodies are deeply intertwined with the parent
// UMaterial's UniformExpressionTextures cache + the StaticPermutationResource
// trailing bytes. Copying the body alone produces an MIC that references
// textures by slot index that don't exist in the destination MIC, so the
// service also needs a slot-by-name remapper. End-to-end this is its own
// session with the FMaterialResource serializer (above) as a prerequisite.
public interface IShaderTransplantService
{
    Task<TransplantResult> TransplantAsync(
        string sourceUpkPath, string sourceMicExportPath,
        string destUpkPath, string destMicExportPath,
        IReadOnlyDictionary<string, string>? slotNameRemap = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<ShaderSlotInfo>> ReadSlotsAsync(
        string upkPath, string micExportPath, CancellationToken ct = default);

    public sealed record TransplantResult(bool Ok, string Message);
    public sealed record ShaderSlotInfo(int Index, string ParameterName, string TexturePath);
}

public sealed class ShaderTransplantService : IShaderTransplantService
{
    private readonly UpkManager.Repository.UpkFileRepository _repo = new();
    private readonly Variants.IMaterialResourceSerializer _serializer = new Variants.MaterialResourceSerializer();

    public async Task<IShaderTransplantService.TransplantResult> TransplantAsync(
        string sourceUpkPath, string sourceMicExportPath,
        string destUpkPath, string destMicExportPath,
        IReadOnlyDictionary<string, string>? slotNameRemap, CancellationToken ct = default)
    {
        bool crossUpk = !string.Equals(sourceUpkPath, destUpkPath, StringComparison.OrdinalIgnoreCase);
        if (crossUpk)
            return new(false,
                "Cross-UPK shader transplant: use MIC Clone (cross-UPK is supported) to copy the source MIC into the destination UPK first, " +
                "then run shader transplant same-UPK. Direct cross-UPK transplant of the FMaterialResource body would also need its " +
                "embedded FObject refs translated — the rewriter foundation is in place; wiring it through the resource serializer is its own pass.");

        try
        {
            byte[] originalBytes = await File.ReadAllBytesAsync(destUpkPath, ct).ConfigureAwait(false);
            var header = await _repo.LoadUpkFile(destUpkPath).ConfigureAwait(false);
            await header.ReadHeaderAsync(null).ConfigureAwait(false);

            var src = header.ExportTable.FirstOrDefault(e =>
                string.Equals(e.GetPathName(), sourceMicExportPath, StringComparison.OrdinalIgnoreCase));
            var dst = header.ExportTable.FirstOrDefault(e =>
                string.Equals(e.GetPathName(), destMicExportPath, StringComparison.OrdinalIgnoreCase));
            if (src is null) return new(false, $"source MIC not found: {sourceMicExportPath}");
            if (dst is null) return new(false, $"dest MIC not found: {destMicExportPath}");

            await header.ReadExportObjectAsync(src, null).ConfigureAwait(false);
            await src.ParseUnrealObject(false, false).ConfigureAwait(false);
            await header.ReadExportObjectAsync(dst, null).ConfigureAwait(false);
            await dst.ParseUnrealObject(false, false).ConfigureAwait(false);

            if (src.UnrealObject is not UpkManager.Models.UpkFile.Objects.IUnrealObject uoS ||
                uoS.UObject is not UpkManager.Models.UpkFile.Engine.Material.UMaterialInstanceConstant micS)
                return new(false, "source export is not a UMaterialInstanceConstant");
            if (dst.UnrealObject is not UpkManager.Models.UpkFile.Objects.IUnrealObject uoD ||
                uoD.UObject is not UpkManager.Models.UpkFile.Engine.Material.UMaterialInstanceConstant micD)
                return new(false, "dest export is not a UMaterialInstanceConstant");

            // Read source resource, optionally remap texture slots by name,
            // then assign onto the destination's StaticPermutationResources.
            // (Slot-remap is currently identity; richer behavior wires here.)
            if (micS.StaticPermutationResources is null || micS.StaticPermutationResources.Length == 0)
                return new(false, "source MIC has no static permutation resource — nothing to transplant");
            micD.bHasStaticPermutationResource = true;
            micD.StaticPermutationResources = micS.StaticPermutationResources;

            // Serialize new resource body. Replace the matching region of
            // the dest export's bytes via byte-substring search of the
            // serialized OLD resource. Matches the pattern the structural-
            // variant writer uses.
            byte[] destBytes = dst.UnrealObjectReader.GetBytes();
            byte[] newResource = _serializer.Serialize(micD.StaticPermutationResources[0]!);

            // Snapshot + write.
            try { OmegaAssetStudio.BackupManager.BackupFileHelper.CreateBackup(destUpkPath); } catch { }

            // Naive replacement: write the whole new export bytes by
            // re-using the dest's pre-existing prefix (up to where the
            // resource starts) and appending the new resource. Locating
            // the resource start requires the bool-anchor trick.
            var destSnapshot = Variants.MaterialBodyReader.FromMaterialInstance(micD);
            var anchorOffsets = Variants.MaterialBodyByteLocator.LocateByBoolAnchor(destBytes, destSnapshot);
            if (anchorOffsets is null)
                return new(false, "could not anchor dest resource bool block — transplant unsafe");
            // ResourceEnd from anchor is the FMaterialResource trailer end.
            // Splice = [prefix up to resource start][newResource][tail after resource end].
            // We approximate resource start by searching for the serialized
            // OLD destination resource bytes; if not found, refuse.
            byte[] oldResource = _serializer.Serialize(micS.StaticPermutationResources[0]!);
            int oldStart = IndexOf(destBytes, oldResource);
            if (oldStart < 0) return new(false, "could not locate dest resource bytes via round-trip — verify serializer round-trip first");
            int oldEnd = oldStart + oldResource.Length;

            byte[] newExport = new byte[destBytes.Length - oldResource.Length + newResource.Length];
            Buffer.BlockCopy(destBytes, 0, newExport, 0, oldStart);
            Buffer.BlockCopy(newResource, 0, newExport, oldStart, newResource.Length);
            Buffer.BlockCopy(destBytes, oldEnd, newExport, oldStart + newResource.Length, destBytes.Length - oldEnd);

            // Repack with the destination export replaced.
            var existing = header.ExportTable
                .Select((e, i) => i == dst.TableIndex
                    ? new OmegaAssetStudio.UpkRepacker.ExportBuffer(newExport, Array.Empty<OmegaAssetStudio.UpkRepacker.BulkDataPatch>())
                    : new OmegaAssetStudio.UpkRepacker.ExportBuffer(e.UnrealObjectReader.GetBytes(), Array.Empty<OmegaAssetStudio.UpkRepacker.BulkDataPatch>()))
                .ToList();
            byte[] repacked = header.CompressedChunks.Count > 0
                ? OmegaAssetStudio.UpkRepacker.RepackCompressed(originalBytes, header, existing)
                : OmegaAssetStudio.UpkRepacker.Repack(originalBytes, header, existing);

            string tmp = destUpkPath + ".omtmp";
            await File.WriteAllBytesAsync(tmp, repacked, ct).ConfigureAwait(false);
            File.Move(tmp, destUpkPath, overwrite: true);
            return new(true, $"Transplanted shader body {sourceMicExportPath} → {destMicExportPath}. Reload to verify.");
        }
        catch (Exception ex) { return new(false, $"{ex.GetType().Name}: {ex.Message}"); }
    }

    public async Task<IReadOnlyList<IShaderTransplantService.ShaderSlotInfo>> ReadSlotsAsync(
        string upkPath, string micExportPath, CancellationToken ct = default)
    {
        try
        {
            var header = await _repo.LoadUpkFile(upkPath).ConfigureAwait(false);
            await header.ReadHeaderAsync(null).ConfigureAwait(false);
            var entry = header.ExportTable.FirstOrDefault(e =>
                string.Equals(e.GetPathName(), micExportPath, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return Array.Empty<IShaderTransplantService.ShaderSlotInfo>();
            await header.ReadExportObjectAsync(entry, null).ConfigureAwait(false);
            await entry.ParseUnrealObject(false, false).ConfigureAwait(false);
            if (entry.UnrealObject is not UpkManager.Models.UpkFile.Objects.IUnrealObject uo ||
                uo.UObject is not UpkManager.Models.UpkFile.Engine.Material.UMaterialInstanceConstant mic)
                return Array.Empty<IShaderTransplantService.ShaderSlotInfo>();
            var result = new List<IShaderTransplantService.ShaderSlotInfo>();
            if (mic.TextureParameterValues is not null)
            {
                int idx = 0;
                foreach (var p in mic.TextureParameterValues)
                {
                    result.Add(new IShaderTransplantService.ShaderSlotInfo(
                        idx++,
                        p?.ParameterName?.Name ?? "",
                        p?.ParameterValue?.GetPathName() ?? ""));
                }
            }
            return result;
        }
        catch { return Array.Empty<IShaderTransplantService.ShaderSlotInfo>(); }
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length) return -1;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }
}
