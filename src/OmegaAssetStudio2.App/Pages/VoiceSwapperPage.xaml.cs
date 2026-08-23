using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using OmegaAssetStudio2.App.Audio;
using OmegaAssetStudio2.App.Services;
using OmegaAssetStudio2.Core.Audio;
using OmegaAssetStudio2.Core.Workspace;
using Windows.Storage.Pickers;

namespace OmegaAssetStudio2.App.Pages;

/// <summary>One audio container in the list.</summary>
public sealed class AudioPackageRow
{
    public required AudioPackageSummary Summary { get; init; }
    public required string Subject { get; init; }
    public required string Detail { get; init; }

    /// <summary>
    /// The voice sets this character has, one per costume that changes it.
    /// </summary>
    /// <remarks>
    /// Empty for most of them. Where it is not, the lines are usually kept in
    /// another container entirely — one character's four sets are all in
    /// InitialDownloadChunk — so choosing one here searches for them rather
    /// than reading this container.
    /// </remarks>
    public IReadOnlyList<CostumeVoice> Costumes { get; init; } = [];

    public Visibility CostumeVisibility =>
        Costumes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>One collapsible section in the container list, and its rows.</summary>
public sealed class AudioPackageGroup : List<AudioPackageRow>, INotifyPropertyChanged
{
    private bool _isOpen;

    public AudioPackageGroup(AudioCategory category, IEnumerable<AudioPackageRow> rows) : base(rows)
    {
        Category = category;
        Heading = $"{AudioCategories.NameOf(category)} ({Count})";
    }

    public AudioCategory Category { get; }
    public string Heading { get; }

    /// <summary>Whether the section is open. Every section starts closed.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (_isOpen == value) return;

            _isOpen = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOpen)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>One entry in the language list.</summary>
/// <remarks>
/// The game marks a sound container's language with a three-letter code on the
/// end of its file name. The codes are the game's own, not Windows culture
/// names, so they are translated here for display and left alone for matching.
/// </remarks>
public sealed class LanguageOption
{
    private const string EverythingCode = "*";
    private const string UnmarkedCode = "";

    public LanguageOption(string code)
    {
        Code = code;
        Label = code switch
        {
            EverythingCode => "All languages",
            UnmarkedCode => "No language (sound effects)",
            _ => $"{NameOf(code)} ({code})",
        };
    }

    public string Code { get; }
    public string Label { get; }

    public bool IsEverything => Code == EverythingCode;

    public bool Matches(string language) =>
        IsEverything || string.Equals(language, Code, StringComparison.OrdinalIgnoreCase);

    public static LanguageOption Everything { get; } = new(EverythingCode);
    public static LanguageOption Unmarked { get; } = new(UnmarkedCode);

    /// <summary>The plain name for a code, or the code itself if unfamiliar.</summary>
    public static string NameOf(string code) => code.ToUpperInvariant() switch
    {
        "INT" => "English",
        "ENG" => "English",
        "DEU" => "German",
        "FRA" => "French",
        "ITA" => "Italian",
        "ESN" => "Spanish",
        "ESM" => "Spanish (Latin America)",
        "PTB" => "Portuguese (Brazil)",
        "RUS" => "Russian",
        "POL" => "Polish",
        "JPN" => "Japanese",
        "KOR" => "Korean",
        "CHN" => "Chinese",
        "CHT" => "Chinese (Traditional)",
        _ => code,
    };
}

/// <summary>One sound in the list.</summary>
public sealed class SoundRow
{
    public required AudioEntry Entry { get; init; }
    public required string Name { get; init; }
    public required string SizeText { get; init; }
    public required SoundKind Kind { get; init; }

    /// <summary>
    /// The container this sound is in. Carried per row because a costume's
    /// lines can come from more than one.
    /// </summary>
    public required string ContainerPath { get; init; }
}

/// <summary>One collapsible section of sounds, and the sounds in it.</summary>
public sealed class SoundGroup : List<SoundRow>, INotifyPropertyChanged
{
    private bool _isOpen = true;

    public SoundGroup(SoundKind kind, IEnumerable<SoundRow> rows) : base(rows)
    {
        Kind = kind;
        Heading = $"{SoundKinds.NameOf(kind)} ({Count})";
    }

    public SoundKind Kind { get; }
    public string Heading { get; }

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (_isOpen == value) return;

