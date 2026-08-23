using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OmegaAssetStudio2.App.Services;
using OmegaAssetStudio.Calligraphy;
using PowerEntry = OmegaAssetStudio.Calligraphy.PowerEntry;
using OmegaAssetStudio2.App.Icons;
using OmegaAssetStudio2.Core.Calligraphy;
using OmegaAssetStudio2.Core.Materials;
using OmegaAssetStudio2.Core.Meshes;
using OmegaAssetStudio2.Core.Workspace;
using Windows.UI;

namespace OmegaAssetStudio2.App.Pages;

/// <summary>One of a character's skills, in the list.</summary>
public sealed class SkillRow : INotifyPropertyChanged
{
    private ImageSource? _picture;

    /// <summary>The picture the game shows for this power, once it is decoded.</summary>
    public ImageSource? Picture
    {
        get => _picture;
        set
        {
            _picture = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Picture)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public required PowerEntry Power { get; init; }

    /// <summary>The packages this skill's colours live in.</summary>
    public required IReadOnlyList<string> Packages { get; init; }
    public required string Name { get; init; }
    public required string Detail { get; init; }
}

/// <summary>One colour-bearing object in the list.</summary>

/// <summary>One editable colour, with its pending value.</summary>

/// <summary>
/// One colour slot the game's own reader found, and whether it is to be
/// changed.
/// </summary>
/// <remarks>
/// The reader knows seven kinds of colour — material instance parameters,
/// material expressions, the two baked constant vectors, and the three particle
/// distributions. Some it can write and some it can only show; a slot it cannot
/// write is listed anyway, because seeing every colour a skill uses is half of
/// knowing which one to change.
/// </remarks>
public sealed class ColourSlotRow : INotifyPropertyChanged
{
    private bool _chosen;

    public required HeroSkillCatalog.SkillColorEntry Entry { get; init; }

