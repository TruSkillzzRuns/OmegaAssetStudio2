using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmegaAssetStudio2.App.Services;
using OmegaAssetStudio2.Core.Workspace;
using OmegaAssetStudio2.Core.Workspace.Backup;
using Windows.Storage.Pickers;

namespace OmegaAssetStudio2.App.Pages;

/// <summary>One backup in the list.</summary>
public sealed class BackupRow
{
    public required BackupEntry Entry { get; init; }

    public required string FileName { get; init; }

    public required string FolderPath { get; init; }

    /// <summary>Which game this file belongs to, or empty when it is elsewhere.</summary>
    public required string GameName { get; init; }

    /// <summary>What kind of thing the file holds, read from its name.</summary>
    public BackupCategory Category { get; init; }

    /// <summary>Who or what it belongs to — a hero, a power's character.</summary>
    public string Owner { get; init; } = string.Empty;

    /// <summary>The kind and the owner together, for the line under the name.</summary>
    public string Belongs => Owner.Length == 0
        ? BackupCategories.Name(Category)
        : BackupCategories.Name(Category) + " · " + Owner;

    /// <summary>What was kept, against what is there now.</summary>
    public required string SizeText { get; init; }

    public required string TakenText { get; init; }

    /// <summary>Changed, untouched, or something wrong. One word where it can be.</summary>
    public required string State { get; init; }

    /// <summary>
    /// Which of the three states this is. Three flags rather than one string,
    /// so a row can show the right pill without a converter and the colours
    /// stay in the theme where a theme change still reaches them.
    /// </summary>
    public bool IsChanged { get; init; }

    public bool IsUntouched { get; init; }

    public bool IsGone { get; init; }

    /// <summary>A letter for the row's badge, from the kind of file it is.</summary>
    public required string Glyph { get; init; }

    // Bound straight to each pill's Visibility. Given as Visibility rather than
    // bool so the row needs no converter and the pills stay plain markup.
    public Visibility ChangedPill => IsChanged ? Visibility.Visible : Visibility.Collapsed;

    public Visibility UntouchedPill => IsUntouched ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GonePill => IsGone ? Visibility.Visible : Visibility.Collapsed;

    public required string Note { get; init; }

    /// <summary>Spare copies of the same backup, which are not worth a row each.</summary>
    public required IReadOnlyList<string> ExtraCopies { get; init; }
}

public sealed partial class BackupPage : Page
{
    private readonly BackupManager _manager = new();
    private readonly ObservableCollection<BackupRow> _visible = [];

    /// <summary>Which kind of thing is being shown, or null for all of them.</summary>
    private BackupCategory? _category;

    private List<BackupRow> _all = [];

    /// <summary>
    /// Folders the user asked for on top of the game folders already known.
    /// Kept so that refreshing, restoring or forgetting does not empty the list
    /// the user is looking at.
    /// </summary>
    private readonly List<string> _alsoScan = [];

    private const string AllGames = "All games";
    private const string Elsewhere = "Other folders";

    /// <summary>True while the dropdown is being refilled, so it does not refilter.</summary>
    private bool _fillingFilter;

    public BackupPage()
    {
        InitializeComponent();
        BackupList.ItemsSource = _visible;
        Load();
    }

