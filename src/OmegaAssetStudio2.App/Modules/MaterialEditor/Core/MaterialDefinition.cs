using System.Collections.Generic;
using System.Linq;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.Core;

public sealed class MaterialDefinition : NotifyPropertyChangedBase
{
    private string name = string.Empty;
    private string path = string.Empty;
    private string sourceUpkPath = string.Empty;
    private string sourceMeshExportPath = string.Empty;
    private string type = string.Empty;
    private List<MaterialTextureSlot> textureSlots = new();
    private List<MaterialParameter> scalarParameters = new();
    private List<MaterialParameter> vectorParameters = new();
    private bool isNative = true;
    private bool isModded;
    private bool isPinned;
    private string namespaceTag = string.Empty;
    private string originalPath = string.Empty;
    private string parentMaterialPath = string.Empty;
    private Guid parentMaterialId = Guid.Empty;

    public string Name
    {
        get => name;
        set => SetProperty(ref name, value);
    }

    public string Path
    {
        get => path;
        set => SetProperty(ref path, value);
    }

    public string SourceUpkPath
    {
        get => sourceUpkPath;
        set => SetProperty(ref sourceUpkPath, value);
    }

    public string SourceMeshExportPath
    {
        get => sourceMeshExportPath;
        set => SetProperty(ref sourceMeshExportPath, value);
    }

    public string Type
    {
        get => type;
        set => SetProperty(ref type, value);
    }

    public List<MaterialTextureSlot> TextureSlots
    {
        get => textureSlots;
        set => SetProperty(ref textureSlots, value);
    }

    public List<MaterialParameter> ScalarParameters
    {
        get => scalarParameters;
        set => SetProperty(ref scalarParameters, value);
    }

    public List<MaterialParameter> VectorParameters
    {
        get => vectorParameters;
        set => SetProperty(ref vectorParameters, value);
    }

    public bool IsNative
    {
        get => isNative;
        set
        {
            if (SetProperty(ref isNative, value))
                OnPropertyChanged(nameof(AssetClassLabel));
        }
    }

    public bool IsModded
    {
        get => isModded;
        set
        {
            if (SetProperty(ref isModded, value))
                OnPropertyChanged(nameof(AssetClassLabel));
        }
    }

    // Pinned materials sort to the top of the browser list. Persisted in
    // the view-model's pinnedMaterialPaths set, mirrored here so XAML
    // templates can bind the star glyph.
    public bool IsPinned
    {
        get => isPinned;
        set => SetProperty(ref isPinned, value);
    }

    public string NamespaceTag
    {
        get => namespaceTag;
        set => SetProperty(ref namespaceTag, value);
    }

    public string OriginalPath
    {
        get => originalPath;
        set => SetProperty(ref originalPath, value);
    }

    public string ParentMaterialPath
    {
        get => parentMaterialPath;
        set => SetProperty(ref parentMaterialPath, value);
    }

    // The parent material's FGuid as captured from FStaticParameterSet.BaseMaterialId
    // during MIC parse. Variants subsystem uses this to look up learned
    // switch deltas. Empty when the export isn't a MIC or has no static
    // permutation resource. See MaterialEditorService.LoadMaterialsAsync.
    public Guid ParentMaterialId
    {
        get => parentMaterialId;
        set => SetProperty(ref parentMaterialId, value);
    }

    // The material's OWN id — FMaterial.Id from the cooked FMaterialResource
    // (StaticPermutationResources[0].Id for a MIC, MaterialResource[0].Id for a
    // master). This is the real, non-empty "Material ID" the reference editor
    // shows; FStaticParameterSet.BaseMaterialId is frequently empty in cooked
    // game content, which is why the UI used to show all zeros.
    private Guid materialId = Guid.Empty;
    public Guid MaterialId
    {
        get => materialId;
        set => SetProperty(ref materialId, value);
    }

    public string AssetClassLabel => IsModded ? "MOD" : "NATIVE";

    // Short class chip for the browser row so the user can tell at a glance
    // whether they're looking at a material-instance-constant (MIC), a base
    // material (MAT), or a material function (FN). Falls back to the first
    // 6 characters of Type for anything else.
    public string MaterialClassLabel => Type switch
    {
        "UMaterialInstanceConstant" => "MIC",
        "UMaterialInstance"         => "MIC",
        "UMaterial"                 => "MAT",
        "UMaterialFunction"         => "FN",
        _ => string.IsNullOrWhiteSpace(Type) ? "?" : (Type.Length > 6 ? Type[..6] : Type),
    };

    public MaterialDefinition Clone()
    {
        return new MaterialDefinition
        {
            Name = Name,
            Path = Path,
            SourceUpkPath = SourceUpkPath,
            SourceMeshExportPath = SourceMeshExportPath,
            Type = Type,
            IsNative = IsNative,
            IsModded = IsModded,
            NamespaceTag = NamespaceTag,
            OriginalPath = OriginalPath,
            ParentMaterialPath = ParentMaterialPath,
            ParentMaterialId = ParentMaterialId,
            TextureSlots = TextureSlots.Select(slot => slot.Clone()).ToList(),
            ScalarParameters = ScalarParameters.Select(parameter => parameter.Clone()).ToList(),
            VectorParameters = VectorParameters.Select(parameter => parameter.Clone()).ToList()
        };
    }

    public void CopyFrom(MaterialDefinition source)
    {
        Name = source.Name;
        Path = source.Path;
        SourceUpkPath = source.SourceUpkPath;
        SourceMeshExportPath = source.SourceMeshExportPath;
        Type = source.Type;
        IsNative = source.IsNative;
        IsModded = source.IsModded;
        NamespaceTag = source.NamespaceTag;
        OriginalPath = source.OriginalPath;
        ParentMaterialPath = source.ParentMaterialPath;
        TextureSlots = source.TextureSlots.Select(slot => slot.Clone()).ToList();
        ScalarParameters = source.ScalarParameters.Select(parameter => parameter.Clone()).ToList();
        VectorParameters = source.VectorParameters.Select(parameter => parameter.Clone()).ToList();
    }
}

