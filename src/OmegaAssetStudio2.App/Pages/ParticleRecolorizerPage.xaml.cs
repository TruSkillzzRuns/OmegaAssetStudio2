using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OmegaAssetStudio.Calligraphy;
using OmegaAssetStudio.WinUI.Modules.MaterialEditor;
using OmegaAssetStudio.WinUI.Modules.MaterialEditor.Core;
using OmegaAssetStudio.WinUI.Services;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace OmegaAssetStudio2.App.Pages;

// Bulk Vector-param sweep over MaterialInstanceConstant exports in a UPK.
// Groups every Vector parameter by name, lets the user pick groups and dial
// an HSV shift + per-channel multiplier, applies in-memory, then saves all
// modified materials back to the UPK via the existing MaterialEditorService
// writer.
//
// Targets the verified pipeline:
//   - 53,134 MIC imports + Vector/Scalar/Texture parameter tables read by
//     MaterialEditorService.LoadMaterialsFromUpkAsync.
//   - 494K particle-LOD rows in the SQLite index whose color/emissive vectors
//     drive particle materials. Recoloring here = recoloring every VFX that
//     references those MICs.
public sealed partial class ParticleRecolorizerPage : Page
{
    private sealed class GroupRow
    {
        public string Name { get; init; } = string.Empty;
        public List<MaterialParameter> Parameters { get; init; } = new();
        public CheckBox Toggle { get; set; } = null!;
        public Border Swatch { get; set; } = null!;
        public TextBlock Subtitle { get; set; } = null!;
    }

    private readonly MaterialEditorService _service = new();
    private readonly List<MaterialDefinition> _materials = new();
    private readonly Dictionary<string, GroupRow> _groups = new(StringComparer.OrdinalIgnoreCase);
    // Hero / skill picker state (Phase 1 of the "edit a skill's FX colors" flow).
    // Lives entirely in this page — the catalog instance is independent of
    // Animation Preview and is disposed when the page goes away.
    private readonly HeroSkillCatalog _heroSkillCatalog = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<HeroRowVM> _heroRowsAll = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<HeroRowVM> _heroRows = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<SkillRowVM> _skillRows = new();
    private HeroRowVM? _selectedHero;
    private bool _heroesPopulated;
    private bool _modeIsHeroSkill;
    // Phase 2: extra color entries from non-MIC sources (raw Material expressions
    // + particle module color distributions). Displayed below the editable MIC
    // groups; read-only until the byte-patcher lands in Phase 2.5.
    private readonly List<HeroSkillCatalog.SkillColorEntry> _extraColorEntries = new();
    // Last resolved VFX for the selected skill — used by the read-only
    // "Inspect color source" diagnostic.
    private OmegaAssetStudio.Calligraphy.PowerVfxResolver.ResolvedVfx? _currentVfx;
    private SkillRowVM? _currentSkillRow;
    // Original snapshot of (parameter -> base Vector4) captured at load time so
    // re-applying with new slider values doesn't compound on prior applies.
    private readonly Dictionary<MaterialParameter, Vector4> _baseValues = new();
    // Tracks which MaterialDefinition each parameter came from so the per-row
    // subtitle can show the actual material export paths (answers "what am I
    // editing?" — the modder needs more than just "1 param across 1 MIC").
    private readonly Dictionary<MaterialParameter, MaterialDefinition> _paramOwners = new();
    private string _currentUpkPath = string.Empty;

    // UPKs the most recent Apply touched, in the order they were saved.
    // Drives the "Restore from backup" button — disabled until at least one
    // Apply has run, then walks this list back through BackupFileHelper.
    private readonly List<string> _lastSavedUpks = new();

    // Per-slot opt-out sets. A slot starts SELECTED (i.e. not in either set);
    // unchecking its row row adds the key here so Apply skips it.
    // _excludedMicParams holds individual MIC VectorParameter refs the user
    // unchecked (MaterialEditorService writes those by mutating p.VectorValue
    // in memory; we just leave excluded params at their base value).
    // _excludedExportPaths holds full UPK export-path-names for material
    // expressions, Constant3/4Vectors, and particle color modules — those
    // route through SkillColorWriter's byte patcher which gets the set passed
    // in directly.
    private readonly HashSet<MaterialParameter> _excludedMicParams = new();
    private readonly HashSet<string> _excludedExportPaths = new(StringComparer.OrdinalIgnoreCase);

    // 12 hand-picked saturated hues for the QUICK PRESETS row. Picked for
    // skill-VFX recoloring specifically: a clean rainbow plus white, gold, and
    // black so modders can either pick a hue or knock a skill to monochrome.
    private static readonly (string Label, Color Color)[] _presetPalette =
    {
        ("Red",     Color.FromArgb(255, 0xE3, 0x1E, 0x24)),
        ("Orange",  Color.FromArgb(255, 0xF2, 0x7A, 0x1A)),
        ("Yellow",  Color.FromArgb(255, 0xF5, 0xC9, 0x18)),
        ("Lime",    Color.FromArgb(255, 0x7E, 0xCC, 0x29)),
        ("Green",   Color.FromArgb(255, 0x2E, 0xB8, 0x4A)),
        ("Cyan",    Color.FromArgb(255, 0x1A, 0xC7, 0xC7)),
        ("Blue",    Color.FromArgb(255, 0x2A, 0x70, 0xE2)),
        ("Indigo",  Color.FromArgb(255, 0x4B, 0x3B, 0xE2)),
        ("Purple",  Color.FromArgb(255, 0x8A, 0x3D, 0xE2)),
        ("Pink",    Color.FromArgb(255, 0xE0, 0x4D, 0x9F)),
        ("White",   Color.FromArgb(255, 0xFA, 0xFA, 0xFA)),
        ("Gold",    Color.FromArgb(255, 0xD4, 0xA0, 0x32)),
    };

    // Resolved against the theme this page is drawn in, so the cards built in
    // code match the ones built in XAML whichever theme is chosen. Asking the
    // application instead would answer with the Windows theme, which is not
    // necessarily the one on screen.
    private static Brush ThemedBrush(string key) => OmegaAssetStudio.WinUI.Services.OmegaThemeBrushes.For(key);

    public ParticleRecolorizerPage()
    {
        InitializeComponent();
        UpdateSliderReadouts();
        UpdateLivePreview();
        UpdateEmptyState();
        BuildPresetSwatches();

        // Which game folder this studio is pointed at. The tool below reads it
        // through GameInstallService, exactly as it always has, so telling that
        // service which client is chosen is all the wiring the copy needs.
        ClientPicker.ClientChanged += (_, client) =>
        {
            OmegaAssetStudio.WinUI.Services.GameInstallService.SetInstallRoot(client?.RootPath);

            // The list belongs to whichever folder is chosen, so it is read
            // again rather than kept from the one before.
            _heroesPopulated = false;
            _ = PopulateHeroesAsync();
        };

        if (ClientPicker.SelectedClient is { } chosen)
            OmegaAssetStudio.WinUI.Services.GameInstallService.SetInstallRoot(chosen.RootPath);
        UpdateRestoreButtonState();
        // Page is now hero-skill first. Mode flag is set up-front so the empty
        // state suppresses correctly, and heroes are populated on first load
        // so the left rail isn't blank.
        _modeIsHeroSkill = true;
        Loaded += async (_, _) =>
        {
            if (!_heroesPopulated)
            {
                HeroSkillPicker.Visibility = Visibility.Visible;
                GroupsScrollView.Visibility = Visibility.Collapsed;
                GroupsEmptyState.Visibility = Visibility.Collapsed;
                await PopulateHeroesAsync().ConfigureAwait(true);
            }
            // Apply any context that came in before Loaded fired (the page is
            // cached, so OnNavigatedTo can land before the first layout pass).
            if (_pendingContext is not null)
                await TryApplyLaunchContextAsync().ConfigureAwait(true);
        };
    }

    // Set by OnNavigatedTo when Reference Explorer (or any other page) sends
    // us a WorkspaceLaunchContext pointing at a per-power UPK. Consumed by
    // TryApplyLaunchContextAsync once the hero rail has populated.
    private OmegaAssetStudio.WinUI.Models.WorkspaceLaunchContext? _pendingContext;