            _isOpen = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOpen)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class VoiceSwapperPage : Page
{
    private readonly AudioCatalog _catalog = new();
    private readonly SoundPreviewService _preview = new();

    private readonly ObservableCollection<AudioPackageGroup> _groups = [];

    /// <summary>Guards the clearing of the other sections' selections.</summary>
    private bool _choosing;

    /// <summary>Every section's list, so a choice in one can clear the rest.</summary>
    private readonly HashSet<ListView> _lists = [];
    private readonly ObservableCollection<SoundGroup> _soundGroups = [];

    /// <summary>Every section's list of sounds, for the same reason as above.</summary>
    private readonly HashSet<ListView> _soundLists = [];

    /// <summary>Names recovered for the container on show, if any.</summary>
    private SoundNameIndex _names = SoundNameIndex.Empty;

    /// <summary>Every sound name in the chosen install, once read.</summary>
    private SoundNameCatalog _soundNames = SoundNameCatalog.Empty;

    /// <summary>Which voice sets each character has, worked out during a search.</summary>
    private Dictionary<string, IReadOnlyList<CostumeVoice>> _costumes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A costume list of sounds, when one is shown instead of a container.</summary>
    private List<PlacedSound> _costumeSounds = [];

    private List<AudioPackageRow> _allPackages = [];
    private GameClient? _client;
    private AudioPackage? _openPackage;
    private AudioEntry? _selectedSound;

    public VoiceSwapperPage()
    {
        InitializeComponent();

        PackageGroups.ItemsSource = _groups;
        SoundGroups.ItemsSource = _soundGroups;

        ClientPicker.ClientChanged += (_, client) =>
        {
            _client = client;
            _soundNames = SoundNameCatalog.Empty;
            _allPackages = [];
            _openPackage = null;
            _soundGroups.Clear();
            _soundLists.Clear();
            ClearSelection();
            ShowLanguages();
            ApplyFilter();
            StatusText.Text = client is null
                ? "Add a game folder on the Home page first."
                : $"Ready to search {client.DisplayName}.";
        };
        _client = ClientPicker.SelectedClient;

        ShowDecoderState();
    }

    /// <summary>
    /// Shows how to get the decoder when it is not installed, and hides all of
    /// that once it is.
    /// </summary>
    private void ShowDecoderState()
    {
        bool have = SoundPreviewService.IsDecoderAvailable;

        DecoderMissingPanel.Visibility = have ? Visibility.Collapsed : Visibility.Visible;

        if (have)
        {
            PlayMessage.Text = string.Empty;
            return;
        }

        PlayMessage.Text = "Sounds cannot be listened to yet.";
    }

    /// <summary>Opens the decoder's own download page.</summary>
    private async void GetDecoderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(SoundPreviewService.DecoderHomePage));

