using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmegaAssetStudio2.App.Services;
using OmegaAssetStudio2.CharacterSwap;
using OmegaAssetStudio2.Core.Swapping;
using OmegaAssetStudio2.Core.Workspace;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace OmegaAssetStudio2.App.Pages;

/// <summary>
/// Takes a costume from a newer game and makes it load in an older one.
/// </summary>
/// <remarks>
/// Three files, named outright: the costume to take, the costume to replace,
/// and where to put the result. Nothing is chosen from a list, so any costume
/// can be tried against any other.
/// <para>
/// The result never goes into the game. Putting it there is a separate act,
/// done knowingly, after it has been looked at - so a costume that does not
/// work cannot take the game with it.
/// </para>
/// </remarks>
public sealed partial class CharacterSwapPage : Page
{
    private string? _takeFrom;
    private string? _replace;
    private string? _writeTo;

    /// <summary>The games set up in this application, in the order shown.</summary>
    private IReadOnlyList<GameClient> _clients = [];

    /// <summary>The build costumes are taken from, once browsed to.</summary>
    private GameClient? _from;

    public CharacterSwapPage()
    {
        InitializeComponent();
        Load();
    }

    /// <summary>Fills the two games and the costumes already worked out.</summary>
    private void Load()
    {
        _clients = AppSettings.Current.ResolvedClients.Where(one => one.Exists).ToList();

        foreach (GameClient one in _clients) IntoClientBox.Items.Add(one.DisplayName);

        // The build costumes were last taken from, if it is still there.
        string remembered = AppSettings.Current.SwapSourceFolder;

        if (remembered.Length > 0 && Directory.Exists(remembered)) Take(remembered);

        foreach (SwapPair pair in KnownSwaps.All) PairBox.Items.Add(KnownSwaps.Describe(pair));

        if (_clients.Count == 1) IntoClientBox.SelectedIndex = 0;

        if (_clients.Count == 0)
        {
            StatusText.Text = "No games are set up yet. Add one on the Home page, or name the files yourself below.";
        }
    }

    private void Client_Changed(object sender, SelectionChangedEventArgs e) => FromTheList();