    /// <summary>Whether this slot is one of the ones to be tinted.</summary>
    public bool Chosen
    {
        get => _chosen;
        set
        {
            _chosen = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Chosen)));
        }
    }

    public bool Editable => Entry.Editable;

    public string Name => Entry.ParameterName.Length > 0 ? Entry.ParameterName : Entry.Kind.ToString();

    public string Detail
    {
        get
        {
            string where = Entry.OwnerLabel.Length > 0 ? Entry.OwnerLabel : Path.GetFileName(Entry.SourceUpkPath);

            string note = Entry.Editable ? string.Empty : "  — shown only, cannot be written";
            string across = Entry.IsCrossPackage ? "  — shared with other packages" : string.Empty;

            return where + across + note;
        }
    }

    public SolidColorBrush SwatchBrush
    {
        get
        {
            static byte Eight(float v) => (byte)Math.Clamp((int)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f), 0, 255);

            return new SolidColorBrush(Color.FromArgb(
                255, Eight(Entry.CurrentColor.X), Eight(Entry.CurrentColor.Y), Eight(Entry.CurrentColor.Z)));
        }
    }

    public string ValueText =>
        $"{Entry.CurrentColor.X:0.###}, {Entry.CurrentColor.Y:0.###}, {Entry.CurrentColor.Z:0.###}";

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class SkillRecolorPage : Page
{
    private readonly ColourCatalog _catalog = new();
    private readonly GameIconLoader _icons = new();

    /// <summary>
    /// The game's own reader of its character, power and icon data.
    /// </summary>
    /// <remarks>
    /// Taken across from the first Omega Asset Studio whole rather than
    /// rewritten. It lists the powers a hero actually has, with the names the
    /// game shows for them — Hammer Strike, Crack the Sky, God Blast — and the
    /// icon each one carries, none of which is recoverable from package names.
    /// </remarks>
    private readonly HeroSkillCatalog _skillCatalog = new();
    /// <summary>Every colour slot the reader found for the chosen skill.</summary>
    private readonly ObservableCollection<ColourSlotRow> _slots = [];

    private readonly ObservableCollection<RosterRow> _rosterVisible = [];
    private readonly ObservableCollection<SkillRow> _skillsVisible = [];

    private List<RosterRow> _roster = [];
    private List<SkillRow> _skills = [];

    private CancellationTokenSource? _scanCancellation;
    private GameClient? _client;
    private RosterRow? _character;

    /// <summary>
    /// False until every control exists. Markup can raise a selection change
    /// while the page is still being built, before the controls a handler
    /// touches have been created.
    /// </summary>
    private bool _ready;

    public SkillRecolorPage()
    {
        InitializeComponent();

        MaterialList.ItemsSource = _slots;
        RosterList.ItemsSource = _rosterVisible;
        SkillList.ItemsSource = _skillsVisible;

        ClientPicker.ClientChanged += (_, client) =>
        {
            _client = client;
            ClearSelection();
            LoadRoster();
            StatusText.Text = client is null
                ? "Add a game folder on the Home page first."
                : $"Pick a character, then one of their skills.";
        };
        _client = ClientPicker.SelectedClient;

        Loaded += SkillRecolorPage_Loaded;
    }

    private void SkillRecolorPage_Loaded(object sender, RoutedEventArgs e)
    {
        _ready = true;

        LoadRoster();
    }

    // ---- Who, and which of their skills ----

    private async void LoadRoster()
    {
        if (!_ready) return;

        _roster = [];
        _skills = [];
        ApplyRosterFilter();
        ApplySkillFilter();

        if (_client is null) return;

        // Heroes only. The rest of the cast has no power list in the game's
        // data, so nothing shown for them could be checked against it.
        const RosterCategory category = RosterCategory.Hero;

        GameClient client = _client;
        IReadOnlyList<RosterEntry> entries =
            await Task.Run(() => CharacterRoster.Build(client, category));

        _roster = entries
            .Select(entry => new RosterRow
            {
                Name = entry.Character,
                Detail = entry.Subtitle,
                PackagePath = entry.PackagePath,
                Token = entry.Token,
                VariantToken = entry.VariantToken,
                SearchText = entry.DisplayName,
            })
            .ToList();

        ApplyRosterFilter();

        await ShowPortraitsAsync(client, _roster);
    }

    /// <summary>
    /// Fills in the characters' portraits after the list is already up.
    /// </summary>
    /// <remarks>
    /// One at a time and after the fact, because the icon packages hold
    /// thousands of textures each and decoding them all before showing anything
    /// would leave the panel blank for as long as it took.
    /// </remarks>
    private async Task ShowPortraitsAsync(GameClient client, List<RosterRow> rows)
    {
        foreach (RosterRow row in rows)
        {
            // Somebody else asked for a different list while this was running.
            if (!ReferenceEquals(_roster, rows)) return;

            // The costume's own portrait, and the character's only where a
            // costume has none of its own.
            string asset = await Task.Run(() =>
            {
                string mine = PowerTree.IconForPackage(client, row.PackagePath);

                return mine.Length > 0 ? mine : PowerTree.IconFor(client, row.Token);
            });

            if (asset.Length == 0) continue;

            row.Picture = await _icons.TryLoadAsync(asset, client.CookedPath);
        }
    }

    private void RosterSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyRosterFilter();

    private void ApplyRosterFilter()
    {
        string needle = RosterSearchBox.Text.Trim();

        IEnumerable<RosterRow> query = _roster;
        if (needle.Length > 0)
            query = query.Where(r => r.SearchText.Contains(needle, StringComparison.OrdinalIgnoreCase));

        _rosterVisible.Clear();
        foreach (RosterRow row in query) _rosterVisible.Add(row);
    }

    private void RosterList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not RosterRow row) return;

        if (ReferenceEquals(RosterList.SelectedItem, row)) LoadSkills(row);
        else RosterList.SelectedItem = row;
    }

    private void RosterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (RosterList.SelectedItem is RosterRow row) LoadSkills(row);
    }

    private async void LoadSkills(RosterRow character)
    {
        _character = character;
        _skills = [];
        ApplySkillFilter();

        if (_client is null) return;

        SkillsHeading.Text = $"Skills — {character.Name}";
        StatusText.Text = $"Listing what {character.Name} can do…";

        GameClient client = _client;
        string token = character.Token.ToLowerInvariant();

        _skillCatalog.TryOpenArchive(Path.Combine(client.RootPath ?? string.Empty, "Data", "Game", "Calligraphy.sip"));

        IReadOnlyList<PowerEntry> powers = await _skillCatalog.GetSkillsAsync(token);

        // The user may have picked somebody else while this was running.
        if (!ReferenceEquals(_character, character)) return;

        _skills = powers
            .Select(power => new SkillRow
            {
                Power = power,
                Name = power.DisplayName,
                Detail = power.PowerUnrealClassName,
                Packages = PackagesFor(client, power),
            })
            .ToList();

        ApplySkillFilter();

        // Started before anything that might return, so the pictures are asked
        // for whichever way the status line below goes.
        _ = ShowSkillIconsAsync(client, character, _skills);

        StatusText.Text = _skills.Count == 0
            ? $"The game lists no powers for {character.Name}."
            : $"{_skills.Count:N0} powers, as the game itself lists them for {character.Name}. "
              + "Pick one to see the colours it uses.";
    }

    /// <summary>
    /// The packages a skill's colours live in.
    /// </summary>
    /// <remarks>
    /// A power says which class implements it, and that class is the package —
    /// PowerThor_ThunderHammer is UC__PowerThor_ThunderHammer_SF.upk. The
    /// effects it applies, the things it throws and what it leaves behind are
    /// named after the same class, and a skill's colour is very often in one of
    /// those rather than in the power's own package.
    /// </remarks>
    private static IReadOnlyList<string> PackagesFor(GameClient client, PowerEntry power)
    {
        string cooked = client.CookedPath ?? string.Empty;
        string cls = power.PowerUnrealClassName;

        if (cooked.Length == 0 || cls.Length == 0) return [];

        var found = new List<string>();

        string own = Path.Combine(cooked, "UC__" + cls + "_SF.upk");
        if (File.Exists(own)) found.Add(own);

        foreach (string prefix in EffectPrefixes)
        {
            foreach (string path in Directory.EnumerateFiles(cooked, prefix + "*_SF.upk"))
            {
                if (Path.GetFileNameWithoutExtension(path).Contains(cls, StringComparison.OrdinalIgnoreCase))
                    found.Add(path);
            }
        }

        return found;
    }

    /// <summary>Where the rest of a skill's appearance is kept.</summary>
    private static readonly string[] EffectPrefixes =
    [
        "UC__MarvelConditionEffect_",
        "UC__MarvelProjectile_",
        "UC__MarvelEntity_",
        "UC__ItemPower",
    ];

    /// <summary>Fills in the powers' pictures after the list is already up.</summary>
    private async Task ShowSkillIconsAsync(GameClient client, RosterRow character, List<SkillRow> rows)
    {
        foreach (SkillRow row in rows)
        {
            if (!ReferenceEquals(_character, character)) return;
            if (row.Power.IconAssetPath.Length == 0) continue;

            row.Picture = await _icons.TryLoadAsync(row.Power.IconAssetPath, client.CookedPath);
        }
    }

    private void SkillSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplySkillFilter();

    private void ApplySkillFilter()
    {
        string needle = SkillSearchBox.Text.Trim();

        IEnumerable<SkillRow> query = _skills;
        if (needle.Length > 0)
            query = query.Where(r => r.Name.Contains(needle, StringComparison.OrdinalIgnoreCase));

        _skillsVisible.Clear();
        foreach (SkillRow row in query) _skillsVisible.Add(row);

        CountText.Text = _skills.Count == 0
            ? string.Empty
            : $"{_skillsVisible.Count:N0} shown of {_skills.Count:N0}";
    }

    private void SkillList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SkillRow row) return;

        if (ReferenceEquals(SkillList.SelectedItem, row)) _ = LoadSkillColoursAsync(row);
        else SkillList.SelectedItem = row;
    }

    private void SkillList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SkillList.SelectedItem is SkillRow row) _ = LoadSkillColoursAsync(row);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _scanCancellation?.Cancel();

    private void ClearSelection()
    {
        _slots.Clear();
        SaveMessage.Text = string.Empty;
        UpdateSaveState();
    }

    private void UpdateSaveState()
    {
        int ticked = _slots.Count(s => s.Chosen && s.Editable);

        SaveButton.IsEnabled = ticked > 0 && _target is not null;
        RevertButton.IsEnabled = ticked > 0;
    }

    /// <summary>The colour every ticked slot is set to.</summary>
    private Windows.UI.Color? _target;

    private async void PickButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new ColorPicker
        {
            Color = _target ?? Color.FromArgb(255, 255, 255, 255),
            IsAlphaEnabled = false,
            IsColorSliderVisible = true,
            IsHexInputVisible = true,
        };

        var dialog = new ContentDialog
        {
            Title = "Colour to apply",
            Content = picker,
            PrimaryButtonText = "Use this colour",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _target = picker.Color;

        TargetSwatch.Background = new SolidColorBrush(picker.Color);
        TargetText.Text = $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}";

        UpdateSaveState();
    }

    private void RevertButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (ColourSlotRow row in _slots) row.Chosen = false;

        SaveMessage.Text = "Nothing is ticked. Nothing was written.";
        UpdateSaveState();
    }

    /// <summary>
    /// Writes the chosen colour into every ticked slot.
    /// </summary>
    /// <remarks>
    /// The ticked slots are handed over as an allowlist, so a shared material
    /// library has only the one material this skill borrows touched and every
    /// other material in that file is left exactly as it was.
    /// </remarks>
    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_target is not Windows.UI.Color colour) return;

        List<ColourSlotRow> ticked = _slots.Where(s => s.Chosen && s.Editable).ToList();
        if (ticked.Count == 0) return;

        SaveButton.IsEnabled = false;
        SaveMessage.Text = "Writing...";

        try
        {
            var packages = ticked
                .Select(s => s.Entry.SourceUpkPath)
                .Where(p => p.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var allowed = new HashSet<string>(
                ticked.Where(s => s.Entry.ExportPath.Length > 0).Select(s => s.Entry.ExportPath),
                StringComparer.OrdinalIgnoreCase);

            var wanted = new System.Numerics.Vector3(colour.R / 255f, colour.G / 255f, colour.B / 255f);

            var writer = new OmegaAssetStudio.Cooked.SkillColorWriter();

            OmegaAssetStudio.Cooked.SkillColorWriter.WriteReport report =
                await writer.ApplyTintAsync(packages, wanted, includedExportPaths: allowed);

            SaveMessage.Text =
                $"{report.Edits.Count:N0} slot(s) written across {report.UpksSaved:N0} package(s)."
                + (report.Errors.Count > 0 ? $" {report.Errors.Count} could not be written." : string.Empty);

            StatusText.Text = SaveMessage.Text;

            // Read the skill again so what is listed is what is on disk.
            if (SkillList.SelectedItem is SkillRow row) await LoadSkillColoursAsync(row);
        }
        catch (Exception ex)
        {
            SaveMessage.Text = $"Could not write: {ex.Message}";
            CrashLog.Write("SkillRecolor.Save", ex);
        }
        finally
        {
            UpdateSaveState();
        }
    }

    /// <summary>
    /// Finds every colour one skill uses, the way the game's own reader finds
    /// them.
    /// </summary>
    /// <remarks>
    /// The skill names the effects it plays; those name the particle systems;
    /// those name their emitters and materials, and the colours are in there.
    /// Walking that chain is what finds the shared materials a skill borrows
    /// from another package, which searching the skill's own packages never
    /// does.
    /// <para>
    /// The hero's own package is read as well. Weapon trails and the effects
    /// hung off animation notifies live there rather than in any power's
    /// package, and a skill that swings a weapon shows them.
    /// </para>
    /// </remarks>
    private async Task LoadSkillColoursAsync(SkillRow skill)
    {
        _scanCancellation?.Cancel();
        _scanCancellation = new CancellationTokenSource();

        ClearSelection();

        if (_client is null) return;

        ScanBar.Visibility = Visibility.Visible;
        CancelButton.IsEnabled = true;

        StatusText.Text = $"Reading the colours {skill.Name} uses…";

        string cooked = _client.CookedPath ?? string.Empty;

        try
        {
            PowerVfxResolver.ResolvedVfx? vfx = await _skillCatalog.ResolveSkillVfxAsync(skill.Power, cooked);

            var found = new List<HeroSkillCatalog.SkillColorEntry>();

            if (vfx is not null && vfx.Bindings.Count > 0)
                found.AddRange(await _skillCatalog.CollectSkillColorsAsync(vfx));

            // Anything the hero carries rather than the power, kept apart from
            // what is already listed so a colour is not offered twice.
            var seen = new HashSet<string>(
                found.Where(e => e.ExportPath.Length > 0).Select(e => e.ExportPath),
                StringComparer.OrdinalIgnoreCase);

            foreach (HeroSkillCatalog.SkillColorEntry entry in
                     await _skillCatalog.CollectHeroPlayerColorsAsync(skill.Power.CharacterToken, cooked))
            {
                if (entry.ExportPath.Length == 0 || seen.Add(entry.ExportPath)) found.Add(entry);
            }

            foreach (HeroSkillCatalog.SkillColorEntry entry in found)
                // Nothing is ticked to begin with. One skill can carry hundreds of
                // slots, and starting with all of them armed makes the first
                // click a mass edit nobody asked for.
                _slots.Add(new ColourSlotRow { Entry = entry, Chosen = false });

            int editable = found.Count(e => e.Editable);

            StatusText.Text = found.Count == 0
                ? $"{skill.Name} has no colours the reader can see. It may play no effects of its own."
                : $"{skill.Name}: {found.Count:N0} colour slot(s), {editable:N0} of them writable. "
                  + "Tick the ones to change, pick a colour, then apply.";

            foreach (ColourSlotRow row in _slots) row.PropertyChanged += (_, _) => UpdateSaveState();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Reading {skill.Name} failed: {ex.Message}";
            CrashLog.Write("SkillRecolor.Skill", ex);
        }
        finally
        {
            ScanBar.Visibility = Visibility.Collapsed;
            CancelButton.IsEnabled = false;
            UpdateSaveState();
        }
    }
}
