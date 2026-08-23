namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.Cloning;

// Service contract for "Create New MIC from Parent". Implementation deferred
// because the existing UpkRepacker can add NameTable entries but not new
// ExportTable entries — that's the prerequisite for inserting a new MIC
// into a UPK and is a separate, well-scoped UpkRepacker extension.
//
// Once that lands, this interface stays stable: the UI will call
// CloneAsync exactly as written below.
public interface IMicCloneService
{
    // Clone a parent UMaterial into a brand-new UMaterialInstanceConstant
    // in the destination UPK. Returns the new MIC's export path.
    //
    // Required behavior:
    //   1. Resolve parentExportPath in sourceUpk; refuse if it's not a UMaterial.
    //   2. If destUpk != sourceUpk: add an Import entry chain pointing at the
    //      parent. Use ResolveImportChain semantics — Outer → Package walked.
    //   3. Add a NameTable entry for newMicName + its outer group if absent.
    //   4. Build a default UMaterialInstanceConstant serial: Parent reference
    //      + empty TextureParameterValues/ScalarParameterValues/Vector* arrays
    //      + bHasStaticPermutationResource=false (no compiled body yet).
    //   5. Add an ExportTable entry pointing at the new bytes.
    //   6. Repack the destination UPK via UpkRepacker (needs the new
    //      "add export" path in UpkRepacker — TODO).
    //   7. Returns the new MIC's full path name (e.g. "Group.NewMicName").
    Task<CloneResult> CloneAsync(
        string sourceUpkPath,
        string parentExportPath,
        string destUpkPath,
        string newMicGroupPath,
        string newMicName,
        CancellationToken ct = default);

    public sealed record CloneResult(bool Ok, string Message, string? NewExportPath = null);
}

// Implementation uses UpkRepacker.RepackWithAddedExports to write a fresh
// UMaterialInstanceConstant export into the destination UPK, with import
// chain to the parent UMaterial when source != dest.
//
// Scope today: same-UPK clone (source == dest). Cross-UPK adds an extra
// import-chain construction step that still depends on the destination's
// NameTable layout — handled by adding any missing names + the parent
// import entry through the same added-names path. Listed under "next" if
// users hit that path heavily.
public sealed class MicCloneService : IMicCloneService
{
    private readonly UpkManager.Repository.UpkFileRepository _repo = new();

