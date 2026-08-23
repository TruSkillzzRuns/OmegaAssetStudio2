using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OmegaAssetStudio2.App.Controls;

public sealed partial class ToolHeader : UserControl
{
    public ToolHeader() => InitializeComponent();

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(ToolHeader),
            new PropertyMetadata(string.Empty, OnTitleChanged));

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(ToolHeader),
            new PropertyMetadata(string.Empty, OnSubtitleChanged));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ToolHeader)d).TitleText.Text = (string)(e.NewValue ?? string.Empty);

    private static void OnSubtitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ToolHeader)d).SubtitleText.Text = (string)(e.NewValue ?? string.Empty);
}
