using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI;

namespace OmegaAssetStudio2.App.Controls;

// Animated gray placeholder block with a slow horizontal shimmer sweep.
// Use one or more of these as ItemsSource (or stack them in a panel) while
// real data is loading; swap to real content on success. Visually matches
// the "skeleton screen" pattern used by LinkedIn / YouTube / Twitter.
public sealed partial class Skeleton : UserControl
{
    private Storyboard? _storyboard;

    public Skeleton()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartShimmer();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _storyboard?.Stop();
        _storyboard = null;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Clip the Grid to its own bounds so the shimmer can't paint outside.
        RootClipGrid.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
        {
            Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height),
        };
        // Re-aim the shimmer animation to the new width.
        RestartShimmer();
    }

    private void StartShimmer()
    {
        if (_storyboard is not null) return;
        if (ActualWidth <= 0) return;

        // Slide the gradient stripe from off-screen-left to off-screen-right,
        // 1.4 s sweep, repeat forever. The stripe width is fixed at 160; we
        // overshoot endpoints by stripe width so the stripe never visually
        // pops in / out — it just appears and disappears at the edges.
        const double stripeWidth = 160;
        double endX = ActualWidth + stripeWidth;
        var anim = new DoubleAnimation
        {
            From = -stripeWidth,
            To = endX,
            Duration = new Duration(System.TimeSpan.FromMilliseconds(1400)),
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(anim, ShimmerTranslate);
        Storyboard.SetTargetProperty(anim, "X");
        _storyboard = new Storyboard();
        _storyboard.Children.Add(anim);
        _storyboard.Begin();
    }

    private void RestartShimmer()
    {
        _storyboard?.Stop();
        _storyboard = null;
        StartShimmer();
    }
}