    public async Task<IMicCloneService.CloneResult> CloneAsync(
        string sourceUpkPath, string parentExportPath, string destUpkPath,
        string newMicGroupPath, string newMicName, CancellationToken ct = default)
    {
        if (!File.Exists(sourceUpkPath))
            return new(false, $"source UPK not found: {sourceUpkPath}");
        bool crossUpk = !string.Equals(sourceUpkPath, destUpkPath, StringComparison.OrdinalIgnoreCase);
        if (crossUpk && !File.Exists(destUpkPath))
            return new(false, $"destination UPK not found: {destUpkPath}");

        if (crossUpk)
        {
            return await CloneCrossUpkAsync(sourceUpkPath, parentExportPath, destUpkPath,
                                            newMicGroupPath, newMicName, ct).ConfigureAwait(false);
        }

        try
        {
            byte[] originalBytes = await File.ReadAllBytesAsync(destUpkPath, ct).ConfigureAwait(false);
            var header = await _repo.LoadUpkFile(destUpkPath).ConfigureAwait(false);
            await header.ReadHeaderAsync(null).ConfigureAwait(false);

            // The "selected material" we're cloning IS the source MIC — the
            // caller picked it via Browser. Reuse its Class/Super/Outer/
            // Archetype refs AND copy its serialized body bytes verbatim so
            // every tagged property (Parent, TextureParameterValues, scalar /
            // vector overrides) carries forward. Without the body copy the
            // resulting "clone" would have no Parent and render pink.
            var sourceMic = header.ExportTable.FirstOrDefault(e =>
                string.Equals(e.GetPathName(), parentExportPath, StringComparison.OrdinalIgnoreCase));
            if (sourceMic is null) return new(false, $"source MIC export not found: {parentExportPath}");

            byte[] micBody = sourceMic.UnrealObjectReader.GetBytes();

            // NameTable: add the new MIC's name (only if not already present).
            int existingNameIndex = -1;
            for (int i = 0; i < header.NameTable.Count; i++)
                if (string.Equals(header.NameTable[i].Name?.String, newMicName, StringComparison.OrdinalIgnoreCase))
                { existingNameIndex = i; break; }

            var addedNames = new List<string>();
            int newNameTableIndex = existingNameIndex;
            if (existingNameIndex < 0)
            {
                addedNames.Add(newMicName);
                newNameTableIndex = header.NameTable.Count + 0; // index after add
            }

            // Existing-export buffers: each existing export's bytes passed through
            // unchanged. We're not modifying any of them.
            var existing = header.ExportTable
                .Select(e => new OmegaAssetStudio.UpkRepacker.ExportBuffer(
                    e.UnrealObjectReader.GetBytes(),
                    Array.Empty<OmegaAssetStudio.UpkRepacker.BulkDataPatch>()))
                .ToList();

            // The new MIC entry: same Class/Super/Outer/Archetype refs as the
            // parent (which is itself a UMaterial — class will resolve to
            // "UMaterialInstanceConstant" via the parent's NameTable lookup
            // if its ClassReference points there). Same-UPK clone is the
            // simplest path; cross-UPK requires class-import rewiring.
            var spec = new OmegaAssetStudio.UpkRepacker.NewExportSpec(
                Data: micBody,
                Patches: Array.Empty<OmegaAssetStudio.UpkRepacker.BulkDataPatch>(),
                ClassRef: sourceMic.ClassReference,
                SuperRef: sourceMic.SuperReference,
                OuterRef: sourceMic.OuterReference,
                ArchetypeRef: sourceMic.ArchetypeReference,
                ObjectNameTableIndex: newNameTableIndex,
                ObjectNameNumeric: 0,
                ObjectFlags: sourceMic.ObjectFlags,
                ExportFlags: sourceMic.ExportFlags,
                NetObjects: sourceMic.NetObjects.ToArray(),
                PackageGuid: sourceMic.PackageGuid ?? new byte[16],
                PackageFlags: sourceMic.PackageFlags);

            byte[] repacked = header.CompressedChunks.Count > 0
                ? OmegaAssetStudio.UpkRepacker.RepackCompressedWithAddedExports(
                    originalBytes, header, existing, new[] { spec }, addedNames,
                    out _, out var addedExportIndices)
                : OmegaAssetStudio.UpkRepacker.RepackWithAddedExports(
                    originalBytes, header, existing, new[] { spec }, addedNames,
                    out _, out addedExportIndices);

            // Crash-safe write.
            OmegaAssetStudio.BackupManager.BackupFileHelper.CreateBackup(destUpkPath);
            string tmp = destUpkPath + ".omtmp";
            await File.WriteAllBytesAsync(tmp, repacked, ct).ConfigureAwait(false);
            File.Move(tmp, destUpkPath, overwrite: true);

            string newPath = string.IsNullOrWhiteSpace(newMicGroupPath)
                ? newMicName : $"{newMicGroupPath}.{newMicName}";
            return new(true, $"Cloned MIC '{newPath}' (export index {addedExportIndices[0]}). Reload UPK to see it.", newPath);
        }
        catch (Exception ex)
        {
            return new(false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // Cross-UPK clone: read source MIC body, rewrite its embedded refs to
    // dest-UPK indices via MicBodyRewriter, then call RepackWithAddedExports
    // on the destination with the queued added-names + added-imports + the
    // new export entry. End-to-end byte-level move.
    private async Task<IMicCloneService.CloneResult> CloneCrossUpkAsync(
        string sourceUpkPath, string parentExportPath,
        string destUpkPath, string newMicGroupPath, string newMicName,
        CancellationToken ct)
    {
        try
        {
            // Load both UPKs.
            byte[] sourceBytes = await File.ReadAllBytesAsync(sourceUpkPath, ct).ConfigureAwait(false);
            var sourceHeader = await _repo.LoadUpkFile(sourceUpkPath).ConfigureAwait(false);
            await sourceHeader.ReadHeaderAsync(null).ConfigureAwait(false);

            byte[] destBytes = await File.ReadAllBytesAsync(destUpkPath, ct).ConfigureAwait(false);
            var destHeader = await _repo.LoadUpkFile(destUpkPath).ConfigureAwait(false);
            await destHeader.ReadHeaderAsync(null).ConfigureAwait(false);

            // Locate the source MIC.
            var sourceMic = sourceHeader.ExportTable.FirstOrDefault(e =>
                string.Equals(e.GetPathName(), parentExportPath, StringComparison.OrdinalIgnoreCase));
            if (sourceMic is null) return new(false, $"source MIC export not found: {parentExportPath}");

            // Single shared translator: body walker AND the MIC's own outer
            // refs index into the same future-name / future-import slots.
            // Using separate translators would double-allocate slot indices.
            var translator = new CrossUpkReferenceTranslator(sourceHeader, destHeader);

            // Translate the MIC's own export-entry refs FIRST. These names /
            // imports go to the head of the queues, which matches what the
            // dest export entry will reference once written.
            int classRef = translator.TranslateObjectRef(sourceMic.ClassReference);
            int superRef = translator.TranslateObjectRef(sourceMic.SuperReference);
            int outerRef = translator.TranslateObjectRef(sourceMic.OuterReference);
            int archetypeRef = translator.TranslateObjectRef(sourceMic.ArchetypeReference);

            // Now rewrite the body using the same translator — names + imports
            // it queues append after whatever the outer-refs queued.
            byte[] sourceBody = sourceMic.UnrealObjectReader.GetBytes();
            var rewrite = MicBodyRewriter.Rewrite(sourceBody, sourceHeader, destHeader, translator);

            // Compute the new MIC's own name index — appended after all of
            // the body's queued names.
            var addedNames = new List<string>(translator.AddedNames);
            int existingNameIndex = -1;
            for (int i = 0; i < destHeader.NameTable.Count; i++)
                if (string.Equals(destHeader.NameTable[i].Name?.String, newMicName, StringComparison.OrdinalIgnoreCase))
                { existingNameIndex = i; break; }
            int newNameIndex;
            if (existingNameIndex >= 0) newNameIndex = existingNameIndex;
            else
            {
                newNameIndex = destHeader.NameTable.Count + addedNames.Count;
                addedNames.Add(newMicName);
            }
            var addedImports = new List<OmegaAssetStudio.UpkRepacker.NewImportSpec>(translator.AddedImports);

            // Existing-export buffers passed through unchanged.
            var existing = destHeader.ExportTable
                .Select(e => new OmegaAssetStudio.UpkRepacker.ExportBuffer(
                    e.UnrealObjectReader.GetBytes(),
                    Array.Empty<OmegaAssetStudio.UpkRepacker.BulkDataPatch>()))
                .ToList();

            // First: add the imports (and any extra names they need).
            byte[] withImports = OmegaAssetStudio.UpkRepacker.RepackWithAddedImports(
                destBytes, destHeader, existing, addedImports, addedNames,
                out _, out _);

            // Re-parse so we have an up-to-date header for the next step.
            await File.WriteAllBytesAsync(destUpkPath + ".omtmp_imports", withImports, ct).ConfigureAwait(false);
            var newDestHeader = await _repo.LoadUpkFile(destUpkPath + ".omtmp_imports").ConfigureAwait(false);
            await newDestHeader.ReadHeaderAsync(null).ConfigureAwait(false);

            // Now add the export. The body has already been rewritten to
            // reference indices that will resolve in the post-imports table.
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
                ObjectFlags: sourceMic.ObjectFlags,
                ExportFlags: sourceMic.ExportFlags,
                NetObjects: sourceMic.NetObjects.ToArray(),
                PackageGuid: sourceMic.PackageGuid ?? new byte[16],
                PackageFlags: sourceMic.PackageFlags);

            byte[] finalBytes = newDestHeader.CompressedChunks.Count > 0
                ? OmegaAssetStudio.UpkRepacker.RepackCompressedWithAddedExports(
                    withImports, newDestHeader, existingAfterImports, new[] { spec }, Array.Empty<string>(),
                    out _, out var newExportIndices)
                : OmegaAssetStudio.UpkRepacker.RepackWithAddedExports(
                    withImports, newDestHeader, existingAfterImports, new[] { spec }, Array.Empty<string>(),
                    out _, out newExportIndices);

            // Backup + atomic write.
            OmegaAssetStudio.BackupManager.BackupFileHelper.CreateBackup(destUpkPath);
            string tmp = destUpkPath + ".omtmp";
            await File.WriteAllBytesAsync(tmp, finalBytes, ct).ConfigureAwait(false);
            File.Move(tmp, destUpkPath, overwrite: true);
            try { File.Delete(destUpkPath + ".omtmp_imports"); } catch { }

            string newPath = string.IsNullOrWhiteSpace(newMicGroupPath)
                ? newMicName : $"{newMicGroupPath}.{newMicName}";
            return new(true,
                $"Cross-UPK cloned MIC '{newPath}' into {Path.GetFileName(destUpkPath)} " +
                $"(+{addedNames.Count} name(s), +{addedImports.Count} import(s)). Reload to see it.",
                newPath);
        }
        catch (Exception ex)
        {
            return new(false, $"cross-UPK clone failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
