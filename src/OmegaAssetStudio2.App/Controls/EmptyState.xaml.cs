using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OmegaAssetStudio2.App.Controls;

// Reusable empty-state component for pages that have a "nothing loaded yet"
// or "no results" condition. Caller sets Title / Subtitle / Glyph / ActionText
// and an ActionInvoked event.
public sealed partial class EmptyState : UserControl
{
    public EmptyState()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => StateTitle.Text;
        set => StateTitle.Text = value ?? string.Empty;
    }

    public string Subtitle
    {
        get => StateSubtitle.Text;
        set => StateSubtitle.Text = value ?? string.Empty;
    }

    public string Glyph
    {
        get => StateIcon.Glyph;
        set => StateIcon.Glyph = value ?? "";
    }

    public string ActionText
    {
        get => StateActionLabel.Text;
        set
        {
            StateActionLabel.Text = value ?? string.Empty;
            StateAction.Visibility = string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public event EventHandler? ActionInvoked;

    private void StateAction_Click(object sender, RoutedEventArgs e)
    {
        ActionInvoked?.Invoke(this, EventArgs.Empty);
    }
}
