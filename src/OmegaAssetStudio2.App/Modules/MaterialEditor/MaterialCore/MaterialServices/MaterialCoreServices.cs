using OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialRenderer;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialServices;

public sealed class MaterialCoreServices
{
    public MaterialInstanceService MaterialInstances { get; } = new();

    public MaterialParameterService Parameters { get; } = new();

    public ShaderService Shaders { get; } = new();

    public ShaderVariantService ShaderVariants { get; } = new();

    public MaterialGraphService Graph { get; } = new();

    public MaterialOverrideService Overrides { get; } = new();

    public MaterialRendererService Renderer { get; } = new();
}

