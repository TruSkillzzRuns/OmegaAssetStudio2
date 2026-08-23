using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Numerics;
using System.Reflection;
using OmegaAssetStudio.BackupManager;
using OmegaAssetStudio.MaterialInspector;
using OmegaAssetStudio.WinUI.Modules.MaterialEditor.Core;
using OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialServices;
using UpkManager.Models.UpkFile.Objects;
using UpkManager.Models.UpkFile;
using UpkManager.Models.UpkFile.Engine.Mesh;
using UpkManager.Models.UpkFile.Engine.Material;
using UpkManager.Models.UpkFile.Tables;
using UpkManager.Models.UpkFile.Types;
using UpkManager.Repository;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor;

public sealed class MaterialEditorService
{
    private static readonly string[] CostumeSlotFieldTokens =
    [
        "CostumeSlotId",
        "CostumeSlot",
        "ArchetypeName",
        "Archetype",
        "MeshPath",
        "ExportPath",
        "SlotId",
        "SlotName"
    ];

    private readonly MaterialRepository repository;
    private readonly MaterialInspectorService inspectorService;
    private readonly UpkFileRepository upkRepository = new();
    private readonly ForeignTextureCatalogService foreignTextureCatalog = new();

    public event Action<string>? LogMessage;

    /// <summary>
    /// Cross-package Texture2D catalogue populated each time materials are
    /// loaded. Empty until the first LoadMaterialsFromUpkAsync call. Surfaces
    /// every texture referenced by any loaded material plus every Texture2D
    /// export discovered in foreign packages those materials reach into.
    /// </summary>
    public ForeignTextureCatalogService ForeignTextureCatalog => foreignTextureCatalog;

    public MaterialEditorService()
        : this(new MaterialRepository(), new MaterialInspectorService())
    {
    }

    public MaterialEditorService(MaterialRepository repository, MaterialInspectorService inspectorService)
    {
        this.repository = repository;
        this.inspectorService = inspectorService;
        // Subscribe ONCE here. Previously this was re-subscribed on every
        // LoadMaterialsFromUpkAsync call, so after N hero loads the catalogue's
        // log fired N times per message and N lambdas leaked.
        foreignTextureCatalog.Log += s => LogMessage?.Invoke(s);
        // Auto-invalidate this service's UPK header cache when Apply / Restore
        // signals files changed on disk — otherwise the next material load would
        // parse stale pre-write bytes and the user would have to relaunch.
        OmegaAssetStudio.Calligraphy.PowerVfxResolver.CacheCleared += () =>
        {
            try { upkRepository.ClearHeaderCache(); } catch { }
        };
    }

    public void Clear()
    {
        repository.Clear();
    }

    public async Task<IReadOnlyList<MaterialDefinition>> LoadMaterialsFromUpkAsync(string upkPath)
    {
        if (string.IsNullOrWhiteSpace(upkPath))
            throw new ArgumentException("UPK path is required.", nameof(upkPath));

        if (!File.Exists(upkPath))
            throw new FileNotFoundException("UPK file not found.", upkPath);

        LogMessage?.Invoke($"Loading materials from {Path.GetFileName(upkPath)}");

        IReadOnlyList<string> skeletalMeshExports = await inspectorService.GetSkeletalMeshExportsAsync(upkPath).ConfigureAwait(true);
        List<MaterialDefinition> materials = [];
        HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);

        foreach (string skeletalMeshExport in skeletalMeshExports)
        {
            LogMessage?.Invoke($"Inspecting {skeletalMeshExport}");
            MaterialInspectorResult result;
            try
            {
                result = await inspectorService.InspectAsync(upkPath, skeletalMeshExport).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"Warning: inspect failed for {skeletalMeshExport}: {ex.Message}");
                continue;
            }

