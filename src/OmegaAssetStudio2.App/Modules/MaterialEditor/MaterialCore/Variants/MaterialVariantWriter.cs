using OmegaAssetStudio;
using OmegaAssetStudio.BackupManager;
using OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.Snapshots;
using UpkManager.Models.UpkFile;
using UpkManager.Models.UpkFile.Engine.Material;
using UpkManager.Models.UpkFile.Objects;
using UpkManager.Models.UpkFile.Tables;
using UpkManager.Repository;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.Variants;

// End-to-end Toggle apply path:
//   1. Snapshot the UPK via MaterialAutoSnapshotStore (cheap rollback path).
//   2. Parse the MIC export so we have a MaterialBodySnapshot anchor.
//   3. Anchor the FMaterialResource inside the export bytes via the
//      bool-block search, scalar-patch via MaterialBodyBytePatcher.
//   4. Repack the export bytes into a new UPK via UpkRepacker.
//   5. Side-by-side .bak + atomic stage-and-rename write.
//
// Scope today: SCALAR-ONLY deltas (bool flags, blend mode, NumTexCoords,
// UsingTransforms, MaxTexDepLen). Texture-array-changing deltas refuse
// with a clear message — those need a full FMaterialResource serializer
// which lives in another session.
public sealed class MaterialVariantWriter
{
    private readonly UpkFileRepository _repository = new();
    private readonly MaterialAutoSnapshotStore _snapshots = new();

    public sealed record WriteResult(bool Ok, string Message);

    public async Task<WriteResult> ApplyAsync(
        string upkPath,
        string exportPath,
        SwitchDeltaEntry delta,
        CancellationToken ct = default)
    {
        if (!File.Exists(upkPath)) return new(false, $"UPK not found: {upkPath}");

        // Snapshot first — never write without a rollback point.
        try { _snapshots.Capture(upkPath, exportPath, $"variant-{delta.SwitchName}", $"toggle switch {delta.SwitchName}"); }
        catch (Exception ex) { return new(false, $"snapshot failed: {ex.Message}"); }
        try { BackupFileHelper.CreateBackup(upkPath); } catch { }

        byte[] originalBytes = await File.ReadAllBytesAsync(upkPath, ct).ConfigureAwait(false);
        UnrealHeader header = await _repository.LoadUpkFile(upkPath).ConfigureAwait(false);
        await header.ReadHeaderAsync(null).ConfigureAwait(false);

        UnrealExportTableEntry? export = header.ExportTable
            .FirstOrDefault(e => string.Equals(e.GetPathName(), exportPath, StringComparison.OrdinalIgnoreCase));
        if (export is null) return new(false, $"export not found: {exportPath}");

        if (export.UnrealObject == null)
        {
            await header.ReadExportObjectAsync(export, null).ConfigureAwait(false);
            await export.ParseUnrealObject(false, false).ConfigureAwait(false);
        }
        if (export.UnrealObject is not IUnrealObject uo || uo.UObject is not UMaterialInstanceConstant mic)
            return new(false, "export is not a MaterialInstanceConstant");

        // Pull the live snapshot — drives the anchor search.
        var snapshot = MaterialBodyReader.FromMaterialInstance(mic);
        byte[] exportBytes = export.UnrealObjectReader.GetBytes();

        var offsets = MaterialBodyByteLocator.LocateByBoolAnchor(exportBytes, snapshot);
        if (offsets is null) return new(false, "could not anchor the FMaterialResource bool block in export bytes");

        // Mutate a copy so a refusal mid-patch doesn't leave us with a
        // half-mutated buffer.
        byte[] patchedExport = (byte[])exportBytes.Clone();
        var patchResult = MaterialBodyBytePatcher.ApplyScalarDelta(patchedExport, /*bodyStart=*/0, delta);
        // bodyStart=0 is fine here because LocateByBoolAnchor uses absolute
        // offsets within the export buffer — we just pass the export bytes
        // directly and the patcher's BodyByteLocator.Walk path isn't used
        // (we hand it pre-located offsets indirectly via the same buffer).
        // But the current ApplyScalarDelta still calls Walk(bodyStart=0)
        // which will fail on tagged-property prefix — we override by
        // patching at the anchored offsets directly.
        if (!patchResult.Ok)
        {
            // Fall back: apply scalar fields manually at the anchored offsets.
            patchedExport = ApplyAtAnchoredOffsets(exportBytes, offsets, delta, out int written);
            if (written == 0) return new(false, $"no scalar fields applied ({patchResult.Refusal ?? "no-op"})");
        }

        // Repack the UPK with the modified export bytes.
        byte[] repacked = header.CompressedChunks.Count > 0
            ? UpkRepacker.RepackCompressed(originalBytes, header, export.TableIndex - 1, patchedExport, Array.Empty<UpkRepacker.BulkDataPatch>())
            : UpkRepacker.Repack(originalBytes, header, export.TableIndex - 1, patchedExport, Array.Empty<UpkRepacker.BulkDataPatch>());

        // Crash-safe stage + atomic rename.
        string tmp = upkPath + ".omtmp";
        await File.WriteAllBytesAsync(tmp, repacked, ct).ConfigureAwait(false);
        File.Move(tmp, upkPath, overwrite: true);

        return new(true, $"applied {delta.SwitchName} ({patchResult.FieldsWritten} field(s)) to {Path.GetFileName(upkPath)}");
    }

