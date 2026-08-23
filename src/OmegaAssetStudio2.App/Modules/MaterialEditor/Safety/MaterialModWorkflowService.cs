using System.Reflection;
using OmegaAssetStudio;
using OmegaAssetStudio.WinUI.Modules.MaterialEditor.Core;
using UpkManager.Helpers;
using UpkManager.Models.UpkFile;
using UpkManager.Models.UpkFile.Engine.Material;
using UpkManager.Models.UpkFile.Objects;
using UpkManager.Models.UpkFile.Tables;
using UpkManager.Repository;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.Safety;

public sealed class MaterialModWorkflowService
{
    private readonly UpkFileRepository repository = new();
    private readonly MaterialUpkWriter writer = new();

    public event Action<string>? LogMessage;

    public MaterialModWorkflowService()
    {
        writer.LogMessage += message => LogMessage?.Invoke(message);
    }

    public async Task<string> EnsureModdedCloneAsync(MaterialDefinition source, string namespaceTag)
    {
        UnrealHeader header = await repository.LoadUpkFile(source.SourceUpkPath).ConfigureAwait(true);
        await header.ReadHeaderAsync(null).ConfigureAwait(true);

        UnrealExportTableEntry sourceExport = ResolveExportByPath(header, source.OriginalPath);
        string cloneName = BuildCloneName(sourceExport.ObjectNameIndex?.Name ?? source.Name, namespaceTag);

        UnrealExportTableEntry? existing = header.ExportTable.FirstOrDefault(export =>
            string.Equals(export.ObjectNameIndex?.Name, cloneName, StringComparison.OrdinalIgnoreCase) &&
            export.OuterReference == sourceExport.OuterReference);

        UnrealExportTableEntry cloneExport;
        if (existing is not null)
        {
            cloneExport = existing;
        }
        else
        {
            cloneExport = await CreateExportCloneAsync(header, sourceExport, cloneName).ConfigureAwait(true);

            // Persist via UpkRepacker rather than SaveUpkFile. Why:
            //   - SaveUpkFile calls WriteObjectBuffer() per export, which RE-SERIALIZES
            //     every parsed UObject from the in-memory model. For some classes that
            //     round-trip is not byte-identical to the original (proven by the
            //     CharacterSwap null-swap repro on costume UPKs) and the resulting
            //     file is rejected by the engine.
            //   - UpkRepacker keeps each export's *original cached bytes* via
            //     UnrealObjectReader.GetBytes() and only re-emits the header + body
            //     offsets. CreateExportCloneAsync above mutated the in-memory header
            //     (added one NameTable entry, one ExportTable entry, one DependsTable
            //     slot) and seeded the new clone's UnrealObjectReader with the cloned
            //     source bytes — so the buffer list below is just "everyone's existing
            //     bytes" and UpkRepacker handles the grown tables in the header re-emit.
            byte[] originalBytes = await File.ReadAllBytesAsync(source.SourceUpkPath).ConfigureAwait(true);
            List<UpkRepacker.ExportBuffer> buffers = new(header.ExportTable.Count);
            foreach (UnrealExportTableEntry export in header.ExportTable)
            {
                if (export.UnrealObjectReader is null)
                    await header.ReadExportObjectAsync(export, null).ConfigureAwait(true);
                buffers.Add(new UpkRepacker.ExportBuffer(export.UnrealObjectReader!.GetBytes(), Array.Empty<UpkRepacker.BulkDataPatch>()));
            }
            byte[] repacked = header.CompressedChunks.Count > 0
                ? UpkRepacker.RepackCompressed(originalBytes, header, buffers)
                : UpkRepacker.Repack(originalBytes, header, buffers);
            await File.WriteAllBytesAsync(source.SourceUpkPath, repacked).ConfigureAwait(true);

            // reload to ensure path/indices are authoritative
            header = await repository.LoadUpkFile(source.SourceUpkPath).ConfigureAwait(true);
            await header.ReadHeaderAsync(null).ConfigureAwait(true);
            cloneExport = header.ExportTable.First(export =>
                string.Equals(export.ObjectNameIndex?.Name, cloneName, StringComparison.OrdinalIgnoreCase) &&
                export.OuterReference == sourceExport.OuterReference);
        }

        // Parent auto import for MIC clones
        if (cloneExport.UnrealObject is null)
        {
            await header.ReadExportObjectAsync(cloneExport, null).ConfigureAwait(true);
            await cloneExport.ParseUnrealObject(false, false).ConfigureAwait(true);
        }
        if (cloneExport.UnrealObject is IUnrealObject cloneObj &&
            cloneObj.UObject is UMaterialInstanceConstant cloneMic &&
            cloneMic.Parent?.TableEntry is UnrealExportTableEntry parentExport &&
            !parentExport.ObjectNameIndex.Name.Contains(namespaceTag, StringComparison.OrdinalIgnoreCase))
        {
            UnrealExportTableEntry parentClone = await CreateOrGetParentCloneAsync(header, parentExport, namespaceTag).ConfigureAwait(true);
            cloneMic.Parent = parentClone.ObjectNameIndex;

            // STILL ON SaveUpkFile (intentional). Why this site CAN'T trivially move
            // to UpkRepacker like the clone-append above:
            //   - The mutation above (cloneMic.Parent = ...) edits the parsed UObject.
            //     To pull that change into the persisted bytes we have to call
            //     WriteObjectBuffer() on the modified MIC — which is exactly the
            //     re-serialize path the CharacterSwap repro proved can be byte-lossy
            //     for some classes.
            //   - The clean fix is a byte-patcher pattern (see MaterialBytePatcher
            //     for the in-place model): locate the parent FName field at its known
            //     offset in the export body, overwrite just those 8 bytes, then splice
            //     the patched body in via UpkRepacker. That helper does not exist for
            //     MIC.Parent yet.
            //   - In the meantime SaveUpkFile is the only path that produces a
            //     consistent file here. If users start reporting corrupted MICs after
            //     a clone-with-parent-rename, build the byte-patcher and replace this.
            string tempPath = Path.Combine(Path.GetDirectoryName(source.SourceUpkPath) ?? string.Empty, $"{Path.GetFileNameWithoutExtension(source.SourceUpkPath)}.modparent.tmp{Path.GetExtension(source.SourceUpkPath)}");
            await repository.SaveUpkFile(header, tempPath, LogMessage).ConfigureAwait(true);
            File.Copy(tempPath, source.SourceUpkPath, true);
            File.Delete(tempPath);
        }

        return cloneExport.GetPathName();
    }

