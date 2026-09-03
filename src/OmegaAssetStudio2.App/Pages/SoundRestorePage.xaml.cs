using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmegaAssetStudio2.App.Services;
using OmegaAssetStudio2.Core.Audio;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Workspace;

namespace OmegaAssetStudio2.App.Pages;

/// <summary>
/// Takes sound back out of a package that has had some put in.
/// </summary>
/// <remarks>
/// The want this answers: a package is built up over hours - pictures, models,
/// animations - and somewhere along the way a sound goes in that turns out to
/// be wrong. Where the tool that put it in cannot take it out again, the only
/// way back is a clean package and every one of those hours over again.
/// <para>
/// It need not cost that. A sound plays because something in the package points
/// at it, that pointing sits in its own entries, and the package as it shipped
/// is in the game folder to be read. So: find what points at sound, set it
/// beside how it shipped, and put back the entries chosen. Nothing else in the
/// package is read or written.
/// </para>
/// </remarks>
public sealed partial class SoundRestorePage : Page
{
    private string? _changedPath;
    private string? _shippedPath;

    private readonly List<PackageSounds.Difference> _differences = [];
    private readonly List<CheckBox> _boxes = [];
    private readonly List<CheckBox> _lines = [];

    /// <summary>One moment of one table, chosen to be quieted on its own.</summary>
    private sealed record Quieting(string Holder, string Moment);

    public SoundRestorePage()
    {
        InitializeComponent();
    }