    // Direct patch at the anchored offsets — bypasses the locator's own
    // Walk-from-bodyStart path which doesn't work on raw export bytes that
    // begin with the tagged-property block.
    private static byte[] ApplyAtAnchoredOffsets(
        byte[] exportBytes,
        MaterialBodyByteLocator.FieldOffsets o,
        SwitchDeltaEntry delta,
        out int written)
    {
        written = 0;
        byte[] copy = (byte[])exportBytes.Clone();
        var span = copy.AsSpan();
        if (delta.TextureCountDelta != 0 || delta.LookupCountDelta != 0 ||
            delta.TexturesAdded.Count > 0 || delta.TexturesRemoved.Count > 0)
            return copy; // structural — refuse silently here, caller already handled

        if (delta.NumTexCoordsDelta != 0)
        {
            int cur = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(span.Slice(o.NumUserTexCoords, 4));
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span.Slice(o.NumUserTexCoords, 4), cur + delta.NumTexCoordsDelta);
            written++;
        }
        if (delta.MaxTextureDependencyLengthDelta != 0)
        {
            int cur = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(span.Slice(o.MaxTextureDependencyLength, 4));
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span.Slice(o.MaxTextureDependencyLength, 4), cur + delta.MaxTextureDependencyLengthDelta);
            written++;
        }
        if (delta.UsingTransformsXor != 0)
        {
            uint cur = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(o.UsingTransforms, 4));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(o.UsingTransforms, 4), cur ^ delta.UsingTransformsXor);
            written++;
        }
        written += WriteBool(span, o.UsesSceneColor,             delta.UsesSceneColorTo);
        written += WriteBool(span, o.UsesSceneDepth,             delta.UsesSceneDepthTo);
        written += WriteBool(span, o.UsesDynamicParameter,       delta.UsesDynamicParameterTo);
        written += WriteBool(span, o.UsesLightmapUVs,            delta.UsesLightmapUVsTo);
        written += WriteBool(span, o.UsesMaterialVertexPositionOffset, delta.UsesMaterialVertexPositionOffsetTo);
        written += WriteBool(span, o.IsBlendModeOverridden,      delta.IsBlendModeOverriddenTo);
        written += WriteBool(span, o.IsMaskedOverride,           delta.IsMaskedOverrideTo);
        if (delta.BlendModeValueTo is int bm)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span.Slice(o.BlendModeOverrideValue, 4), bm);
            written++;
        }
        return copy;
    }

    private static int WriteBool(Span<byte> span, int offset, bool? to)
    {
        if (to is not bool v) return 0;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), v ? 1 : 0);
        return 1;
    }
}