    protected override async void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not OmegaAssetStudio.WinUI.Models.WorkspaceLaunchContext ctx) return;
        _pendingContext = ctx;
        if (_heroesPopulated)
            await TryApplyLaunchContextAsync().ConfigureAwait(true);
    }

    // Resolves a per-power UPK (UC__Power<Hero>_<Power>_SF.upk) to
    //   hero token   = "<Hero>"
    //   class stem   = "Power<Hero>_<Power>"
    // then drives the hero rail selection + the skill-row selection so the
    // user lands on exactly the power they came in for. Without this the
    // page opens to a blank state and the pill click feels like a no-op.
    private async System.Threading.Tasks.Task TryApplyLaunchContextAsync()
    {
        var ctx = _pendingContext;
        _pendingContext = null;
        if (ctx is null) return;

        string fn = System.IO.Path.GetFileNameWithoutExtension(ctx.UpkPath ?? string.Empty);
        if (string.IsNullOrEmpty(fn)) return;
        string stem = fn;
        if (stem.StartsWith("UC__", System.StringComparison.OrdinalIgnoreCase)) stem = stem.Substring(4);
        if (stem.EndsWith("_SF", System.StringComparison.OrdinalIgnoreCase)) stem = stem.Substring(0, stem.Length - 3);
        // stem now looks like "Power<Hero>_<Power>" (PowerCatalog convention).
        if (!stem.StartsWith("Power", System.StringComparison.OrdinalIgnoreCase)) return;
        string afterPower = stem.Substring(5);
        int firstUnderscore = afterPower.IndexOf('_');
        if (firstUnderscore <= 0) return;
        string heroToken = afterPower.Substring(0, firstUnderscore);

        // Switch into the by-hero skill mode if the user happened to be on the
        // legacy UPK-mode view.
        if (ModeBySkillRadio is not null && ModeBySkillRadio.IsChecked != true)
            ModeBySkillRadio.IsChecked = true;
        if (HeroSkillPicker is not null) HeroSkillPicker.Visibility = Visibility.Visible;
        if (GroupsScrollView is not null) GroupsScrollView.Visibility = Visibility.Collapsed;

        if (!_heroesPopulated) await PopulateHeroesAsync().ConfigureAwait(true);

        var heroVm = _heroRows.FirstOrDefault(h =>
            string.Equals(h.Token, heroToken, System.StringComparison.OrdinalIgnoreCase));
        if (heroVm is null) return;

        // Driving the rail selection triggers the existing
        // HeroRailListView_SelectionChanged → HeroListView_SelectionChanged
        // chain, which loads the skill rows.
        if (HeroRailListView is not null)
        {
            HeroRailListView.SelectedItem = heroVm;
            try { HeroRailListView.ScrollIntoView(heroVm); } catch { }
        }

        // Poll briefly for the skill rows to land (the load is async). 2s
        // upper bound covers Calligraphy.sip open + power resolution.
        for (int i = 0; i < 40 && _skillRows.Count == 0; i++)
            await System.Threading.Tasks.Task.Delay(50).ConfigureAwait(true);

        // Pick the row whose underlying class matches the stem we parsed. Try
        // exact match first, then a suffix match for stolen-power synthetic
        // rows (their class stems are the UPK-derived name too).
        var skillVm = _skillRows.FirstOrDefault(s =>
            string.Equals(s.Power?.PowerUnrealClassName, stem, System.StringComparison.OrdinalIgnoreCase))
            ?? _skillRows.FirstOrDefault(s =>
                (s.Power?.PowerUnrealClassName ?? string.Empty)
                    .EndsWith("_" + stem.Substring(stem.IndexOf('_') + 1), System.StringComparison.OrdinalIgnoreCase));
        if (skillVm is null) return;

        if (SkillListView is not null)
        {
            SkillListView.SelectedItem = skillVm;
            try { SkillListView.ScrollIntoView(skillVm); } catch { }
        }
    }

    // Builds a clickable swatch button for every preset hue. Each one just
    // pushes its color into the ColorPicker; the existing ColorChanged plumbing
    // updates the after-preview and Apply uses the current picker value.
    private void BuildPresetSwatches()
    {
        if (PresetWrap is null) return;
        PresetWrap.Children.Clear();
        foreach (var (label, color) in _presetPalette)
        {
            var btn = new Button
            {
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = ThemedBrush("OmegaAssetStudio.PanelBorderBrush"),
                Background = new SolidColorBrush(color),
                Margin = new Thickness(2),
            };
            ToolTipService.SetToolTip(btn, label);
            var captured = color;
            btn.Click += (_, _) =>
            {
                NewColorPicker.Color = captured;
                UpdateAfterPreview();
            };
            PresetWrap.Children.Add(btn);
        }
    }

    private void UpdateEmptyState()
    {
        if (GroupsEmptyState is null) return;
        // The empty card belongs to "Load a UPK" mode only — it tells the user
        // they need to pick a file. In Hero Skills mode the hero/skill picker
        // is the entry point, so suppress the card regardless of _groups state.
        // Also suppress it when any colors (MIC or extra Phase-2 sources) are
        // present so it doesn't overlay loaded content.
        bool hasGroups = _groups.Count > 0 || _extraColorEntries.Count > 0;
        if (_modeIsHeroSkill || hasGroups)
            GroupsEmptyState.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        else
            GroupsEmptyState.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
    }

    private async void GroupsEmptyState_ActionInvoked(object sender, System.EventArgs e)
    {
        // Funnel the empty-state CTA through the same Load handler the header
        // button uses, so behavior is identical.
        LoadButton_Click(sender, new Microsoft.UI.Xaml.RoutedEventArgs());
        await System.Threading.Tasks.Task.CompletedTask;
    }

    private async void LoadButton_Click(object sender, RoutedEventArgs e)
    {
        FileOpenPicker picker = new() { ViewMode = PickerViewMode.List, SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.SettingsIdentifier = "ParticleRecolorizer.LoadUpk";
        picker.FileTypeFilter.Add(".upk");
        IntPtr hwnd = WindowNative.GetWindowHandle(App.MainWindow!);
        InitializeWithWindow.Initialize(picker, hwnd);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        await LoadUpkAsync(file.Path);
    }

    private async Task LoadUpkAsync(string path)
    {
        // Drop a shimmer skeleton over the empty groups column while the
        // async load runs. Min 150ms hold keeps it visible even on fast loads.
        GroupsContainer.Visibility = Visibility.Collapsed;
        GroupsEmptyState.Visibility = Visibility.Collapsed;
        GroupsSkeleton.Visibility = Visibility.Visible;
        var minHold = Task.Delay(150);
        try
        {
            StatusText.Text = "Loading materials...";
            _currentUpkPath = path;
            CurrentUpkText.Text = Path.GetFileName(path);
            CurrentSubText.Text = path;

            _materials.Clear();
            _groups.Clear();
            _baseValues.Clear();
            _paramOwners.Clear();
            GroupsContainer.Children.Clear();

            IReadOnlyList<MaterialDefinition> mats = await _service.LoadMaterialsFromUpkAsync(path).ConfigureAwait(true);
            _materials.AddRange(mats);

            // Group all VectorParameters by name across every material AND
            // remember which material each one came from so we can show real
            // export paths in the row subtitle.
            foreach (MaterialDefinition mat in _materials)
            {
                foreach (MaterialParameter p in mat.VectorParameters)
                {
                    if (string.IsNullOrWhiteSpace(p.Name)) continue;
                    if (!_groups.TryGetValue(p.Name, out GroupRow? row))
                    {
                        row = new GroupRow { Name = p.Name };
                        _groups[p.Name] = row;
                    }
                    row.Parameters.Add(p);
                    _paramOwners[p] = mat;
                    if (p.VectorValue is Vector4 v && !_baseValues.ContainsKey(p))
                        _baseValues[p] = v;
                }
            }

            // Honest count of how many ParticleSystem exports the loaded UPK
            // actually has. Modders who load a character UPK expecting to find
            // particles deserve to see "0 particle systems" up front so they
            // know what they're editing isn't VFX.
            int particleCount = await CountParticleSystemExportsAsync(path).ConfigureAwait(true);
            ParticleSystemChip.Visibility = Visibility.Visible;
            ParticleSystemChipText.Text = particleCount switch
            {
                0 => "0 particle systems",
                1 => "1 particle system",
                _ => particleCount + " particle systems",
            };

            MicCountChip.Text = _materials.Count == 1 ? "1 material" : _materials.Count + " materials";
            GroupCountChip.Text = _groups.Count == 1 ? "1 color" : _groups.Count + " colors";
            RebuildGroupList();
            StatusText.Text = string.Format(CultureInfo.InvariantCulture,
                "Loaded {0} material{1} · {2} color group{3} · {4} particle system{5}",
                _materials.Count, _materials.Count == 1 ? string.Empty : "s",
                _groups.Count, _groups.Count == 1 ? string.Empty : "s",
                particleCount, particleCount == 1 ? string.Empty : "s");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Load failed: " + ex.Message;
        }
        finally
        {
            try { await minHold; } catch { }
            GroupsSkeleton.Visibility = Visibility.Collapsed;
            GroupsContainer.Visibility = Visibility.Visible;
        }
    }

    private void RebuildGroupList()
    {
        // The cards are drawn by BuildSkillSummary, which is called right after
        // this and clears the container itself. This keeps the headless toggle
        // each material group exposes, because the writer walks them through it.
        //
        // A second card builder used to live here. Its checkboxes were created
        // with IsChecked set, and setting that on a fresh box fires Checked —
        // which cleared the slot from the excluded set. So every rebuild, and
        // every keystroke in the filter, silently re-armed every slot the user
        // had turned off, and Apply then recoloured all of them.
        GroupsContainer.Children.Clear();

        foreach (var kvp in _groups)
        {
            kvp.Value.Toggle = new CheckBox { IsChecked = true };
        }

        UpdateSelectedCount();
        UpdateEmptyState();
    }



    private void UpdateSelectedCount()
    {
        int sel = _groups.Values.Count(g => g.Toggle is { IsChecked: true })
                + _slotCheckboxes.Count(cb => cb.IsChecked == true);

        SelectedCountChip.Text = sel + " selected";
    }

    private static Color ComputeAverageColor(List<MaterialParameter> parameters)
    {
        if (parameters.Count == 0) return Color.FromArgb(255, 136, 136, 136);
        double r = 0, g = 0, b = 0;
        int n = 0;
        foreach (MaterialParameter p in parameters)
        {
            if (p.VectorValue is not Vector4 v) continue;
            r += System.Math.Clamp(v.X, 0f, 1f);
            g += System.Math.Clamp(v.Y, 0f, 1f);
            b += System.Math.Clamp(v.Z, 0f, 1f);
            n++;
        }
        if (n == 0) return Color.FromArgb(255, 136, 136, 136);
        return Color.FromArgb(255, (byte)(r / n * 255), (byte)(g / n * 255), (byte)(b / n * 255));
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => RebuildGroupList();

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        // Legacy MIC group toggles (kept for back-compat with older UPK-by-name mode).
        foreach (GroupRow row in _groups.Values)
            if (row.Toggle is not null) row.Toggle.IsChecked = true;
        // Per-slot checkboxes — these are the visible Select/Deselect targets in
        // hero-skill mode. Setting IsChecked fires the existing Checked handler
        // which clears the matching entry from _excludedExportPaths /
        // _excludedMicParams, so Apply will then include the slot.
        foreach (CheckBox cb in _slotCheckboxes)
            if (cb.IsEnabled) cb.IsChecked = true;
    }

    private void SelectNoneButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (GroupRow row in _groups.Values)
            if (row.Toggle is not null) row.Toggle.IsChecked = false;
        foreach (CheckBox cb in _slotCheckboxes)
            if (cb.IsEnabled) cb.IsChecked = false;
    }

    // Full-corpus recolor reference: walks every hero × every skill, dumps
    // each editable color slot to a single Markdown file the user can browse
    // outside the app. Output lives at
    //   %USERPROFILE%\Desktop\OmegaAssetStudio_Docs\HeroSkillRecolorReference.md
    // per CLAUDE.md's no-MD-in-repo rule.
    private CancellationTokenSource? _reportCts;
    private async void GenerateRecolorReportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_reportCts is not null) { _reportCts.Cancel(); _reportCts = null; GenerateRecolorReportButton.Content = "Generate Report"; return; }
        string? cooked = OmegaAssetStudio.WinUI.Services.GameInstallService.GetCookedDataDir();
        if (string.IsNullOrEmpty(cooked))
        {
            ReportProgressText.Text = "Set the game install path in Settings first.";
            return;
        }
        // Make sure the Calligraphy archive is open so per-hero skill lookups
        // resolve. The page normally opens it during a hero pick; for the
        // report we may run before any pick.
        try
        {
            string? sipPath = OmegaAssetStudio.WinUI.Services.GameInstallService.GetCalligraphySipPath();
            if (!string.IsNullOrEmpty(sipPath))
                _heroSkillCatalog.TryOpenArchive(sipPath);
        }
        catch { /* report builder still runs, just with fewer skills */ }

        string docsDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "OmegaAssetStudio_Docs");
        try { Directory.CreateDirectory(docsDir); }
        catch (Exception ex) { ReportProgressText.Text = $"Can't create docs folder: {ex.Message}"; return; }
        string outPath = System.IO.Path.Combine(docsDir, "HeroSkillRecolorReference.md");

        _reportCts = new System.Threading.CancellationTokenSource();
        var ct = _reportCts.Token;
        GenerateRecolorReportButton.Content = "Cancel report";
        ReportProgressText.Text = "Scanning…";

        var progress = new Progress<HeroSkillRecolorReportBuilder.Progress>(p =>
        {
            string skillSuffix = string.IsNullOrEmpty(p.SkillLabel) ? "" : $" → {p.SkillLabel}";
            ReportProgressText.Text = $"[{p.HeroIndex}/{p.HeroTotal}] {p.HeroLabel}{skillSuffix}";
        });

        try
        {
            string md = await System.Threading.Tasks.Task.Run(() =>
                HeroSkillRecolorReportBuilder.BuildAsync(_heroSkillCatalog, cooked, progress, ct)
            ).ConfigureAwait(true);
            await File.WriteAllTextAsync(outPath, md, ct).ConfigureAwait(true);
            ReportProgressText.Text = $"Report saved → {outPath}";
            try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + outPath + "\""); } catch { }
        }
        catch (OperationCanceledException)
        {
            ReportProgressText.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            ReportProgressText.Text = $"Report failed: {ex.Message}";
            OmegaAssetStudio.WinUI.App.WriteDiagnosticsLog("SkillRecolor.Report", ex.ToString());
        }
        finally
        {
            _reportCts?.Dispose();
            _reportCts = null;
            GenerateRecolorReportButton.Content = "Generate Report";
        }
    }

    private void DeltaSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateSliderReadouts();
        UpdateLivePreview();
    }

    // ===== Simple recolor flow (replacement for the HSV/multiplier sliders) =====
    //
    // When a skill is loaded we compute its "dominant" color (saturation-weighted
    // average across every editable + read-only color slot), seed the color
    // picker with it, and store it so Apply can compute the hue delta.

    private Vector4 _currentSkillDominantColor = new(0.5f, 0.5f, 0.5f, 1f);
    private bool _hasSkillLoaded;

    private void NewColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        // A change the page did not make itself is the user choosing a colour,
        // and from then on it is theirs to keep.
        if (!_seedingPicker) _userChoseColour = true;

        UpdateAfterPreview();
    }

    /// <summary>Whether the colour in the picker was chosen by the user.</summary>
    /// <remarks>
    /// Applying reloads the skill so the swatches show what is now on disk, and
    /// that reload used to re-seed the picker from the skill's own colour —
    /// quietly replacing the colour the user had picked with the one they had
    /// just moved away from. A second Apply then wrote that instead.
    /// </remarks>
    private bool _userChoseColour;

    /// <summary>True while the page is setting the picker itself.</summary>
    private bool _seedingPicker;

    // Drives the AFTER swatch from the picker's current color. Called both
    // when the picker fires ColorChanged (drag the spectrum) and when a
    // preset button stamps a new color in.
    private void UpdateAfterPreview()
    {
        if (PreviewAfterBrush is null || PreviewAfterText is null) return;
        var c = NewColorPicker.Color;
        PreviewAfterBrush.Color = c;
        PreviewAfterText.Text = string.Format(CultureInfo.InvariantCulture, "{0:0.00}  {1:0.00}  {2:0.00}", c.R / 255f, c.G / 255f, c.B / 255f);
    }

    // Read-only diagnostic: trace where the skill's locked/parameterized color
    // really lives (baked InstanceParameters vs script-set). Shows a report.
    private async void InspectColorSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentVfx is null)
        {
            OmegaAssetStudio.WinUI.Services.ToastService.Info("Pick a skill first.");
            return;
        }
        string report;
        try
        {
            report = await _heroSkillCatalog.InspectColorSourceAsync(_currentVfx).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            report = "Inspection failed: " + ex.Message;
        }

        // Prepend an attributable header using the data the UI ALREADY collected
        // (the catalog re-walk can miss parameterized slots when a component
        // export fails to parse; _extraColorEntries already has the shapes).
        int paramSlots = _extraColorEntries.Count(en => en.Shape == HeroSkillCatalog.DistributionShape.Parameterized);
        var paramKinds = _extraColorEntries
            .Where(en => en.Shape == HeroSkillCatalog.DistributionShape.Parameterized)
            .GroupBy(en => en.Kind)
            .Select(g => $"{g.Key} x{g.Count()}")
            .ToList();
        string header =
            $"Skill: {CurrentUpkText?.Text}\n" +
            $"Parameterized (locked) color slots seen by the UI: {paramSlots}" +
            (paramKinds.Count > 0 ? "  [" + string.Join(", ", paramKinds) + "]" : "") + "\n" +
            new string('-', 40) + "\n\n";
        report = header + report;

        // Also write the full report to the diagnostics log as a reliable copy.
        OmegaAssetStudio.WinUI.App.WriteDiagnosticsLog("SkillRecolor.InspectColorSource", "\n" + report);

        // A selectable TextBlock inside a ScrollViewer renders the full multi-line
        // report reliably (a read-only TextBox collapsed to one line in the dialog).
        var text = new TextBlock
        {
            Text = report,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
        };
        var scroll = new ScrollViewer
        {
            Content = text,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Width = 560,
            Height = 440,
        };
        var dlg = new ContentDialog
        {
            Title = "Where does this skill's color come from?",
            Content = scroll,
            CloseButtonText = "Close",
            XamlRoot = this.XamlRoot,
        };
        await dlg.ShowAsync();
    }

    // ===== Find effect by name (cross-content search) =====
    private void EffectSearchBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; _ = RunEffectSearchAsync(); }
    }

    private async void EffectSearchButton_Click(object sender, RoutedEventArgs e) => await RunEffectSearchAsync();

    private async Task RunEffectSearchAsync()
    {
        string kw = EffectSearchBox?.Text?.Trim() ?? string.Empty;
        string? cooked = GameInstallService.GetCookedDataDir();
        if (string.IsNullOrEmpty(cooked) || !Directory.Exists(cooked))
        { ToastService.Warning("Game install not set — configure it in Settings."); return; }

        // Scope to the CURRENT HERO. A power's full effect set is split across
        // many sibling UPKs (<SkillA>=primary, <SkillB>=secondary,
        // LightningStrike, BoltSpray, …) that the VFX resolver doesn't bind. We
        // list the hero's on-disk power/projectile/condition UPKs, filtered by
        // the keyword, so the user can recolor the one that owns the effect they
        // see in game — no game-wide noise, no global shared-material edits.
        string heroToken = _currentSkillRow?.Power?.CharacterToken ?? string.Empty;
        if (string.IsNullOrWhiteSpace(heroToken)) { ToastService.Info("Load a skill first."); return; }

        List<(string display, string path)> hits;
        try
        {
            hits = await Task.Run(() =>
            {
                var prefixes = new[] { "UC__Power", "UC__MarvelProjectile", "UC__MarvelConditionEffect", "UC__MarvelEntity", "UC__ItemPower" };
                var found = new List<(string, string)>();
                foreach (string file in Directory.EnumerateFiles(cooked, "UC__*.upk"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (!prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
                    if (name.IndexOf(heroToken, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (kw.Length > 0 && name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    // Friendly display: drop the UC__ prefix + _SF suffix.
                    string disp = name;
                    if (disp.StartsWith("UC__", StringComparison.OrdinalIgnoreCase)) disp = disp[4..];
                    if (disp.EndsWith("_SF", StringComparison.OrdinalIgnoreCase)) disp = disp[..^3];
                    found.Add((disp, file));
                }
                return found.OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).ToList();
            });
        }
        catch (Exception ex) { ToastService.Error("Search failed: " + ex.Message); return; }

        if (hits.Count == 0)
        {
            ToastService.Info(kw.Length > 0
                ? $"No {heroToken} effect UPKs match \"{kw}\". Try a broader word (e.g. lightning, hammer, bolt)."
                : $"No extra effect UPKs found for {heroToken}.");
            return;
        }

        var list = new ListView { SelectionMode = ListViewSelectionMode.Single };
        foreach (var (disp, path) in hits)
        {
            var panel = new StackPanel { Spacing = 1, Padding = new Thickness(2) };
            panel.Children.Add(new TextBlock { Text = disp, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = Path.GetFileName(path), FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
            list.Items.Add(new ListViewItem { Content = panel, Tag = path });
        }
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var dlg = new ContentDialog
        {
            Title = $"{heroToken} effects" + (kw.Length > 0 ? $" matching \"{kw}\"" : "") + $" ({hits.Count})",
            Content = scroll,
            PrimaryButtonText = "Load colors from this effect",
            CloseButtonText = "Cancel",
            MinWidth = 560,
            XamlRoot = this.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        if (list.SelectedItem is not ListViewItem sel || sel.Tag is not string upkPath)
        { ToastService.Info("Nothing selected."); return; }

        IReadOnlyList<HeroSkillCatalog.SkillColorEntry> entries;
        try { entries = await _heroSkillCatalog.CollectColorsFromUpkAsync(upkPath); }
        catch (Exception ex) { ToastService.Error("Collect failed: " + ex.Message); return; }
        if (entries.Count == 0) { ToastService.Info($"{Path.GetFileNameWithoutExtension(upkPath)} has no recolorable color slots."); return; }

        int added = 0;
        foreach (var en in entries)
        {
            if (en.Kind == HeroSkillCatalog.SkillColorKind.MicVectorParam) continue; // MIC handled by the groups view
            if (_extraColorEntries.Any(x => x.ExportPath == en.ExportPath && x.ParameterName == en.ParameterName
                                            && string.Equals(x.SourceUpkPath, en.SourceUpkPath, StringComparison.OrdinalIgnoreCase)))
                continue;
            _extraColorEntries.Add(en); added++;
        }
        RebuildGroupList();
        RefreshCrossPackageWarningFromEntries();
        ToastService.Success($"Added {added} color slot(s) from {Path.GetFileNameWithoutExtension(upkPath)}. Pick a color and Apply to recolor it.");
    }

    private async void ApplyRecolorButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_hasSkillLoaded)
        {
            StatusText.Text = "Pick a skill first.";
            return;
        }

        var picked = NewColorPicker.Color;
        Vector4 target = new(picked.R / 255f, picked.G / 255f, picked.B / 255f, 1f);

        // Direct tint: each color slot's brightness is preserved but its
        // hue/saturation is replaced by the picked color. Gives a visible
        // "make this skill red" effect; the hue-rotation approach we tried
        // first was barely visible because particle samples are stored dim
        // (the renderer brightens them via additive blending + emissive).
        Vector3 targetRgb = new(target.X, target.Y, target.Z);

        ApplyRecolorButton.IsEnabled = false;
        ApplySummaryText.Text = "Saving...";
        StatusText.Text = "Saving...";

        try
        {
            // PER-HERO ISOLATION guard (hoisted up so the MIC save loop below
            // can use it too). Every UPK we mutate must pass — keeps the
            // recolor confined to the current hero's content and never leaks
            // edits into MarvelGame.upk / chBaseMaterials / a different hero's
            // VFX library.
            string? heroToken = _selectedHero?.Token;
            var refusedUpks = new List<string>();
            bool IsAllowed(string upk)
            {
                var g = OmegaAssetStudio.Cooked.SharedPackageGuard.IsSafeToWrite(upk, heroToken);
                if (!g.Allowed)
                {
                    refusedUpks.Add($"{System.IO.Path.GetFileName(upk)} — {g.Reason}");
                    OmegaAssetStudio.WinUI.App.WriteDiagnosticsLog("SkillRecolor.Guard",
                        $"REFUSED: {upk}  reason='{g.Reason}'  hero='{heroToken ?? "?"}'");
                }
                return g.Allowed;
            }

            // Apply the brightness-preserving tint in-memory to every MIC
            // vector param the skill uses. Same write path as before — the
            // existing MaterialEditorService writer handles the byte patch.
            int micEdits = 0;
            int micSkipped = 0;
            foreach (var row in _groups.Values)
            foreach (var p in row.Parameters)
            {
                if (!_baseValues.TryGetValue(p, out Vector4 baseColor)) continue;
                // Slot the user unchecked → leave the parameter at its base.
                if (_excludedMicParams.Contains(p))
                {
                    p.VectorValue = baseColor;
                    micSkipped++;
                    continue;
                }
                float brightness = Math.Max(Math.Max(Math.Abs(baseColor.X), Math.Abs(baseColor.Y)), Math.Abs(baseColor.Z));
                p.VectorValue = new Vector4(targetRgb.X * brightness, targetRgb.Y * brightness, targetRgb.Z * brightness, baseColor.W);
                micEdits++;
            }

            int savedMaterials = 0;
            foreach (var mat in _materials)
            {
                // Don't save MICs that live in a shared/master/cross-hero UPK.
                if (!IsAllowed(mat.SourceUpkPath)) continue;
                try
                {
                    await _service.SaveMaterialAsync(mat).ConfigureAwait(true);
                    savedMaterials++;
                }
                catch (Exception ex)
                {
                    OmegaAssetStudio.WinUI.App.WriteDiagnosticsLog("SkillRecolor.Save", ex.ToString());
                }
            }

            // Particle distribution + Material expression writes via the byte
            // patcher. Partition UPKs by how widely we're allowed to touch them:
            //   • A UPK whose ONLY slots are cross-package material refs is a
            //     SHARED LIBRARY (e.g. chbasematerials.upk). Patch it SURGICALLY —
            //     an allowlist of just the referenced material's checked
            //     expressions — so we never blanket-recolor the whole library.
            //   • Everything else (the skill's own UPKs + dedicated VFX UPKs) uses
            //     the normal opt-out pass.
            var byUpk = _extraColorEntries
                .Where(en => !string.IsNullOrEmpty(en.SourceUpkPath))
                .GroupBy(en => en.SourceUpkPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var sharedOnlyUpks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in byUpk)
                if (g.All(en => en.IsCrossPackage)) sharedOnlyUpks.Add(g.Key);

            int extraEditsApplied = 0;
            int extraUpksSaved = 0;
            var writerErrors = new List<string>();
            var allTouchedUpks = new List<string>();
            var writer = new OmegaAssetStudio.Cooked.SkillColorWriter();
            void WriterLog(string line) => OmegaAssetStudio.WinUI.App.WriteDiagnosticsLog("SkillRecolor.Writer", line);

            // Pass 1 — the skill's own UPKs. Patch ONLY the export paths the UI
            // surfaced as color slots (minus the user's unchecked rows). Without
            // this allowlist the writer would walk every color-bearing export in
            // the UPK and recolor modules that belong to OTHER skills sharing the
            // same VFX UPK — that's the "checked X, but Y also changed" bug.
            var localGroups = byUpk
                .Where(g => !sharedOnlyUpks.Contains(g.Key))
                .Where(g => IsAllowed(g.Key))
                .ToList();

            // Every package the skill's colours live in, and what became of it.
            // A skill routinely spans five — its own, the condition effect it
            // applies, the hotspot it leaves, and a projectile per variant —
            // and a package silently dropped here is the difference between a
            // recolour that shows in game and one that does not.
            foreach (var g in byUpk)
            {
                int kept = g.Count(en => !string.IsNullOrEmpty(en.ExportPath)
                                         && !_excludedExportPaths.Contains(en.ExportPath));

                string fate = !IsAllowed(g.Key) ? "REFUSED by the guard"
                            : kept == 0 ? "SKIPPED — every slot in it is unticked"
                            : sharedOnlyUpks.Contains(g.Key) ? $"shared pass, {kept} of {g.Count()} slots"
                            : $"writing {kept} of {g.Count()} slots";

                OmegaAssetStudio.WinUI.App.WriteDiagnosticsLog("SkillRecolor.Packages",
                    $"{System.IO.Path.GetFileName(g.Key)}: {fate}");
            }
            foreach (var g in localGroups)
            {
                var included = g
                    .Select(en => en.ExportPath)
                    .Where(p => !string.IsNullOrEmpty(p) && !_excludedExportPaths.Contains(p))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (included.Count == 0) continue; // every checked-by-UI slot in this UPK is unchecked
                var report = await writer.ApplyTintAsync(
                    new[] { g.Key }, targetRgb, WriterLog,
                    excludedExportPaths: null,
                    includedExportPaths: included)
                    .ConfigureAwait(true);
                extraEditsApplied += report.Edits.Count;
                extraUpksSaved += report.UpksSaved;
                writerErrors.AddRange(report.Errors);
                allTouchedUpks.Add(g.Key);
            }

            // Pass 2 — shared libraries, allowlist-scoped to just the referenced
            // material's checked expressions. Pre-filtered through the same
            // SharedPackageGuard so MarvelGame.upk / chBaseMaterials / cross-
            // hero libs are NEVER written, even with the allowlist scope.
            foreach (var g in byUpk.Where(g => sharedOnlyUpks.Contains(g.Key) && IsAllowed(g.Key)))
            {
                var included = g
                    .Select(en => en.ExportPath)
                    .Where(p => !string.IsNullOrEmpty(p) && !_excludedExportPaths.Contains(p))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (included.Count == 0) continue; // every shared slot unchecked
                var report = await writer.ApplyTintAsync(
                    new[] { g.Key }, targetRgb, WriterLog,
                    excludedExportPaths: null,
                    includedExportPaths: included)
                    .ConfigureAwait(true);
                extraEditsApplied += report.Edits.Count;
                extraUpksSaved += report.UpksSaved;
                writerErrors.AddRange(report.Errors);
                allTouchedUpks.Add(g.Key);
            }

            int totalSlotEdits = micEdits + extraEditsApplied;
            int totalUpkSaves = savedMaterials + extraUpksSaved;
            // Distinct count — the same package can be refused from both passes.
            var refusedDistinct = refusedUpks.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string guardSuffix = refusedDistinct.Count > 0
                ? $"  •  Protected {refusedDistinct.Count} shared/master file(s) from cross-hero edits."
                : string.Empty;
            ApplySummaryText.Text =
                $"Saved {totalUpkSaves} UPK file(s): {totalSlotEdits} color slot(s) recolored "
                + $"to #{picked.R:X2}{picked.G:X2}{picked.B:X2}.{guardSuffix}";
            StatusText.Text = ApplySummaryText.Text;
            if (refusedDistinct.Count > 0)
                OmegaAssetStudio.WinUI.App.WriteDiagnosticsLog("SkillRecolor.Guard",
                    "Refused write list (per-hero isolation):\n  " + string.Join("\n  ", refusedDistinct));

            // Files on disk just changed. Invalidate every in-memory cache that
            // was holding pre-write bytes so the next operation (probe, re-apply,
            // skill reload) reads FRESH bytes — no app relaunch required. The
            // ClearCache event fans out to HeroSkillCatalog._repo and
            // MaterialEditorService.upkRepository which both subscribe.
            try
            {
                OmegaAssetStudio.Calligraphy.PowerVfxResolver.ClearCache();
                OmegaAssetStudio.Calligraphy.PowerAnimResolver.ClearCache();
            }
            catch { }
            // The dry-run "Preview localize plan" reads _currentVfx; null it so
            // the next probe re-resolves from disk instead of replaying stale data.
            _currentVfx = null;
            _lastCrossRefs = System.Array.Empty<HeroSkillCatalog.SkillMaterialRef>();

            // Remember which UPKs we just touched so the Restore button can
            // walk them. Both writers create one .bak per file on first edit.
            _lastSavedUpks.Clear();
            foreach (var mat in _materials)
            {
                string p = mat.SourceUpkPath;
                if (!string.IsNullOrEmpty(p) && !_lastSavedUpks.Contains(p, StringComparer.OrdinalIgnoreCase))
                    _lastSavedUpks.Add(p);
            }
            foreach (string p in allTouchedUpks)
            {
                if (!_lastSavedUpks.Contains(p, StringComparer.OrdinalIgnoreCase))
                    _lastSavedUpks.Add(p);
            }
            UpdateRestoreButtonState();

            if (totalUpkSaves > 0)
            {
                OmegaAssetStudio.WinUI.Services.ToastService.Success(
                    $"Skill recolored: {totalUpkSaves} file(s) saved, {totalSlotEdits} color(s) changed");

                // Re-read the just-saved UPKs so every swatch in the particle
                // system list paints the post-save color. Without this the
                // rows stay on whatever _extraColorEntries.CurrentColor /
                // _baseValues / GroupRow cached at first load and the user
                // sees stale pre-save colors despite the bytes on disk
                // having changed. UpkFileRepository.LoadUpkFile re-reads on
                // write-time mismatch (Repository line 31-33), so reloading
                // gets fresh data.
                if (SkillListView.SelectedItem is SkillRowVM activeSkill)
                {
                    try { await LoadMaterialsForSkillAsync(activeSkill).ConfigureAwait(true); }
                    catch (Exception reloadEx)
                    {
                        OmegaAssetStudio.WinUI.App.WriteDiagnosticsLog(
                            "SkillRecolor.PostSaveReload",
                            "Reload after save failed: " + reloadEx.Message);
                    }
                }
            }
            else if (writerErrors.Count > 0)
            {
                OmegaAssetStudio.WinUI.Services.ToastService.Warning(
                    "Save reported errors. Check Diagnostics for details.");
            }
            else
            {
                OmegaAssetStudio.WinUI.Services.ToastService.Info(
                    "No color slots in this skill matched the writable types.");
            }
        }
        catch (Exception ex)
        {
            ApplySummaryText.Text = "Save failed: " + ex.Message;
            StatusText.Text = ApplySummaryText.Text;
        }
        finally
        {
            ApplyRecolorButton.IsEnabled = true;
        }
    }

    // Returns (H in [0,1], S in [0,1], V in [0,1]). Standard RGB->HSV conversion.
    private static Vector4 RgbToHsv(Vector4 rgb)
    {
        float r = Math.Clamp(rgb.X, 0f, 1f);
        float g = Math.Clamp(rgb.Y, 0f, 1f);
        float b = Math.Clamp(rgb.Z, 0f, 1f);
        float max = Math.Max(r, Math.Max(g, b));
        float min = Math.Min(r, Math.Min(g, b));
        float v = max;
        float d = max - min;
        float s = max <= 0f ? 0f : d / max;
        float h = 0f;
        if (d > 0f)
        {
            if (max == r)       h = (g - b) / d + (g < b ? 6f : 0f);
            else if (max == g)  h = (b - r) / d + 2f;
            else                h = (r - g) / d + 4f;
            h /= 6f;
        }
        return new Vector4(h, s, v, rgb.W);
    }

    private static Vector4 HsvToRgb(Vector4 hsv)
    {
        float h = (hsv.X % 1f + 1f) % 1f;
        float s = Math.Clamp(hsv.Y, 0f, 1f);
        float v = Math.Clamp(hsv.Z, 0f, 1f);
        float c = v * s;
        float hp = h * 6f;
        float x = c * (1f - MathF.Abs(hp % 2f - 1f));
        float r1, g1, b1;
        if (hp < 1)      { r1 = c; g1 = x; b1 = 0; }
        else if (hp < 2) { r1 = x; g1 = c; b1 = 0; }
        else if (hp < 3) { r1 = 0; g1 = c; b1 = x; }
        else if (hp < 4) { r1 = 0; g1 = x; b1 = c; }
        else if (hp < 5) { r1 = x; g1 = 0; b1 = c; }
        else             { r1 = c; g1 = 0; b1 = x; }
        float m = v - c;
        return new Vector4(r1 + m, g1 + m, b1 + m, hsv.W);
    }

    private static Vector4 ShiftHue(Vector4 rgb, float hueDelta)
    {
        Vector4 hsv = RgbToHsv(rgb);
        hsv.X = (hsv.X + hueDelta) % 1f;
        if (hsv.X < 0) hsv.X += 1f;
        return HsvToRgb(hsv);
    }

    // Builds the WHAT WILL CHANGE chip row. One chip per kind that's
    // present in this skill, labelled "Kind × count". MIC params come from
    // _groups (since they have their own UI history); the rest come from
    // _extraColorEntries.
    private void RefreshBreakdownChips()
    {
        if (BreakdownWrap is null) return;
        BreakdownWrap.Children.Clear();

        // Count per kind. MIC params: total Vector entries across every group.
        int micCount = _groups.Values.Sum(g => g.Parameters.Count);
        var counts = new Dictionary<HeroSkillCatalog.SkillColorKind, int>();
        if (micCount > 0) counts[HeroSkillCatalog.SkillColorKind.MicVectorParam] = micCount;
        foreach (var e in _extraColorEntries)
        {
            counts.TryGetValue(e.Kind, out int n);
            counts[e.Kind] = n + 1;
        }

        if (counts.Count == 0)
        {
            if (BreakdownEmptyText is not null) BreakdownEmptyText.Visibility = Visibility.Visible;
            return;
        }
        if (BreakdownEmptyText is not null) BreakdownEmptyText.Visibility = Visibility.Collapsed;

        // Stable display order — keeps the chip strip looking the same skill
        // to skill so the eye learns the layout.
        var order = new[]
        {
            HeroSkillCatalog.SkillColorKind.MicVectorParam,
            HeroSkillCatalog.SkillColorKind.MaterialExpressionVector,
            HeroSkillCatalog.SkillColorKind.MaterialExpressionConstant3Vector,
            HeroSkillCatalog.SkillColorKind.MaterialExpressionConstant4Vector,
            HeroSkillCatalog.SkillColorKind.ParticleStartColor,
            HeroSkillCatalog.SkillColorKind.ParticleColorOverLife,
            HeroSkillCatalog.SkillColorKind.ParticleColorScaleOverLife,
        };
        foreach (var kind in order)
        {
            if (!counts.TryGetValue(kind, out int n) || n == 0) continue;
            BreakdownWrap.Children.Add(BuildKindChip(kind, n));
        }
    }

    private static Border BuildKindChip(HeroSkillCatalog.SkillColorKind kind, int count, bool showCount = true)
    {
        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = KindBrush(kind),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var label = new TextBlock
        {
            Text = showCount ? $"{DescribeKind(kind)} · {count}" : DescribeKind(kind),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.None,
            TextWrapping = TextWrapping.Wrap,
        };
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(dot);
        panel.Children.Add(label);
        return new Border
        {
            Padding = new Thickness(8, 3, 8, 3),
            CornerRadius = new CornerRadius(10),
            Background = ThemedBrush("OmegaAssetStudio.PanelSecondaryBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemedBrush("OmegaAssetStudio.PanelBorderBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0),
            Child = panel,
        };
    }

    // The shared (cross-UPK) material refs from the last loaded skill, captured
    // so the "Preview localize plan" button can build a dry-run clone plan.
    private IReadOnlyList<HeroSkillCatalog.SkillMaterialRef> _lastCrossRefs =
        System.Array.Empty<HeroSkillCatalog.SkillMaterialRef>();

    // Read-only dry run: shows exactly which exports a "localize to this hero"
    // clone would copy into the hero's particle UPK (so a recolor only affects
    // this hero). Writes nothing — this is stage 1 of the localize feature.
    private async void LocalizePreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastCrossRefs.Count == 0)
        { StatusText.Text = "No shared materials to localize for this skill."; return; }

        string? cooked = GameInstallService.GetCookedDataDir();
        if (string.IsNullOrEmpty(cooked))
        { StatusText.Text = "Game install not set — open Settings → Game Install."; return; }

        // The hero's particle UPK (where emitters live and the localized copies
        // would land) is the host UPK of the particle refs.
        string heroUpk = _lastCrossRefs[0].SourceUpkPath;

        // Resolve each shared material to the cooked UPK that actually hosts it.
        var shared = _lastCrossRefs.Select(r =>
        {
            string leaf = r.MaterialExportPath.Contains('.')
                ? r.MaterialExportPath[(r.MaterialExportPath.LastIndexOf('.') + 1)..]
                : r.MaterialExportPath;
            string? hostFile = OmegaAssetStudio.WinUI.Services.PackageReferenceQueryService
                .ResolveHostUpkFileName(cooked, leaf);
            string hostPath = string.IsNullOrEmpty(hostFile)
                ? r.SourceUpkPath
                : System.IO.Path.Combine(cooked, hostFile);
            return (r.MaterialExportPath, hostPath);
        });

        StatusText.Text = "Building localize plan…";
        OmegaAssetStudio.Cooked.SharedMaterialLocalizer.LocalizePlan plan;
        try
        {
            plan = await new OmegaAssetStudio.Cooked.SharedMaterialLocalizer()
                .BuildPlanAsync(heroUpk, shared).ConfigureAwait(true);
        }
        catch (System.Exception ex)
        { StatusText.Text = $"Localize plan failed: {ex.Message}"; return; }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Target hero UPK : {System.IO.Path.GetFileName(heroUpk)}");
        sb.AppendLine($"Shared materials: {plan.Materials.Count}");
        sb.AppendLine($"Exports to clone: {plan.TotalExportsToClone}");
        if (plan.AnyMasterPackage)
            sb.AppendLine("WARNING: some materials live in MarvelGame.upk (game-wide) — high-risk to localize.");
        sb.AppendLine();
        foreach (var m in plan.Materials)
        {
            sb.AppendLine($"• {m.MaterialExportPath}  [{m.MaterialClass}]");
            sb.AppendLine($"    host: {System.IO.Path.GetFileName(m.HostUpkPath)}{(m.HostIsMasterPackage ? "  (MASTER)" : string.Empty)}");
            if (!m.Found) { sb.AppendLine($"    NOT FOUND — {m.Note}"); continue; }
            sb.AppendLine($"    clone closure: {m.Closure.Count} export(s)");
            foreach (var c in m.Closure.Take(14))
                sb.AppendLine($"      - {c.ObjectName} [{c.ClassName}] {c.SerialSize}B");
            if (m.Closure.Count > 14) sb.AppendLine($"      … +{m.Closure.Count - 14} more");
        }

        // Color-source probe: for each emitter using a shared material, does the
        // tint live in a LOCAL particle color module (case A — recolor in place)
        // or is it baked into the material (case B — needs clone + material
        // recolor)? This decides whether localizing the material even helps.
        if (_currentVfx is not null)
        {
            try
            {
                var probes = await _heroSkillCatalog.ProbeEmitterColorSourcesAsync(_currentVfx).ConfigureAwait(true);
                var blue = probes.Where(p => p.BlueDominant).ToList();
                sb.AppendLine();
                sb.AppendLine("── BLUE-DOMINANT EMITTERS (likely still blue) BY HOST UPK ──");
                if (blue.Count == 0)
                    sb.AppendLine("(none detected — every emitter's color is already non-blue or has no module)");
                foreach (var g in blue.GroupBy(p => p.HostUpk, StringComparer.OrdinalIgnoreCase)
                                      .OrderByDescending(g => g.Count()))
                {
                    sb.AppendLine($"  {g.Key}  ({g.Count()} emitter(s)):");
                    foreach (var p in g.Take(20))
                        sb.AppendLine($"    • {p.Emitter}  {p.ColorModule}  mat={p.MaterialName}");
                }
                var hosts = blue.Select(p => p.HostUpk).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                sb.AppendLine();
                sb.AppendLine(hosts.Count > 0
                    ? $"Blue color modules live in: {string.Join(", ", hosts)}"
                    : "No blue color modules detected.");
                sb.AppendLine("(Any UPK above that the recolor did NOT write is why that effect stayed blue.)");

                // LINGERING emitters: high per-particle lifetime OR forever-looping
                // emitter. These produce the after-cast visuals. Surface them with
                // their host UPK + color so we can see which one is still blue.
                var lingering = probes
                    .Where(p => p.EmitterLoops || p.ParticleLifetime >= 1.5f)
                    .OrderByDescending(p => p.EmitterLoops)
                    .ThenByDescending(p => p.ParticleLifetime)
                    .ToList();
                sb.AppendLine();
                sb.AppendLine("── LINGERING / AFTER-CAST EMITTERS (long lifetime or forever-loop) ──");
                if (lingering.Count == 0)
                    sb.AppendLine("(none — every emitter has short lifetime and a finite duration)");
                foreach (var g in lingering.GroupBy(p => p.HostUpk, StringComparer.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"  {g.Key}  ({g.Count()} emitter(s)):");
                    foreach (var p in g.Take(30))
                    {
                        string flags = (p.EmitterLoops ? "LOOPS" : "") + (p.ParticleLifetime >= 1.5f ? $" life={p.ParticleLifetime:0.0}s" : "");
                        sb.AppendLine($"    • ps={p.ParticleSystem} emitter={p.Emitter} {flags} {p.ColorModule} mat={p.MaterialName}");
                    }
                }
            }
            catch (System.Exception ex) { sb.AppendLine($"(color probe failed: {ex.Message})"); }
        }

        StatusText.Text = $"Localize plan: {plan.TotalExportsToClone} export(s) across {plan.Materials.Count} shared material(s).";

        string planText = sb.ToString();
        var dlg = new ContentDialog
        {
            Title = "Localize plan — dry run (no files changed)",
            Content = new ScrollViewer
            {
                MaxHeight = 480,
                Content = new TextBlock
                {
                    Text = planText,
                    // Selectable so the user can also drag-select + Ctrl+C.
                    IsTextSelectionEnabled = true,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
            // Primary button copies the whole plan to the clipboard and keeps the
            // dialog open (Cancel = true) so the user can copy then read on.
            PrimaryButtonText = "Copy",
            CloseButtonText = "Close",
            XamlRoot = this.XamlRoot,
        };
        dlg.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            try
            {
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(planText);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                StatusText.Text = "Localize plan copied to clipboard.";
            }
            catch { }
        };
        try { await dlg.ShowAsync(); } catch { }
    }

    // Shows the yellow callout iff at least one of the skill's particle
    // emitters references a material that lives in another UPK (Import in
    // the host UPK's table). The detail text names how many shared refs
    // were seen so the modder knows the scope.
    private void RefreshCrossPackageWarning(IReadOnlyList<HeroSkillCatalog.SkillMaterialRef> crossRefs)
    {
        if (CrossPackageWarning is null || CrossPackageDetail is null) return;
        if (crossRefs is null || crossRefs.Count == 0)
        {
            CrossPackageWarning.Visibility = Visibility.Collapsed;
            return;
        }
        CrossPackageWarning.Visibility = Visibility.Visible;

        // Resolve each shared material to the cooked UPK that actually hosts it
        // via the reference index. These often cook into MarvelGame.upk (the
        // master package) rather than a like-named <package>.upk file.
        string? cooked = GameInstallService.GetCookedDataDir();
        bool anyMaster = false;
        var named = crossRefs
            .Select(r => r.MaterialExportPath ?? string.Empty)
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name =>
            {
                string leaf = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
                string? host = string.IsNullOrEmpty(cooked)
                    ? null
                    : OmegaAssetStudio.WinUI.Services.PackageReferenceQueryService.ResolveHostUpkFileName(cooked, leaf);
                if (!string.IsNullOrEmpty(host) && host.StartsWith("MarvelGame", StringComparison.OrdinalIgnoreCase))
                    anyMaster = true;
                return string.IsNullOrEmpty(host) ? leaf : $"{leaf}  —  in {host}";
            })
            .ToList();

        if (CrossPackageMaterials is not null)
            CrossPackageMaterials.Text = string.Join("\n", named);

        int n = crossRefs.Count;
        string emitters = n == 1 ? "1 emitter" : n + " emitters";
        CrossPackageDetail.Text = anyMaster
            ? $"{emitters} use the shared material(s) above. These live in MarvelGame.upk — the game's master package — so they're used GAME-WIDE. Per-hero isolation guard BLOCKS writes to MarvelGame.upk; to recolor them for this hero only, use Localize → clone the material into the hero's UPK first."
            : $"{emitters} use the shared material(s) above. The recolor's per-hero guard refuses writes to shared/master/cross-hero libraries — only this hero's own UPKs are modified. To recolor a shared material for this hero only, use Localize (clone into the hero's UPK and rebind).";
    }

    // Refines the cross-package warning once the color slots are collected, using
    // each shared entry's RESOLVED host UPK (more accurate than guessing the
    // package from the material's path). Shows "materialName — in <file>.upk".
    private void RefreshCrossPackageWarningFromEntries()
    {
        if (CrossPackageWarning is null || CrossPackageMaterials is null || CrossPackageDetail is null) return;
        var cross = _extraColorEntries.Where(en => en.IsCrossPackage).ToList();
        if (cross.Count == 0) return; // keep whatever the ref-based pass set

        CrossPackageWarning.Visibility = Visibility.Visible;
        var named = cross
            .Select(en =>
            {
                string mat = en.OwnerLabel.StartsWith("Material: ", StringComparison.OrdinalIgnoreCase)
                    ? en.OwnerLabel["Material: ".Length..]
                    : en.OwnerLabel;
                string upk = System.IO.Path.GetFileName(en.SourceUpkPath ?? string.Empty);
                return string.IsNullOrEmpty(upk) ? mat : $"{mat}  —  in {upk}";
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        CrossPackageMaterials.Text = string.Join("\n", named);
        CrossPackageDetail.Text = "Apply recolors ONLY the color expressions of the material(s) above, in the UPK shown — backed up first, not the whole library. Because they're shared, other skills/heroes that use them will change too.";
    }

    // Enables the Restore button only when at least one .bak from the last
    // save still exists on disk. We re-check every time because the user
    // could have manually deleted backups in Backup Manager.
    private void UpdateRestoreButtonState()
    {
        if (RestoreButton is null) return;
        bool hasAny = _lastSavedUpks.Any(p =>
            !string.IsNullOrEmpty(p) && OmegaAssetStudio.BackupManager.BackupFileHelper.FindExistingBackup(p) is not null);
        RestoreButton.IsEnabled = hasAny;
        if (RestoreButtonText is not null)
            RestoreButtonText.Text = hasAny
                ? $"Restore {_lastSavedUpks.Count} file(s) from backup"
                : "Restore from backup";
    }

    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastSavedUpks.Count == 0) return;
        RestoreButton.IsEnabled = false;
        int restored = 0;
        var errors = new List<string>();
        foreach (string path in _lastSavedUpks.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string? bak = OmegaAssetStudio.BackupManager.BackupFileHelper.FindExistingBackup(path);
                if (bak is null || !File.Exists(bak)) continue;
                // BackupFileHelper.CreateBackup is a one-shot pristine snapshot, so
                // restoring is just a file copy back from the .bak into place.
                File.Copy(bak, path, overwrite: true);
                restored++;
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
                OmegaAssetStudio.WinUI.App.WriteDiagnosticsLog("SkillRecolor.Restore", ex.ToString());
            }
            await Task.Yield();
        }
        if (errors.Count == 0)
        {
            StatusText.Text = $"Restored {restored} file(s) from backup.";
            ApplySummaryText.Text = StatusText.Text;
            OmegaAssetStudio.WinUI.Services.ToastService.Success($"Restored {restored} file(s) from backup.");
        }
        else
        {
            StatusText.Text = $"Restored {restored} file(s), {errors.Count} failed. Check Diagnostics.";
            ApplySummaryText.Text = StatusText.Text;
            OmegaAssetStudio.WinUI.Services.ToastService.Warning(StatusText.Text);
        }
        UpdateRestoreButtonState();
    }

    private void UpdateCurrentColorPreview()
    {
        if (PreviewBaseBrush is null || PreviewBaseText is null) return;
        // Saturation-weighted average so dim gray slots don't drag the
        // dominant toward neutral. A black mask + a saturated red gives "red".
        double rW = 0, gW = 0, bW = 0, wSum = 0;
        void Accumulate(Vector4 v)
        {
            float r = Math.Clamp(v.X, 0f, 1f);
            float g = Math.Clamp(v.Y, 0f, 1f);
            float b = Math.Clamp(v.Z, 0f, 1f);
            float max = Math.Max(r, Math.Max(g, b));
            float min = Math.Min(r, Math.Min(g, b));
            float sat = max <= 0f ? 0f : (max - min) / max;
            double w = 0.15 + sat;  // floor so pure grays still count a little
            rW += r * w; gW += g * w; bW += b * w; wSum += w;
        }
        foreach (var p in _baseValues.Values) Accumulate(p);
        foreach (var e in _extraColorEntries) Accumulate(e.CurrentColor);
        if (wSum <= 0)
        {
            PreviewBaseBrush.Color = Color.FromArgb(255, 136, 136, 136);
            PreviewBaseText.Text = "Pick a skill to see its current color";
            _hasSkillLoaded = false;
            ApplyRecolorButton.IsEnabled = false;
            return;
        }
        float baseR = (float)(rW / wSum);
        float baseG = (float)(gW / wSum);
        float baseB = (float)(bW / wSum);
        _currentSkillDominantColor = new Vector4(baseR, baseG, baseB, 1f);
        _hasSkillLoaded = true;
        PreviewBaseBrush.Color = Color.FromArgb(255, (byte)(baseR * 255), (byte)(baseG * 255), (byte)(baseB * 255));
        PreviewBaseText.Text = string.Format(CultureInfo.InvariantCulture, "{0:0.00}  {1:0.00}  {2:0.00}", baseR, baseG, baseB);
        // Seed the picker with the current color so the user has a sensible
        // starting point — but never over a colour they picked themselves.
        if (!_userChoseColour)
        {
            _seedingPicker = true;
            NewColorPicker.Color = Color.FromArgb(255, (byte)(baseR * 255), (byte)(baseG * 255), (byte)(baseB * 255));
            _seedingPicker = false;
        }
        UpdateAfterPreview();
        int total = _groups.Sum(g => g.Value.Parameters.Count) + _extraColorEntries.Count;
        ApplySummaryText.Text = $"This skill has {total} color slot(s). Pick a new color and click Apply to recolor them all.";
        ApplyRecolorButton.IsEnabled = true;
        RefreshBreakdownChips();
    }

    private void UpdateSliderReadouts()
    {
        // XAML parses elements top-to-bottom; sliders with Value != default
        // fire ValueChanged during construction, BEFORE their sibling readout
        // TextBlocks exist. Guard every field, not just the first.
        if (HueValueText is null || SatValueText is null || ValValueText is null
            || MulRValueText is null || MulGValueText is null || MulBValueText is null)
            return;
        HueValueText.Text = ((int)HueSlider.Value).ToString(CultureInfo.InvariantCulture) + "°";
        SatValueText.Text = SatSlider.Value.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
        ValValueText.Text = ValSlider.Value.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
        MulRValueText.Text = MulRSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
        MulGValueText.Text = MulGSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
        MulBValueText.Text = MulBSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private void UpdateLivePreview()
    {
        // Same construction-order guard as UpdateSliderReadouts — this method
        // is called from the same ValueChanged event that fires during XAML
        // load before downstream elements exist.
        if (PreviewBaseBrush is null || PreviewShiftedBrush is null
            || PreviewBaseText is null || PreviewShiftedText is null) return;
        // Use the average of currently selected groups as the base; if none
        // selected fall back to first available group.
        List<MaterialParameter> sample = new();
        foreach (GroupRow row in _groups.Values)
            if (row.Toggle is { IsChecked: true }) sample.AddRange(row.Parameters);
        if (sample.Count == 0)
        {
            GroupRow? first = _groups.Values.FirstOrDefault();
            if (first is not null) sample.AddRange(first.Parameters);
        }
        if (sample.Count == 0)
        {
            PreviewBaseBrush.Color = Color.FromArgb(255, 136, 136, 136);
            PreviewShiftedBrush.Color = Color.FromArgb(255, 136, 136, 136);
            PreviewBaseText.Text = "—";
            PreviewShiftedText.Text = "—";
            return;
        }
        double r = 0, g = 0, b = 0;
        int n = 0;
        foreach (MaterialParameter p in sample)
        {
            if (!_baseValues.TryGetValue(p, out Vector4 v)) continue;
            r += System.Math.Clamp(v.X, 0f, 1f);
            g += System.Math.Clamp(v.Y, 0f, 1f);
            b += System.Math.Clamp(v.Z, 0f, 1f);
            n++;
        }
        if (n == 0)
        {
            PreviewBaseBrush.Color = Color.FromArgb(255, 136, 136, 136);
            PreviewShiftedBrush.Color = Color.FromArgb(255, 136, 136, 136);
            return;
        }
        float baseR = (float)(r / n);
        float baseG = (float)(g / n);
        float baseB = (float)(b / n);
        Vector4 baseV = new(baseR, baseG, baseB, 1f);
        Vector4 shifted = ApplyShift(baseV);
        PreviewBaseBrush.Color = ToColor(baseV);
        PreviewShiftedBrush.Color = ToColor(shifted);
        PreviewBaseText.Text = string.Format(CultureInfo.InvariantCulture, "{0:0.00} {1:0.00} {2:0.00}", baseR, baseG, baseB);
        PreviewShiftedText.Text = string.Format(CultureInfo.InvariantCulture, "{0:0.00} {1:0.00} {2:0.00}", shifted.X, shifted.Y, shifted.Z);
    }

    private static Color ToColor(Vector4 v)
    {
        byte r = (byte)System.Math.Clamp(v.X * 255f, 0, 255);
        byte g = (byte)System.Math.Clamp(v.Y * 255f, 0, 255);
        byte b = (byte)System.Math.Clamp(v.Z * 255f, 0, 255);
        return Color.FromArgb(255, r, g, b);
    }

    // HSV-shift + per-channel multiply applied to the base color. Hue rotates
    // around the wheel; sat/val clamp to [0,1]. Multiplier is HDR-aware (no
    // clamp) so emissive values can exceed 1.0 deliberately.
    private Vector4 ApplyShift(Vector4 baseColor)
    {
        double hueDeg = HueSlider.Value;
        double satDelta = SatSlider.Value;
        double valDelta = ValSlider.Value;
        double mulR = MulRSlider.Value;
        double mulG = MulGSlider.Value;
        double mulB = MulBSlider.Value;

        (double h, double s, double v) = RgbToHsv(
            System.Math.Clamp(baseColor.X, 0f, 1f),
            System.Math.Clamp(baseColor.Y, 0f, 1f),
            System.Math.Clamp(baseColor.Z, 0f, 1f));

        h = (h + hueDeg + 360.0) % 360.0;
        s = System.Math.Clamp(s + satDelta, 0.0, 1.0);
        v = System.Math.Clamp(v + valDelta, 0.0, 1.0);

        (double rr, double gg, double bb) = HsvToRgb(h, s, v);

        rr *= mulR;
        gg *= mulG;
        bb *= mulB;

        return new Vector4((float)rr, (float)gg, (float)bb, baseColor.W);
    }

    private static (double h, double s, double v) RgbToHsv(double r, double g, double b)
    {
        double max = System.Math.Max(r, System.Math.Max(g, b));
        double min = System.Math.Min(r, System.Math.Min(g, b));
        double v = max;
        double s = max == 0 ? 0 : (max - min) / max;
        double h = 0;
        if (max != min)
        {
            double d = max - min;
            if (max == r) h = ((g - b) / d) % 6;
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
            if (h < 0) h += 360;
        }
        return (h, s, v);
    }

    private static (double r, double g, double b) HsvToRgb(double h, double s, double v)
    {
        double c = v * s;
        double hp = h / 60.0;
        double x = c * (1 - System.Math.Abs(hp % 2 - 1));
        double r, g, b;
        if (hp < 1) { r = c; g = x; b = 0; }
        else if (hp < 2) { r = x; g = c; b = 0; }
        else if (hp < 3) { r = 0; g = c; b = x; }
        else if (hp < 4) { r = 0; g = x; b = c; }
        else if (hp < 5) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        double m = v - c;
        return (r + m, g + m, b + m);
    }

    // Re-apply from the base snapshot every time, so adjusting sliders after a
    // previous Apply doesn't compound: result = ApplyShift(baseValues[p]).
    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        int touched = 0;
        foreach (GroupRow row in _groups.Values)
        {
            if (row.Toggle is not { IsChecked: true }) continue;
            foreach (MaterialParameter p in row.Parameters)
            {
                if (!_baseValues.TryGetValue(p, out Vector4 baseV)) continue;
                Vector4 next = ApplyShift(baseV);
                p.VectorValue = next;
                touched++;
            }
            // Refresh the swatch so the user sees the actual stored color.
            row.Swatch.Background = new SolidColorBrush(ComputeAverageColor(row.Parameters));
        }
        StatusText.Text = string.Format(CultureInfo.InvariantCulture,
            "Applied delta to {0} parameter(s). Save to commit to UPK.", touched);
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        HueSlider.Value = 0;
        SatSlider.Value = 0;
        ValSlider.Value = 0;
        MulRSlider.Value = 1;
        MulGSlider.Value = 1;
        MulBSlider.Value = 1;
        // Restore originals
        foreach (var kvp in _baseValues) kvp.Key.VectorValue = kvp.Value;
        foreach (GroupRow row in _groups.Values)
            row.Swatch.Background = new SolidColorBrush(ComputeAverageColor(row.Parameters));
        UpdateSliderReadouts();
        UpdateLivePreview();
        StatusText.Text = "Reset to base values.";
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_materials.Count == 0)
        {
            StatusText.Text = "Nothing to save — load a UPK first.";
            return;
        }
        int saved = 0;
        StatusText.Text = "Saving...";
        try
        {
            // Take a single snapshot up front (MaterialEditorService.SaveMaterialAsync
            // also snapshots, but it would no-op on subsequent saves because the .bak
            // is one-shot pristine — this rolling snapshot captures the state right
            // before this bulk recolor sweep specifically).
            if (!string.IsNullOrWhiteSpace(_currentUpkPath))
                OmegaAssetStudio.WinUI.Services.EditHistoryService.Snapshot(_currentUpkPath, "ParticleRecolorizer");

            foreach (MaterialDefinition mat in _materials)
            {
                await _service.SaveMaterialAsync(mat).ConfigureAwait(true);
                saved++;
            }
            StatusText.Text = string.Format(CultureInfo.InvariantCulture,
                "Saved {0} material(s) back to {1}.", saved, Path.GetFileName(_currentUpkPath));
            OmegaAssetStudio.WinUI.Services.ToastService.Success(
                $"Saved {saved} material(s) to {Path.GetFileName(_currentUpkPath)}");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Save failed: " + ex.Message;
        }
    }

    // Builds the per-row "where does this come from?" subtitle.
    // First line lists up to 3 material names (real export paths shortened to
    // the leaf, so the user can recognize "body", "rim", "Pyrokinetic_aura"
    // etc.) plus an "...and N more" suffix; second line tells the user how
    // many materials in the UPK share this color.
    private string BuildRowSubtitle(GroupRow row)
    {
        if (row.Parameters.Count == 0) return string.Empty;

        // De-duplicate by material — multiple parameter entries can map to
        // the same MIC if a material exposes the same name twice (rare).
        var materials = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in row.Parameters)
        {
            if (!_paramOwners.TryGetValue(p, out var mat)) continue;
            string label = ShortenMaterialName(mat);
            if (seen.Add(label)) materials.Add(label);
        }

        const int maxShown = 3;
        string shown = string.Join(", ", materials.Take(maxShown));
        string more = materials.Count > maxShown ? $"  +{materials.Count - maxShown} more" : string.Empty;
        return $"Used by: {shown}{more}";
    }

    // Stems "ChBaseMaterials.M_Hawkeye_Ronin_Body" → "M_Hawkeye_Ronin_Body".
    private static string ShortenMaterialName(MaterialDefinition mat)
    {
        string path = !string.IsNullOrEmpty(mat.Path) ? mat.Path : mat.Name;
        if (string.IsNullOrEmpty(path)) return "(unnamed)";
        int dot = path.LastIndexOf('.');
        return dot >= 0 && dot + 1 < path.Length ? path[(dot + 1)..] : path;
    }

    // Light heuristic that converts parameter names into plain-English hints.
    // Returns null when nothing useful applies, so we don't add noise.
    private static string? DescribeParameterRole(string parameterName)
    {
        if (string.IsNullOrWhiteSpace(parameterName)) return null;
        string n = parameterName.ToLowerInvariant();
        if (n.Contains("emissive") || n.Contains("glow")) return "Likely controls glow / emissive intensity";
        if (n.Contains("rim"))                            return "Likely controls rim-light color around silhouettes";
        if (n.Contains("aura"))                           return "Likely controls an aura particle effect";
        if (n.Contains("trail") || n.Contains("ribbon"))  return "Likely controls a movement trail";
        if (n.Contains("fire") || n.Contains("flame"))    return "Likely controls fire / flame color";
        if (n.Contains("ice")  || n.Contains("frost"))    return "Likely controls ice / frost tint";
        if (n.Contains("light"))                          return "Likely controls a light color";
        if (n.Contains("tint")  || n.Contains("color") ||
            n.Contains("diffuse") || n.Contains("base"))  return "Likely controls the base / diffuse tint";
        if (n.Contains("spec"))                           return "Likely controls specular highlight color";
        return null;
    }

    // Walks the UPK export table looking for ParticleSystem class names.
    // Cheap header-only read; doesn't parse any export bodies.
    private static async Task<int> CountParticleSystemExportsAsync(string upkPath)
    {
        try
        {
            var repository = new UpkManager.Repository.UpkFileRepository();
            var header = await repository.LoadUpkFile(upkPath).ConfigureAwait(true);
            await header.ReadHeaderAsync(null).ConfigureAwait(true);
            int count = 0;
            foreach (var export in header.ExportTable)
            {
                string cls = export.ClassReferenceNameIndex?.Name ?? string.Empty;
                if (string.Equals(cls, "ParticleSystem", StringComparison.OrdinalIgnoreCase))
                    count++;
            }
            return count;
        }
        catch
        {
            return 0;
        }
    }

    // ===== Hero Skills mode =====
    //
    // The mode radios toggle visibility between the existing "load a UPK"
    // workflow and a hero/skill picker. Picking a skill populates the same
    // _materials/_groups state the by-UPK path uses, so the right-side
    // sliders, preview, and save flow work without any changes.

    public sealed class HeroRowVM
    {
        public string Token { get; init; } = string.Empty;
        public string Variant { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string UpkPath { get; init; } = string.Empty;
    }

    // Container-slot expansion: looks for any row whose PowerName contains
    // "Stolen" AND has no resolved class, replaces those rows with one entry
    // per matching `UC__Power<Hero>_Stolen*_SF.upk` file in the cooked dir.
    // The synthetic PowerEntry carries the UPK's class-name stem as
    // PowerUnrealClassName so the rest of the recolor pipeline can resolve
    // FX bindings normally. Display name is derived from the trailing token.
    private void TryExpandStolenSlotRows(string heroToken, List<SkillRowVM> rows)
    {
        if (rows.Count == 0) return;
        string? cookedDir = OmegaAssetStudio.WinUI.Services.GameInstallService.GetCookedDataDir();
        if (string.IsNullOrWhiteSpace(cookedDir) || !System.IO.Directory.Exists(cookedDir))
            return;

        // Identify the container-slot rows. Names look like "StolenPassivePowerSlot1",
        // "StolenPowerLibrarySlot1" — they have "Stolen" AND no class.
        var slotRows = rows
            .Where(r => string.IsNullOrEmpty(r.Power?.PowerUnrealClassName ?? string.Empty))
            .Where(r => (r.Power?.PowerName ?? string.Empty)
                         .IndexOf("Stolen", System.StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();
        if (slotRows.Count == 0) return;

        // Catalog the actual stolen-power UPKs on disk. Match both the
        // "_Stolen_" (singular, AngelDeathFromAbove etc.) and "_StolenPowers_"
        // (plural, SummonMoloids etc.) families. Strip "UC__" prefix + "_SF"
        // suffix to land on the class-name stem ("Power<Hero>_StolenPower_<UpkLeaf>").
        string filePrefix = $"UC__Power{heroToken}_Stolen";
        var classStems = new List<string>();
        try
        {
            foreach (string upkPath in System.IO.Directory.EnumerateFiles(cookedDir, $"{filePrefix}*_SF.upk"))
            {
                string fn = System.IO.Path.GetFileNameWithoutExtension(upkPath);
                if (!fn.StartsWith("UC__", System.StringComparison.OrdinalIgnoreCase)) continue;
                if (!fn.EndsWith("_SF", System.StringComparison.OrdinalIgnoreCase)) continue;
                string stem = fn.Substring(4, fn.Length - 4 - 3); // strip "UC__" + "_SF"
                classStems.Add(stem);
            }
        }
        catch { return; }
        if (classStems.Count == 0) return;
        classStems.Sort(System.StringComparer.OrdinalIgnoreCase);

        // Drop the abstract slot rows from both backing lists.
        foreach (var slot in slotRows)
        {
            _skillRows.Remove(slot);
            rows.Remove(slot);
        }

        // Inject one row per discovered UPK.
        foreach (string className in classStems)
        {
            string display = DerivePrettyName(className, heroToken);
            var synth = new OmegaAssetStudio.Calligraphy.PowerEntry
            {
                PrototypePath = "[synthetic]",
                PowerName = className,
                PowerUnrealClassName = className,
                DisplayName = display,
                CharacterToken = heroToken,
                // Reuse the slot's icon string as a fallback so the row at
                // least shows the steal glyph until per-power icon lookup
                // exists. Picking the first slot's path is good enough — all
                // slots in the source data share the same steal-icon.
                IconAssetPath = slotRows[0].Power?.IconAssetPath ?? string.Empty,
            };
            var vm = new SkillRowVM { DisplayName = display, Subtitle = className, Power = synth };
            _skillRows.Add(vm);
            rows.Add(vm);
        }
    }

    // "Power<Hero>_StolenPower_<CamelLeaf>" → "<spaced camel leaf>"
    // "Power<Hero>_StolenPowers_<CamelLeaf>" → "<spaced camel leaf>"
    private static string DerivePrettyName(string className, string heroToken)
    {
        string s = className;
        string[] strips = {
            $"Power{heroToken}_StolenPowers_",
            $"Power{heroToken}_StolenPower_",
            $"Power{heroToken}_",
        };
        foreach (string p in strips)
        {
            if (s.StartsWith(p, System.StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring(p.Length);
                break;
            }
        }
        // Camel-case → spaced words. "AngelDeathFromAbove" → "Angel Death From Above"
        var sb = new System.Text.StringBuilder(s.Length + 8);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(s[i - 1]) && s[i - 1] != '_')
                sb.Append(' ');
            sb.Append(c == '_' ? ' ' : c);
        }
        return sb.ToString();
    }

    public sealed class SkillRowVM : System.ComponentModel.INotifyPropertyChanged
    {
        public string DisplayName { get; init; } = string.Empty;
        public string Subtitle { get; init; } = string.Empty;
        public OmegaAssetStudio.Calligraphy.PowerEntry? Power { get; init; }

        // Typed as ImageSource so WriteableBitmap (built from decoded RGBA
        // pixels) is assignable. BitmapImage would only allow stream sources.
        private Microsoft.UI.Xaml.Media.ImageSource? _iconBitmap;
        public Microsoft.UI.Xaml.Media.ImageSource? IconBitmap
        {
            get => _iconBitmap;
            set { _iconBitmap = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IconBitmap))); }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    private async void ModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        // The mode radios are kept (collapsed) for back-compat after the
        // hero-first redesign, but the Checked event still fires during XAML
        // init — when the rest of the page hasn't been constructed yet.
        // Null-guard every child reference so initialization can't crash.
        if (ModeBySkillRadio is null || ModeByUpkRadio is null) return;
        _modeIsHeroSkill = ModeBySkillRadio.IsChecked == true;

        if (_modeIsHeroSkill)
        {
            if (CurrentSubText is not null)
                CurrentSubText.Text = "Pick a hero on the left, then click one of their skills to load just the colors that drive that skill's particle effects.";
            if (HeroSkillPicker is not null) HeroSkillPicker.Visibility = Visibility.Visible;
            if (GroupsScrollView is not null) GroupsScrollView.Visibility = Visibility.Collapsed;
            if (GroupsEmptyState is not null) GroupsEmptyState.Visibility = Visibility.Collapsed;
            if (!_heroesPopulated) await PopulateHeroesAsync().ConfigureAwait(true);
        }
        else
        {
            if (CurrentSubText is not null)
                CurrentSubText.Text = "Load any UPK with material exports. For particle effects pick an Effects_*.upk; for costumes pick a character UPK.";
            if (HeroSkillPicker is not null) HeroSkillPicker.Visibility = Visibility.Collapsed;
            if (GroupsScrollView is not null) GroupsScrollView.Visibility = Visibility.Visible;
            UpdateEmptyState();
        }
    }

    /// <summary>
    /// The packages that carry a power's art when its own package does not.
    /// </summary>
    /// <remarks>
    /// The game splits some powers into versions and leaves the named package
    /// a stub that only points at them. One skill is
    /// PowerThor_LightningStrike, whose package holds six exports and no
    /// particles, while PowerThor_LightningStrikeOF and
    /// PowerThor_LightningStrikeNoOF beside it hold the whole effect — the
    /// Odinforce and plain versions of the same skill.
    /// <para>
    /// Found by the class name the power itself declares, not by its display
    /// name: the class is what the game writes the package after, so a package
    /// beginning with it is a version of this power and nothing else is.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Adds every colour the skill's own packages hold that the walk out from
    /// its bindings did not reach — including the packages beside it that the
    /// bindings never name.
    /// </summary>
    /// <remarks>
    /// Two things go missing otherwise, both measured on one ground-slam skill.
    /// <para>
    /// The walk follows a skill to the emitters it binds and reads the modules
    /// on the way, so a module hanging off an emitter it never reaches is never
    /// offered and never written. The condition effect behind Rolling Thunder
    /// holds 33 colour modules and the walk found 21.
    /// </para>
    /// <para>
    /// And a power is more files than its bindings name. Rolling Thunder binds
    /// five packages, but the wave that travels the ground is drawn by a sixth,
    /// UC__PowerThor_ShockwaveMissileEffect_SF, which holds 39 colours of its
    /// own. Recolouring the five turned the burst red and left the ground blue.
    /// Those siblings are found the same way the no-bindings path already finds
    /// them: by the class name the power itself declares.
    /// </para>
    /// <para>
    /// The hero's own package is left out. It carries every skill's trails at
    /// once, so sweeping it whole would put another skill's effects in this
    /// one's list.
    /// </para>
    /// </remarks>
    private async System.Threading.Tasks.Task<int> CompleteColorsFromSkillPackagesAsync(
        PowerEntry? power, string? cookedDir)
    {
        var seen = new System.Collections.Generic.HashSet<string>(
            _extraColorEntries.Where(en => !string.IsNullOrEmpty(en.ExportPath)).Select(en => en.ExportPath),
            System.StringComparer.OrdinalIgnoreCase);

        var packages = _extraColorEntries
            .Select(en => en.SourceUpkPath)
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(path => path!)
            .Concat(VariantPackagesFor(power, cookedDir))
            .Where(path => !System.IO.Path.GetFileName(path)
                .StartsWith("UC__MarvelPlayer_", System.StringComparison.OrdinalIgnoreCase))
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .ToList();

        int added = 0;

        foreach (string package in packages)
        {
            // Materials and material instances as well as particles, which is
            // what reading a package whole gives that reading its modules does
            // not.
            foreach (var en in await _heroSkillCatalog.CollectColorsFromUpkAsync(package).ConfigureAwait(true))
            {
                if (en.Kind == HeroSkillCatalog.SkillColorKind.MicVectorParam) continue;
                if (string.IsNullOrEmpty(en.ExportPath) || !seen.Add(en.ExportPath)) continue;

                _extraColorEntries.Add(en);
                added++;
            }

            // Then the modules an object walk of that package still steps past.
            foreach (var en in WholePackageColours.In(package))
            {
                if (string.IsNullOrEmpty(en.ExportPath) || !seen.Add(en.ExportPath)) continue;

                _extraColorEntries.Add(en);
                added++;
            }
        }

        OmegaAssetStudio.WinUI.App.WriteDiagnosticsLog("SkillRecolor.Complete",
            $"completed {packages.Count} package(s): {added} colour(s) the binding walk missed");

        return added;
    }

    private static IEnumerable<string> VariantPackagesFor(PowerEntry? power, string? cookedDir)
    {
        string cls = power?.PowerUnrealClassName ?? string.Empty;

        if (cls.Length == 0 || string.IsNullOrEmpty(cookedDir) || !Directory.Exists(cookedDir))
            yield break;

        string own = Path.Combine(cookedDir, "UC__" + cls + "_SF.upk");

        foreach (string path in Directory.EnumerateFiles(cookedDir, "UC__" + cls + "*_SF.upk"))
        {
            // The power's own package was already resolved and found wanting.
            if (string.Equals(path, own, StringComparison.OrdinalIgnoreCase)) continue;

            yield return path;
        }
    }

    /// <summary>
    /// The package holding this power's art for one costume, where the game
    /// ships one.
    /// </summary>
    /// <remarks>
    /// A hero's power list is the same whichever costume is worn — the game's
    /// own data gates only nine progression entries on a costume across all
    /// three clients, and none at all in two of them. What does differ is the
    /// art: one melee skill ships once plainly and again as
    /// BroadStrike_SuperSoldier and BroadStrike_TheCaptain, 77 such packages
    /// across 37 of the game's 514 costumes.
    /// <para>
    /// So a costume is not a different list of powers, it is a different file
    /// for some of them — and editing that file is what recolours one costume
    /// and leaves the others alone. The costume's own name is taken from the
    /// model package the row was built from, so it is spelled exactly as the
    /// power package spells it.
    /// </para>
    /// </remarks>
    private static string? CostumePackageFor(PowerEntry? power, HeroRowVM? hero, string? cookedDir)
    {
        string cls = power?.PowerUnrealClassName ?? string.Empty;

        if (cls.Length == 0 || hero is null || string.IsNullOrEmpty(cookedDir)) return null;

        string stem = Path.GetFileNameWithoutExtension(hero.UpkPath);

        const string lead = "UC__MarvelPlayer_";
        const string tail = "_SF";

        if (!stem.StartsWith(lead, StringComparison.OrdinalIgnoreCase)) return null;

        string middle = stem[lead.Length..];
        if (middle.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) middle = middle[..^tail.Length];

        int cut = middle.IndexOf('_');
        if (cut < 0) return null;                       // the default costume has no name of its own

        string costume = middle[(cut + 1)..];
        if (costume.Length == 0) return null;

        string path = Path.Combine(cookedDir, "UC__" + cls + "_" + costume + tail + ".upk");

        return File.Exists(path) ? path : null;
    }

    private async Task PopulateHeroesAsync()
    {
        // Belt-and-suspenders: never touch UI fields before the page has
        // been fully constructed. The Loaded event always fires after every
        // x:Name element is initialized; init-time event handlers don't.
        if (StatusText is null || HeroRailListView is null) return;
        StatusText.Text = "Enumerating heroes...";
        string? cookedDir = GameInstallService.GetCookedDataDir();
        if (string.IsNullOrWhiteSpace(cookedDir) || !Directory.Exists(cookedDir))
        {
            StatusText.Text = "Set your game folder in Settings to use Hero Skills mode.";
            return;
        }
        string? sipPath = GameInstallService.GetCalligraphySipPath();
        if (string.IsNullOrWhiteSpace(sipPath) || !File.Exists(sipPath))
        {
            StatusText.Text = "Calligraphy.sip not found. Set the game install in Settings.";
            return;
        }

        if (!_heroSkillCatalog.TryOpenArchive(sipPath))
        {
            StatusText.Text = "Couldn't open Calligraphy.sip.";
            return;
        }

        var heroes = await _heroSkillCatalog.EnumerateHeroesAsync(cookedDir).ConfigureAwait(true);
        _heroRowsAll.Clear();
        _heroRows.Clear();
        foreach (var h in heroes)
        {
            var vm = new HeroRowVM
            {
                Token = h.Token,
                Variant = h.Variant,
                DisplayName = h.DisplayName,
                UpkPath = h.UpkPath,
            };
            _heroRowsAll.Add(vm);
            _heroRows.Add(vm);
        }
        // Both the left rail (always visible) and the legacy hidden ListView
        // bind to the same source so hero filter / selection stays in sync.
        if (HeroRailListView is not null) HeroRailListView.ItemsSource = _heroRows;
        HeroListView.ItemsSource = _heroRows;
        SkillListView.ItemsSource = _skillRows;
        _heroesPopulated = true;
        StatusText.Text = $"Loaded {_heroRowsAll.Count} hero entries. Pick one.";
    }

    // New left-rail handlers. The rail is the primary navigation; the old
    // HeroListView is kept (collapsed) so the existing selection handler still
    // runs the skill-load flow.
    private void HeroRailListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HeroRailListView.SelectedItem is HeroRowVM hero)
        {
            // Mirror to the legacy ListView so the existing handler picks it up.
            HeroListView.SelectedItem = hero;
            SkillsHeroNameText.Text = hero.DisplayName;
            SkillsBackButton.Visibility = Visibility.Visible;
            // Make sure we're showing the skill picker, not stale color groups.
            HeroSkillPicker.Visibility = Visibility.Visible;
            GroupsScrollView.Visibility = Visibility.Collapsed;
            // Picking a different hero on the rail also rewinds us out of
            // any active skill-colors view, so the per-skill Back button
            // shouldn't linger in the header.
            if (BackToSkillsButton is not null) BackToSkillsButton.Visibility = Visibility.Collapsed;
        }
    }

    private void HeroRailFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string q = (HeroRailFilterBox?.Text ?? string.Empty).Trim();
        _heroRows.Clear();
        foreach (var h in _heroRowsAll)
        {
            if (q.Length == 0 || h.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase))
                _heroRows.Add(h);
        }
    }

    private void SkillsBackButton_Click(object sender, RoutedEventArgs e)
    {
        HeroRailListView.SelectedItem = null;
        HeroListView.SelectedItem = null;
        _skillRows.Clear();
        SkillsHeroNameText.Text = "Pick a hero on the left";
        SkillHintText.Text = "Pick a hero on the left to see their skills.";
        SkillsBackButton.Visibility = Visibility.Collapsed;
        // Clearing the hero invalidates the loaded color slots — wipe the
        // breakdown chips and warning so the right panel doesn't show stale
        // info from the previous skill.
        if (BreakdownWrap is not null) BreakdownWrap.Children.Clear();
        if (BreakdownEmptyText is not null) BreakdownEmptyText.Visibility = Visibility.Visible;
        RefreshCrossPackageWarning(System.Array.Empty<HeroSkillCatalog.SkillMaterialRef>());
    }

    private void HeroFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string q = (HeroFilterBox?.Text ?? string.Empty).Trim();
        _heroRows.Clear();
        foreach (var h in _heroRowsAll)
        {
            if (q.Length == 0 || h.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase))
                _heroRows.Add(h);
        }
    }

    private async void HeroListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HeroListView.SelectedItem is not HeroRowVM hero)
        {
            _selectedHero = null;
            _skillRows.Clear();
            SkillHintText.Text = "Pick a hero to see their skills.";
            return;
        }

        _selectedHero = hero;
        _skillRows.Clear();
        SkillHintText.Text = "Loading skills...";
        var skills = await _heroSkillCatalog.GetSkillsAsync(hero.Token).ConfigureAwait(true);
        var newRows = new List<SkillRowVM>();
        foreach (var p in skills)
        {
            string name = string.IsNullOrEmpty(p.DisplayName) ? p.PowerName : p.DisplayName;
            string subtitle = string.IsNullOrEmpty(p.PowerUnrealClassName)
                ? "no power class resolved"
                : p.PowerUnrealClassName;
            var vm = new SkillRowVM { DisplayName = name, Subtitle = subtitle, Power = p };
            _skillRows.Add(vm);
            newRows.Add(vm);
        }

        // Expand slot-container powers (any hero with "Stolen" trait/power
        // collection rows, and any future hero with the same shape) into
        // one row per underlying per-power UPK on disk. Those rows show "no
        // power class resolved" because the slot prototype has no direct
        // PowerUnrealClass field — the actual power is decided at runtime —
        // but the per-power FX UPKs absolutely exist (UC__Power<Char>_Stolen*_SF.upk)
        // and ARE individually recolorable. So we drop the abstract slot
        // rows and replace them with the concrete UPK roster.
        TryExpandStolenSlotRows(hero.Token, newRows);

        SkillHintText.Text = _skillRows.Count == 0
            ? "No visible skills found for this hero."
            : $"Click any of the {_skillRows.Count} skills to edit its FX colors.";

        // Kick off icon decoding in the background so the list shows
        // immediately and icons populate as they decode.
        _ = LoadSkillIconsAsync(newRows);
    }

    // Per-page icon decode cache so re-clicking the same hero doesn't
    // re-decode every texture. Keyed by "<upk>::<exportPath>".
    private readonly Dictionary<string, Microsoft.UI.Xaml.Media.ImageSource?> _skillIconCache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly OmegaAssetStudio.TexturePreview.UpkTextureLoader _iconLoader = new();

    // Loads the texture for each skill's IconAssetPath in the background and
    // assigns to the row's IconBitmap on the UI thread. Self-contained — does
    // not share state with AnimationPreviewPage's loader. Same path-parsing
    // rules apply (undotted asset names fall back to "MarvelUIIcons", icon
    // UPK is "ICO__<pkg>_SF.upk" first then bare "<pkg>.upk").
    private async Task LoadSkillIconsAsync(List<SkillRowVM> rows)
    {
        string? cookedDir = GameInstallService.GetCookedDataDir();
        if (string.IsNullOrWhiteSpace(cookedDir) || !Directory.Exists(cookedDir)) return;

        // Count only rows that have an icon path. Rows without one will never
        // produce an icon and shouldn't drag the progress denominator down.
        int totalWithIcon = rows.Count(r => !string.IsNullOrWhiteSpace(r.Power?.IconAssetPath));
        int doneCount = 0;
        UpdateIconProgress(doneCount, totalWithIcon, justStarting: true);

        await Task.Run(async () =>
        {
            foreach (var row in rows)
            {
                string assetPath = row.Power?.IconAssetPath ?? string.Empty;
                if (string.IsNullOrWhiteSpace(assetPath)) continue;

                int dot = assetPath.IndexOf('.');
                string pkg, asset;
                if (dot <= 0 || dot >= assetPath.Length - 1)
                {
                    pkg = "MarvelUIIcons";
                    asset = assetPath;
                }
                else
                {
                    pkg = assetPath.Substring(0, dot);
                    asset = assetPath.Substring(dot + 1);
                }

                string upk = Path.Combine(cookedDir, $"ICO__{pkg}_SF.upk");
                if (!File.Exists(upk))
                {
                    string alt = Path.Combine(cookedDir, pkg + ".upk");
                    if (File.Exists(alt)) upk = alt;
                    else continue;
                }

                string exportPath = $"{pkg.ToLowerInvariant()}.{asset.ToLowerInvariant()}";
                string cacheKey = $"{Path.GetFileName(upk)}::{exportPath}";

                if (_skillIconCache.TryGetValue(cacheKey, out var cached))
                {
                    if (cached is not null)
                    {
                        var rowRef = row;
                        DispatcherQueue?.TryEnqueue(() => { rowRef.IconBitmap = cached; });
                    }
                    doneCount++;
                    UpdateIconProgress(doneCount, totalWithIcon);
                    continue;
                }

                try
                {
                    var preview = await _iconLoader.LoadFromUpkAsync(
                        upk, exportPath,
                        OmegaAssetStudio.TexturePreview.TexturePreviewMaterialSlot.Diffuse,
                        null, requestedMipIndex: null).ConfigureAwait(false);
                    if (preview is null || preview.RgbaPixels is null || preview.Width <= 0 || preview.Height <= 0)
                    {
                        _skillIconCache[cacheKey] = null;
                        doneCount++;
                        UpdateIconProgress(doneCount, totalWithIcon);
                        continue;
                    }

                    int w = preview.Width;
                    int h = preview.Height;
                    // RGBA -> BGRA for WriteableBitmap.
                    byte[] bgra = new byte[preview.RgbaPixels.Length];
                    for (int i = 0; i < preview.RgbaPixels.Length; i += 4)
                    {
                        bgra[i + 0] = preview.RgbaPixels[i + 2];
                        bgra[i + 1] = preview.RgbaPixels[i + 1];
                        bgra[i + 2] = preview.RgbaPixels[i + 0];
                        bgra[i + 3] = preview.RgbaPixels[i + 3];
                    }
                    var tcs = new TaskCompletionSource<Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap?>();
                    var rowRef = row;
                    DispatcherQueue?.TryEnqueue(() =>
                    {
                        try
                        {
                            var wb = new Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap(w, h);
                            using var s = wb.PixelBuffer.AsStream();
                            s.Write(bgra, 0, bgra.Length);
                            wb.Invalidate();
                            rowRef.IconBitmap = wb;
                            tcs.TrySetResult(wb);
                        }
                        catch { tcs.TrySetResult(null); }
                    });
                    var produced = await tcs.Task.ConfigureAwait(false);
                    _skillIconCache[cacheKey] = produced;
                    doneCount++;
                    UpdateIconProgress(doneCount, totalWithIcon);
                }
                catch
                {
                    _skillIconCache[cacheKey] = null;
                    doneCount++;
                    UpdateIconProgress(doneCount, totalWithIcon);
                }
            }
        }).ConfigureAwait(false);
    }

    // Drives the three-state progress bubble in the skills header.
    // Red    : just starting (done == 0)
    // Yellow : in progress (0 < done < total)
    // Green  : complete (done >= total, including the 0/0 "nothing to load" case)
    private void UpdateIconProgress(int done, int total, bool justStarting = false)
    {
        void Apply()
        {
            if (IconProgressBubble is null || IconProgressDot is null || IconProgressText is null) return;
            if (total <= 0)
            {
                IconProgressBubble.Visibility = Visibility.Collapsed;
                return;
            }
            IconProgressBubble.Visibility = Visibility.Visible;
            IconProgressText.Text = $"Icons {done}/{total}";
            Windows.UI.Color fill;
            if (done >= total)        fill = Windows.UI.Color.FromArgb(0xFF, 0x3F, 0xBD, 0x52); // green
            else if (justStarting || done == 0) fill = Windows.UI.Color.FromArgb(0xFF, 0xD2, 0x3C, 0x3C); // red
            else                       fill = Windows.UI.Color.FromArgb(0xFF, 0xE6, 0xC2, 0x29); // yellow
            IconProgressDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(fill);
        }
        if (DispatcherQueue?.HasThreadAccess == true) Apply();
        else DispatcherQueue?.TryEnqueue(Apply);
    }

    private async void SkillListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SkillListView.SelectedItem is not SkillRowVM skill || skill.Power is null) return;

        // A different skill starts fresh: its own colour to seed the picker
        // from, and nothing turned off. Both are kept across the reload that
        // Apply does, and neither should carry over to the next skill.
        _userChoseColour = false;
        _excludedExportPaths.Clear();
        _excludedMicParams.Clear();

        await LoadMaterialsForSkillAsync(skill).ConfigureAwait(true);
        // Once a skill is loaded the colors view takes over the middle panel,
        // so surface the Back-to-Skills button. The picker swap itself happens
        // inside LoadMaterialsForSkillAsync.
        BackToSkillsButton.Visibility = Visibility.Visible;
    }

    // Mirror the picker-swap LoadMaterialsForSkillAsync does, in reverse:
    // hide the colors view, show the skill picker, clear the active selection
    // so re-clicking the same skill still re-fires SelectionChanged. We don't
    // wipe _materials / _groups / _extraColorEntries — they're cheap to keep
    // and if the user immediately re-picks the same skill the cached header
    // makes the reload essentially free.
    private void BackToSkillsButton_Click(object sender, RoutedEventArgs e)
    {
        BackToSkillsButton.Visibility = Visibility.Collapsed;
        GroupsScrollView.Visibility = Visibility.Collapsed;
        HeroSkillPicker.Visibility = Visibility.Visible;

        // SelectedItem = null suppresses the SelectionChanged handler firing
        // (WinUI's ListView raises it with a null SelectedItem on this path
        // but our handler early-returns on null), and clears the highlight
        // so the user sees a clean skill list to pick from.
        SkillListView.SelectedItem = null;

        // Reset header readouts so the colors-view chips don't mislead.
        CurrentUpkText.Text = SkillsHeroNameText.Text ?? string.Empty;
        CurrentSubText.Text = "Pick a skill on the list to load its color slots.";
        StatusText.Text = "// AWAITING SKILL //";
    }

    // Resolves the skill's PowerFX bindings, walks the referenced particle
    // systems for material refs, then loads materials from each source UPK
    // and filters down to just the referenced names. Replaces the current
    // _materials/_groups so the existing slider/save flow operates on the
    // skill scope.
    private async Task LoadMaterialsForSkillAsync(SkillRowVM skill)
    {
        if (skill.Power is null) return;
        string? cookedDir = GameInstallService.GetCookedDataDir();
        if (string.IsNullOrWhiteSpace(cookedDir)) return;

        StatusText.Text = $"Resolving FX for {skill.DisplayName}...";
        HeroSkillPicker.Visibility = Visibility.Collapsed;
        GroupsScrollView.Visibility = Visibility.Visible;
        GroupsContainer.Children.Clear();
        GroupsSkeleton.Visibility = Visibility.Visible;

        try
        {
            var vfx = await _heroSkillCatalog.ResolveSkillVfxAsync(skill.Power, cookedDir).ConfigureAwait(true);
            _currentVfx = vfx; // stash for the "Inspect color source" diagnostic
            _currentSkillRow = skill; // stash for the hero-scoped "find related effect" search
            if (vfx is null || vfx.Bindings.Count == 0)
            {
                // No bindings does not mean no colours. A skill that declares
                // no particle systems of its own is still drawn — one sky-strike
                // skill has none and is anything but invisible — because what
                // it shows hangs off the hero: weapon trails, and the effects
                // animation notifies fire. Those live in the hero's own package
                // and the hero-wide scan finds them, so it is run here rather
                // than giving up with an empty panel.
                _materials.Clear();
                _groups.Clear();
                _baseValues.Clear();
                _paramOwners.Clear();
                _extraColorEntries.Clear();

                string loneToken = skill.Power?.CharacterToken ?? string.Empty;
                string? loneCooked = OmegaAssetStudio.WinUI.Services.GameInstallService.GetCookedDataDir();

                // The power's own variants first. A skill whose own package is
                // a stub is usually one the game splits in two — Crack the Sky
                // ships an Odinforce and a non-Odinforce version, and its own
                // package holds six exports and no particles at all while the
                // two beside it hold 180 KB apiece. Those are this skill's
                // colours; the hero's trails are not.
                int fromVariants = 0;

                // This costume's own version of the power, where the game ships
                // one. Editing it recolours this costume and leaves the rest.
                string? costumeOnly = CostumePackageFor(skill.Power, _selectedHero, loneCooked);

                if (costumeOnly is not null)
                {
                    foreach (var en in await _heroSkillCatalog
                                 .CollectColorsFromUpkAsync(costumeOnly).ConfigureAwait(true))
                    {
                        _extraColorEntries.Add(en);
                        fromVariants++;
                    }
                }

                foreach (string variant in VariantPackagesFor(skill.Power, loneCooked))
                {
                    foreach (var en in await _heroSkillCatalog
                                 .CollectColorsFromUpkAsync(variant).ConfigureAwait(true))
                    {
                        _extraColorEntries.Add(en);
                        fromVariants++;
                    }
                }

                // Only where the power itself yields nothing: what the hero
                // carries, which is every skill's rather than this one's.
                if (fromVariants == 0 && !string.IsNullOrEmpty(loneToken) && !string.IsNullOrEmpty(loneCooked))
                {
                    foreach (var en in await _heroSkillCatalog
                                 .CollectHeroPlayerColorsAsync(loneToken, loneCooked).ConfigureAwait(true))
                    {
                        if (en.Kind == HeroSkillCatalog.SkillColorKind.MicVectorParam) continue;
                        _extraColorEntries.Add(en);
                    }
                }

                await CompleteColorsFromSkillPackagesAsync(skill.Power, loneCooked).ConfigureAwait(true);

                MicCountChip.Text = "0 materials";
                GroupCountChip.Text = $"{_extraColorEntries.Count} colors";
                ParticleSystemChip.Visibility = Visibility.Collapsed;
                CurrentUpkText.Text = skill.DisplayName;

                string where = fromVariants > 0 ? "its own variant packages" : "the hero";

                CurrentSubText.Text = _extraColorEntries.Count == 0
                    ? "This skill has no particle bindings in its per-power UPK, and nothing beside it either."
                    : $"This skill's own package declares no particle systems. Showing the {_extraColorEntries.Count} colour slot(s) from {where}.";

                StatusText.Text = _extraColorEntries.Count == 0
                    ? $"{skill.DisplayName}: no colours found for this skill."
                    : $"{skill.DisplayName}: {_extraColorEntries.Count} colour slot(s) from {where}.";

                RebuildGroupList();

                // The same cards and the same finish every other skill gets.
                // BuildSkillSummary reads the colour entries and the material
                // groups, never the bindings, so it draws this case as happily
                // as any other — and its per-slot ticks are the ones the writer
                // honours.
                BuildSkillSummary(skill, vfx!, 0, _extraColorEntries.Count);
                UpdateCurrentColorPreview();
                return;
            }

            var matRefs = await _heroSkillCatalog.CollectParticleMaterialsAsync(vfx).ConfigureAwait(true);
            // Surface the specific cross-package materials so the warning can name them.
            var crossRefs = matRefs.Where(r => r.IsCrossPackage).ToList();
            _lastCrossRefs = crossRefs;
            RefreshCrossPackageWarning(crossRefs);
            // Group by source UPK so we only load each UPK once.
            var byUpk = matRefs
                .GroupBy(r => r.SourceUpkPath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => new HashSet<string>(g.Select(x => x.MaterialExportPath), StringComparer.OrdinalIgnoreCase),
                              StringComparer.OrdinalIgnoreCase);

            _materials.Clear();
            _groups.Clear();
            _baseValues.Clear();
            _paramOwners.Clear();
            // Fresh skill load → every slot starts selected.
            _excludedMicParams.Clear();
            _excludedExportPaths.Clear();

            foreach (var (upkPath, wantedNames) in byUpk)
            {
                IReadOnlyList<MaterialDefinition> mats;
                try { mats = await _service.LoadMaterialsFromUpkAsync(upkPath).ConfigureAwait(true); }
                catch { continue; }
                foreach (var mat in mats)
                {
                    // The particle module references a material by short name —
                    // accept any MIC whose Name OR last path segment matches one
                    // of the wanted names.
                    string leaf = mat.Name;
                    string fullLeaf = mat.Path;
                    int dot = fullLeaf.LastIndexOf('.');
                    if (dot >= 0 && dot + 1 < fullLeaf.Length) fullLeaf = fullLeaf[(dot + 1)..];
                    if (!wantedNames.Contains(leaf) && !wantedNames.Contains(fullLeaf)) continue;
                    _materials.Add(mat);
                    foreach (MaterialParameter p in mat.VectorParameters)
                    {
                        if (string.IsNullOrWhiteSpace(p.Name)) continue;
                        if (!_groups.TryGetValue(p.Name, out GroupRow? row))
                        {
                            row = new GroupRow { Name = p.Name };
                            _groups[p.Name] = row;
                        }
                        row.Parameters.Add(p);
                        _paramOwners[p] = mat;
                        if (p.VectorValue is Vector4 v && !_baseValues.ContainsKey(p))
                            _baseValues[p] = v;
                    }
                }
            }

            // Phase 2 — surface every color slot the skill exposes, not just MIC
            // params. Material expressions + particle color modules are read-only
            // until the byte-patcher lands; they still show so the user can SEE
            // every color the skill uses (verified against MHO_HeroSkill_Color_DeepDive.md).
            _extraColorEntries.Clear();
            try
            {
                var allEntries = await _heroSkillCatalog.CollectSkillColorsAsync(vfx).ConfigureAwait(true);
                // The MIC entries are already shown via GroupRow; the new card
                // pass only renders the OTHER three kinds.
                foreach (var e in allEntries)
                {
                    if (e.Kind != HeroSkillCatalog.SkillColorKind.MicVectorParam)
                        _extraColorEntries.Add(e);
                }

                // Hero-wide augmentation. Prototype-driven binding misses
                // weapon-trail / anim-notify particle systems that live inside
                // UC__MarvelPlayer_<Hero>_SF.upk (vfx_*_animtrail_*,
                // vfx_*_attack_trails_*, etc) — surface them here so the user
                // sees the full recolorable inventory the legacy MHModelEditor
                // showed. Dedup by ExportPath against per-skill entries.
                string heroToken = skill.Power?.CharacterToken ?? string.Empty;
                string? cookedForHero = OmegaAssetStudio.WinUI.Services.GameInstallService.GetCookedDataDir();
                if (!string.IsNullOrEmpty(heroToken) && !string.IsNullOrEmpty(cookedForHero))
                {
                    var heroWide = await _heroSkillCatalog.CollectHeroPlayerColorsAsync(heroToken, cookedForHero).ConfigureAwait(true);
                    var seenPaths = new System.Collections.Generic.HashSet<string>(
                        _extraColorEntries.Where(en => !string.IsNullOrEmpty(en.ExportPath)).Select(en => en.ExportPath),
                        System.StringComparer.OrdinalIgnoreCase);
                    int added = 0;
                    foreach (var e in heroWide)
                    {
                        if (e.Kind == HeroSkillCatalog.SkillColorKind.MicVectorParam) continue;
                        if (string.IsNullOrEmpty(e.ExportPath) || seenPaths.Add(e.ExportPath))
                        {
                            _extraColorEntries.Add(e);
                            added++;
                        }
                    }
                    OmegaAssetStudio.WinUI.App.WriteDiagnosticsLog("SkillRecolor.HeroWide",
                        $"=== {skill.DisplayName} hero='{heroToken}': hero-wide scan added {added} extra slot(s) from {heroWide.Count} candidate(s) ===");
                }

                // The costume's own version of this power, where the game ships
                // one. Shown last and counted apart, because these are the only
                // slots whose colours belong to one costume alone — everything
                // above is shared by every costume the character has.
                string? costumeOwn = CostumePackageFor(skill.Power, _selectedHero, cookedForHero);

                if (costumeOwn is not null)
                {
                    var already = new System.Collections.Generic.HashSet<string>(
                        _extraColorEntries.Where(en => !string.IsNullOrEmpty(en.ExportPath)).Select(en => en.ExportPath),
                        System.StringComparer.OrdinalIgnoreCase);

                    int mine = 0;

                    foreach (var en in await _heroSkillCatalog
                                 .CollectColorsFromUpkAsync(costumeOwn).ConfigureAwait(true))
                    {
                        if (string.IsNullOrEmpty(en.ExportPath) || already.Add(en.ExportPath))
                        {
                            _extraColorEntries.Add(en);
                            mine++;
                        }
                    }

                    OmegaAssetStudio.WinUI.App.WriteDiagnosticsLog("SkillRecolor.Costume",
                        $"=== {skill.DisplayName}: {mine} slot(s) from this costume's own package {System.IO.Path.GetFileName(costumeOwn)} ===");
                }
                await CompleteColorsFromSkillPackagesAsync(skill.Power, cookedForHero).ConfigureAwait(true);

                // DIAGNOSTIC: dump every discovered color source so we can see
                // which sources (and which UPK) feed each visual element.
                OmegaAssetStudio.WinUI.App.WriteDiagnosticsLog("SkillRecolor.Sources",
                    $"=== {skill.DisplayName}: {allEntries.Count} color source(s) ===");
                foreach (var e in allEntries)
                    OmegaAssetStudio.WinUI.App.WriteDiagnosticsLog("SkillRecolor.Sources",
                        $"  {e.Kind} | {(e.IsCrossPackage ? "CROSS" : "local")} | edit={e.Editable} | {System.IO.Path.GetFileName(e.SourceUpkPath ?? "")} | {e.OwnerLabel} | {e.ExportPath}");
                try
                {
                    var proto = await _heroSkillCatalog.DumpPowerPrototypeAsync(skill.Power).ConfigureAwait(true);
                    OmegaAssetStudio.WinUI.App.WriteDiagnosticsLog("SkillRecolor.Proto", $"=== {skill.DisplayName}: prototype graph ===");
                    foreach (var ln in proto)
                        OmegaAssetStudio.WinUI.App.WriteDiagnosticsLog("SkillRecolor.Proto", ln);
                }
                catch { }
                OmegaAssetStudio.WinUI.App.WriteDiagnosticsLog("SkillRecolor.Bindings", $"=== {skill.DisplayName}: {vfx.Bindings.Count} binding(s) ===");
                foreach (var b in vfx.Bindings)
                    OmegaAssetStudio.WinUI.App.WriteDiagnosticsLog("SkillRecolor.Bindings",
                        $"  {b.ComponentClass} | {b.ComponentName} | psRef={b.ParticleSystemRef} | full={b.ParticleSystemFullPath} | resolved={(b.ResolvedParticleSystem is not null)} | srcUpk={System.IO.Path.GetFileName(b.SourceUpkFullPath ?? "")}");
                try
                {
                    var tree = await _heroSkillCatalog.DumpVfxMaterialTreeAsync(vfx).ConfigureAwait(true);
                    OmegaAssetStudio.WinUI.App.WriteDiagnosticsLog("SkillRecolor.Tree", $"=== {skill.DisplayName}: emitter/material tree ===");
                    foreach (var ln in tree)
                        OmegaAssetStudio.WinUI.App.WriteDiagnosticsLog("SkillRecolor.Tree", ln);
                }
                catch { }
            }
            catch { /* read-side is best-effort */ }

            // Now that the cross-package material color slots are collected, refine
            // the warning to name each shared material AND the exact UPK it lives in.
            RefreshCrossPackageWarningFromEntries();

            // Header / chips reflect the skill scope.
            CurrentUpkText.Text = skill.DisplayName;
            CurrentSubText.Text = "Skill scope — colors below drive this skill's particle effects.";
            if (EffectSearchBar is not null) EffectSearchBar.Visibility = Visibility.Visible;
            MicCountChip.Text = _materials.Count == 1 ? "1 material" : _materials.Count + " materials";
            int totalColors = _groups.Count + _extraColorEntries.Count;
            GroupCountChip.Text = totalColors == 1 ? "1 color" : totalColors + " colors";
            ParticleSystemChip.Visibility = Visibility.Visible;
            int psCount = vfx.Bindings.Count(b => b.ResolvedParticleSystem is not null);
            ParticleSystemChipText.Text = psCount == 1 ? "1 particle system" : psCount + " particle systems";
            // Rebuild the internal toggle state, then render the skill summary
            // cards into the middle column so modders can SEE what they're
            // about to recolor (particle systems, emitters, materials, every
            // color slot with current swatch + editability).
            RebuildGroupList();
            BuildSkillSummary(skill, vfx, psCount, totalColors);
            UpdateCurrentColorPreview();
            StatusText.Text = $"{skill.DisplayName}: {totalColors} color slot(s) across {_materials.Count} material(s) and {psCount} particle system(s).";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Skill load failed: " + ex.Message;
        }
        finally
        {
            GroupsSkeleton.Visibility = Visibility.Collapsed;
        }
    }

    // Renders a full "what is in this skill" card stack into the middle
    // column's GroupsContainer. Top-level layout (top to bottom):
    //
    //   [OVERVIEW CARD] — skill name + headline counts
    //   [PARTICLE SYSTEMS SECTION] — one group per particle system, with
    //       per-emitter cards listing every particle color module the
    //       writer can patch (StartColor, ColorOverLife, ColorScaleOverLife).
    //   [MATERIALS SECTION] — one group per MIC / Material, listing every
    //       MIC vector parameter and Material expression color (Vector
    //       parameter, Constant3Vector, Constant4Vector).
    //
    // Every color slot is shown as a row card with a live swatch, the slot
    // name, its owner, a colored kind chip, and a lock icon when the slot
    // is read-only (e.g. parameterized particle distributions).
    private void BuildSkillSummary(
        SkillRowVM skill,
        OmegaAssetStudio.Calligraphy.PowerVfxResolver.ResolvedVfx vfx,
        int particleSystemCount,
        int totalColorSlots)
    {
        if (GroupsContainer is null) return;
        GroupsContainer.Children.Clear();
        _slotCheckboxes.Clear();

        // ----- OVERVIEW CARD -----
        GroupsContainer.Children.Add(BuildSkillOverviewCard(skill, particleSystemCount, totalColorSlots));

        // ----- PARTICLE SYSTEMS -----
        // Bucket every particle entry under its host ps name -> emitter name
        // by parsing the OwnerLabel the catalog already produces.
        var particleKinds = new HashSet<HeroSkillCatalog.SkillColorKind>
        {
            HeroSkillCatalog.SkillColorKind.ParticleStartColor,
            HeroSkillCatalog.SkillColorKind.ParticleColorOverLife,
            HeroSkillCatalog.SkillColorKind.ParticleColorScaleOverLife,
        };
        var byParticle = new Dictionary<string, Dictionary<string, List<HeroSkillCatalog.SkillColorEntry>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _extraColorEntries)
        {
            if (!particleKinds.Contains(entry.Kind)) continue;
            (string ps, string emitter) = SplitParticleOwner(entry.OwnerLabel);
            if (!byParticle.TryGetValue(ps, out var emitterMap))
            {
                emitterMap = new Dictionary<string, List<HeroSkillCatalog.SkillColorEntry>>(StringComparer.OrdinalIgnoreCase);
                byParticle[ps] = emitterMap;
            }
            if (!emitterMap.TryGetValue(emitter, out var list))
            {
                list = new List<HeroSkillCatalog.SkillColorEntry>();
                emitterMap[emitter] = list;
            }
            list.Add(entry);
        }

        if (byParticle.Count > 0)
        {
            GroupsContainer.Children.Add(BuildSectionLabel("PARTICLE SYSTEMS"));
            foreach (var (psName, emitterMap) in byParticle.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                GroupsContainer.Children.Add(BuildParticleSystemCard(psName, emitterMap));
        }

        // ----- MATERIALS -----
        // MIC vector params live in _groups + _paramOwners. Material
        // expressions (Vector parameter / Constant3 / Constant4) live in
        // _extraColorEntries keyed by "Material: <name>" OwnerLabel.
        var byMaterial = new Dictionary<string, List<HeroSkillCatalog.SkillColorEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _extraColorEntries)
        {
            if (particleKinds.Contains(entry.Kind)) continue;
            string owner = entry.OwnerLabel.StartsWith("Material:", StringComparison.OrdinalIgnoreCase)
                ? entry.OwnerLabel["Material:".Length..].Trim()
                : entry.OwnerLabel;
            if (!byMaterial.TryGetValue(owner, out var list))
            {
                list = new List<HeroSkillCatalog.SkillColorEntry>();
                byMaterial[owner] = list;
            }
            list.Add(entry);
        }

        bool anyMaterialContent = _materials.Count > 0 || byMaterial.Count > 0;
        if (anyMaterialContent)
        {
            GroupsContainer.Children.Add(BuildSectionLabel("MATERIALS"));

            // MICs first, then base materials. MICs are the most common
            // recolor surface — they get top billing.
            foreach (var mat in _materials.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
                GroupsContainer.Children.Add(BuildMicMaterialCard(mat));

            foreach (var (matName, entries) in byMaterial.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                GroupsContainer.Children.Add(BuildBaseMaterialCard(matName, entries));
        }

        if (byParticle.Count == 0 && !anyMaterialContent)
        {
            GroupsContainer.Children.Add(new TextBlock
            {
                Text = "This skill has no recolorable color sources.",
                FontSize = 11,
                Opacity = 0.6,
                Margin = new Thickness(10, 14, 10, 14),
            });
        }
    }

    private static (string PsName, string EmitterName) SplitParticleOwner(string ownerLabel)
    {
        // Catalog format: "Particle: psName → emitterName"
        const string prefix = "Particle:";
        string body = ownerLabel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? ownerLabel[prefix.Length..].Trim()
            : ownerLabel;
        int arrow = body.IndexOf('→');
        if (arrow < 0) return (body, "(emitter)");
        return (body[..arrow].Trim(), body[(arrow + 1)..].Trim());
    }

    private static TextBlock BuildSectionLabel(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = 120,
            Opacity = 0.6,
            Margin = new Thickness(2, 12, 0, 2),
        };
    }

    private Border BuildSkillOverviewCard(SkillRowVM skill, int particleSystemCount, int totalColorSlots)
    {
        // Headline numbers — three big tiles, each with a count + label.
        // Reads at a glance even without parsing the cards below.
        Grid stats = new() { ColumnSpacing = 8, RowSpacing = 0 };
        stats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        stats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        stats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Border t1 = BuildStatTile(particleSystemCount.ToString(CultureInfo.InvariantCulture), "particle system" + (particleSystemCount == 1 ? string.Empty : "s"));
        Border t2 = BuildStatTile(_materials.Count.ToString(CultureInfo.InvariantCulture), "material" + (_materials.Count == 1 ? string.Empty : "s"));
        Border t3 = BuildStatTile(totalColorSlots.ToString(CultureInfo.InvariantCulture), "color slot" + (totalColorSlots == 1 ? string.Empty : "s"));
        Grid.SetColumn(t1, 0); Grid.SetColumn(t2, 1); Grid.SetColumn(t3, 2);
        stats.Children.Add(t1); stats.Children.Add(t2); stats.Children.Add(t3);

        // Skill identity row — icon (if cached) + display name. We reuse the
        // already-decoded skill icon from the picker list so it appears
        // instantly without re-loading.
        var idGrid = new Grid { ColumnSpacing = 12 };
        idGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        idGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconHost = new Border
        {
            Width = 52, Height = 52,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemedBrush("OmegaAssetStudio.PanelBorderBrush"),
            Background = ThemedBrush("OmegaAssetStudio.PanelSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (skill.IconBitmap is not null)
            iconHost.Child = new Microsoft.UI.Xaml.Controls.Image { Source = skill.IconBitmap, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, Margin = new Thickness(2) };
        Grid.SetColumn(iconHost, 0);
        idGrid.Children.Add(iconHost);

        var nameStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        nameStack.Children.Add(new TextBlock
        {
            Text = skill.DisplayName,
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        nameStack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(skill.Subtitle) ? "Power" : skill.Subtitle,
            FontSize = 11,
            Opacity = 0.6,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(nameStack, 1);
        idGrid.Children.Add(nameStack);

        var outer = new StackPanel { Spacing = 12 };
        outer.Children.Add(idGrid);
        outer.Children.Add(stats);

        return new Border
        {
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(6),
            Background = ThemedBrush("OmegaAssetStudio.PanelSecondaryBrush"),
            BorderBrush = ThemedBrush("OmegaAssetStudio.PanelBorderBrush"),
            BorderThickness = new Thickness(1),
            Child = outer,
        };
    }

    private static Border BuildStatTile(string number, string label)
    {
        var panel = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new TextBlock
        {
            Text = number,
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 10,
            Opacity = 0.65,
            CharacterSpacing = 60,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        return new Border
        {
            Padding = new Thickness(8, 10, 8, 8),
            CornerRadius = new CornerRadius(4),
            Background = ThemedBrush("OmegaAssetStudio.PanelBackgroundBrush"),
            BorderBrush = ThemedBrush("OmegaAssetStudio.PanelBorderBrush"),
            BorderThickness = new Thickness(1),
            Child = panel,
        };
    }

    private Border BuildParticleSystemCard(string psName, Dictionary<string, List<HeroSkillCatalog.SkillColorEntry>> emitterMap)
    {
        var content = new StackPanel { Spacing = 8 };
        var rowsForCard = new List<Grid>();

        // Header row: particle system name + per-card All / None toggles so
        // the modder can flip the entire system in one click.
        content.Children.Add(BuildCardHeader(psName, rowsForCard));

        // One row per emitter, then color rows beneath.
        foreach (var (emitterName, entries) in emitterMap.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            content.Children.Add(new TextBlock
            {
                Text = "Emitter: " + emitterName,
                FontSize = 11,
                Opacity = 0.7,
                Margin = new Thickness(0, 4, 0, 0),
            });
            foreach (var entry in entries.OrderBy(e => e.ParameterName, StringComparer.OrdinalIgnoreCase))
            {
                var rowGrid = BuildColorSlotRow(entry);
                content.Children.Add(rowGrid);
                rowsForCard.Add(rowGrid);
            }
        }

        return new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(6),
            Background = ThemedBrush("OmegaAssetStudio.PanelSecondaryBrush"),
            BorderBrush = ThemedBrush("OmegaAssetStudio.PanelBorderBrush"),
            BorderThickness = new Thickness(1),
            Child = content,
        };
    }

    // Header row used by both particle-system and material cards: title on
    // the left, two tiny "All / None" link-style buttons on the right that
    // fan out to the rows the card owns.
    private Grid BuildCardHeader(string title, List<Grid> rowsForCard)
    {
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(titleBlock, 0);
        header.Children.Add(titleBlock);

        var allBtn = new Button { Content = "All", FontSize = 10, MinHeight = 0, Padding = new Thickness(8, 2, 8, 2), Background = ThemedBrush("OmegaAssetStudio.PanelBackgroundBrush") };
        allBtn.Click += (_, _) => SetAllRowsChecked(rowsForCard, true);
        ToolTipService.SetToolTip(allBtn, "Select every editable slot in this card.");

        var noneBtn = new Button { Content = "None", FontSize = 10, MinHeight = 0, Padding = new Thickness(8, 2, 8, 2), Background = ThemedBrush("OmegaAssetStudio.PanelBackgroundBrush") };
        noneBtn.Click += (_, _) => SetAllRowsChecked(rowsForCard, false);
        ToolTipService.SetToolTip(noneBtn, "Skip every slot in this card on Apply.");

        var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        btns.Children.Add(allBtn);
        btns.Children.Add(noneBtn);
        Grid.SetColumn(btns, 1);
        header.Children.Add(btns);
        return header;
    }

    private Border BuildMicMaterialCard(MaterialDefinition mat)
    {
        var content = new StackPanel { Spacing = 6 };
        var rowsForCard = new List<Grid>();
        content.Children.Add(BuildCardHeader(mat.Name, rowsForCard));
        if (!string.IsNullOrEmpty(mat.Path) && !string.Equals(mat.Path, mat.Name, StringComparison.OrdinalIgnoreCase))
        {
            content.Children.Add(new TextBlock
            {
                Text = mat.Path,
                FontSize = 10,
                Opacity = 0.6,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        // Subtitle chip: MIC vs base material plus param count.
        int paramCount = mat.VectorParameters.Count(p => !string.IsNullOrWhiteSpace(p.Name));
        content.Children.Add(new TextBlock
        {
            Text = $"Material Instance · {paramCount} vector param{(paramCount == 1 ? string.Empty : "s")}",
            FontSize = 10,
            Opacity = 0.55,
            Margin = new Thickness(0, 0, 0, 2),
        });

        foreach (var p in mat.VectorParameters)
        {
            if (string.IsNullOrWhiteSpace(p.Name)) continue;
            Vector4 v = p.VectorValue is Vector4 vv ? vv : new Vector4(1f, 1f, 1f, 1f);
            var rowGrid = BuildColorSlotRow(
                new MicSlotKey(p),
                p.Name,
                "MIC parameter",
                v,
                HeroSkillCatalog.SkillColorKind.MicVectorParam,
                editable: true,
                shape: HeroSkillCatalog.DistributionShape.NotApplicable);
            content.Children.Add(rowGrid);
            rowsForCard.Add(rowGrid);
        }

        return new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(6),
            Background = ThemedBrush("OmegaAssetStudio.PanelSecondaryBrush"),
            BorderBrush = ThemedBrush("OmegaAssetStudio.PanelBorderBrush"),
            BorderThickness = new Thickness(1),
            Child = content,
        };
    }

    private Border BuildBaseMaterialCard(string matName, List<HeroSkillCatalog.SkillColorEntry> entries)
    {
        var content = new StackPanel { Spacing = 6 };
        var rowsForCard = new List<Grid>();
        content.Children.Add(BuildCardHeader(matName, rowsForCard));
        content.Children.Add(new TextBlock
        {
            Text = $"Base Material · {entries.Count} expression color{(entries.Count == 1 ? string.Empty : "s")}",
            FontSize = 10,
            Opacity = 0.55,
            Margin = new Thickness(0, 0, 0, 2),
        });

        foreach (var entry in entries.OrderBy(e => e.ParameterName, StringComparer.OrdinalIgnoreCase))
        {
            var rowGrid = BuildColorSlotRow(entry);
            content.Children.Add(rowGrid);
            rowsForCard.Add(rowGrid);
        }

        return new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(6),
            Background = ThemedBrush("OmegaAssetStudio.PanelSecondaryBrush"),
            BorderBrush = ThemedBrush("OmegaAssetStudio.PanelBorderBrush"),
            BorderThickness = new Thickness(1),
            Child = content,
        };
    }

    // Discriminator used by every clickable color row to know which
    // exclusion set its checkbox should add to / remove from on toggle.
    private abstract record SlotKey;
    private sealed record MicSlotKey(MaterialParameter Param) : SlotKey;
    private sealed record ExportSlotKey(string Path) : SlotKey;

    // Tracks every row checkbox so the section-level "All / None" toggles
    // (and the selected-count label) don't need to crawl the visual tree.
    private readonly List<CheckBox> _slotCheckboxes = new();

    private Grid BuildColorSlotRow(HeroSkillCatalog.SkillColorEntry entry)
    {
        SlotKey key = new ExportSlotKey(entry.ExportPath);
        return BuildColorSlotRow(key, entry.ParameterName, entry.OwnerLabel, entry.CurrentColor, entry.Kind, entry.Editable, entry.Shape);
    }

    // The canonical one-line color-slot card: checkbox + swatch + name +
    // sub-label + kind chip + (optional) shape chip + (optional) read-only
    // lock. Default checked = slot will be tinted; uncheck to opt out.
    private Grid BuildColorSlotRow(
        SlotKey key,
        string parameterName,
        string subLabel,
        Vector4 color,
        HeroSkillCatalog.SkillColorKind kind,
        bool editable,
        HeroSkillCatalog.DistributionShape shape)
    {
        // A parameterized distribution holds no colour of its own: the game
        // supplies one when the particle spawns, and this slot carries the
        // range that value is mapped through. The writer will not touch it —
        // writing a colour over that range left min above max and hung the game
        // on the load screen — so it must not be offered as though it would.
        // The reader marked all 31 in one character's set as editable, which let them
        // be ticked and then silently do nothing.
        if (shape == HeroSkillCatalog.DistributionShape.Parameterized) editable = false;

        var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(4, 4, 6, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });      // checkbox
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });   // swatch
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });      // chip stack

        // Checkbox first so the modder can opt slots in/out at a glance.
        bool keyUsable = !(key is ExportSlotKey ep && string.IsNullOrEmpty(ep.Path));

        // What the user last said about this slot, not a fresh yes. Applying
        // reloads the skill so the swatches show the new colours, and rebuilding
        // every box as ticked threw away the choice — the next Apply then went
        // at everything.
        bool alreadyChosen = key switch
        {
            MicSlotKey mk when mk.Param is not null => !_excludedMicParams.Contains(mk.Param),
            ExportSlotKey ek when !string.IsNullOrEmpty(ek.Path) => !_excludedExportPaths.Contains(ek.Path),
            _ => true,
        };

        var check = new CheckBox
        {
            IsChecked = alreadyChosen,
            IsEnabled = editable && keyUsable,
            MinWidth = 24,
            MinHeight = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (!editable)
        {
            ToolTipService.SetToolTip(check,
                shape == HeroSkillCatalog.DistributionShape.Parameterized
                    ? "The game picks this colour when the particle spawns. This slot only holds the range it is mapped through, so recolouring it would not change what you see — and writing it hangs the game."
                    : "Read-only (parameterized distribution — color is supplied at spawn).");
        }
        else if (!keyUsable)
            ToolTipService.SetToolTip(check, "Per-slot toggle unavailable for this entry.");
        else
            ToolTipService.SetToolTip(check, "Uncheck to leave this slot untouched on Apply.");
        check.Checked   += (_, _) => SetSlotExcluded(key, false);
        check.Unchecked += (_, _) => SetSlotExcluded(key, true);
        Grid.SetColumn(check, 0);
        row.Children.Add(check);
        _slotCheckboxes.Add(check);

        // Swatch — clamped to [0,1] for display since emissive values can blow
        // past 1 and saturate to white; the writer still handles HDR.
        var c = Color.FromArgb(
            255,
            (byte)(Math.Clamp(color.X, 0f, 1f) * 255),
            (byte)(Math.Clamp(color.Y, 0f, 1f) * 255),
            (byte)(Math.Clamp(color.Z, 0f, 1f) * 255));
        var swatch = new Border
        {
            Width = 24, Height = 24,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemedBrush("OmegaAssetStudio.PanelBorderBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(c),
        };
        ToolTipService.SetToolTip(swatch, string.Format(CultureInfo.InvariantCulture, "{0:0.000}  {1:0.000}  {2:0.000}", color.X, color.Y, color.Z));
        Grid.SetColumn(swatch, 1);
        row.Children.Add(swatch);

        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = parameterName,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (!string.IsNullOrEmpty(subLabel))
        {
            text.Children.Add(new TextBlock
            {
                Text = subLabel,
                FontSize = 10,
                Opacity = 0.6,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
        Grid.SetColumn(text, 2);
        row.Children.Add(text);

        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        chips.Children.Add(BuildKindChip(kind, 1, showCount: false));
        if (shape != HeroSkillCatalog.DistributionShape.NotApplicable && shape != HeroSkillCatalog.DistributionShape.Unknown)
            chips.Children.Add(BuildShapeChip(shape));
        if (!editable)
        {
            chips.Children.Add(new FontIcon
            {
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                Glyph = "",  // lock
                FontSize = 11,
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        Grid.SetColumn(chips, 3);
        row.Children.Add(chips);

        return row;
    }

    private void SetSlotExcluded(SlotKey key, bool excluded)
    {
        switch (key)
        {
            case MicSlotKey mk when mk.Param is not null:
                if (excluded) _excludedMicParams.Add(mk.Param);
                else _excludedMicParams.Remove(mk.Param);
                break;
            case ExportSlotKey ek when !string.IsNullOrEmpty(ek.Path):
                if (excluded) _excludedExportPaths.Add(ek.Path);
                else _excludedExportPaths.Remove(ek.Path);
                break;
        }
        UpdateSelectedSlotsLabel();
    }

    // Surfaces "X of Y slots selected" next to Apply so the modder knows how
    // many slots they're about to touch after toggling some off.
    private void UpdateSelectedSlotsLabel()
    {
        if (ApplySummaryText is null) return;
        int total = _slotCheckboxes.Count(c => c.IsEnabled);
        int selected = _slotCheckboxes.Count(c => c.IsEnabled && c.IsChecked == true);
        if (total <= 0)
        {
            ApplySummaryText.Text = "Pick a hero and a skill on the left to begin.";
            return;
        }
        ApplySummaryText.Text = selected == total
            ? $"This skill has {total} editable color slot(s). Pick a new color and click Apply to recolor them all."
            : $"{selected} of {total} editable color slot(s) selected. Apply will only recolor the checked ones.";
    }

    // Flips every editable row in the given list of slot-row Grids to a
    // target checked state. Used by the section-level All / None buttons.
    private static void SetAllRowsChecked(IEnumerable<Grid> rows, bool isChecked)
    {
        foreach (var row in rows)
        {
            // First child of each row is the CheckBox by construction.
            if (row.Children.Count == 0) continue;
            if (row.Children[0] is CheckBox cb && cb.IsEnabled)
                cb.IsChecked = isChecked;
        }
    }

    private static Border BuildShapeChip(HeroSkillCatalog.DistributionShape shape)
    {
        return new Border
        {
            Padding = new Thickness(6, 1, 6, 1),
            CornerRadius = new CornerRadius(8),
            Background = ThemedBrush("OmegaAssetStudio.PanelBackgroundBrush"),
            BorderBrush = ThemedBrush("OmegaAssetStudio.PanelBorderBrush"),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = shape.ToString(),
                FontSize = 9,
                Opacity = 0.7,
            },
        };
    }

    // Short, modder-readable label for each color source. Used in the
    // "what will change" breakdown chips.
    private static string DescribeKind(HeroSkillCatalog.SkillColorKind kind) => kind switch
    {
        HeroSkillCatalog.SkillColorKind.MicVectorParam => "MIC param",
        HeroSkillCatalog.SkillColorKind.MaterialExpressionVector => "Material param",
        HeroSkillCatalog.SkillColorKind.MaterialExpressionConstant3Vector => "Material RGB",
        HeroSkillCatalog.SkillColorKind.MaterialExpressionConstant4Vector => "Material RGBA",
        HeroSkillCatalog.SkillColorKind.ParticleStartColor => "Start color",
        HeroSkillCatalog.SkillColorKind.ParticleColorOverLife => "Color curve",
        HeroSkillCatalog.SkillColorKind.ParticleColorScaleOverLife => "Color scale",
        _ => "Color",
    };

    // Distinct hue per kind so the chip row reads like a legend at a glance.
    private static Brush KindBrush(HeroSkillCatalog.SkillColorKind kind) => kind switch
    {
        HeroSkillCatalog.SkillColorKind.MicVectorParam => new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x83, 0xE2)),       // blue
        HeroSkillCatalog.SkillColorKind.MaterialExpressionVector => new SolidColorBrush(Color.FromArgb(0xFF, 0x4C, 0xAF, 0x50)), // green
        HeroSkillCatalog.SkillColorKind.MaterialExpressionConstant3Vector => new SolidColorBrush(Color.FromArgb(0xFF, 0x66, 0xBB, 0x6A)), // light green
        HeroSkillCatalog.SkillColorKind.MaterialExpressionConstant4Vector => new SolidColorBrush(Color.FromArgb(0xFF, 0x81, 0xC7, 0x84)), // lighter green
        HeroSkillCatalog.SkillColorKind.ParticleStartColor => new SolidColorBrush(Color.FromArgb(0xFF, 0x7C, 0x4D, 0xFF)),    // purple
        HeroSkillCatalog.SkillColorKind.ParticleColorOverLife => new SolidColorBrush(Color.FromArgb(0xFF, 0x03, 0xA9, 0xF4)), // cyan
        HeroSkillCatalog.SkillColorKind.ParticleColorScaleOverLife => new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x98, 0x00)), // orange
        _ => new SolidColorBrush(Color.FromArgb(0xFF, 0x80, 0x80, 0x80)),
    };
}