            foreach (MaterialInspectorSectionInfo section in result.Sections)
            {
                foreach (MaterialInspectorMaterialNode node in section.MaterialChain)
                {
                    if (string.IsNullOrWhiteSpace(node.Path) || node.Path is "<missing>" or "<unresolved>")
                        continue;

                    if (!seenPaths.Add(node.Path))
                        continue;

                    MaterialDefinition material = CreateMaterialDefinition(upkPath, skeletalMeshExport, node);
                    repository.AddOrUpdate(material);
                    if (!ShouldHideFromBrowser(material))
                        materials.Add(material);
                }
            }
        }

        await LoadDirectMaterialExportsAsync(upkPath, materials, seenPaths).ConfigureAwait(true);

        // Fill the real MaterialId / BaseMaterialId for every material that came
        // from the mesh's MaterialChain (those nodes carry no export bytes). We
        // open the UPK once, map export path → entry, and byte-read each id.
        await FillMaterialIdsAsync(upkPath, materials).ConfigureAwait(true);

        // Populate the cross-package texture catalogue against this batch.
        // Lifted from MHE MaterialResolver (line 20730-20775) — gives the
        // editor visibility into textures that live in parent-MIC packages
        // not just the textures already bound to slots.
        foreignTextureCatalog.Clear();
        try { await foreignTextureCatalog.PopulateAsync(materials, upkPath).ConfigureAwait(true); }
        catch (Exception ex) { LogMessage?.Invoke($"Foreign texture catalogue population failed: {ex.Message}"); }

        if (materials.Count == 0)
            LogMessage?.Invoke($"No material definitions were resolved from {Path.GetFileName(upkPath)}.");

        return materials;
    }

    public async Task<IReadOnlyList<string>> GetSkeletalMeshExportsAsync(string upkPath)
    {
        if (string.IsNullOrWhiteSpace(upkPath))
            throw new ArgumentException("UPK path is required.", nameof(upkPath));

        if (!File.Exists(upkPath))
            throw new FileNotFoundException("UPK file not found.", upkPath);

        return await inspectorService.GetSkeletalMeshExportsAsync(upkPath).ConfigureAwait(true);
    }

    public sealed record TextureExportInfo(string DisplayName, string Path);

    /// <summary>
    /// Lists every Texture2D export in <paramref name="upkPath"/>. Used by the Material Editor
    /// to populate a "Repoint slot" picker — repointing a texture slot to an already-cooked
    /// texture in the same UPK is the safest texture swap because no new import or new pixel
    /// data is introduced; only the FObject reference int32 inside the MIC changes.
    /// </summary>
    public async Task<IReadOnlyList<TextureExportInfo>> GetTexture2DExportsAsync(string upkPath)
    {
        if (string.IsNullOrWhiteSpace(upkPath))
            throw new ArgumentException("UPK path is required.", nameof(upkPath));

        if (!File.Exists(upkPath))
            throw new FileNotFoundException("UPK file not found.", upkPath);

        UnrealHeader header = await upkRepository.LoadUpkFile(upkPath).ConfigureAwait(true);
        await header.ReadHeaderAsync(null).ConfigureAwait(true);

        List<TextureExportInfo> infos = [];
        foreach (UnrealExportTableEntry candidate in header.ExportTable)
        {
            string className = candidate.ClassReferenceNameIndex?.Name ?? string.Empty;
            if (!string.Equals(className, "Texture2D", StringComparison.OrdinalIgnoreCase))
                continue;

            string objectName = candidate.ObjectNameIndex?.Name ?? string.Empty;
            string pathName = candidate.GetPathName();
            if (string.IsNullOrWhiteSpace(pathName))
                continue;

            infos.Add(new TextureExportInfo(objectName, pathName));
        }

        infos.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return infos;
    }

    public async Task<int> GetSkeletalMeshLodCountAsync(string upkPath, string skeletalMeshExportPath)
    {
        if (string.IsNullOrWhiteSpace(upkPath))
            throw new ArgumentException("UPK path is required.", nameof(upkPath));

        if (!File.Exists(upkPath))
            throw new FileNotFoundException("UPK file not found.", upkPath);

        if (string.IsNullOrWhiteSpace(skeletalMeshExportPath))
            return 1;

        UnrealHeader header = await upkRepository.LoadUpkFile(upkPath).ConfigureAwait(true);
        await header.ReadHeaderAsync(null).ConfigureAwait(true);

        UnrealExportTableEntry export = header.ExportTable.FirstOrDefault(entry => string.Equals(entry.GetPathName(), skeletalMeshExportPath, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"SkeletalMesh export '{skeletalMeshExportPath}' was not found.");

        if (export.UnrealObject is null)
        {
            await header.ReadExportObjectAsync(export, null).ConfigureAwait(true);
            await export.ParseUnrealObject(false, false).ConfigureAwait(true);
        }

        if (export.UnrealObject is not IUnrealObject unrealObject || unrealObject.UObject is not USkeletalMesh skeletalMesh)
            throw new InvalidOperationException($"Export '{skeletalMeshExportPath}' is not a SkeletalMesh.");

        return Math.Max(1, skeletalMesh.LODModels?.Count ?? 1);
    }

    private async Task LoadDirectMaterialExportsAsync(string upkPath, List<MaterialDefinition> materials, HashSet<string> seenPaths)
    {
        UnrealHeader header = await upkRepository.LoadUpkFile(upkPath).ConfigureAwait(true);
        await header.ReadHeaderAsync(null).ConfigureAwait(true);

        foreach (UnrealExportTableEntry export in header.ExportTable)
        {
            string path = export.GetPathName();
            string className = export.ClassReferenceNameIndex?.Name ?? string.Empty;
            if (string.IsNullOrWhiteSpace(path) || seenPaths.Contains(path))
                continue;

            if (!className.Contains("Material", StringComparison.OrdinalIgnoreCase) &&
                !className.Contains("MaterialInstance", StringComparison.OrdinalIgnoreCase))
                continue;

            // Exclude raw MaterialExpression* graph nodes — they're internal
            // pieces of a material's shader graph, not renderable on their
            // own. Selecting one previously gave the user a magenta "missing
            // material" preview because expressions have no shader. We keep
            // UMaterial, UMaterialInstanceConstant, UMaterialFunction (which
            // ARE usable as material parents).
            if (className.StartsWith("MaterialExpression", StringComparison.OrdinalIgnoreCase))
                continue;

            if (export.UnrealObject is null)
            {
                try
                {
                    await header.ReadExportObjectAsync(export, null).ConfigureAwait(true);
                    await export.ParseUnrealObject(false, false).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke($"Warning: skipped material export '{path}' due to parse failure: {ex.Message}");
                    continue;
                }
            }

            if (export.UnrealObject is not IUnrealObject unrealObject)
                continue;

            object? resolved = unrealObject.UObject;
            if (resolved is null)
                continue;

            MaterialDefinition material = new()
            {
                Name = path,
                Path = path,
                Type = resolved.GetType().Name,
                SourceUpkPath = upkPath,
                SourceMeshExportPath = string.Empty
            };

            // Read the REAL MaterialId (+ MIC BaseMaterialId) directly from the
            // cooked export bytes. The object-model path returns empty GUIDs on
            // most cooked game MICs, which is why the UI showed all zeros; the
            // byte walker mirrors the reference editor and is robust.
            try
            {
                byte[] idBody = export.UnrealObjectReader?.GetBytes() ?? Array.Empty<byte>();
                // MITV uses the same MIC parameter layout — treat it as MIC for id reading too.
                bool isMicForId = className.Equals("materialinstanceconstant", StringComparison.OrdinalIgnoreCase)
                              || className.Equals("materialinstancetimevarying", StringComparison.OrdinalIgnoreCase);
                var (mid, bid) = MaterialIdReader.Read(idBody, header, isMicForId);
                if (mid is not null) material.MaterialId = mid.Value;
                if (bid is not null) material.ParentMaterialId = bid.Value;
            }
            catch { /* ids stay empty */ }

            if (resolved is UMaterialInstanceConstant instanceConstant)
            {
                material.ParentMaterialPath = instanceConstant.Parent?.GetPathName() ?? string.Empty;
                // Fallback for the parent FGuid (Variants subsystem) if the byte
                // walker didn't fill it.
                if (material.ParentMaterialId == Guid.Empty
                    && instanceConstant.StaticParameters is { Length: > 0 } sp && sp[0]?.BaseMaterialId is not null)
                    material.ParentMaterialId = sp[0].BaseMaterialId.ToSystemGuid();
                material.TextureSlots = (instanceConstant.TextureParameterValues ?? [])
                    .Select(parameter => new MaterialTextureSlot
                    {
                        SlotName = parameter.ParameterName?.Name ?? "<unnamed>",
                        TextureName = parameter.ParameterValue?.GetPathName() ?? "<null>",
                        TexturePath = parameter.ParameterValue?.GetPathName() ?? "<null>",
                        IsOverride = true
                    })
                    .ToList();

                material.ScalarParameters = (instanceConstant.ScalarParameterValues ?? [])
                    .Select(parameter => new MaterialParameter
                    {
                        Name = parameter.ParameterName?.Name ?? "<unnamed>",
                        Category = "Scalar",
                        ScalarValue = parameter.ParameterValue,
                        DefaultScalarValue = parameter.ParameterValue
                    })
                    .ToList();

                material.VectorParameters = (instanceConstant.VectorParameterValues ?? [])
                    .Select(parameter => new MaterialParameter
                    {
                        Name = parameter.ParameterName?.Name ?? "<unnamed>",
                        Category = "Vector",
                        VectorValue = new Vector4(parameter.ParameterValue.R, parameter.ParameterValue.G, parameter.ParameterValue.B, parameter.ParameterValue.A),
                        DefaultVectorValue = new Vector4(parameter.ParameterValue.R, parameter.ParameterValue.G, parameter.ParameterValue.B, parameter.ParameterValue.A)
                    })
                    .ToList();
            }

            repository.AddOrUpdate(material);
            if (!ShouldHideFromBrowser(material))
                materials.Add(material);
            seenPaths.Add(path);
        }
    }

    public async Task SyncCostumeSlot(string referenceUpkPath, string targetUpkPath)
    {
        if (string.IsNullOrWhiteSpace(referenceUpkPath))
            throw new ArgumentException("Reference UPK path is required.", nameof(referenceUpkPath));
        if (string.IsNullOrWhiteSpace(targetUpkPath))
            throw new ArgumentException("Target UPK path is required.", nameof(targetUpkPath));
        if (!File.Exists(referenceUpkPath))
            throw new FileNotFoundException("Reference UPK file not found.", referenceUpkPath);
        if (!File.Exists(targetUpkPath))
            throw new FileNotFoundException("Target UPK file not found.", targetUpkPath);

        LogMessage?.Invoke($"Costume Slot Sync: reference={Path.GetFileName(referenceUpkPath)} target={Path.GetFileName(targetUpkPath)}");

        UnrealHeader referenceHeader = await upkRepository.LoadUpkFile(referenceUpkPath).ConfigureAwait(true);
        await referenceHeader.ReadHeaderAsync(null).ConfigureAwait(true);
        UnrealHeader targetHeader = await upkRepository.LoadUpkFile(targetUpkPath).ConfigureAwait(true);
        await targetHeader.ReadHeaderAsync(null).ConfigureAwait(true);

        List<SlotFieldValue> sourceFields = await ExtractSlotFieldValuesAsync(referenceHeader).ConfigureAwait(true);
        if (sourceFields.Count == 0)
        {
            LogMessage?.Invoke("Warning: no slot-binding fields found in reference UPK.");
            throw new InvalidOperationException("No slot-binding fields found in reference UPK.");
        }

        int applied = await ApplySlotFieldValuesAsync(targetHeader, sourceFields).ConfigureAwait(true);
        if (applied == 0)
            LogMessage?.Invoke("Warning: no matching slot-binding fields were applied to target UPK.");

        string backupPath = BackupFileHelper.CreateBackup(targetUpkPath);
        string tempPath = Path.Combine(
            Path.GetDirectoryName(targetUpkPath) ?? string.Empty,
            $"{Path.GetFileNameWithoutExtension(targetUpkPath)}.costumesync{Path.GetExtension(targetUpkPath)}");

        if (File.Exists(tempPath))
            File.Delete(tempPath);

        // STILL ON SaveUpkFile (intentional). Why this CAN'T move to UpkRepacker yet:
        //   - ApplySlotFieldValuesAsync above mutates parsed UObjects in place. To
        //     persist those mutations without losing them we have to re-serialize the
        //     changed exports via WriteObjectBuffer, which is the path the
        //     CharacterSwap null-swap repro proved can be byte-lossy for some classes
        //     (the engine then rejects the resulting file).
        //   - Clean fix is a body-byte-patcher pass (see MaterialBytePatcher for the
        //     in-place model) — locate each changed slot field at its known offset,
        //     overwrite just those bytes, then splice the patched bodies in via
        //     UpkRepacker. That helper does not exist for slot-binding fields yet.
        //   - Until then SaveUpkFile is the only path that produces a consistent file
        //     here. If users start reporting corrupted Costume Slot Sync output,
        //     build the byte-patcher and replace this.
        await upkRepository.SaveUpkFile(targetHeader, tempPath, message => LogMessage?.Invoke(message)).ConfigureAwait(true);
        File.Copy(tempPath, targetUpkPath, true);
        File.Delete(tempPath);

        LogMessage?.Invoke($"Costume Slot Sync complete. Applied fields: {applied}. Backup: {backupPath}");
    }

    private async Task<List<SlotFieldValue>> ExtractSlotFieldValuesAsync(UnrealHeader header)
    {
        List<SlotFieldValue> values = [];

        foreach (UnrealExportTableEntry export in header.ExportTable)
        {
            object? exportObject = await ResolveExportObjectAsync(header, export).ConfigureAwait(true);
            if (exportObject is null)
                continue;

            foreach (PropertyInfo property in exportObject.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || !property.CanWrite)
                    continue;

                if (!IsSlotField(property.Name))
                    continue;

                object? value = property.GetValue(exportObject);
                if (value is null)
                    continue;

                string normalized = NormalizeValue(value);
                if (string.IsNullOrWhiteSpace(normalized))
                    continue;

                if (values.Any(existing => string.Equals(existing.FieldName, property.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;

                values.Add(new SlotFieldValue(property.Name, value, property.PropertyType));
                LogMessage?.Invoke($"Reference field captured: {property.Name} = {normalized}");
            }
        }

        return values;
    }

    private async Task<int> ApplySlotFieldValuesAsync(UnrealHeader targetHeader, IReadOnlyList<SlotFieldValue> sourceFields)
    {
        int appliedCount = 0;
        foreach (UnrealExportTableEntry export in targetHeader.ExportTable)
        {
            object? exportObject = await ResolveExportObjectAsync(targetHeader, export).ConfigureAwait(true);
            if (exportObject is null)
                continue;

            Type objectType = exportObject.GetType();
            foreach (SlotFieldValue sourceField in sourceFields)
            {
                PropertyInfo? property = objectType.GetProperty(sourceField.FieldName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (property is null || !property.CanWrite || !property.CanRead)
                    continue;

                object? converted = TryConvertValue(sourceField.Value, property.PropertyType);
                if (converted is null)
                {
                    LogMessage?.Invoke($"Warning: could not convert {sourceField.FieldName} for {objectType.Name}.");
                    continue;
                }

                object? current = property.GetValue(exportObject);
                if (ValuesEqual(current, converted))
                    continue;

                property.SetValue(exportObject, converted);
                appliedCount++;
                LogMessage?.Invoke($"Applied field: {sourceField.FieldName} -> {NormalizeValue(converted)} ({export.GetPathName()})");
            }
        }

        return appliedCount;
    }

    private static async Task<object?> ResolveExportObjectAsync(UnrealHeader header, UnrealExportTableEntry export)
    {
        try
        {
            if (export.UnrealObject is null)
            {
                await header.ReadExportObjectAsync(export, null).ConfigureAwait(true);
                await export.ParseUnrealObject(false, false).ConfigureAwait(true);
            }

            return (export.UnrealObject as IUnrealObject)?.UObject;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSlotField(string propertyName)
    {
        return CostumeSlotFieldTokens.Any(token => propertyName.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ValuesEqual(object? left, object? right)
    {
        if (left is null && right is null)
            return true;
        if (left is null || right is null)
            return false;
        return string.Equals(NormalizeValue(left), NormalizeValue(right), StringComparison.Ordinal);
    }

    private static object? TryConvertValue(object value, Type destinationType)
    {
        if (destinationType.IsInstanceOfType(value))
            return value;

        try
        {
            if (destinationType == typeof(string))
                return NormalizeValue(value);

            if (destinationType.IsEnum)
                return Enum.Parse(destinationType, NormalizeValue(value), true);

            return Convert.ChangeType(value, destinationType);
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeValue(object value)
    {
        if (value is null)
            return string.Empty;

        if (value is FName name)
            return name.Name ?? string.Empty;

        MethodInfo? getPathName = value.GetType().GetMethod("GetPathName", BindingFlags.Public | BindingFlags.Instance);
        if (getPathName is not null && getPathName.ReturnType == typeof(string))
            return getPathName.Invoke(value, null) as string ?? string.Empty;

        return value.ToString() ?? string.Empty;
    }

    private readonly record struct SlotFieldValue(string FieldName, object Value, Type ValueType);

    public Task SaveMaterialAsync(MaterialDefinition material)
    {
        if (material is null)
            throw new ArgumentNullException(nameof(material));

        // Take a rolling auto-snapshot before mutating the UPK. Separate from
        // BackupFileHelper's one-shot pristine .bak; this is a scrubable
        // per-file edit history (capped at 10 newest snapshots per UPK).
        try
        {
            if (!string.IsNullOrWhiteSpace(material.SourceUpkPath))
                OmegaAssetStudio.WinUI.Services.EditHistoryService.Snapshot(
                    material.SourceUpkPath, "MaterialEditor");
        }
        catch { /* snapshot is best-effort */ }

        return SaveMaterialInternalAsync(material);
    }

    public async Task<string> ApplySafeIndexSwapAsync(string upkPath, string nativeMaterialPath, MaterialDefinition moddedMaterial)
    {
        if (string.IsNullOrWhiteSpace(upkPath))
            throw new ArgumentException("UPK path is required.", nameof(upkPath));
        if (!File.Exists(upkPath))
            throw new FileNotFoundException("UPK file not found.", upkPath);
        if (string.IsNullOrWhiteSpace(nativeMaterialPath))
            throw new ArgumentException("Native material path is required.", nameof(nativeMaterialPath));
        ArgumentNullException.ThrowIfNull(moddedMaterial);

        UnrealHeader header = await upkRepository.LoadUpkFile(upkPath).ConfigureAwait(true);
        await header.ReadHeaderAsync(null).ConfigureAwait(true);

        UnrealExportTableEntry export = header.ExportTable.FirstOrDefault(entry => string.Equals(entry.GetPathName(), nativeMaterialPath, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Native material export '{nativeMaterialPath}' was not found.");

        if (export.UnrealObject is null)
        {
            await header.ReadExportObjectAsync(export, null).ConfigureAwait(true);
            await export.ParseUnrealObject(false, false).ConfigureAwait(true);
        }

        if (export.UnrealObject is not IUnrealObject materialObject || materialObject.UObject is not UMaterialInstanceConstant nativeMic)
            throw new InvalidOperationException($"Export '{nativeMaterialPath}' is not a MaterialInstanceConstant.");

        MaterialUpkWriter.PatchSet patches = BuildPatchSet(nativeMic, moddedMaterial);
        if (patches.IsEmpty)
            return string.Empty;

        MaterialUpkWriter writer = new();
        writer.LogMessage += message => LogMessage?.Invoke(message);
        MaterialUpkWriter.PatchResult result = await writer.ApplyAsync(upkPath, nativeMaterialPath, patches).ConfigureAwait(true);
        return result.BackupPath;
    }

    public async Task<MaterialValidationResult> ValidateMaterialRoundTripAsync(MaterialDefinition material)
    {
        if (material is null)
            throw new ArgumentNullException(nameof(material));

        if (string.IsNullOrWhiteSpace(material.SourceUpkPath))
            return new MaterialValidationResult
            {
                IsValid = false,
                MaterialName = material.Name,
                MaterialPath = material.Path,
                Message = "Material has no source UPK path."
            };

        if (!File.Exists(material.SourceUpkPath))
            return new MaterialValidationResult
            {
                IsValid = false,
                MaterialName = material.Name,
                MaterialPath = material.Path,
                Message = "Source UPK file not found."
            };

        try
        {
            UnrealHeader header = await upkRepository.LoadUpkFile(material.SourceUpkPath).ConfigureAwait(true);
            await header.ReadHeaderAsync(null).ConfigureAwait(true);

            UnrealExportTableEntry export = header.ExportTable.FirstOrDefault(entry => string.Equals(entry.GetPathName(), material.Path, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Material export '{material.Path}' was not found.");

            if (export.UnrealObject is null)
            {
                await header.ReadExportObjectAsync(export, null).ConfigureAwait(true);
                await export.ParseUnrealObject(false, false).ConfigureAwait(true);
            }

            if (export.UnrealObject is not IUnrealObject materialObject || materialObject.UObject is not UMaterialInstanceConstant)
                throw new InvalidOperationException($"Export '{material.Path}' did not reopen as a MaterialInstanceConstant.");

            return new MaterialValidationResult
            {
                IsValid = true,
                MaterialName = material.Name,
                MaterialPath = material.Path,
                TextureSlotCount = material.TextureSlots.Count,
                ScalarParameterCount = material.ScalarParameters.Count,
                VectorParameterCount = material.VectorParameters.Count,
                Message = "Round-trip validation succeeded."
            };
        }
        catch (Exception ex)
        {
            return new MaterialValidationResult
            {
                IsValid = false,
                MaterialName = material.Name,
                MaterialPath = material.Path,
                Message = ex.Message
            };
        }
    }

    public MaterialValidationResult ValidateMaterialDefinition(MaterialDefinition material)
    {
        if (material is null)
            throw new ArgumentNullException(nameof(material));

        List<string> issues = [];
        List<string> notes = [];

        if (string.IsNullOrWhiteSpace(material.Name))
            issues.Add("Material name is missing.");

        if (string.IsNullOrWhiteSpace(material.Path))
            issues.Add("Material path is missing.");

        if (string.IsNullOrWhiteSpace(material.SourceUpkPath))
            issues.Add("Source UPK path is missing.");
        else if (!File.Exists(material.SourceUpkPath))
            issues.Add("Source UPK file not found.");

        if (string.IsNullOrWhiteSpace(material.SourceMeshExportPath))
            notes.Add("Material is not linked to a preview skeletal mesh export.");

        if (material.TextureSlots.Count == 0)
            notes.Add("Material has no texture slots.");

        if (material.ScalarParameters.Count == 0)
            notes.Add("Material has no scalar parameters.");

        if (material.VectorParameters.Count == 0)
            notes.Add("Material has no vector parameters.");

        HashSet<string> textureNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (MaterialTextureSlot slot in material.TextureSlots)
        {
            if (string.IsNullOrWhiteSpace(slot.SlotName))
                issues.Add("A texture slot is missing a slot name.");
            else if (!textureNames.Add(slot.SlotName.Trim()))
                notes.Add($"Duplicate texture slot name detected: {slot.SlotName}");

            if (string.IsNullOrWhiteSpace(slot.TextureName) || string.IsNullOrWhiteSpace(slot.TexturePath))
                notes.Add($"Texture slot '{slot.SlotName}' is not bound to a texture export.");
        }

        HashSet<string> scalarNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (MaterialParameter parameter in material.ScalarParameters)
        {
            if (string.IsNullOrWhiteSpace(parameter.Name))
                issues.Add("A scalar parameter is missing a name.");
            else if (!scalarNames.Add(parameter.Name.Trim()))
                notes.Add($"Duplicate scalar parameter detected: {parameter.Name}");
        }

        HashSet<string> vectorNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (MaterialParameter parameter in material.VectorParameters)
        {
            if (string.IsNullOrWhiteSpace(parameter.Name))
                issues.Add("A vector parameter is missing a name.");
            else if (!vectorNames.Add(parameter.Name.Trim()))
                notes.Add($"Duplicate vector parameter detected: {parameter.Name}");
        }

        return new MaterialValidationResult
        {
            IsValid = issues.Count == 0,
            MaterialName = material.Name,
            MaterialPath = material.Path,
            TextureSlotCount = material.TextureSlots.Count,
            ScalarParameterCount = material.ScalarParameters.Count,
            VectorParameterCount = material.VectorParameters.Count,
            Message = issues.Count == 0
                ? notes.Count == 0
                    ? "Material definition is valid."
                    : string.Join(" | ", notes)
                : string.Join(" | ", issues.Concat(notes))
        };
    }

    private async Task SaveMaterialInternalAsync(MaterialDefinition material)
    {
        if (string.IsNullOrWhiteSpace(material.SourceUpkPath))
            throw new InvalidOperationException($"Material '{material.Name}' does not have a source UPK path.");

        if (!File.Exists(material.SourceUpkPath))
            throw new FileNotFoundException("Source UPK file not found.", material.SourceUpkPath);

        MaterialValidationResult validation = ValidateMaterialDefinition(material);
        if (!validation.IsValid)
            throw new InvalidOperationException($"Material validation failed: {validation.Message}");

        // Compare against the loaded MIC to only patch parameters the user actually changed.
        UnrealHeader header = await upkRepository.LoadUpkFile(material.SourceUpkPath).ConfigureAwait(true);
        await header.ReadHeaderAsync(null).ConfigureAwait(true);

        UnrealExportTableEntry export = header.ExportTable.FirstOrDefault(entry => string.Equals(entry.GetPathName(), material.Path, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Material export '{material.Path}' was not found in {Path.GetFileName(material.SourceUpkPath)}.");

        if (export.UnrealObject is null)
        {
            await header.ReadExportObjectAsync(export, null).ConfigureAwait(true);
            await export.ParseUnrealObject(false, false).ConfigureAwait(true);
        }

        if (export.UnrealObject is not IUnrealObject materialObject || materialObject.UObject is not UMaterialInstanceConstant mic)
            throw new InvalidOperationException($"Export '{material.Path}' is not a MaterialInstanceConstant.");

        MaterialUpkWriter.PatchSet patches = BuildPatchSet(mic, material);

        if (patches.IsEmpty)
        {
            LogMessage?.Invoke($"No parameter changes detected for '{material.Name}'; nothing to save.");
            return;
        }

        LogMessage?.Invoke($"Saving material '{material.Name}' to {Path.GetFileName(material.SourceUpkPath)}");

        MaterialUpkWriter writer = new();
        writer.LogMessage += message => LogMessage?.Invoke(message);
        MaterialUpkWriter.PatchResult result = await writer.ApplyAsync(material.SourceUpkPath, material.Path, patches).ConfigureAwait(true);

        foreach (string skipped in result.Skipped)
            LogMessage?.Invoke($"Warning: {skipped}");

        repository.AddOrUpdate(material);
        LogMessage?.Invoke($"Material '{material.Name}' saved: {result.ScalarsWritten} scalar / {result.VectorsWritten} vector / {result.TexturesWritten} texture patch(es).");
    }

    private static MaterialUpkWriter.PatchSet BuildPatchSet(UMaterialInstanceConstant mic, MaterialDefinition material)
    {
        MaterialUpkWriter.PatchSet patches = new();

        // Scalars — only include parameters whose value the user changed away from what the MIC currently holds on disk.
        Dictionary<string, float> micScalars = new(StringComparer.OrdinalIgnoreCase);
        foreach (FScalarParameterValue parameter in mic.ScalarParameterValues ?? [])
        {
            string name = parameter.ParameterName?.Name ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(name))
                micScalars[name] = parameter.ParameterValue;
        }

        foreach (MaterialParameter parameter in material.ScalarParameters)
        {
            string name = parameter.Name?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                continue;
            float desired = parameter.ScalarValue ?? parameter.DefaultScalarValue ?? 0f;
            if (!micScalars.TryGetValue(name, out float current) || !FloatEquals(current, desired))
                patches.Scalars[name] = desired;
        }

        // Vectors
        Dictionary<string, Vector4> micVectors = new(StringComparer.OrdinalIgnoreCase);
        foreach (FVectorParameterValue parameter in mic.VectorParameterValues ?? [])
        {
            string name = parameter.ParameterName?.Name ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(name) && parameter.ParameterValue is not null)
                micVectors[name] = new Vector4(parameter.ParameterValue.R, parameter.ParameterValue.G, parameter.ParameterValue.B, parameter.ParameterValue.A);
        }

        foreach (MaterialParameter parameter in material.VectorParameters)
        {
            string name = parameter.Name?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                continue;
            Vector4 desired = parameter.VectorValue ?? parameter.DefaultVectorValue ?? Vector4.Zero;
            if (!micVectors.TryGetValue(name, out Vector4 current) || !VectorEquals(current, desired))
                patches.Vectors[name] = desired;
        }

        // Texture references
        Dictionary<string, string> micTextures = new(StringComparer.OrdinalIgnoreCase);
        foreach (FTextureParameterValue parameter in mic.TextureParameterValues ?? [])
        {
            string name = parameter.ParameterName?.Name ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                continue;
            micTextures[name] = parameter.ParameterValue?.GetPathName() ?? string.Empty;
        }

        foreach (MaterialTextureSlot slot in material.TextureSlots)
        {
            string name = slot.SlotName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                continue;
            string desired = slot.TexturePath?.Trim() ?? string.Empty;
            // "<null>" / "<missing>" / "<unresolved>" sentinels come from the loader for null refs.
            if (desired.StartsWith('<') && desired.EndsWith('>'))
                desired = string.Empty;
            if (!micTextures.TryGetValue(name, out string? current) ||
                !string.Equals(current ?? string.Empty, desired, StringComparison.OrdinalIgnoreCase))
            {
                patches.Textures[name] = desired;
            }
        }

        return patches;
    }

    private static bool FloatEquals(float a, float b) => MathF.Abs(a - b) < 1e-6f;

    private static bool VectorEquals(Vector4 a, Vector4 b) =>
        FloatEquals(a.X, b.X) && FloatEquals(a.Y, b.Y) && FloatEquals(a.Z, b.Z) && FloatEquals(a.W, b.W);

    // Open the UPK once and byte-read the real MaterialId (+ MIC BaseMaterialId)
    // for every local material whose id is still empty. This is the path that
    // actually fills the IDs the UI shows, because the browser materials come
    // from the mesh MaterialChain (which has no export bytes), not the direct
    // export loop.
    private async Task FillMaterialIdsAsync(string upkPath, List<MaterialDefinition> materials)
    {
        var pending = materials
            .Where(m => string.Equals(m.SourceUpkPath, upkPath, StringComparison.OrdinalIgnoreCase) && m.MaterialId == Guid.Empty)
            .ToList();
        if (pending.Count == 0) return;

        UnrealHeader header;
        try
        {
            header = await upkRepository.LoadUpkFile(upkPath).ConfigureAwait(true);
            await header.ReadHeaderAsync(null).ConfigureAwait(true);
        }
        catch { return; }

        var byPath = new Dictionary<string, UnrealExportTableEntry>(StringComparer.OrdinalIgnoreCase);
        var byLeaf = new Dictionary<string, UnrealExportTableEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var ex in header.ExportTable)
        {
            string cls = ex.ClassReferenceNameIndex?.Name ?? string.Empty;
            if (!cls.Equals("Material", StringComparison.OrdinalIgnoreCase)
                && !cls.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase)) continue;
            string p = ex.GetPathName();
            if (!string.IsNullOrEmpty(p)) byPath[p] = ex;
            string leaf = ex.ObjectNameIndex?.Name ?? string.Empty;
            if (!string.IsNullOrEmpty(leaf)) byLeaf[leaf] = ex;
        }

        foreach (var mat in pending)
        {
            UnrealExportTableEntry? ex = null;
            if (!byPath.TryGetValue(mat.Path, out ex))
            {
                int dot = mat.Path.LastIndexOf('.');
                string leaf = dot >= 0 && dot + 1 < mat.Path.Length ? mat.Path[(dot + 1)..] : mat.Path;
                byLeaf.TryGetValue(leaf, out ex);
            }
            if (ex is null) continue;
            try
            {
                if (ex.UnrealObjectReader is null)
                    await header.ReadExportObjectAsync(ex, null).ConfigureAwait(true);
                byte[] body = ex.UnrealObjectReader?.GetBytes() ?? Array.Empty<byte>();
                bool isMic = (ex.ClassReferenceNameIndex?.Name ?? string.Empty)
                    .Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase);
                var (mid, bid) = MaterialIdReader.Read(body, header, isMic);
                if (mid is not null) mat.MaterialId = mid.Value;
                if (bid is not null) mat.ParentMaterialId = bid.Value;
            }
            catch { /* id stays empty */ }
        }
    }

    private static MaterialDefinition CreateMaterialDefinition(string upkPath, string skeletalMeshExportPath, MaterialInspectorMaterialNode node)
    {
        MaterialDefinition material = new()
        {
            Name = node.Path,
            Path = node.Path,
            Type = node.TypeName,
            SourceUpkPath = upkPath,
            SourceMeshExportPath = skeletalMeshExportPath,
            IsNative = true,
            IsModded = false,
            OriginalPath = node.Path
        };

        material.TextureSlots = node.TextureParameters.Select(parameter => new MaterialTextureSlot
        {
            SlotName = parameter.Name,
            TextureName = parameter.TexturePath,
            TexturePath = parameter.TexturePath,
            IsOverride = !string.IsNullOrWhiteSpace(parameter.TexturePath)
        }).ToList();

        // Phase: base UMaterial whose Expression graph was stripped at cook
        // time still parks the actual textures in
        // FMaterial.UniformExpressionTextures. When the MIC chain didn't
        // surface any TextureParameters, fall back to these so the editor
        // shows what the cooked shader actually binds. Each slot's SlotName
        // is the name-suffix classification (Diffuse/Normal/Specular/...).
        if (material.TextureSlots.Count == 0 && node.UniformExpressionTextures.Count > 0)
        {
            material.TextureSlots = node.UniformExpressionTextures.Select(parameter => new MaterialTextureSlot
            {
                SlotName = parameter.Name,
                TextureName = parameter.TexturePath,
                TexturePath = parameter.TexturePath,
                IsOverride = !string.IsNullOrWhiteSpace(parameter.TexturePath)
            }).ToList();
        }

        material.ScalarParameters = node.ScalarParameters.Select(parameter => new MaterialParameter
        {
            Name = parameter.Name,
            Category = "Scalar",
            ScalarValue = parameter.Value,
            DefaultScalarValue = parameter.Value
        }).ToList();

        material.VectorParameters = node.VectorParameters.Select(parameter => new MaterialParameter
        {
            Name = parameter.Name,
            Category = "Vector",
            VectorValue = new System.Numerics.Vector4(parameter.Value.X, parameter.Value.Y, parameter.Value.Z, 1.0f),
            DefaultVectorValue = new System.Numerics.Vector4(parameter.Value.X, parameter.Value.Y, parameter.Value.Z, 1.0f)
        }).ToList();

        return material;
    }

    private static bool ShouldHideFromBrowser(MaterialDefinition material)
    {
        string value = $"{material.Name} {material.Path}".Trim().ToLowerInvariant();
        return value.Contains("chbasematerials")
            || value.Contains("chvfxmaterials")
            || value.Contains("vfx_shared_materials")
            || value.Contains("engine_materialfunctions02");
    }
}
