using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace OmegaAssetStudio2.App.Controls;

/// <summary>
/// A draggable divider between two columns of a <see cref="Grid"/>.
/// </summary>
/// <remarks>
/// Resizes the column to its right, respecting that column's MinWidth. Side
/// panels hold names that must stay readable, so the user needs to be able to
/// widen them; a fixed-width panel is what makes long names get clipped.
/// </remarks>
// Border is sealed in WinUI, so this derives from ContentControl, which still
// gives Background, sizing, and the pointer events the drag needs.
public sealed partial class PanelSplitter : ContentControl
{
    private bool _dragging;

    /// <summary>
    /// Resize the column to the LEFT instead of the one to the right.
    /// A panel at the far left edge has no divider before it, so its divider
    /// sits after it and has to reach back the other way.
    /// </summary>
    public bool ResizesPreviousColumn { get; set; }
    private double _startX;
    private double _startWidth;

    public PanelSplitter()
    {
        Width = 6;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Stretch;
        ManipulationMode = ManipulationModes.TranslateX;

        PointerEntered += (_, _) => ProtectedCursor =
            InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        PointerExited += (_, _) => { if (!_dragging) ProtectedCursor = null; };

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => _dragging = false;
    }

    private ColumnDefinition? TargetColumn
    {
        get
        {
            if (Parent is not Grid grid) return null;
            int index = Grid.GetColumn(this) + (ResizesPreviousColumn ? -1 : 1);
            return index >= 0 && index < grid.ColumnDefinitions.Count ? grid.ColumnDefinitions[index] : null;
        }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        ColumnDefinition? column = TargetColumn;
        if (column is null || Parent is not Grid grid) return;

        _dragging = true;
        _startX = e.GetCurrentPoint(grid).Position.X;
        _startWidth = column.ActualWidth;
        CapturePointer(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        ColumnDefinition? column = TargetColumn;
        if (column is null || Parent is not Grid grid) return;

        // Dragging left widens the right-hand column, hence the negated delta;
        // a divider that reaches back to the left widens the other way round.
        double delta = _startX - e.GetCurrentPoint(grid).Position.X;
        if (ResizesPreviousColumn) delta = -delta;
        double proposed = _startWidth + delta;
        double min = column.MinWidth > 0 ? column.MinWidth : 120;

        column.Width = new GridLength(Math.Max(min, proposed), GridUnitType.Pixel);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        ReleasePointerCapture(e.Pointer);
    }
}