    public async Task<MaterialUpkWriter.PatchResult> CommitToModdedCloneAsync(MaterialDefinition source, string moddedPath)
    {
        return await writer.ApplyAsync(source.SourceUpkPath, moddedPath, BuildPatchSetFromDefinition(source)).ConfigureAwait(true);
    }

    public async Task<string> SwapReferencesToModdedAsync(string upkPath, string nativePath, string moddedPath)
    {
        UnrealHeader header = await repository.LoadUpkFile(upkPath).ConfigureAwait(true);
        await header.ReadHeaderAsync(null).ConfigureAwait(true);

        UnrealExportTableEntry nativeExport = ResolveExportByPath(header, nativePath);
        UnrealExportTableEntry moddedExport = ResolveExportByPath(header, moddedPath);
        int nativeRef = nativeExport.TableIndex;
        int moddedRef = moddedExport.TableIndex;

        byte[] originalBytes = await File.ReadAllBytesAsync(upkPath).ConfigureAwait(true);
        List<UpkRepacker.ExportBuffer> buffers = [];
        foreach (UnrealExportTableEntry export in header.ExportTable)
        {
            if (export.UnrealObjectReader is null)
                await header.ReadExportObjectAsync(export, null).ConfigureAwait(true);

            byte[] bytes = export.UnrealObjectReader!.GetBytes();
            byte[] patched = new byte[bytes.Length];
            Buffer.BlockCopy(bytes, 0, patched, 0, bytes.Length);

            ReplaceInt32(patched, nativeRef, moddedRef);
            buffers.Add(new UpkRepacker.ExportBuffer(patched, []));
        }

        byte[] repacked = header.CompressedChunks.Count > 0
            ? UpkRepacker.RepackCompressed(originalBytes, header, buffers)
            : UpkRepacker.Repack(originalBytes, header, buffers);

        string backupPath = OmegaAssetStudio.BackupManager.BackupFileHelper.CreateBackup(upkPath);
        // Rolling per-edit snapshot for the Edit History timeline.
        try { OmegaAssetStudio.WinUI.Services.EditHistoryService.Snapshot(upkPath, "ModWorkflow"); }
        catch { }
        await File.WriteAllBytesAsync(upkPath, repacked).ConfigureAwait(true);
        return backupPath;
    }

    private static MaterialUpkWriter.PatchSet BuildPatchSetFromDefinition(MaterialDefinition material)
    {
        MaterialUpkWriter.PatchSet patchSet = new();
        foreach (MaterialParameter scalar in material.ScalarParameters)
        {
            if (!string.IsNullOrWhiteSpace(scalar.Name))
                patchSet.Scalars[scalar.Name] = scalar.ScalarValue ?? scalar.DefaultScalarValue ?? 0f;
        }
        foreach (MaterialParameter vector in material.VectorParameters)
        {
            if (!string.IsNullOrWhiteSpace(vector.Name))
                patchSet.Vectors[vector.Name] = vector.VectorValue ?? vector.DefaultVectorValue ?? System.Numerics.Vector4.Zero;
        }
        foreach (MaterialTextureSlot slot in material.TextureSlots)
        {
            if (!string.IsNullOrWhiteSpace(slot.SlotName))
                patchSet.Textures[slot.SlotName] = slot.TexturePath ?? string.Empty;
        }
        return patchSet;
    }