    private async void PickButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".upk");

            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();

            if (file is null) return;

            _changedPath = file.Path;

            Look();
        }
        catch (Exception ex)
        {
            StatusText.Text = "that could not be opened: " + ex.Message;
        }
    }

    /// <summary>Reads the chosen package against the one it shipped as.</summary>
    private void Look()
    {
        _differences.Clear();
        _boxes.Clear();
        _lines.Clear();
        FindingsList.Children.Clear();
        RestoreButton.IsEnabled = false;
        StatusText.Text = string.Empty;

        if (_changedPath is null) return;

        ChosenText.Text = "Changed: " + _changedPath;

        // The same package as the game ships it, in whichever install is set up.
        _shippedPath = null;

        foreach (GameClient client in AppSettings.Current.ResolvedClients)
        {
            if (!client.Exists) continue;

            _shippedPath = SoundRestoreService.ShippedCounterpart(_changedPath, client.CookedPath);

            if (_shippedPath is not null) break;
        }

        if (_shippedPath is null)
        {
            ShippedText.Text = string.Empty;

            FindingsText.Text =
                "No package of that name is in the game folder, so there is nothing to set this one "
                + "beside. Point the app at the game install in Settings, or choose a package that "
                + "came from it.";

            return;
        }

        ShippedText.Text = "As it shipped: " + _shippedPath;

        try
        {
            Package changed = Package.Open(_changedPath);
            Package shipped = Package.Open(_shippedPath);

            _differences.AddRange(PackageSounds.Compare(changed, shipped));

            IReadOnlyList<PackageSounds.Hook> hooks = PackageSounds.Read(changed);

            Show(hooks);
        }
        catch (Exception ex)
        {
            FindingsText.Text = "that could not be read: " + ex.Message;
        }
    }

    /// <summary>Lays out what was found, with the ones worth putting back first.</summary>
    private void Show(IReadOnlyList<PackageSounds.Hook> hooks)
    {
        var canPutBack = _differences.Where(d => d.IsAltered).ToList();
        var added = _differences.Where(d => d.IsNew).ToList();

        FindingsText.Text = canPutBack.Count == 0
            ? $"Nothing in this package's own tables differs from how it shipped. It carries "
              + $"{hooks.Count:N0} wirings and {added.Count:N0} added entries that name a sound."
            : $"{canPutBack.Count:N0} of this package's tables no longer match how it shipped, "
              + $"holding {canPutBack.Sum(d => d.Hooks):N0} of its {hooks.Count:N0} wirings. "
              + $"{added.Count:N0} entries naming a sound were added alongside.";

        foreach (PackageSounds.Difference one in canPutBack.OrderByDescending(d => d.Hooks))
        {
            var box = new CheckBox
            {
                IsChecked = one.Hooks > 0,
                Tag = one.Name,
                Content = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = one.Name + "  (" + one.Kind + ")",
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        },
                        new TextBlock
                        {
                            Text = Describe(one),
                            FontSize = 12,
                            Opacity = 0.75,
                            TextWrapping = TextWrapping.Wrap,
                        },
                    },
                },
            };

            box.Checked += (_, _) => Ready();
            box.Unchecked += (_, _) => Ready();

            _boxes.Add(box);
            FindingsList.Children.Add(box);

            // Every moment it wires up, each able to go on its own.
            //
            // One wrong line should cost one line. The whole table above is
            // there for putting all of it back at once; these are for the
            // times when the rest of it is wanted.
            var mine = hooks
                .Where(h => h.HolderName.Equals(one.Name, StringComparison.OrdinalIgnoreCase))
                .GroupBy(h => h.Moment, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var moment in mine)
            {
                string plays = string.Join(", ", moment.Select(h => h.Sound).Distinct());

                var line = new CheckBox
                {
                    Margin = new Thickness(26, 0, 0, 0),
                    MinWidth = 0,
                    Tag = new Quieting(one.Name, moment.Key),
                    Content = new TextBlock
                    {
                        Text = moment.Key + "  plays  " + plays,
                        FontSize = 12,
                        Opacity = 0.75,
                        TextWrapping = TextWrapping.Wrap,
                    },
                };

                line.Checked += (_, _) => Ready();
                line.Unchecked += (_, _) => Ready();

                _lines.Add(line);
                FindingsList.Children.Add(line);
            }
        }

        Ready();
    }

    private static string Describe(PackageSounds.Difference one) =>
        $"{one.ShippedSize:N0} bytes as it shipped, {one.ChangedSize:N0} now"
        + (one.Hooks > 0 ? $", wiring up {one.Hooks:N0} sounds" : ", wiring up none");

    private void Ready()
    {
        // A table being put back whole takes its own lines with it, so those
        // are turned off rather than left looking as though they still matter.
        var whole = _boxes
            .Where(b => b.IsChecked == true)
            .Select(b => (string)b.Tag)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (CheckBox line in _lines)
        {
            bool covered = line.Tag is Quieting q && whole.Contains(q.Holder);

            line.IsEnabled = !covered;
            if (covered) line.IsChecked = false;
        }

        int tables = whole.Count;
        int moments = _lines.Count(l => l.IsChecked == true);

        RestoreButton.IsEnabled = tables > 0 || moments > 0;

        RestoreLabel.Text = tables > 0 && moments > 0 ? $"Put back {tables}, quiet {moments}"
            : tables > 1 ? $"Put back {tables} tables"
            : tables == 1 ? "Put the sound back"
            : moments == 1 ? "Quiet this one"
            : $"Quiet {moments}";
    }

    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_changedPath is null || _shippedPath is null) return;

        var names = _boxes
            .Where(b => b.IsChecked == true)
            .Select(b => (string)b.Tag)
            .ToList();

        // Moments chosen on their own, gathered by the table they sit in.
        var lines = _lines
            .Where(l => l.IsChecked == true && l.Tag is Quieting)
            .Select(l => (Quieting)l.Tag)
            .GroupBy(q => q.Holder, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (names.Count == 0 && lines.Count == 0) return;

        Busy.IsActive = true;
        RestoreButton.IsEnabled = false;
        PickButton.IsEnabled = false;
        StatusText.Text = "putting it back...";

        try
        {
            var said = new List<string>();
            bool allWell = true;

            // The single moments first, because putting a whole table back
            // would move everything after it and leave the rest looking for
            // moments that are no longer where they were.
            foreach (var byTable in lines)
            {
                SoundRestoreService.Outcome quieted = await SoundRestoreService.QuietAsync(
                    _changedPath, byTable.Key, byTable.Select(q => q.Moment).ToList());

                said.Add(quieted.Message);
                allWell &= quieted.Ok;

                if (!quieted.Ok) break;
            }

            if (allWell && names.Count > 0)
            {
                SoundRestoreService.Outcome outcome =
                    await SoundRestoreService.RestoreAsync(_changedPath, _shippedPath, names);

                said.Add(outcome.Message);
                allWell &= outcome.Ok;
            }

            StatusText.Text = string.Join("  ", said)
                + (allWell
                    ? "  The package as you gave it is kept beside it, named .before-sound-restore."
                    : string.Empty);

            if (allWell) Look();
        }
        catch (Exception ex)
        {
            StatusText.Text = "that did not work: " + ex.Message;
        }
        finally
        {
            Busy.IsActive = false;
            PickButton.IsEnabled = true;
            Ready();
        }
    }
}
