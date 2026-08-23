namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.Safety;

public sealed class MaterialValidationIssue
{
    public required string Severity { get; init; }
    public required string Message { get; init; }
}
