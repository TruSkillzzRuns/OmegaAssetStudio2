using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace OmegaAssetStudio2.App.Controls;

/// <summary>
/// A purely-informational "how to use" band that renders a left-to-right
/// numbered step strip. Pages set <see cref="Steps"/> to a delimited string
/// (split on '|') and the control builds numbered chips with arrow
/// separators — the same beginner guide used across the tool suite.
/// </summary>
public sealed partial class OmegaStepGuide : UserControl
{
    public static readonly DependencyProperty StepsProperty =
        DependencyProperty.Register(nameof(Steps), typeof(string), typeof(OmegaStepGuide),
            new PropertyMetadata(string.Empty, OnStepsChanged));

    /// <summary>Pipe-delimited step labels, e.g. "Load a file|Pick an item|Save".</summary>
    public string Steps
    {
        get => (string)GetValue(StepsProperty);
        set => SetValue(StepsProperty, value);
    }

    public OmegaStepGuide()
    {
        this.InitializeComponent();
    }

    private static void OnStepsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is OmegaStepGuide guide)
            guide.Rebuild(e.NewValue as string ?? string.Empty);
    }

    private void Rebuild(string steps)
    {
        if (StepsHost is null) return;
        StepsHost.Children.Clear();

        string[] parts = steps.Split('|', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
        Brush accent = ResolveAccentBrush();

        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                StepsHost.Children.Add(new TextBlock
                {
                    Text = "→", // rightwards arrow
                    FontSize = 14,
                    Opacity = 0.45,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            StepsHost.Children.Add(BuildStep(i + 1, parts[i], accent));
        }
    }

    private static StackPanel BuildStep(int number, string label, Brush accent)
    {
        var chip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };

        var badge = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Background = accent,
            Child = new TextBlock
            {
                Text = number.ToString(),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        chip.Children.Add(badge);
        chip.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = accent,
            VerticalAlignment = VerticalAlignment.Center
        });
        return chip;
    }

    private Brush ResolveAccentBrush()
    {
        if (Application.Current.Resources.TryGetValue("OmegaAssetStudio.AccentBrush", out object? res) && res is Brush b)
            return b;
        return new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x0E, 0xA5, 0xE9));
    }
}
