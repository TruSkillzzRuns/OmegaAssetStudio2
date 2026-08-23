using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OmegaAssetStudio2.App.Controls;

public sealed partial class OmegaPageHeader : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(OmegaPageHeader),
            new PropertyMetadata(string.Empty, OnTitleChanged));

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(OmegaPageHeader),
            new PropertyMetadata(string.Empty, OnSubtitleChanged));

    public static readonly DependencyProperty RightContentProperty =
        DependencyProperty.Register(nameof(RightContent), typeof(object), typeof(OmegaPageHeader),
            new PropertyMetadata(null, OnRightContentChanged));

    // v2: status chips strip (e.g. Mod Check, Track Coverage). Pages assign
    // a StackPanel of pill borders; header shows it inline. Optional.
    public static readonly DependencyProperty ChipsContentProperty =
        DependencyProperty.Register(nameof(ChipsContent), typeof(object), typeof(OmegaPageHeader),
            new PropertyMetadata(null, OnChipsContentChanged));

    // v2: single accent "primary action" slot. Pages assign an AccentButton
    // (Save / Inject / Apply). The header keeps the button visually
    // distinct from secondary controls in RightContent. Optional.
    public static readonly DependencyProperty PrimaryActionProperty =
        DependencyProperty.Register(nameof(PrimaryAction), typeof(object), typeof(OmegaPageHeader),
            new PropertyMetadata(null, OnPrimaryActionChanged));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public object? RightContent { get => GetValue(RightContentProperty); set => SetValue(RightContentProperty, value); }
    public object? ChipsContent { get => GetValue(ChipsContentProperty); set => SetValue(ChipsContentProperty, value); }
    public object? PrimaryAction { get => GetValue(PrimaryActionProperty); set => SetValue(PrimaryActionProperty, value); }

    public OmegaPageHeader()
    {
        this.InitializeComponent();
    }

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is OmegaPageHeader h && h.TitleTextBlock is not null)
            h.TitleTextBlock.Text = e.NewValue as string ?? string.Empty;
    }

    private static void OnSubtitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is OmegaPageHeader h && h.SubtitleTextBlock is not null)
            h.SubtitleTextBlock.Text = e.NewValue as string ?? string.Empty;
    }

    private static void OnRightContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is OmegaPageHeader h && h.RightContentPresenter is not null)
            h.RightContentPresenter.Content = e.NewValue;
    }

    private static void OnChipsContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is OmegaPageHeader h && h.ChipsHost is not null && h.ChipsContentPresenter is not null)
        {
            h.ChipsContentPresenter.Content = e.NewValue;
            h.ChipsHost.Visibility = e.NewValue is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private static void OnPrimaryActionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is OmegaPageHeader h && h.PrimaryActionHost is not null && h.PrimaryActionPresenter is not null)
        {
            h.PrimaryActionPresenter.Content = e.NewValue;
            h.PrimaryActionHost.Visibility = e.NewValue is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