    private void Load(string? alsoScanFolder = null)
    {
        try
        {
            // Anything an earlier version put in the vault is brought back
            // beside the file it protects, where a backup belongs.
            int brought = BackupFileHelper.MoveVaultBackupsBesideTheirFiles();

            if (alsoScanFolder is not null &&
                !_alsoScan.Contains(alsoScanFolder, StringComparer.OrdinalIgnoreCase))
            {
                _alsoScan.Add(alsoScanFolder);
            }

            // Every game folder set up in this application, without being asked:
            // a backup now sits beside the file it protects, so that is where
            // they are. Then anything else the user pointed at, then the vault
            // an earlier version used.
            var folders = new List<string>();

            foreach (GameClient client in AppSettings.Current.ResolvedClients)
            {
                if (client.Exists) folders.Add(client.CookedPath);
            }

            folders.AddRange(_alsoScan);

            var entries = new List<BackupEntry>();
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string folder in folders)
            {
                foreach (BackupEntry entry in _manager.ScanFolder(folder))
                {
                    if (known.Add(entry.BackupPath)) entries.Add(entry);
                }
            }

            foreach (BackupEntry entry in _manager.ScanVault())
            {
                if (known.Add(entry.BackupPath)) entries.Add(entry);
            }

            // One row per file that is protected, not one per file on disk.
            // A folder can hold <name>.bak and <name>.bak.bak - the second is a
            // spare copy of the same original, made when a backup was itself
            // handed to something expecting a live file. Both used to get a row,
            // so a costume appeared twice and the list read as though there were
            // half as many protected files again as there are.
            _all = entries
                .GroupBy(e => e.OriginalPath, StringComparer.OrdinalIgnoreCase)
                .Select(ToRow)
                .ToList();
            FillClientFilter();
            ApplyFilter();

            VaultText.Text =
                "A backup sits next to the file it protects, as <name>.bak, in the same folder. " +
                "One copy per file, taken before its first change and never overwritten." +
                (brought > 0 ? $" {brought:N0} backup(s) from the old location were moved back beside their files." : "");

            int changed = _all.Count(r => r.IsChanged);
            int untouched = _all.Count(r => r.IsUntouched);
            int gone = _all.Count(r => r.IsGone);

            ProtectedCount.Text = _all.Count.ToString("N0");
            ChangedCount.Text = changed.ToString("N0");
            UntouchedCount.Text = untouched.ToString("N0");

            StatusText.Text = _all.Count == 0
                ? "No backups yet. One is taken automatically the first time a file is changed."
                : gone > 0
                    ? $"{changed:N0} changed, {untouched:N0} untouched, {gone:N0} whose file is gone."
                    : $"{changed:N0} changed, {untouched:N0} untouched.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not read backups: {ex.Message}";
            CrashLog.Write("Backup.Load", ex);
        }
    }

    private static BackupRow ToRow(IGrouping<string, BackupEntry> group)
    {
        // The canonical copy is the one named <file>.bak. Anything else in the
        // group says the same thing twice.
        BackupEntry entry = group.FirstOrDefault(e =>
            e.BackupPath.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
            && !e.BackupPath.EndsWith(".bak.bak", StringComparison.OrdinalIgnoreCase))
            ?? group.First();

        var extras = group.Where(e => !ReferenceEquals(e, entry)).Select(e => e.BackupPath).ToList();

        // Spare copies are not mentioned. A row stands for a file that can be
        // put back, and how many copies of the saved original happen to sit on
        // disk is the tool's business, not the reader's - they select the file
        // and press Restore.
        var notes = new List<string>();
        if (entry.IsLegacyLocation) notes.Add("kept away from the file by an earlier version");

        string state =
            !entry.OriginalExists ? "the file is gone"
            : entry.LooksModified ? "changed"
            : "untouched";

        long? now = entry.OriginalSizeBytes;

        return new BackupRow
        {
            Entry = entry,
            FileName = entry.FileName,
            FolderPath = entry.FolderPath,
            SizeText = now is null || now == entry.BackupSizeBytes
                ? FormatSize(entry.BackupSizeBytes)
                : $"{FormatSize(entry.BackupSizeBytes)} \u2192 {FormatSize(now.Value)}",
            TakenText = entry.TakenUtc == DateTime.MinValue
                ? string.Empty
                : entry.TakenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            State = state,
            IsChanged = entry.OriginalExists && entry.LooksModified,
            IsUntouched = entry.OriginalExists && !entry.LooksModified,
            IsGone = !entry.OriginalExists,
            Glyph = GlyphFor(entry.FileName),
            GameName = GameFor(entry.FolderPath),
            Category = BackupCategories.Of(entry.FileName),
            Owner = BackupCategories.Owner(entry.FileName),
            Note = string.Join(" \u00b7 ", notes),
            ExtraCopies = extras,
        };
    }

    /// <summary>
    /// The game a file belongs to, by the folder it sits in. Empty for anything
    /// under a folder the user pointed at by hand rather than a game they set
    /// up, which is why the dropdown keeps an entry for those too.
    /// </summary>
    private static string GameFor(string folderPath)
    {
        foreach (GameClient client in AppSettings.Current.ResolvedClients)
        {
            if (!client.Exists) continue;

            if (folderPath.StartsWith(client.RootPath, StringComparison.OrdinalIgnoreCase)
                || folderPath.StartsWith(client.CookedPath, StringComparison.OrdinalIgnoreCase))
                return client.DisplayName;
        }

        return string.Empty;
    }

    /// <summary>
    /// A letter for the row's badge. Enough to tell a costume from a sound
    /// bank while scanning, without needing an icon set.
    /// </summary>
    private static string GlyphFor(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".upk" => "U",
        ".pck" => "S",
        ".tfc" => "T",
        ".sip" => "C",
        _ => "F",
    };

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} GB",
    };

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => Load();

    private async void ScanFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");

            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            Load(folder.Path);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not scan that folder: {ex.Message}";
            CrashLog.Write("Backup.ScanFolder", ex);
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    /// <summary>
    /// Fills the game dropdown from the games that actually have files in the
    /// list, keeping whatever was chosen if it is still there.
    /// </summary>
    private void FillClientFilter()
    {
        string? chosen = ClientFilter.SelectedItem as string;

        // Every game that is set up, whether or not it has a backup yet. The
        // dropdown is for choosing which install to look at; a game with
        // nothing changed still has an answer worth seeing, and one that
        // disappeared from the list the moment it was clean would be worse.
        var games = AppSettings.Current.ResolvedClients
                        .Select(c => c.DisplayName)
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                        .ToList();

        var items = new List<string> { AllGames };

        items.AddRange(games);

        if (_all.Any(r => r.GameName.Length == 0)) items.Add(Elsewhere);

        _fillingFilter = true;

        ClientFilter.ItemsSource = items;
        ClientFilter.SelectedItem = chosen is not null && items.Contains(chosen) ? chosen : AllGames;

        _fillingFilter = false;
    }

    private void ClientFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_fillingFilter) ApplyFilter();
    }

    /// <summary>
    /// Draws one tab per kind of thing that is actually backed up.
    /// </summary>
    /// <remarks>
    /// Built from what is there rather than from the full list of kinds: a tab
    /// for Icons when no icon has ever been changed is a promise of something
    /// to look at that turns out to be empty. Counts are of what the game
    /// filter is already showing, so they agree with the tiles above.
    /// </remarks>
    /// <summary>
    /// Re-tints the tabs when the theme changes under them.
    /// </summary>
    /// <remarks>
    /// Their colours are chosen in code rather than bound, so unlike everything
    /// in XAML they do not follow a switch on their own.
    /// </remarks>
    private void Themed_ActualThemeChanged(FrameworkElement sender, object args) => RefreshCategoryTabs();

    private void RefreshCategoryTabs()
    {
        if (CategoryTabs is null) return;

        string game = ClientFilter.SelectedItem as string ?? AllGames;

        IEnumerable<BackupRow> inScope = _all;

        if (game == Elsewhere) inScope = inScope.Where(r => r.GameName.Length == 0);
        else if (game != AllGames)
            inScope = inScope.Where(r => r.GameName.Equals(game, StringComparison.OrdinalIgnoreCase));

        List<BackupRow> rows = inScope.ToList();

        CategoryTabs.Children.Clear();

        Add(null, "All", rows.Count);

        foreach (BackupCategory category in BackupCategories.Order)
        {
            int count = rows.Count(r => r.Category == category);
            if (count == 0) continue;

            Add(category, BackupCategories.Name(category), count);
        }

        void Add(BackupCategory? category, string label, int count)
        {
            bool chosen = _category == category;

            var button = new Button
            {
                Content = $"{label}  {count:N0}",
                Tag = category,
                Padding = new Thickness(12, 6, 12, 6),
            };

            if (chosen)
            {
                // Asked of this page rather than of the application: the
                // application answers with the Windows theme, which is not
                // necessarily the one the page is drawn in. Black on the
                // accent is right in one theme and unreadable in the other,
                // so the colour to put on it is a token too.
                button.Background = OmegaAssetStudio.WinUI.Services.OmegaThemeBrushes.For("OmegaAssetStudio.AccentBrush");
                button.Foreground = OmegaAssetStudio.WinUI.Services.OmegaThemeBrushes.For("OmegaAssetStudio.OnAccentBrush");
            }

            button.Click += (sender, _) =>
            {
                _category = (sender as Button)?.Tag as BackupCategory?;
                ApplyFilter();
            };

            CategoryTabs.Children.Add(button);
        }
    }

    private void ApplyFilter()
    {
        string needle = SearchBox.Text.Trim();
        string game = ClientFilter.SelectedItem as string ?? AllGames;

        IEnumerable<BackupRow> query = _all;

        if (_category is BackupCategory only) query = query.Where(r => r.Category == only);

        if (game == Elsewhere) query = query.Where(r => r.GameName.Length == 0);
        else if (game != AllGames)
            query = query.Where(r => r.GameName.Equals(game, StringComparison.OrdinalIgnoreCase));

        if (needle.Length > 0)
        {
            query = query.Where(r =>
                r.FileName.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                r.FolderPath.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        _visible.Clear();

        // Grouped by who they belong to and then by name, so one character's
        // files sit together instead of scattered through the list.
        foreach (BackupRow row in query
                     .OrderBy(r => r.Owner, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(r => r.FileName, StringComparer.OrdinalIgnoreCase))
        {
            _visible.Add(row);
        }

        RefreshCategoryTabs();
        ActualThemeChanged -= Themed_ActualThemeChanged;
        ActualThemeChanged += Themed_ActualThemeChanged;

        CountText.Text = _all.Count == 0
            ? string.Empty
            : $"{_visible.Count:N0} shown of {_all.Count:N0}";

        // The tiles count what the dropdown is showing, so picking a game
        // answers "what have I changed in THIS install" rather than in all of
        // them at once.
        ProtectedCount.Text = _visible.Count.ToString("N0");
        ChangedCount.Text = _visible.Count(r => r.IsChanged).ToString("N0");
        UntouchedCount.Text = _visible.Count(r => r.IsUntouched).ToString("N0");
    }

    private List<BackupEntry> SelectedEntries() =>
        BackupList.SelectedItems.OfType<BackupRow>().Select(r => r.Entry).ToList();

    private void SelectAll_Click(object sender, RoutedEventArgs e) => BackupList.SelectAll();

    private async void RestoreSelected_Click(object sender, RoutedEventArgs e)
    {
        List<BackupEntry> selected = SelectedEntries();
        if (selected.Count == 0)
        {
            StatusText.Text = "Select something to restore first.";
            return;
        }

        await RestoreAsync(selected);
    }

    private async void RestoreAll_Click(object sender, RoutedEventArgs e)
    {
        List<BackupEntry> shown = _visible.Select(r => r.Entry).ToList();
        if (shown.Count == 0)
        {
            StatusText.Text = "Nothing to restore.";
            return;
        }

        await RestoreAsync(shown);
    }

    /// <summary>
    /// Restores after confirming. Restoring overwrites live game files, so it is
    /// always confirmed and always says exactly how many.
    /// </summary>
    private async Task RestoreAsync(List<BackupEntry> entries)
    {
        var dialog = new ContentDialog
        {
            Title = "Restore original files?",
            Content = $"This will overwrite {entries.Count:N0} file(s) in your game folder with the " +
                      "originals saved before they were changed. The backups are kept, so you can do " +
                      "this again later.",
            PrimaryButtonText = $"Restore {entries.Count:N0} file(s)",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        StatusText.Text = $"Restoring {entries.Count:N0} file(s)...";

        RestoreReport report = await Task.Run(() => _manager.RestoreAll(entries));

        StatusText.Text = report.AllSucceeded
            ? $"Restored {report.Restored:N0} file(s)."
            : $"Restored {report.Restored:N0} file(s); {report.Failures.Count} could not be. " +
              $"First problem: {report.Failures[0]}";

        Load();
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        List<BackupEntry> selected = SelectedEntries();
        if (selected.Count == 0)
        {
            StatusText.Text = "Select something to forget first.";
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Forget these backups?",
            Content = $"This deletes {selected.Count:N0} saved original(s). Your game files are not " +
                      "touched, but you will no longer be able to undo the changes made to them.",
            PrimaryButtonText = $"Forget {selected.Count:N0}",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        RestoreReport report = await Task.Run(() => _manager.Delete(selected));

        StatusText.Text = report.AllSucceeded
            ? $"Forgot {report.Restored:N0} backup(s)."
            : $"Forgot {report.Restored:N0}; {report.Failures.Count} could not be removed.";

        Load();
    }
}