    private void FromFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? folder = FolderBrowser.Pick(
                WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow),
                "The build to take costumes from");

            if (folder is null) return;

            Take(folder);

            AppSettings.Current.SwapSourceFolder = _from?.RootPath ?? folder;
            AppSettings.Save();

            FromTheList();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not use that folder: {ex.Message}";
            CrashLog.Write("CharacterSwap.FromFolder", ex);
        }
    }

    /// <summary>
    /// Takes a folder as the build to take from, whether it is the build's own
    /// folder or the folder of cooked packages inside it.
    /// </summary>
    private void Take(string folder)
    {
        GameClient? found = GameClientLocator.FromRoot(folder, "the build costumes come from");

        // Pointed straight at the cooked packages, which is what a build kept
        // aside usually amounts to.
        found ??= Directory.EnumerateFiles(folder, "*.upk").Any()
            ? new GameClient
            {
                DisplayName = "the build costumes come from",
                RootPath = folder,
                CookedPath = folder,
            }
            : null;

        if (found is null || !found.Exists)
        {
            FromFolderNote.Text = "There are no cooked packages in there.";
            _from = null;

            return;
        }

        _from = found;
        FromFolderBox.Text = folder;

        int costumes = Directory.EnumerateFiles(found.CookedPath, "UC__MarvelPlayer_*_SF.upk").Count();

        FromFolderNote.Text = $"{costumes:N0} costume(s) in there.";
    }

    private void Pair_Changed(object sender, SelectionChangedEventArgs e) => FromTheList();

    /// <summary>
    /// Turns a chosen pair and two games into the three files the swap needs.
    /// </summary>
    private void FromTheList()
    {
        if (_from is null || IntoClientBox.SelectedIndex < 0) return;
        if (PairBox.SelectedIndex < 0 || PairBox.SelectedIndex >= KnownSwaps.All.Count) return;

        GameClient from = _from;
        GameClient into = _clients[IntoClientBox.SelectedIndex];
        SwapPair pair = KnownSwaps.All[PairBox.SelectedIndex];

        string takeFrom = SwapSurvey.PathOf(from, pair.Source);
        string replace = SwapSurvey.PathOf(into, pair.Chassis);

        var said = new List<string>();

        if (!File.Exists(takeFrom)) said.Add($"{pair.Source} is not in {from.DisplayName}.");
        if (!File.Exists(replace)) said.Add($"{pair.Chassis} is not in {into.DisplayName}.");

        if (said.Count > 0)
        {
            PairNote.Text = string.Join(" ", said);
            RunButton.IsEnabled = false;

            return;
        }

        _takeFrom = takeFrom;
        _replace = replace;

        TakeFromBox.Text = takeFrom;
        ReplaceBox.Text = replace;

        WhereItGoes();

        PairNote.Text = $"{Path.GetFileName(takeFrom)} over {Path.GetFileName(replace)}.";

        Ready();
    }

    private async void TakeFrom_Click(object sender, RoutedEventArgs e)
    {
        string? chosen = await ChooseACostume();

        if (chosen is null) return;

        _takeFrom = chosen;
        TakeFromBox.Text = chosen;
        Ready();
    }

    private async void Replace_Click(object sender, RoutedEventArgs e)
    {
        string? chosen = await ChooseACostume();

        if (chosen is null) return;

        _replace = chosen;
        ReplaceBox.Text = chosen;

        // Somewhere to put it, suggested from what it replaces: the same name,
        // so it can be copied into the game as it stands.
        WhereItGoes();

        Ready();
    }

    private async void WriteTo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };

            picker.FileTypeChoices.Add("Cooked package", [".upk"]);
            picker.SuggestedFileName = _replace is null
                ? "swapped"
                : Path.GetFileNameWithoutExtension(_replace);

            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

            StorageFile? file = await picker.PickSaveFileAsync();

            if (file is null) return;

            _writeTo = file.Path;
            WriteToBox.Text = file.Path;
            Ready();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not choose a place: {ex.Message}";
            CrashLog.Write("CharacterSwap.WriteTo", ex);
        }
    }

    private async Task<string?> ChooseACostume()
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };

            picker.FileTypeFilter.Add(".upk");

            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

            StorageFile? file = await picker.PickSingleFileAsync();

            return file?.Path;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not choose a costume: {ex.Message}";
            CrashLog.Write("CharacterSwap.Choose", ex);

            return null;
        }
    }

    private void Where_Changed(object sender, RoutedEventArgs e) => WhereItGoes();

    /// <summary>
    /// Where the result is to go: over the costume it replaces, or beside this
    /// application's own files.
    /// </summary>
    private void WhereItGoes()
    {
        if (_replace is null) return;

        if (IntoTheGameBox.IsChecked == true)
        {
            _writeTo = _replace;
            WriteToBox.Text = _replace;

            WhereNote.Text =
                $"{Path.GetFileName(_replace)} in the game folder. What is there now is kept as " +
                $"{Path.GetFileName(_replace)}.bak beside it, once, and every swap is built from that " +
                "kept copy - so doing this again does not build on the last attempt.";
        }
        else
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Omega Asset Studio 2", "Swapped costumes");

            _writeTo = Path.Combine(folder, Path.GetFileName(_replace));
            WriteToBox.Text = _writeTo;

            WhereNote.Text = "Somewhere of your own. Copy it into the game yourself when you want to try it.";
        }

        Ready();
    }

    private void Ready() =>
        RunButton.IsEnabled = _takeFrom is not null && _replace is not null && _writeTo is not null;

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_takeFrom is null || _replace is null || _writeTo is null) return;

        RunButton.IsEnabled = false;
        OpenFolderButton.IsEnabled = false;
        Busy.IsActive = true;
        StatusText.Text = "Making the swap…";
        ReportText.Text = "";

        try
        {
            var options = new SwapOptions
            {
                TranslateMatchedSizeChanged = MatchedTranslateBox.IsChecked == true,
                MergeWithTarget = MergeWithTargetBox.IsChecked == true,
                IntoTheGame = IntoTheGameBox.IsChecked == true,
            };

            SwapOutcome done = await Task.Run(
                () => Swap.RunAsync(_takeFrom, _replace, _writeTo, options));

            ReportText.Text = string.Join(Environment.NewLine, done.Report);

            StatusText.Text = done.Succeeded
                ? IntoTheGameBox.IsChecked == true
                    ? "Done. It is in the game - start the client and try it."
                    : "Done. Copy it into the game's CookedPCConsole folder when you want to try it."
                : "It was not made.";

            OpenFolderButton.IsEnabled = done.Succeeded;
        }
        catch (Exception ex)
        {
            ReportText.Text = ex.Message;
            StatusText.Text = "It stopped on something unexpected.";
            CrashLog.Write("CharacterSwap.Run", ex);
        }
        finally
        {
            Busy.IsActive = false;
            RunButton.IsEnabled = true;
        }
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_writeTo is null || !File.Exists(_writeTo)) return;

        try
        {
            StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(_writeTo)!);
            StorageFile file = await StorageFile.GetFileFromPathAsync(_writeTo);

            var options = new Windows.System.FolderLauncherOptions();
            options.ItemsToSelect.Add(file);

            await Windows.System.Launcher.LaunchFolderAsync(folder, options);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not open the folder: {ex.Message}";
            CrashLog.Write("CharacterSwap.OpenFolder", ex);
        }
    }
}