    private static void ReplaceInt32(byte[] bytes, int sourceValue, int targetValue)
    {
        byte[] source = BitConverter.GetBytes(sourceValue);
        byte[] target = BitConverter.GetBytes(targetValue);
        for (int i = 0; i <= bytes.Length - 4; i++)
        {
            if (bytes[i] == source[0] && bytes[i + 1] == source[1] && bytes[i + 2] == source[2] && bytes[i + 3] == source[3])
            {
                bytes[i] = target[0];
                bytes[i + 1] = target[1];
                bytes[i + 2] = target[2];
                bytes[i + 3] = target[3];
            }
        }
    }

    private static async Task<UnrealExportTableEntry> CreateOrGetParentCloneAsync(UnrealHeader header, UnrealExportTableEntry parentExport, string namespaceTag)
    {
        string cloneName = BuildCloneName(parentExport.ObjectNameIndex?.Name ?? "ParentMaterial", namespaceTag);
        UnrealExportTableEntry? existing = header.ExportTable.FirstOrDefault(export =>
            string.Equals(export.ObjectNameIndex?.Name, cloneName, StringComparison.OrdinalIgnoreCase) &&
            export.OuterReference == parentExport.OuterReference);
        if (existing is not null)
            return existing;

        return await CreateExportCloneAsync(header, parentExport, cloneName).ConfigureAwait(true);
    }

    private static async Task<UnrealExportTableEntry> CreateExportCloneAsync(UnrealHeader header, UnrealExportTableEntry sourceExport, string cloneName)
    {
        if (sourceExport.UnrealObjectReader is null)
            await header.ReadExportObjectAsync(sourceExport, null).ConfigureAwait(true);

        UnrealExportTableEntry clone = (UnrealExportTableEntry)Activator.CreateInstance(typeof(UnrealExportTableEntry), nonPublic: true)!;
        SetNonPublic(clone, "UnrealHeader", header);
        SetNonPublic(clone, "ClassReference", GetNonPublic<int>(sourceExport, "ClassReference"));
        SetNonPublic(clone, "SuperReference", GetNonPublic<int>(sourceExport, "SuperReference"));
        SetNonPublic(clone, "OuterReference", sourceExport.OuterReference);
        SetNonPublic(clone, "ArchetypeReference", GetNonPublic<int>(sourceExport, "ArchetypeReference"));
        SetNonPublic(clone, "ObjectFlags", GetNonPublic<ulong>(sourceExport, "ObjectFlags"));
        SetNonPublic(clone, "ExportFlags", GetNonPublic<uint>(sourceExport, "ExportFlags"));
        SetNonPublic(clone, "PackageGuid", (byte[])GetNonPublic<byte[]>(sourceExport, "PackageGuid").Clone());
        SetNonPublic(clone, "PackageFlags", GetNonPublic<uint>(sourceExport, "PackageFlags"));
        SetNonPublic(clone, "NetObjects", new List<int>(GetNonPublic<List<int>>(sourceExport, "NetObjects")));

        UnrealNameTableEntry nameEntry = EnsureNameEntry(header, cloneName);
        FObject objectName = new(clone);
        objectName.SetNameTableIndex(nameEntry);
        SetNonPublic(clone, "ObjectNameIndex", objectName);

        byte[] bytes = sourceExport.UnrealObjectReader!.GetBytes();
        SetNonPublic(clone, "SerialDataSize", bytes.Length);
        SetNonPublic(clone, "SerialDataOffset", 0);
        SetNonPublic(clone, "UnrealObjectReader", ByteArrayReader.CreateNew(bytes, 0));
        SetNonPublic(clone, "UnrealObject", null);
        clone.TableIndex = header.ExportTable.Count + 1;
        header.ExportTable.Add(clone);
        header.DependsTable.Add(0);
        return clone;
    }

    private static UnrealNameTableEntry EnsureNameEntry(UnrealHeader header, string value)
    {
        UnrealNameTableEntry? existing = header.NameTable.FirstOrDefault(entry => string.Equals(entry.Name.String, value, StringComparison.Ordinal));
        if (existing is not null)
            return existing;

        UnrealString name = new();
        name.SetString(value);
        UnrealNameTableEntry created = new();
        int index = header.NameTable.Count;
        created.SetNameTableEntry(name, 0x0007001000000000, index);
        header.NameTable.Add(created);
        return created;
    }

    private static UnrealExportTableEntry ResolveExportByPath(UnrealHeader header, string path)
    {
        UnrealExportTableEntry? export = header.ExportTable.FirstOrDefault(entry =>
            string.Equals(entry.GetPathName(), path, StringComparison.OrdinalIgnoreCase));
        if (export is null)
            throw new InvalidOperationException($"Export not found: {path}");
        return export;
    }

    private static string BuildCloneName(string sourceObjectName, string namespaceTag)
    {
        string safeName = new string((sourceObjectName ?? "Material").Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        if (safeName.Length == 0)
            safeName = "Material";
        return $"{namespaceTag}_{safeName}";
    }

    private static T GetNonPublic<T>(object instance, string propertyName)
    {
        PropertyInfo? property = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        object? value = property?.GetValue(instance);
        return value is T typed ? typed : default!;
    }

    private static void SetNonPublic(object instance, string propertyName, object? value)
    {
        PropertyInfo? property = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        property?.SetValue(instance, value);
    }
}
