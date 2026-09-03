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
    private readonly List<CheckBox> _recordings = [];
    private readonly List<CheckBox> _coming = [];

    private string? _sourcePath;

    /// <summary>What each moment names, so its recording can be looked for.</summary>
    private readonly Dictionary<string, List<string>> _sounds =
        new(StringComparer.OrdinalIgnoreCase);

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
        _sounds.Clear();
        FindingsList.Children.Clear();
        RestoreButton.IsEnabled = false;
        RepointButton.IsEnabled = false;
        FindRecordingsButton.IsEnabled = false;
        ReplaceRecordingButton.IsEnabled = false;
        FromAnotherButton.IsEnabled = false;
        BringAcrossButton.IsEnabled = false;
        _recordings.Clear();
        _coming.Clear();
        _sourcePath = null;
        SoundPicker.ItemsSource = null;
        SoundPicker.IsEnabled = false;
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

            // What a moment may be pointed at: whatever this package already
            // names, since it holds no sound of its own.
            var sounds = PackageSounds.SoundsIn(changed)
                .Select(s => s.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            SoundPicker.ItemsSource = sounds;
            SoundPicker.IsEnabled = sounds.Count > 0;

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
                var named = moment.Select(h => h.Sound).Distinct().ToList();

                _sounds[moment.Key] = named;

                string plays = string.Join(", ", named);

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

        RepointButton.IsEnabled = moments > 0 && SoundPicker.SelectedItem is string;
        FindRecordingsButton.IsEnabled = moments > 0;

        int ticked = _recordings.Count(r => r.IsChecked == true);

        ReplaceRecordingButton.IsEnabled = ticked > 0;
        FromAnotherButton.IsEnabled = _changedPath is not null;

        int bringing = _coming.Count(c => c.IsChecked == true);

        BringAcrossButton.IsEnabled = bringing > 0;
        BringLabel.Text = bringing > 1 ? $"Bring {bringing} across" : "Bring ticked across";

        ReplaceLabel.Text = ticked > 1 ? $"Replace {ticked} with a file" : "Replace ticked with a file";

        RepointLabel.Text = moments > 1 ? $"Play this for {moments}" : "Play this instead";

        RestoreLabel.Text = tables > 0 && moments > 0 ? $"Put back {tables}, quiet {moments}"
            : tables > 1 ? $"Put back {tables} tables"
            : tables == 1 ? "Put the sound back"
            : moments == 1 ? "Quiet this one"
            : $"Quiet {moments}";
    }

    /// <summary>
    /// Lists the sounds another package names, to be brought into this one.
    /// </summary>
    /// <remarks>
    /// Only the naming comes across - the three small entries that stand for a
    /// sound. The recording itself stays where it is, in the containers beside
    /// the game, because no package has ever held one.
    /// </remarks>
    private async void FromAnotherButton_Click(object sender, RoutedEventArgs e)
    {
        if (_changedPath is null) return;

        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".upk");

            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            Windows.Storage.StorageFile? picked = await picker.PickSingleFileAsync();

            if (picked is null) return;

            _sourcePath = picked.Path;
        }
        catch (Exception ex)
        {
            StatusText.Text = "that could not be opened: " + ex.Message;
            return;
        }

        _coming.Clear();

        try
        {
            Package target = Package.Open(_changedPath);
            Package source = Package.Open(_sourcePath);

            var mine = SoundImport.Where(target, "akevent");
            var theirs = SoundImport.Where(source, "akevent");

            var news = theirs.Keys
                .Where(k => !mine.ContainsKey(k))
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();

            FindingsList.Children.Add(new TextBlock
            {
                Margin = new Thickness(0, 14, 0, 2),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Text = news.Count == 0
                    ? Path.GetFileNameWithoutExtension(_sourcePath)
                      + " names nothing this package has not got already."
                    : $"{news.Count} sounds in {Path.GetFileNameWithoutExtension(_sourcePath)} "
                      + "that this package does not name:",
            });

            foreach (string one in news)
            {
                var box = new CheckBox
                {
                    Margin = new Thickness(26, 0, 0, 0),
                    Tag = one,
                    Content = new TextBlock
                    {
                        Text = one,
                        FontSize = 12,
                        Opacity = 0.8,
                        TextWrapping = TextWrapping.Wrap,
                    },
                };

                box.Checked += (_, _) => Ready();
                box.Unchecked += (_, _) => Ready();

                _coming.Add(box);
                FindingsList.Children.Add(box);
            }

            StatusText.Text = news.Count == 0
                ? string.Empty
                : "tick the ones to bring in. Their recordings stay where they are.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "that could not be read: " + ex.Message;
        }
        finally
        {
            Ready();
        }
    }

    private async void BringAcrossButton_Click(object sender, RoutedEventArgs e)
    {
        if (_changedPath is null || _sourcePath is null) return;

        var names = _coming
            .Where(c => c.IsChecked == true && c.Tag is string)
            .Select(c => (string)c.Tag)
            .ToList();

        if (names.Count == 0) return;

        Busy.IsActive = true;
        BringAcrossButton.IsEnabled = false;
        PickButton.IsEnabled = false;
        StatusText.Text = "bringing them in...";

        try
        {
            SoundRestoreService.Outcome outcome =
                await SoundImportService.ImportAsync(_changedPath, _sourcePath, names);

            StatusText.Text = outcome.Message
                + (outcome.Ok
                    ? "  The package as you gave it is kept beside it, named .before-sound-restore."
                    : string.Empty);

            if (outcome.Ok) Look();
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

    private void SoundPicker_SelectionChanged(object sender, SelectionChangedEventArgs e) => Ready();

    /// <summary>
    /// Shows the recordings behind the ticked moments, before any is replaced.
    /// </summary>
    /// <remarks>
    /// Shown rather than acted on at once, because one line is not one
    /// recording: each language keeps its own, and a line spoken more than once
    /// keeps a take apiece. Five moments of one costume stood in front of
    /// fifteen recordings across three languages. Replacing all of those
    /// because someone asked about one would be a poor way to behave.
    /// <para>
    /// This writes to the containers beside the game rather than to the
    /// package, since a package only names a sound. A line changed here is
    /// changed for everything that plays it.
    /// </para>
    /// </remarks>
    private void FindRecordingsButton_Click(object sender, RoutedEventArgs e)
    {
        var names = _lines
            .Where(l => l.IsChecked == true && l.Tag is Quieting)
            .Select(l => (Quieting)l.Tag)
            .SelectMany(q => _sounds.GetValueOrDefault(q.Moment) ?? new List<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _recordings.Clear();

        if (names.Count == 0)
        {
            StatusText.Text = "those moments name no sound to look for.";
            Ready();
            return;
        }

        GameClient? client = AppSettings.Current.ResolvedClients.FirstOrDefault(c => c.Exists);

        if (client is null)
        {
            StatusText.Text = "no game install is set up to look in. Set one in Settings.";
            return;
        }

        Busy.IsActive = true;

        try
        {
            var found = SoundRestoreService.RecordingsBehind(client, names);

            FindingsList.Children.Add(new TextBlock
            {
                Margin = new Thickness(0, 14, 0, 2),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Text = found.Count == 0
                    ? "No recording found for " + string.Join(", ", names) + "."
                    : $"{found.Count} recordings behind those sounds, across every language:",
            });

            foreach (PlacedSound one in found)
            {
                var box = new CheckBox
                {
                    Margin = new Thickness(26, 0, 0, 0),
                    Tag = one,
                    Content = new TextBlock
                    {
                        Text = $"{one.Name}   {one.Entry.Size:N0} bytes   in {one.ContainerName}",
                        FontSize = 12,
                        Opacity = 0.8,
                        TextWrapping = TextWrapping.Wrap,
                    },
                };

                box.Checked += (_, _) => Ready();
                box.Unchecked += (_, _) => Ready();

                _recordings.Add(box);
                FindingsList.Children.Add(box);
            }

            StatusText.Text = found.Count == 0
                ? "nothing found. The sounds may be in a container this install does not have."
                : "tick the ones to replace, then choose a file.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "they could not be looked for: " + ex.Message;
        }
        finally
        {
            Busy.IsActive = false;
            Ready();
        }
    }

    private async void ReplaceRecordingButton_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _recordings
            .Where(r => r.IsChecked == true && r.Tag is PlacedSound)
            .Select(r => (PlacedSound)r.Tag)
            .ToList();

        if (chosen.Count == 0) return;

        string file;

        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".wem");

            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            Windows.Storage.StorageFile? picked = await picker.PickSingleFileAsync();

            if (picked is null) return;

            file = picked.Path;
        }
        catch (Exception ex)
        {
            StatusText.Text = "that could not be opened: " + ex.Message;
            return;
        }

        Busy.IsActive = true;
        ReplaceRecordingButton.IsEnabled = false;
        StatusText.Text = "putting it in...";

        try
        {
            var said = new List<string>();

            foreach (PlacedSound one in chosen)
            {
                SoundRestoreService.Outcome outcome =
                    await SoundRestoreService.ReplaceRecordingAsync(one, file);

                said.Add(one.Name + ": " + outcome.Message);
            }

            StatusText.Text = string.Join("  ", said);
        }
        catch (Exception ex)
        {
            StatusText.Text = "that did not work: " + ex.Message;
        }
        finally
        {
            Busy.IsActive = false;
            Ready();
        }
    }

    private async void RepointButton_Click(object sender, RoutedEventArgs e)
    {
        if (_changedPath is null) return;
        if (SoundPicker.SelectedItem is not string sound) return;

        var lines = _lines
            .Where(l => l.IsChecked == true && l.Tag is Quieting)
            .Select(l => (Quieting)l.Tag)
            .GroupBy(q => q.Holder, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (lines.Count == 0) return;

        Busy.IsActive = true;
        RepointButton.IsEnabled = false;
        RestoreButton.IsEnabled = false;
        PickButton.IsEnabled = false;
        StatusText.Text = "pointing them at " + sound + "...";

        try
        {
            var said = new List<string>();
            bool allWell = true;

            foreach (var byTable in lines)
            {
                SoundRestoreService.Outcome outcome = await SoundRestoreService.RepointAsync(
                    _changedPath, byTable.Key, byTable.Select(q => q.Moment).ToList(), sound);

                said.Add(outcome.Message);
                allWell &= outcome.Ok;

                if (!outcome.Ok) break;
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