            // That page offers six builds and only one of them is this. Name it
            // exactly rather than saying "the Windows build" and leaving the
            // user to work out which of the three Windows ones is meant.
            DecoderPathText.Text =
                "On that page, under Downloads, take “Command-line (64-bit)” — the one with the blue " +
                "Win label. Unzip it anywhere, then press “I already have it” and choose that folder.";
        }
        catch (Exception ex)
        {
            CrashLog.Write("VoiceSwapper.GetDecoder", ex);
            DecoderPathText.Text = $"Could not open the page. It is at {SoundPreviewService.DecoderHomePage}";
        }
    }

    /// <summary>Points the application at a copy the user already has.</summary>
    private void FindDecoderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);

            // On this thread deliberately: the dialog is modal and needs the
            // apartment the interface lives in, which is the one the interface
            // runs on. Pushing it to the thread pool lands it in the wrong one.
            string? path = FolderBrowser.Pick(hwnd, "Where did you unzip vgmstream?");
            if (path is null) return;

            if (!SoundPreviewService.HoldsDecoder(path))
            {
                DecoderPathText.Text =
                    $"{SoundPreviewService.DecoderExecutable} is not in that folder. Choose the one you " +
                    "unzipped, which holds that program.";

                return;
            }

            AppSettings.Current.DecoderFolder = path;
            AppSettings.Save();

            DecoderPathText.Text = $"Found it in {path}. Sounds can be listened to now.";

            ShowDecoderState();
        }
        catch (Exception ex)
        {
            CrashLog.Write("VoiceSwapper.FindDecoder", ex);
            DecoderPathText.Text = $"That folder could not be used: {ex.Message}";
        }
    }

    /// <summary>How many sounds a container holds, and how big it is.</summary>
    private static string Describe(AudioPackageSummary summary)
    {
        string sounds = $"{summary.SoundCount:N0} sounds";

        // Say where they are when they are not where a person would expect. A
        // container of banks reads as empty otherwise, which is what made
        // one container look like it had nothing in it.
        if (summary.EmbeddedCount > 0 && summary.StreamCount == 0)
            sounds += " (in banks)";
        else if (summary.EmbeddedCount > 0)
            sounds += $" ({summary.EmbeddedCount:N0} in banks)";

        return $"{sounds} — {summary.TotalBytes / 1024 / 1024:N0} MB";
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null)
        {
            StatusText.Text = "No game selected. Add one on the Home page first.";
            return;
        }

        ScanButton.IsEnabled = false;
        ScanBar.Visibility = Visibility.Visible;

        int skipped = 0;
        try
        {
            var progress = new Progress<AudioScanProgress>(p =>
            {
                ScanBar.Maximum = Math.Max(1, p.Total);
                ScanBar.Value = p.Scanned;
                StatusText.Text = $"Reading {p.Scanned:N0} of {p.Total:N0} containers";
            });

            IReadOnlyList<AudioPackageSummary> found =
                await _catalog.ScanAsync(_client, progress, onError: (_, _) => skipped++);

            _allPackages = found
                .OrderBy(p => p.Subject, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Language, StringComparer.OrdinalIgnoreCase)
                .Select(p => new AudioPackageRow
                {
                    Summary = p,
                    Subject = p.Language.Length > 0 ? $"{p.Subject} ({p.Language})" : p.Subject,
                    Detail = Describe(p),
                })
                .ToList();

            ShowLanguages();
            ApplyFilter();

            long total = found.Sum(p => (long)p.SoundCount);
            StatusText.Text =
                $"Found {found.Count:N0} containers holding {total:N0} sounds." +
                (skipped > 0 ? $" {skipped} could not be read." : string.Empty);

            // Now the names. The containers record only numbers, and the names
            // are spread across every package in the install, so this is one
            // pass over all of them — half a minute the first time, and kept
            // afterwards, so later searches skip straight past it.
            GameClient client = _client;

            var reading = new Progress<SoundNameProgress>(p =>
            {
                ScanBar.Maximum = Math.Max(1, p.Total);
                ScanBar.Value = p.Read;
                StatusText.Text =
                    $"Reading sound names: {p.Read:N0} of {p.Total:N0} packages, {p.Found:N0} found";
            });

            IReadOnlyList<AudioPackageSummary> characters = found
                .Where(p => p.Category is AudioCategory.Hero or AudioCategory.TeamUp)
                .ToList();

            (_soundNames, _costumes) = await Task.Run(() =>
            {
                SoundNameCatalog names = SoundNameCatalog.LoadOrBuild(client, reading);

                var sets = new Dictionary<string, IReadOnlyList<CostumeVoice>>(StringComparer.OrdinalIgnoreCase);

                foreach (AudioPackageSummary summary in characters)
                {
                    if (sets.ContainsKey(summary.Subject)) continue;

                    IReadOnlyList<CostumeVoice> costumes = CostumeVoices.For(client, summary.Subject);
                    if (costumes.Count > 0) sets[summary.Subject] = costumes;
                }

                return (names, sets);
            });

            // Rebuilt once the voice sets are known, so the rows carry them.
            _allPackages = found
                .OrderBy(p => p.Subject, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Language, StringComparer.OrdinalIgnoreCase)
                .Select(p => new AudioPackageRow
                {
                    Summary = p,
                    Subject = p.Language.Length > 0 ? $"{p.Subject} ({p.Language})" : p.Subject,
                    Detail = Describe(p),
                    Costumes = _costumes.GetValueOrDefault(p.Subject, []),
                })
                .ToList();

            ApplyFilter();

            StatusText.Text =
                $"Found {found.Count:N0} containers holding {total:N0} sounds, " +
                $"and {_soundNames.Count:N0} sound names to go with them." +
                (skipped > 0 ? $" {skipped} containers could not be read." : string.Empty);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Search failed: {ex.Message}";
            CrashLog.Write("VoiceSwapper.Scan", ex);
        }
        finally
        {
            ScanButton.IsEnabled = true;
            ScanBar.Visibility = Visibility.Collapsed;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void LanguagePicker_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    /// <summary>
    /// Fills the language list from what the search actually found.
    /// </summary>
    /// <remarks>
    /// Not a fixed list. Which languages a client ships varies: the Steam
    /// install here carries INT, DEU and FRA and nothing else, so offering
    /// Japanese or Russian would be offering an empty list. Whatever is on
    /// disk is what appears.
    /// </remarks>
    private void ShowLanguages()
    {
        string? chosen = (LanguagePicker.SelectedItem as LanguageOption)?.Code;

        var options = new List<LanguageOption> { LanguageOption.Everything };

        options.AddRange(_allPackages
            .Select(r => r.Summary.Language)
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => LanguageOption.NameOf(l), StringComparer.CurrentCulture)
            .Select(l => new LanguageOption(l)));

        // Anything with no language suffix at all — the shared sound effects.
        if (_allPackages.Any(r => r.Summary.Language.Length == 0))
            options.Add(LanguageOption.Unmarked);

        LanguagePicker.ItemsSource = options;

        LanguagePicker.SelectedItem =
            options.FirstOrDefault(o => o.Code == chosen) ?? options[0];
    }

    private void ApplyFilter()
    {
        string needle = SearchBox.Text.Trim();

        IEnumerable<AudioPackageRow> query = _allPackages;
        if (needle.Length > 0)
            query = query.Where(r => r.Subject.Contains(needle, StringComparison.OrdinalIgnoreCase));

        if (LanguagePicker.SelectedItem is LanguageOption { IsEverything: false } language)
            query = query.Where(r => language.Matches(r.Summary.Language));

        List<AudioPackageRow> shown = query.ToList();

        // Grouped under headings — Heroes, Team-Ups, Zones and so on — because a
        // flat list of 227 containers gives no way to find a character except by
        // scrolling. Empty groups are left out entirely.
        _groups.Clear();

        // The old sections' lists go with them.
        _lists.Clear();

        foreach (var group in shown
                     .GroupBy(r => r.Summary.Category)
                     .OrderBy(g => AudioCategories.OrderOf(g.Key)))
        {
            _groups.Add(new AudioPackageGroup(
                group.Key,
                group.OrderBy(r => r.Subject, StringComparer.CurrentCultureIgnoreCase))
            {
                // Closed after a search of the whole client, which is what the
                // button does. Open while something is typed in the search box,
                // because the point of typing is to see the few rows that match
                // rather than a heading hiding them.
                IsOpen = needle.Length > 0,
            });
        }

        CountText.Text = _allPackages.Count == 0
            ? string.Empty
            : $"{shown.Count:N0} shown of {_allPackages.Count:N0}";
    }

    /// <summary>
    /// A container was chosen in one of the sections.
    /// </summary>
    /// <remarks>
    /// Each section owns its own list, so a choice in one has to clear the
    /// others by hand — otherwise two rows stay highlighted and the panel on
    /// the right shows only one of them.
    /// </remarks>
    private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_choosing || sender is not ListView list) return;

        _lists.Add(list);

        if (list.SelectedItem is null) return;

        _choosing = true;
        try
        {
            foreach (ListView other in _lists.Where(l => l != list))
                other.SelectedItem = null;
        }
        finally
        {
            _choosing = false;
        }

        _ = ShowContainerAsync(list.SelectedItem as AudioPackageRow);
    }

    /// <summary>
    /// Opens a container and lists what is in it, grouped by what each sound is
    /// for.
    /// </summary>
    /// <remarks>
    /// Recovering the names means reading the container's banks and the name
    /// tables of the packages that mention its subject — around 150 ms for a
    /// character, which is too long to hold the interface still for, so it
    /// happens off this thread.
    /// </remarks>
    private async Task ShowContainerAsync(AudioPackageRow? chosen)
    {
        _soundGroups.Clear();
        _soundLists.Clear();
        _names = SoundNameIndex.Empty;
        _costumeSounds = [];
        SoundSearchBox.Text = string.Empty;
        ClearSelection();

        if (chosen is not { } row) return;

        try
        {
            StatusText.Text = $"Reading {row.Summary.Name}...";

            SoundNameCatalog catalog = _soundNames;

            (AudioPackage package, SoundNameIndex names) = await Task.Run(() =>
            {
                AudioPackage opened = AudioPackage.Open(row.Summary.Path);

                return (opened, SoundNames.Recover(opened, catalog));
            });

            _openPackage = package;
            _names = names;

            ShowSounds();

            int named = _openPackage.Sounds.Count(e => _names.Of(e.Id) is not null);

            StatusText.Text =
                $"{row.Summary.Name}: {_openPackage.Sounds.Count():N0} sounds, " +
                $"{named:N0} with names recovered.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not read that container: {ex.Message}";
            CrashLog.Write("VoiceSwapper.OpenPackage", ex);
        }
    }

    private void SoundSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ShowSounds();

    /// <summary>
    /// Shows the lines a character speaks while wearing one costume.
    /// </summary>
    /// <remarks>
    /// Not read from the container the dropdown sits on. One character has 71
    /// sounds there and every one is an effect; those four voices are kept in
    /// InitialDownloadChunk with the rest of the launch heroes. So this searches
    /// the containers of the chosen language for the lines that set asks for.
    /// </remarks>
    private async void CostumePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox picker || picker.SelectedItem is not CostumeVoice costume) return;
        if (_client is null) return;

        try
        {
            StatusText.Text = $"Finding the {costume.Costume} lines for {costume.Hero}...";

            GameClient client = _client;
            SoundNameCatalog catalog = _soundNames;

            string language = (LanguagePicker.SelectedItem as LanguageOption) is { IsEverything: false } chosen
                ? chosen.Code
                : "INT";

            List<PlacedSound> sounds = await Task.Run(() =>
                CostumeVoices.Sounds(client, costume, language, catalog).ToList());

            _openPackage = null;
            _names = SoundNameIndex.Empty;
            _costumeSounds = sounds;
            SoundSearchBox.Text = string.Empty;

            ClearSelection();
            ShowSounds();

            StatusText.Text = sounds.Count == 0
                ? $"{costume.Hero}, {costume.Costume}: no lines found in {language}."
                : $"{costume.Hero}, {costume.Costume}: {sounds.Count:N0} sounds, from " +
                  $"{string.Join(", ", sounds.Select(s => s.ContainerName).Distinct().Take(3))}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not read that costume: {ex.Message}";
            CrashLog.Write("VoiceSwapper.Costume", ex);
        }
    }

    /// <summary>Lists the open container's sounds, grouped by what they are for.</summary>
    private void ShowSounds()
    {
        _soundGroups.Clear();
        _soundLists.Clear();

        string needle = SoundSearchBox.Text.Trim();
        var rows = new List<SoundRow>();

        // Either a container is open, or a costume list is being shown - and
        // those lines can come from several containers at once.
        IEnumerable<(AudioEntry Entry, string? Name, string Path)> sounds =
            _openPackage is not null
                ? _openPackage.Sounds
                    .OrderBy(s => s.Offset)
                    .Select(s => (s, _names.Of(s.Id), _openPackage.Path))
                : _costumeSounds
                    .Select(s => (s.Entry, (string?)s.Name, s.ContainerPath));

        foreach ((AudioEntry entry, string? name, string path) in sounds)
        {
            if (needle.Length > 0)
            {
                string haystack = name ?? entry.Id.ToString();
                if (!haystack.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
            }

            rows.Add(new SoundRow
            {
                Entry = entry,
                Name = name ?? entry.Id.ToString(),
                SizeText = $"{entry.Size / 1024:N0} KB",
                Kind = SoundKinds.Of(name),
                ContainerPath = path,
            });
        }

        foreach (var group in rows
                     .GroupBy(r => r.Kind)
                     .OrderBy(g => SoundKinds.OrderOf(g.Key)))
        {
            _soundGroups.Add(new SoundGroup(
                group.Key,
                group.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)));
        }
    }

    /// <summary>Puts the selection back on a sound after the list is rebuilt.</summary>
    private void SelectSound(uint id)
    {
        foreach (SoundGroup group in _soundGroups)
        {
            SoundRow? row = group.FirstOrDefault(r => r.Entry.Id == id);
            if (row is null) continue;

            group.IsOpen = true;

            foreach (ListView list in _soundLists)
            {
                if (!list.Items.Contains(row)) continue;

                list.SelectedItem = row;
                return;
            }

            return;
        }
    }

    private async void SoundList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_choosing || sender is not ListView list) return;

        _soundLists.Add(list);

        if (list.SelectedItem is not SoundRow row) return;

        // The open container follows the sound. A costume list is gathered from
        // wherever the lines are kept, so exporting or swapping one has to act
        // on the container that actually holds it.
        if (_openPackage is null ||
            !string.Equals(_openPackage.Path, row.ContainerPath, StringComparison.OrdinalIgnoreCase))
        {
            try { _openPackage = AudioPackage.Open(row.ContainerPath); }
            catch (Exception ex)
            {
                StatusText.Text = $"Could not open that container: {ex.Message}";
                CrashLog.Write("VoiceSwapper.OpenForSound", ex);
                return;
            }
        }

        _choosing = true;
        try
        {
            foreach (ListView other in _soundLists.Where(l => l != list))
                other.SelectedItem = null;
        }
        finally
        {
            _choosing = false;
        }

        _selectedSound = row.Entry;

        DetailName.Text = row.Name;
        DetailInfo.Text =
            $"identifier  {row.Entry.Id}\n" +
            $"size        {row.Entry.Size:N0} bytes\n" +
            $"offset      {row.Entry.Offset:N0}\n" +
            $"language    {(row.Entry.Language.Length > 0 ? row.Entry.Language : "none")}";

        ExportButton.IsEnabled = true;
        ReplaceButton.IsEnabled = true;
        SwapMessage.Text = string.Empty;

        if (!SoundPreviewService.IsDecoderAvailable)
        {
            ShowDecoderState();
            return;
        }

        PlayMessage.Text = "Decoding...";
        try
        {
            string? wave = await _preview.TryDecodeAsync(_openPackage, row.Entry);
            if (wave is null)
            {
                PlayMessage.Text = "This sound could not be decoded.";
                return;
            }

            Player.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(wave));
            PlayMessage.Text = string.Empty;
        }
        catch (Exception ex)
        {
            PlayMessage.Text = $"Could not play this sound: {ex.Message}";
            CrashLog.Write("VoiceSwapper.Preview", ex);
        }
    }

    private void ClearSelection()
    {
        _selectedSound = null;
        DetailName.Text = "Nothing selected";
        DetailInfo.Text = "—";
        ExportButton.IsEnabled = false;
        ReplaceButton.IsEnabled = false;
        Player.Source = null;
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_openPackage is null || _selectedSound is null) return;

        try
        {
            var picker = new FileSavePicker { SuggestedFileName = $"sound_{_selectedSound.Id}" };
            picker.FileTypeChoices.Add("Wwise sound", [".wem"]);

            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            Windows.Storage.StorageFile? file = await picker.PickSaveFileAsync();
            if (file is null) return;

            await SoundPreviewService.ExportAsync(_openPackage, _selectedSound, file.Path);

            SwapMessage.Text =
                $"Exported {_selectedSound.Size:N0} bytes. A replacement must be a .wem no larger than this.";
        }
        catch (Exception ex)
        {
            SwapMessage.Text = $"Export failed: {ex.Message}";
            CrashLog.Write("VoiceSwapper.Export", ex);
        }
    }

    private async void ReplaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_openPackage is null || _selectedSound is null) return;

        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".wem");

            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null) return;

            ReplaceButton.IsEnabled = false;
            SwapMessage.Text = "Checking...";

            byte[] replacement = await File.ReadAllBytesAsync(file.Path);

            AudioReplaceResult check = AudioReplacer.CanReplace(_selectedSound, replacement);
            if (!check.Succeeded)
            {
                SwapMessage.Text = check.Message;
                return;
            }

            SwapMessage.Text = "Writing...";
            AudioReplaceResult result = await AudioReplacer.ReplaceAsync(
                _openPackage, _selectedSound, replacement);

            SwapMessage.Text = result.Message;
            StatusText.Text = result.Message;

            if (result.Succeeded)
            {
                // Re-open so the listed sizes match what is now on disk, and drop
                // the cached preview so the new sound is what plays.
                _preview.Clear();
                Player.Source = null;

                string path = _openPackage.Path;
                _openPackage = AudioPackage.Open(path);

                uint id = _selectedSound.Id;

                // The names belong to the sounds rather than to the file, so
                // they survive the swap and are not recovered again.
                ShowSounds();
                SelectSound(id);
            }
        }
        catch (Exception ex)
        {
            SwapMessage.Text = $"Replace failed: {ex.Message}";
            CrashLog.Write("VoiceSwapper.Replace", ex);
        }
        finally
        {
            ReplaceButton.IsEnabled = _selectedSound is not null;
        }
    }
}
