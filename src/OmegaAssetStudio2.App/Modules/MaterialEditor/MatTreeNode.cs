using System.Collections.ObjectModel;
using System.ComponentModel;
using OmegaAssetStudio.WinUI.Modules.MaterialEditor.Core;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor;

// Kinds of node in the simplified Material Editor tree (mirrors the reference
// MHMaterialEditor layout: Skeletal Meshes → Sections, Master Materials,
// Material Instances, Textures).
public enum MatNodeKind { Group, SkelMesh, Section, Material, Mic, Texture }

// One node in the left-hand grouped tree. Group nodes are headers; leaf nodes
// carry a typed payload the right-hand detail panel renders on selection.
public sealed class MatTreeNode : INotifyPropertyChanged
{
    public string Header { get; set; } = string.Empty;
    public string Glyph { get; set; } = string.Empty;   // Segoe Fluent Icons glyph, optional
    public MatNodeKind Kind { get; set; }
    public bool IsGroup => Kind == MatNodeKind.Group;
    public bool IsExpanded { get; set; }

    // Group headers render bold (matches the reference's bold category rows).
    public Windows.UI.Text.FontWeight HeaderWeight =>
        IsGroup ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;

    // Tree depth (0 = group, 1 = mesh/material/etc, 2 = section). Used to compute
    // a per-node text wrap width that accounts for the row's indentation.
    public int Depth { get; set; }

    // WinUI's bound TreeView measures item content with infinite width, so a
    // plain TextWrapping never wraps and long names clip. We instead bind the
    // text's MaxWidth to this value and recompute it from the TreeView's live
    // width (minus indent) on SizeChanged — guaranteeing readable wrapped names
    // at every window size.
    private double textMaxWidth = 240;
    public double TextMaxWidth
    {
        get => textMaxWidth;
        set { if (value != textMaxWidth) { textMaxWidth = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TextMaxWidth))); } }
    }

    // Payloads (set per kind):
    public MaterialDefinition? Material { get; set; }    // Material / Mic
    public string MeshExportPath { get; set; } = string.Empty; // SkelMesh / Section
    public string SourceUpk { get; set; } = string.Empty;       // SkelMesh / Section host UPK
    public int SectionIndex { get; set; } = -1;         // Section
    public int MaterialIndex { get; set; } = -1;        // Section (current slot)
    public int TriangleCount { get; set; }              // Section
    public string TextureName { get; set; } = string.Empty;    // Texture
    public string TexturePath { get; set; } = string.Empty;    // Texture

    public ObservableCollection<MatTreeNode> Children { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
}
