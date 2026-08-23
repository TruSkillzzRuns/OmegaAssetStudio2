using System.ComponentModel;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OmegaAssetStudio2.App.Rendering;
using OmegaAssetStudio2.App.Services;
using OmegaAssetStudio2.Core.Meshes;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Textures;
using OmegaAssetStudio2.Core.Workspace;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace OmegaAssetStudio2.App.Pages;

/// <summary>One entry in the left-hand list, from either the cast or a search.</summary>
public sealed class RosterRow : INotifyPropertyChanged
{
    private ImageSource? _picture;

    /// <summary>
    /// The character's own portrait, which arrives after the row does.
    /// </summary>
    /// <remarks>
    /// Read out of the game's icon packages, which hold thousands of textures
    /// apiece, so the list appears at once and fills in as the pictures are
    /// decoded rather than waiting on all of them.
    /// </remarks>
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

    public required string Name { get; init; }
    public required string Detail { get; init; }
    public required string PackagePath { get; init; }

    /// <summary>
    /// The character's name as package names spell it. Everything else the game
    /// ships for them is named after this, so other tools match on it.
    /// </summary>
    public string Token { get; init; } = string.Empty;

    /// <summary>
    /// The costume as package names spell it, empty for a character's default.
    /// </summary>
    public string VariantToken { get; init; } = string.Empty;

    /// <summary>Set when the row came from a search, which already knows the export.</summary>
    public int ExportIndex { get; init; } = -1;

    public required string SearchText { get; init; }
}

/// <summary>One model inside the package a row points at.</summary>
public sealed class ModelChoice
{
    public required int ExportIndex { get; init; }
    public required string Name { get; init; }

    public override string ToString() => Name;
}

public sealed partial class MeshPage : Page
{
    private const int EveryPackageIndex = 4;

    private readonly MeshCatalog _catalog = new();
    private readonly ObservableCollection<RosterRow> _visible = [];
    private readonly ModelRenderer _renderer = new();

    private List<RosterRow> _all = [];
    private CancellationTokenSource? _scanCancellation;
    private GameClient? _client;

    private Package? _package;
    private SkeletalMesh? _mesh;
    private IReadOnlyList<MeshSurface> _surfaces = [];

    /// <summary>
    /// Built once per game folder, in the background, and reused. It is what
    /// lets a material stored in a different file be found; a few seconds spent
    /// once beats failing to paint a costume.
    /// </summary>
    private Task<PackageIndex>? _index;

    /// <summary>The costume's own package, and which export the model is.</summary>
    private string? _costumePath;
    private int _meshExport = -1;

    /// <summary>The index once it has finished building, for use off the thread.</summary>
    private PackageIndex? _resolvedIndex;
    private string? _indexedFolder;

    /// <summary>
    /// False until every control exists. Guards the handlers that markup can
    /// raise while the page is still being built.
    /// </summary>
    private bool _ready;

    private bool _rendererStarted;
    private bool _needsDraw;

    /// <summary>The stand currently under the model, if any.</summary>
    private PedestalMesh? _stand;

    private bool _turning;
    private bool _sliding;
    private Point _lastPointer;

    public MeshPage()
    {
        InitializeComponent();

        RosterList.ItemsSource = _visible;

        ClientPicker.ClientChanged += (_, client) =>
        {
            _client = client;
            LoadCategory();
        };
        _client = ClientPicker.SelectedClient;

        Loaded += MeshPage_Loaded;
        Unloaded += MeshPage_Unloaded;
    }

    // ---- The viewport ----

    private void MeshPage_Loaded(object sender, RoutedEventArgs e)
    {
        StartRenderer();

        // Every control exists from here on, so handlers are safe to run.
        _ready = true;

        FillStandPicker();

        if (CategoryPicker.SelectedIndex < 0)
        {
            CategoryPicker.SelectedIndex = 0;   // raises the change that fills the list
            return;
        }

        LoadCategory();
    }

    private void MeshPage_Unloaded(object sender, RoutedEventArgs e)
    {
        CompositionTarget.Rendering -= OnFrame;
        _renderer.Dispose();
        _package = null;
    }

