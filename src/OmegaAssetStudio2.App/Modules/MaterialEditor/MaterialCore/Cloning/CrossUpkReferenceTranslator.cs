using System.Buffers.Binary;
using UpkManager.Models.UpkFile;
using UpkManager.Models.UpkFile.Tables;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.Cloning;

// Name + object reference translator for cross-UPK clones. Maintains two
// dictionaries — source-index → dest-index — and lazily queues new names /
// imports as it encounters refs the destination doesn't yet have. Caller
// (MicBodyRewriter) consumes the queues to extend the destination UPK via
// UpkRepacker.RepackWithAddedNames/Imports.
public sealed class CrossUpkReferenceTranslator
{
    private readonly UnrealHeader _source;
    private readonly UnrealHeader _dest;
    private readonly Dictionary<int, int> _nameMap = new();           // sourceNameIndex → destNameIndex
    private readonly Dictionary<int, int> _objMap = new();             // sourceObjRef → destObjRef
    private readonly Dictionary<string, int> _destNameByValue = new(StringComparer.OrdinalIgnoreCase);
    public List<string> AddedNames { get; } = new();
    public List<OmegaAssetStudio.UpkRepacker.NewImportSpec> AddedImports { get; } = new();

    // Caller-supplied overrides: "if you see source ref X, return dest ref Y
    // instead of materializing an Import". Used by Import Full Material when
    // it copies a Texture2D source export into the dest UPK as a local export
    // and then needs the importing Material body to point at the new export
    // rather than at an Import back to the donor UPK.
    public void RegisterRefOverride(int sourceRef, int destRef)
        => _objMap[sourceRef] = destRef;

    public CrossUpkReferenceTranslator(UnrealHeader source, UnrealHeader dest)
    {
        _source = source;
        _dest = dest;
        // Pre-index destination NameTable for O(1) name→index lookup.
        for (int i = 0; i < _dest.NameTable.Count; i++)
        {
            string? n = _dest.NameTable[i].Name?.String;
            if (!string.IsNullOrEmpty(n)) _destNameByValue[n] = i;
        }
    }

    // Translate a source NameTable index to a dest NameTable index. If the
    // source name isn't in the dest table, queues it via AddedNames and
    // returns the future index (= dest.NameTable.Count + AddedNames.Count - 1).
    public int TranslateName(int sourceNameIndex)
    {
        if (sourceNameIndex < 0 || sourceNameIndex >= _source.NameTable.Count) return 0;
        if (_nameMap.TryGetValue(sourceNameIndex, out int cached)) return cached;
        string? name = _source.NameTable[sourceNameIndex].Name?.String;
        if (string.IsNullOrEmpty(name))
        {
            _nameMap[sourceNameIndex] = 0;
            return 0;
        }
        if (_destNameByValue.TryGetValue(name, out int existing))
        {
            _nameMap[sourceNameIndex] = existing;
            return existing;
        }
        int newIndex = _dest.NameTable.Count + AddedNames.Count;
        AddedNames.Add(name);
        _destNameByValue[name] = newIndex;
        _nameMap[sourceNameIndex] = newIndex;
        return newIndex;
    }

    // Translate a UE3 object reference (positive=export, negative=import,
    // 0=null) from source-table coordinates to dest-table coordinates. For
    // exports referenced from the SOURCE, we create an Import entry in the
    // destination pointing back at the source's package — the cloned MIC's
    // references continue to resolve against the original UPK's assets.
    public int TranslateObjectRef(int sourceRef)
    {
        if (sourceRef == 0) return 0;
        if (_objMap.TryGetValue(sourceRef, out int cached)) return cached;

        if (sourceRef > 0)
        {
            // Source export — create an Import in dest that points at the
            // source UPK's package + the export's outer chain.
            int sourceIdx = sourceRef - 1;
            if (sourceIdx >= _source.ExportTable.Count) { _objMap[sourceRef] = 0; return 0; }
            var srcExport = _source.ExportTable[sourceIdx];
            int newRef = MaterializeImportForSourceExport(srcExport);
            _objMap[sourceRef] = newRef;
            return newRef;
        }
        else
        {
            // Source import — create a matching Import in dest. Same outer
            // chain (recursively translated).
            int sourceIdx = -sourceRef - 1;
            if (sourceIdx >= _source.ImportTable.Count) { _objMap[sourceRef] = 0; return 0; }
            var srcImport = _source.ImportTable[sourceIdx];
            int newRef = MaterializeImportForSourceImport(srcImport);
            _objMap[sourceRef] = newRef;
            return newRef;
        }
    }

    // Look up (or create) a destination Import entry that points at a
    // source-UPK export. The Import's PackageName is the source UPK's
    // package name (typically the filename stem); ClassName mirrors the
    // export's class; ObjectName mirrors the export's name. Outer is the
    // recursively-translated outer chain.
    private int MaterializeImportForSourceExport(UnrealExportTableEntry srcExport)
    {
        // Find or add the package-name FName.
        string sourcePackageName = Path.GetFileNameWithoutExtension(_source.FullFilename);
        int packageNameIdx = EnsureNameAdded(sourcePackageName);

        // Find or add the class name.
        string className = srcExport.ClassReferenceNameIndex?.Name ?? "Object";
        int classNameIdx = EnsureNameAdded(className);

        // Outer: translate the export's outer ref into a dest ref.
        int outerRef = TranslateObjectRef(srcExport.OuterReference);

        // Object name FName.
        string objectName = srcExport.ObjectNameIndex?.Name ?? "None";
        int objectNameIdx = EnsureNameAdded(objectName);

        // Already-added check: scan AddedImports for a matching (pkg,cls,outer,obj) tuple.
        for (int i = 0; i < AddedImports.Count; i++)
        {
            var x = AddedImports[i];
            if (x.PackageNameTableIndex == packageNameIdx &&
                x.ClassNameTableIndex == classNameIdx &&
                x.OuterRef == outerRef &&
                x.ObjectNameTableIndex == objectNameIdx)
                return -(_dest.ImportTable.Count + i + 1);
        }
        // Also scan EXISTING dest imports for the same identity.
        for (int i = 0; i < _dest.ImportTable.Count; i++)
        {
            var d = _dest.ImportTable[i];
            bool sameObjName = string.Equals(d.ObjectNameIndex?.Name, objectName, StringComparison.OrdinalIgnoreCase);
            bool sameClassName = string.Equals(d.ClassNameIndex?.Name, className, StringComparison.OrdinalIgnoreCase);
            if (sameObjName && sameClassName) return -(i + 1);
        }
        // New import.
        AddedImports.Add(new OmegaAssetStudio.UpkRepacker.NewImportSpec(
            PackageNameTableIndex: packageNameIdx,
            PackageNameNumeric: 0,
            ClassNameTableIndex: classNameIdx,
            ClassNameNumeric: 0,
            OuterRef: outerRef,
            ObjectNameTableIndex: objectNameIdx,
            ObjectNameNumeric: 0));
        return -(_dest.ImportTable.Count + AddedImports.Count);
    }

    private int MaterializeImportForSourceImport(UnrealImportTableEntry srcImport)
    {
        string pkg = srcImport.PackageNameIndex?.Name ?? "Core";
        string cls = srcImport.ClassNameIndex?.Name ?? "Object";
        string obj = srcImport.ObjectNameIndex?.Name ?? "None";
        int pkgIdx = EnsureNameAdded(pkg);
        int clsIdx = EnsureNameAdded(cls);
        int outerRef = TranslateObjectRef(srcImport.OuterReference);
        int objIdx = EnsureNameAdded(obj);
        // Existing dest dedup
        for (int i = 0; i < _dest.ImportTable.Count; i++)
        {
            var d = _dest.ImportTable[i];
            if (string.Equals(d.ObjectNameIndex?.Name, obj, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(d.ClassNameIndex?.Name, cls, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(d.PackageNameIndex?.Name, pkg, StringComparison.OrdinalIgnoreCase))
                return -(i + 1);
        }
        // Newly-queued dedup
        for (int i = 0; i < AddedImports.Count; i++)
        {
            var x = AddedImports[i];
            if (x.PackageNameTableIndex == pkgIdx && x.ClassNameTableIndex == clsIdx &&
                x.ObjectNameTableIndex == objIdx && x.OuterRef == outerRef)
                return -(_dest.ImportTable.Count + i + 1);
        }
        AddedImports.Add(new OmegaAssetStudio.UpkRepacker.NewImportSpec(
            PackageNameTableIndex: pkgIdx, PackageNameNumeric: 0,
            ClassNameTableIndex: clsIdx, ClassNameNumeric: 0,
            OuterRef: outerRef,
            ObjectNameTableIndex: objIdx, ObjectNameNumeric: 0));
        return -(_dest.ImportTable.Count + AddedImports.Count);
    }

    private int EnsureNameAdded(string name)
    {
        if (_destNameByValue.TryGetValue(name, out int existing)) return existing;
        int newIndex = _dest.NameTable.Count + AddedNames.Count;
        AddedNames.Add(name);
        _destNameByValue[name] = newIndex;
        return newIndex;
    }
}