    private void StartRenderer()
    {
        if (_rendererStarted) return;

        int width = (int)Viewport.ActualWidth;
        int height = (int)Viewport.ActualHeight;

        // The panel has no size until it is laid out; the size-changed handler
        // comes back here once it does.
        if (width <= 0 || height <= 0) return;

        _rendererStarted = true;

        if (!_renderer.Attach(Viewport, width, height))
        {
            ShowViewportMessage(_renderer.Problem ?? "The 3D viewport could not start.");
            return;
        }

        CompositionTarget.Rendering += OnFrame;
        RequestDraw();
    }

    /// <summary>
    /// Draws only after something changed. A model viewer is still most of the
    /// time, and redrawing an unchanged picture sixty times a second heats the
    /// machine for nothing.
    /// </summary>
    private void OnFrame(object? sender, object e)
    {
        // The light on the stand moves, so while a stand is up the viewport
        // draws every frame. Without a stand nothing on screen changes on its
        // own and it goes back to drawing only when something asks it to.
        if (_renderer.HasStand && _renderer.StandProjects) _needsDraw = true;

        if (!_needsDraw) return;

        _needsDraw = false;
        _renderer.Draw();
    }

    private void RequestDraw() => _needsDraw = true;

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_rendererStarted)
        {
            StartRenderer();
            return;
        }

        _renderer.Resize((int)e.NewSize.Width, (int)e.NewSize.Height);
        RequestDraw();
    }

    private void Viewport_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(Viewport);

        _turning = point.Properties.IsLeftButtonPressed;
        _sliding = point.Properties.IsRightButtonPressed || point.Properties.IsMiddleButtonPressed;
        _lastPointer = point.Position;

        if (_turning || _sliding) Viewport.CapturePointer(e.Pointer);
    }

    private void Viewport_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_turning && !_sliding) return;

        Point position = e.GetCurrentPoint(Viewport).Position;
        double dx = position.X - _lastPointer.X;
        double dy = position.Y - _lastPointer.Y;
        _lastPointer = position;

        if (_turning)
        {
            // Dragging right turns the model to the right, which means moving
            // the camera the other way.
            _renderer.Camera.Rotate((float)-dx * 0.01f, (float)dy * 0.01f);
        }
        else
        {
            _renderer.Camera.Pan((float)-dx * 0.0015f, (float)dy * 0.0015f);
        }

        RequestDraw();
    }

    private void Viewport_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _turning = false;
        _sliding = false;
        Viewport.ReleasePointerCaptures();
    }

    private void Viewport_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(Viewport).Properties.MouseWheelDelta;

        _renderer.Camera.Zoom(delta > 0 ? ZoomStep : 1f / ZoomStep);
        RequestDraw();
    }

    /// <summary>
    /// One step closer. The same factor the wheel uses, so the two agree.
    /// </summary>
    private void ZoomInButton_Click(object sender, RoutedEventArgs e)
    {
        _renderer.Camera.Zoom(ZoomStep);
        RequestDraw();
    }

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
    {
        _renderer.Camera.Zoom(1f / ZoomStep);
        RequestDraw();
    }

    /// <summary>How far one notch of the wheel, or one press, moves the camera.</summary>
    private const float ZoomStep = 0.88f;

    private void ResetViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mesh is not null) { _renderer.FrameModel(_mesh.Bounds); _renderer.FitStand(_mesh.Bounds); }
        RequestDraw();
    }

    // ---- What the model stands on ----

    /// <summary>
    /// Offers the stand the user has chosen, and a way to choose another.
    /// </summary>
    private void FillStandPicker()
    {
        if (!_ready) return;

        string remembered = AppSettings.Current.StandPath;

        StandPicker.SelectionChanged -= StandPicker_SelectionChanged;
        StandPicker.Items.Clear();

        StandPicker.Items.Add(new ComboBoxItem { Content = "No stand", Tag = string.Empty });

        if (!string.IsNullOrWhiteSpace(remembered) && File.Exists(remembered))
        {
            StandPicker.Items.Add(new ComboBoxItem
            {
                Content = Path.GetFileNameWithoutExtension(remembered),
                Tag = remembered,
            });
        }

        StandPicker.Items.Add(new ComboBoxItem { Content = "Choose a file…", Tag = ChooseAFile });

        StandPicker.SelectedIndex = _stand is null ? 0 : 1;
        StandPicker.SelectionChanged += StandPicker_SelectionChanged;

        // The light the stand throws, which starts on the beam. Set with the
        // handler off, as the stand's own picker above is: letting it fire
        // would frame the model again and put the camera back to its start.
        if (ProjectionPicker.SelectedIndex < 0)
        {
            ProjectionPicker.SelectionChanged -= ProjectionPicker_SelectionChanged;
            ProjectionPicker.SelectedIndex = (int)ModelRenderer.Projection.Beam;
            ProjectionPicker.SelectionChanged += ProjectionPicker_SelectionChanged;

            _renderer.Projects = ModelRenderer.Projection.Beam;
        }
    }

    private void ProjectionPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int chosen = ProjectionPicker.SelectedIndex;

        _renderer.Projects = chosen >= 0
            ? (ModelRenderer.Projection)chosen
            : ModelRenderer.Projection.None;

        // The shape of a column differs from a cone's, so the sleeve is built
        // again rather than only shaded differently - the geometry alone,
        // because framing the model again would also put the camera back to
        // where it starts and throw away whatever the viewer had orbited to.
        _renderer.RebuildProjection();

        RequestDraw();
    }

    /// <summary>Marks the entry that opens a file picker rather than loading one.</summary>
    private const string ChooseAFile = "\u0000choose";

    private async void StandPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;

        string? tag = (StandPicker.SelectedItem as ComboBoxItem)?.Tag as string;

        if (tag == ChooseAFile)
        {
            string? chosen = await AskForStandAsync();

            if (string.IsNullOrWhiteSpace(chosen))
            {
                FillStandPicker();
                return;
            }

            AppSettings.Current.StandPath = chosen;
            AppSettings.Save();

            await LoadStandAsync(chosen);
            FillStandPicker();
            return;
        }

        if (string.IsNullOrWhiteSpace(tag))
        {
            _stand = null;
            _renderer.ReleaseStand();
            RequestDraw();
            return;
        }

        await LoadStandAsync(tag);
    }

    /// <summary>Asks for a model file to stand things on.</summary>
    private async Task<string?> AskForStandAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };

        foreach (string extension in new[] { ".fbx", ".obj", ".dae", ".gltf", ".glb", ".ply", ".stl" })
            picker.FileTypeFilter.Add(extension);

        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        StorageFile? file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    /// <summary>
    /// Reads a stand and puts it under the model.
    /// </summary>
    /// <remarks>
    /// Read away from the interface thread: a detailed stand is hundreds of
    /// thousands of triangles with several large pictures beside it, and doing
    /// that in front of the user would lock the window for a second or two.
    /// </remarks>
    private async Task LoadStandAsync(string path)
    {
        StatusText.Text = $"Reading {Path.GetFileNameWithoutExtension(path)}…";

        try
        {
            PedestalMesh stand = await Task.Run(() => PedestalLoader.Load(path));

            _stand = stand;
            _renderer.SetStand(stand);

            if (_mesh is not null) _renderer.FitStand(_mesh.Bounds);

            RequestDraw();

            StatusText.Text =
                $"{stand.Name}: {stand.TriangleCount:N0} triangles" +
                (stand.Colour is null ? ", no colour map found" : string.Empty);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Mesh.Stand", ex);
            StatusText.Text = "That file could not be read as a model.";

            _stand = null;
            _renderer.ReleaseStand();
            RequestDraw();
        }
    }

    private void ShowViewportMessage(string message)
    {
        ViewportMessage.Text = message;
        ViewportMessage.Visibility = Visibility.Visible;
    }

    private void HideViewportMessage() => ViewportMessage.Visibility = Visibility.Collapsed;

    // ---- Choosing who to look at ----

    private void CategoryPicker_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadCategory();

    private bool SearchingEveryPackage => CategoryPicker.SelectedIndex == EveryPackageIndex;

    private async void LoadCategory()
    {
        // Handlers can fire while the page is still being built — a control that
        // starts out selected raises its change event before the controls below
        // it exist. Nothing here is safe until the page has loaded.
        if (!_ready) return;

        ScanControls.Visibility = SearchingEveryPackage ? Visibility.Visible : Visibility.Collapsed;

        _all = [];
        ApplyFilter();

        if (_client is null)
        {
            StatusText.Text = "Add a game folder on the Home page first.";
            return;
        }

        if (SearchingEveryPackage)
        {
            StatusText.Text = "Choose a file pattern and search, or pick a category above.";
            return;
        }

        RosterCategory category = CategoryPicker.SelectedIndex switch
        {
            1 => RosterCategory.TeamUp,
            2 => RosterCategory.Boss,
            3 => RosterCategory.Enemy,
            _ => RosterCategory.Hero,
        };

        StatusText.Text = "Reading the list…";

        GameClient client = _client;
        IReadOnlyList<RosterEntry> entries =
            await Task.Run(() => CharacterRoster.Build(client, category));

        _all = entries
            .Select(entry => new RosterRow
            {
                Name = entry.DisplayName,
                Detail = entry.Subtitle,
                PackagePath = entry.PackagePath,
                Token = entry.Token,
                SearchText = entry.DisplayName,
            })
            .ToList();

        ApplyFilter();

        StatusText.Text = _all.Count == 0
            ? "Nothing in this category for the selected game."
            : $"{_all.Count:N0} to choose from. Pick one to see the model.";

        // Started now so it is ready by the time somebody picks a costume that
        // needs it, rather than stalling that first click.
        _ = WarmIndexAsync(client);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        string needle = SearchBox.Text.Trim();

        IEnumerable<RosterRow> query = _all;
        if (needle.Length > 0)
            query = query.Where(r => r.SearchText.Contains(needle, StringComparison.OrdinalIgnoreCase));

        _visible.Clear();
        foreach (RosterRow row in query) _visible.Add(row);

        CountText.Text = _all.Count == 0
            ? string.Empty
            : $"{_visible.Count:N0} shown of {_all.Count:N0}";
    }

    // ---- Searching every package ----

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null)
        {
            StatusText.Text = "No game selected. Add one on the Home page first.";
            return;
        }

        _scanCancellation?.Cancel();
        _scanCancellation = new CancellationTokenSource();

        ScanButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        ScanBar.Visibility = Visibility.Visible;

        int skipped = 0;

        try
        {
            var progress = new Progress<MeshScanProgress>(p =>
            {
                ScanBar.Maximum = Math.Max(1, p.PackageCount);
                ScanBar.Value = p.PackagesScanned;
                StatusText.Text =
                    $"Searching {p.PackagesScanned:N0} of {p.PackageCount:N0} packages — " +
                    $"{p.MeshesFound:N0} models found";
            });

            IReadOnlyList<MeshInfo> found = await _catalog.ScanAsync(
                _client, "*.upk", progress, onError: (_, _) => skipped++, _scanCancellation.Token);

            _all = found
                .Select(m => new RosterRow
                {
                    Name = m.Name,
                    Detail = $"{(m.Kind == MeshKind.Skeletal ? "skinned" : "static")} — " +
                             Path.GetFileNameWithoutExtension(m.PackagePath),
                    PackagePath = m.PackagePath,
                    SearchText = m.Name,
                })
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            ApplyFilter();

            StatusText.Text = _all.Count == 0
                ? "No models found."
                : $"Found {_all.Count:N0} models." +
                  (skipped > 0 ? $" {skipped} package(s) could not be read." : string.Empty);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Search stopped.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Search failed: {ex.Message}";
            CrashLog.Write("Mesh.Scan", ex);
        }
        finally
        {
            ScanButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            ScanBar.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _scanCancellation?.Cancel();

    // ---- Loading a model ----

    /// <summary>Clicking a row loads it, including the row already selected.</summary>
    private void RosterList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not RosterRow row) return;

        // Selecting it raises the change that does the loading. When it is
        // already selected nothing is raised, so it is loaded directly.
        if (ReferenceEquals(RosterList.SelectedItem, row))
            _ = LoadRowAsync(row);
        else
            RosterList.SelectedItem = row;
    }

    private void RosterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;

        if (RosterList.SelectedItem is not RosterRow row)
        {
            ClearSelection();
            return;
        }

        _ = LoadRowAsync(row);
    }

    private async Task LoadRowAsync(RosterRow row)
    {
        try
        {
            await OpenRowAsync(row);
        }
        catch (Exception ex)
        {
            // A model that will not load must not take the page with it.
            CrashLog.Write("Mesh.Open", ex);
            StatusText.Text = $"That entry could not be loaded: {ex.Message}";
            ShowViewportMessage("That entry could not be loaded.");
        }
    }

    /// <summary>The models a package holds, in a settled order.</summary>
    private static List<ModelChoice> ModelsIn(Package package) =>
        package
            .FindExportsOfClass(SkeletalMeshReader.SkeletalMeshClass)
            .Where(index => !OnlyPlaceholder(package, index))
            .Select(index => new ModelChoice
            {
                ExportIndex = index,
                Name = package.GetExportName(index),
            })
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The material the game leaves on a model that nothing shows.</summary>
    private const string Placeholder = "placeholder_mat";

    /// <summary>
    /// Whether every material a model names is the placeholder, in which case
    /// nothing ever shows it and it is left out of the list.
    /// </summary>
    /// <remarks>
    /// Black Bolt's blackbolt_base is one. It is the body an avatar starts as
    /// before a costume is put on it: its component names it with no material
    /// of its own, its one slot is placeholder.placeholder_mat, and the game's
    /// costume data lists ANAD, Classic and InhumansTV for him and no default -
    /// so every costume he has replaces it. Shown in the list it reads as a
    /// broken costume, because the placeholder is bright magenta.
    /// <para>
    /// A model that names no material at all is kept. Those are effects and
    /// props that are genuinely drawn, and hiding them would lose them.
    /// </para>
    /// </remarks>
    private static bool OnlyPlaceholder(Package package, int index)
    {
        // The material list alone. Reading the whole model to answer this
        // decoded every level of detail of every model in the package, and
        // then the one that was chosen was decoded again.
        IReadOnlyList<ObjectReference>? materials;
        try { materials = SkeletalMeshReader.TryReadMaterials(package, index); }
        catch (Exception) { return false; }

        if (materials is null || materials.Count == 0) return false;

        bool named = false;

        foreach (ObjectReference reference in materials)
        {
            if (reference.IsNull) continue;

            named = true;

            if (!package.ResolveName(reference).Contains(Placeholder, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return named;
    }

    /// <summary>
    /// The character's own package, for a costume package that holds no model.
    /// </summary>
    /// <remarks>
    /// These are named for the character and then the costume, so the character
    /// alone is found by dropping the costume words one at a time and seeing
    /// which of the shorter names is a package that has a model. One character's
    /// Classic costume is a package of effects; the model it wears is in the
    /// package named for that character with no costume at all.
    /// </remarks>
    private static string? BasePackageFor(string path)
    {
        string? folder = Path.GetDirectoryName(path);
        if (folder is null) return null;

        string[] parts = Path.GetFileNameWithoutExtension(path).Split('_');

        // Needs a character and a costume to have something to drop.
        for (int drop = 1; drop < parts.Length - 3; drop++)
        {
            string name = string.Join('_', parts.Take(parts.Length - 1 - drop).Append(parts[^1]));
            string candidate = Path.Combine(folder, name + ".upk");

            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private async Task OpenRowAsync(RosterRow row)
    {
        StatusText.Text = $"Opening {Path.GetFileName(row.PackagePath)}…";

        string path = row.PackagePath;

        (Package? package, List<ModelChoice> choices, string? problem, string? borrowedFrom) = await Task.Run(() =>
        {
            try
            {
                Package opened = Package.Open(path);
                List<ModelChoice> found = ModelsIn(opened);

                // A costume package that holds no model is not broken: it is a
                // package of effects, sounds and power animations for a costume
                // whose model is the character's own. Falling back to the
                // character's base package is what the game itself does, and it
                // recovers 21 of the 22 such packages in this client.
                if (found.Count == 0)
                {
                    string? baseline = BasePackageFor(path);

                    if (baseline is not null)
                    {
                        Package fallback = Package.Open(baseline);
                        List<ModelChoice> theirs = ModelsIn(fallback);

                        if (theirs.Count > 0)
                            return (fallback, theirs, (string?)null, Path.GetFileNameWithoutExtension(baseline));
                    }
                }

                return (opened, found, (string?)null, (string?)null);
            }
            catch (Exception ex)
            {
                return ((Package?)null, new List<ModelChoice>(), ex.Message, (string?)null);
            }
        });

        if (package is null)
        {
            ClearSelection();
            ShowViewportMessage($"That package could not be opened: {problem}");
            StatusText.Text = "The package could not be opened.";
            return;
        }

        _package = package;

        // The costume's own package, which is not always the one the model came
        // from: a costume with no model of its own borrows one, and the pieces
        // it hangs stay in its own file.
        _costumePath = path;

        ModelPicker.ItemsSource = choices;

        if (borrowedFrom is not null)
            StatusText.Text = $"This costume carries no model of its own; showing the one from {borrowedFrom}.";

        if (choices.Count == 0)
        {
            ClearSelection();
            ShowViewportMessage(
                "This package holds no skinned model.\n" +
                "Some entries are effects or scripted actors that borrow another package's model.");
            StatusText.Text = "Nothing to show for that entry.";
            return;
        }

        // The first model is the one people want in nearly every package that
        // has more than one; the rest are usually detached parts.
        ModelPicker.SelectedIndex = 0;
        StatusText.Text = choices.Count == 1
            ? "Loaded."
            : $"{choices.Count} models in this package.";
    }

    private void ModelPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_package is null || ModelPicker.SelectedItem is not ModelChoice choice) return;

        string? problem = null;
        SkeletalMesh? mesh = SkeletalMeshReader.TryRead(_package, choice.ExportIndex, why => problem = why);
        _meshExport = choice.ExportIndex;

        if (mesh is null)
        {
            ClearModel();
            ShowViewportMessage($"That model could not be read: {problem}");
            return;
        }

        _mesh = mesh;
        ExportButton.IsEnabled = true;

        DetailPicker.ItemsSource = Enumerable
            .Range(0, mesh.Lods.Count)
            .Select(i => i == 0 ? "Full detail" : $"Reduced {i}")
            .ToList();

        DetailPicker.SelectedIndex = 0;   // loads the geometry

        ShowMeshDetails(mesh);

        _ = LoadSurfacesAsync(mesh);
    }

    /// <summary>
    /// Saves the model on screen to a file a modelling tool can open.
    /// </summary>
    /// <remarks>
    /// The level of detail shown is the one written, so what is saved is what
    /// is being looked at. Nothing in the game is touched.
    /// </remarks>
    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mesh is null) { StatusText.Text = "Load a model first."; return; }

        int level = Math.Clamp(DetailPicker.SelectedIndex, 0, _mesh.Lods.Count - 1);
        SkeletalMeshLod lod = _mesh.Lods[level];

        if (!lod.HasGeometry) { StatusText.Text = "This level of detail carries no geometry."; return; }

        try
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };

            picker.FileTypeChoices.Add("Model", [".fbx"]);
            picker.FileTypeChoices.Add("Collada", [".dae"]);
            picker.FileTypeChoices.Add("Wavefront", [".obj"]);
            picker.SuggestedFileName = _mesh.Name;

            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            StorageFile? file = await picker.PickSaveFileAsync();
            if (file is null) return;

            string path = file.Path;
            SkeletalMesh mesh = _mesh;

            IReadOnlyList<string> materials = _surfaces.Select(s => s.MaterialName).Distinct().ToList();

            StatusText.Text = $"Saving {Path.GetFileName(path)}…";

            await Task.Run(() => FbxExporter.Write(path, mesh, lod, materials));

            StatusText.Text =
                $"Saved {mesh.Name} to {path} — {lod.Positions.Count:N0} vertices, " +
                $"{lod.TriangleCount:N0} triangles, {mesh.Bones.Count:N0} bones.";
        }
        catch (MeshExportException ex)
        {
            StatusText.Text = ex.Message;
        }
        catch (Exception ex)
        {
            CrashLog.Write("Mesh.Export", ex);
            StatusText.Text = $"Saving failed: {ex.Message}";
        }
    }

    /// <summary>The pieces this costume hangs on itself, and where they hang.</summary>
    private IReadOnlyList<MeshAttachment> _hung = [];
    private IReadOnlyList<MeshSocket> _sockets = [];

    /// <summary>
    /// The model with anything the costume hangs on it folded in, so the pieces
    /// are drawn and shaded exactly as the rest of it is.
    /// </summary>
    private SkeletalMeshLod WithPieces(
        SkeletalMesh mesh, SkeletalMeshLod lod, GameClient? client, PackageIndex? index)
    {
        if (_hung.Count == 0 || client is null) return lod;

        try
        {
            var reader = new TextureReader(client.CookedPath);

            (SkeletalMeshLod whole, IReadOnlyList<MeshSurface> painted) = AttachmentMerger.Merge(
                lod, mesh, _hung, _sockets, reader, new ObjectLocator(index), _surfaces, client.CookedPath);

            _surfaces = painted;
            return whole;
        }
        catch (Exception ex)
        {
            CrashLog.Write("Mesh.Attachments", ex);
            return lod;
        }
    }

    private void DetailPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_mesh is null) return;

        int index = Math.Clamp(DetailPicker.SelectedIndex, 0, _mesh.Lods.Count - 1);
        SkeletalMeshLod lod = _mesh.Lods[index];

        if (!lod.HasGeometry)
        {
            ClearModel();
            ShowViewportMessage("This level of detail carries no geometry.");
            return;
        }

        // Which build this model came from, so its textures are sampled the way
        // that build's own settings ask for and no other build's drawing moves.
        _renderer.ModelBuild = _client?.Build ?? string.Empty;

        _renderer.SetModel(WithPieces(_mesh, lod, _client, _resolvedIndex), _surfaces);
        _renderer.FrameModel(_mesh.Bounds);
        _renderer.FitStand(_mesh.Bounds);

        HideViewportMessage();
        RequestDraw();

        DetailGeometry.Text =
            $"vertices  {lod.Positions.Count:N0}\n" +
            $"triangles {lod.TriangleCount:N0}\n" +
            $"sections  {lod.Sections.Count}\n" +
            $"painted   {_renderer.TexturedPartCount} of {_renderer.PartCount} parts\n" +
            $"layout    {lod.Layout}";
    }

    private void TextureToggle_Click(object sender, RoutedEventArgs e)
    {
        _renderer.ShowTextures = TextureToggle.IsChecked == true;
        RequestDraw();
    }

    /// <summary>
    /// Builds the index ahead of time, quietly. Nothing is reported: it is not
    /// something the user asked for, and it is only ever an optimisation.
    /// </summary>
    private async Task WarmIndexAsync(GameClient client)
    {
        if (_index is not null && _indexedFolder == client.CookedPath) return;

        _indexedFolder = client.CookedPath;
        _index = Task.Run(() => PackageIndex.Build(client));

        try { await _index; }
        catch (Exception ex) { CrashLog.Write("Mesh.Index", ex); }
    }

    /// <summary>
    /// Gets the index for a game folder, building it on the first request.
    /// </summary>
    /// <remarks>
    /// Returns null rather than failing if the folder cannot be read; the model
    /// then paints with whatever its own package holds, which is most of it.
    /// </remarks>
    private async Task<PackageIndex?> GetIndexAsync(GameClient client)
    {
        if (_index is null || _indexedFolder != client.CookedPath)
        {
            _indexedFolder = client.CookedPath;
            StatusText.Text = "Reading the game folder so borrowed materials can be found…";

            _index = Task.Run(() =>
            {
                try { return PackageIndex.Build(client); }
                catch (Exception ex)
                {
                    CrashLog.Write("Mesh.Index", ex);
                    throw;
                }
            });
        }

        try { return await _index; }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Finds the picture for each of the model's material slots.
    /// </summary>
    /// <remarks>
    /// Decoding can mean reading several megabytes out of the shared texture
    /// cache, so it happens away from the interface thread and the model is
    /// shown untextured until it lands.
    /// </remarks>
    private async Task LoadSurfacesAsync(SkeletalMesh mesh)
    {
        if (_package is null || _client is null) return;

        Package package = _package;
        GameClient client = _client;
        var skipped = new List<string>();

        // Materials a costume borrows from another file cannot be found without
        // the index. It is worth waiting for, but only the first time.
        PackageIndex? index = await GetIndexAsync(client);

        if (!ReferenceEquals(_mesh, mesh)) return;

        IReadOnlyList<MeshAttachment> hung = [];
        IReadOnlyList<MeshSocket> sockets = [];

        IReadOnlyList<MeshSurface> surfaces = await Task.Run(() =>
        {
            try
            {
                var reader = new TextureReader(client.CookedPath);
                var locator = new ObjectLocator(index);

                IReadOnlyList<MeshSurface> found = MeshSurfaceResolver.Resolve(
                    package, mesh, reader,
                    onSkipped: (_, why) => skipped.Add(why),
                    locator: locator,
                    colours: client.CookedPath);

                // Several costumes have no model of their own and are the plain
                // model with pieces hung on it - strings of lights, a cape's
                // fastenings, a weapon. Those pieces live in the costume's own
                // package as static meshes, and each names the socket it hangs
                // from.
                Package costume = _costumePath is not null && _costumePath != package.Path
                    ? Package.Open(_costumePath)
                    : package;

                hung = AttachmentReader.Read(costume, locator);

                if (hung.Count > 0 && _meshExport >= 0)
                    sockets = AttachmentReader.ReadSockets(package, _meshExport);

                return found;
            }
            catch (Exception ex)
            {
                CrashLog.Write("Mesh.Surfaces", ex);
                return [];
            }
        });

        // The user may have moved on while this was decoding.
        if (!ReferenceEquals(_mesh, mesh)) return;

        _surfaces = surfaces;
        _hung = hung;
        _sockets = sockets;

        int detail = Math.Clamp(DetailPicker.SelectedIndex, 0, mesh.Lods.Count - 1);
        SkeletalMeshLod lod = WithPieces(mesh, mesh.Lods[detail], client, index);
        _resolvedIndex = index;

        if (lod.HasGeometry)
        {
            _renderer.SetModel(lod, _surfaces);
            RequestDraw();
        }

        DetailMaterials.Text = BuildMaterialReport(mesh, surfaces, skipped);

        DetailGeometry.Text =
            $"vertices  {lod.Positions.Count:N0}\n" +
            $"triangles {lod.TriangleCount:N0}\n" +
            $"sections  {lod.Sections.Count}\n" +
            $"painted   {_renderer.TexturedPartCount} of {_renderer.PartCount} parts\n" +
            $"layout    {lod.Layout}";
    }

    /// <summary>
    /// Lists what each material slot is covered with, and says plainly what was
    /// not found rather than leaving a slot silently blank.
    /// </summary>
    private static string BuildMaterialReport(
        SkeletalMesh mesh, IReadOnlyList<MeshSurface> surfaces, IReadOnlyList<string> skipped)
    {
        if (mesh.Materials.Count == 0) return "None referenced.";

        var lines = surfaces
            .Select(s =>
                $"{s.MaterialName}\n    {s.TextureName}  ({s.Image.Width}x{s.Image.Height}, {s.ParameterName})" +
                (s.FromAnotherPackage ? "\n    borrowed from another package" : string.Empty))
            .ToList();

        if (skipped.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"{skipped.Count} slot(s) left plain:");
            lines.AddRange(skipped.Distinct().Select(reason => $"    {reason}"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void ShowMeshDetails(SkeletalMesh mesh)
    {
        DetailName.Text = mesh.Name;
        DetailPath.Text = $"{mesh.ObjectPath}\n{_package?.Path}";

        MeshBounds bounds = mesh.Bounds;
        DetailBounds.Text =
            $"width   {bounds.Width:0.##}\n" +
            $"depth   {bounds.Depth:0.##}\n" +
            $"height  {bounds.Height:0.##}\n" +
            $"radius  {bounds.Radius:0.##}\n" +
            $"bones   {mesh.Bones.Count:N0}\n" +
            $"levels  {mesh.Lods.Count}";

        DetailMaterials.Text = mesh.Materials.Count == 0
            ? "None referenced."
            : string.Join(
                Environment.NewLine,
                mesh.Materials.Select(m => _package?.ResolveName(m) is { Length: > 0 } name ? name : "(unnamed)"));
    }

    private void ClearModel()
    {
        _mesh = null;
        _surfaces = [];
        _renderer.ClearModel();
        DetailGeometry.Text = "—";
        RequestDraw();
    }

    private void ClearSelection()
    {
        ClearModel();
        _package = null;

        ModelPicker.ItemsSource = null;
        DetailPicker.ItemsSource = null;

        DetailName.Text = "Nothing selected";
        DetailPath.Text = string.Empty;
        DetailBounds.Text = "—";
        DetailMaterials.Text = "—";

        ShowViewportMessage("Pick a character on the left.");
    }
}
