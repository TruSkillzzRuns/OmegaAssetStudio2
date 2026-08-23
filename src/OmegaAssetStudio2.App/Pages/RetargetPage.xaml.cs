using System.Collections.ObjectModel;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OmegaAssetStudio2.App.Rendering;
using OmegaAssetStudio2.App.Services;
using OmegaAssetStudio2.Core.Meshes;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Retargeting;
using OmegaAssetStudio2.Core.Workspace;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace OmegaAssetStudio2.App.Pages;

/// <summary>One thing the tool found wrong with a model, and what it did.</summary>
public sealed class FindingRow
{
    public required string Mark { get; init; }
    public required string What { get; init; }
    public required string Detail { get; init; }
    public required Microsoft.UI.Xaml.Media.Brush Colour { get; init; }

    public static FindingRow From(ModelFinding finding) => new()
    {
        // A tick for something put right, a mark for something the user should
        // still look at. The two need telling apart at a glance, because one
        // needs no action and the other does.
        Mark = finding.Kind == FindingKind.Warned ? "!" : "✓",
        What = finding.What,
        Detail = finding.Detail,
        Colour = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            finding.Kind == FindingKind.Warned
                ? Microsoft.UI.Colors.Orange
                : Microsoft.UI.Colors.LightGreen),
    };
}

/// <summary>One bone in the skeleton list.</summary>
public sealed class BoneRow
{
    public required string Text { get; init; }
}

/// <summary>One model inside the chosen target package.</summary>
public sealed class TargetMeshChoice
{
    public required int ExportIndex { get; init; }
    public required string Name { get; init; }

    public override string ToString() => Name;
}

public sealed partial class RetargetPage : Page
{
    private readonly ObservableCollection<BoneRow> _bones = [];
    private readonly ModelRenderer _renderer = new();

    private GameClient? _client;

    private Package? _targetPackage;
    private SkeletalMesh? _target;
    private int _targetExportIndex = -1;
    private SourceModel? _source;
    private string _sourceName = string.Empty;

    private RetargetOutcome? _outcome;

    private bool _ready;
    private bool _rendererStarted;
    private bool _needsDraw;

    private bool _turning;
    private bool _sliding;
    private Point _lastPointer;

    public RetargetPage()
    {
        InitializeComponent();

        BoneList.ItemsSource = _bones;

        ClientPicker.ClientChanged += (_, client) => _client = client;
        _client = ClientPicker.SelectedClient;

        Loaded += RetargetPage_Loaded;
        Unloaded += RetargetPage_Unloaded;
    }

    private void RetargetPage_Loaded(object sender, RoutedEventArgs e)
    {
        _ready = true;

        StartRenderer();

        if (ViewPicker.SelectedIndex < 0) ViewPicker.SelectedIndex = 1;
        if (ShapePicker.SelectedIndex < 0) ShapePicker.SelectedIndex = 0;
    }

    private void RetargetPage_Unloaded(object sender, RoutedEventArgs e)
    {
        CompositionTarget.Rendering -= OnFrame;
        _renderer.Dispose();
    }

    // ---- Step one: the target ----

    private async void BrowseTargetButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add(".upk");

            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null) return;

            await LoadTargetPackageAsync(file.Path);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Retarget.BrowseTarget", ex);
            StatusText.Text = $"That package could not be opened: {ex.Message}";
        }
    }

    /// <param name="preferExport">
    /// Which model to select once the package is open, so re-reading a package
    /// after writing to it lands back on the same one rather than the first.
    /// </param>
    private async Task LoadTargetPackageAsync(string path, int preferExport = -1)
    {
        TargetPathBox.Text = path;
        StatusText.Text = $"Opening {Path.GetFileName(path)}…";

        (Package? package, List<TargetMeshChoice> choices, string? problem) = await Task.Run(() =>
        {
            try
            {
                Package opened = Package.Open(path);

                var found = opened
                    .FindExportsOfClass(SkeletalMeshReader.SkeletalMeshClass)
                    .Select(index => new TargetMeshChoice
                    {
                        ExportIndex = index,
                        Name = opened.GetExportName(index),
                    })
                    .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return (opened, found, (string?)null);
            }
            catch (Exception ex)
            {
                return ((Package?)null, new List<TargetMeshChoice>(), ex.Message);
            }
        });

        if (package is null)
        {
            TargetStatus.Text = $"Could not be opened: {problem}";
            StatusText.Text = "That package could not be opened.";
            return;
        }

        _targetPackage = package;
        TargetMeshPicker.ItemsSource = choices;

        if (choices.Count == 0)
        {
            TargetStatus.Text = "This package holds no model with a skeleton.";
            StatusText.Text = "Nothing here to fit a model to.";
            return;
        }

        TargetStatus.Text = $"{choices.Count} model(s) with a skeleton.";

        int wanted = preferExport < 0 ? -1 : choices.FindIndex(c => c.ExportIndex == preferExport);

        // The largest is the character; the rest are usually their weapons.
        TargetMeshPicker.SelectedIndex = wanted >= 0 ? wanted : 0;
    }

    private void TargetMeshPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _targetPackage is null) return;
        if (TargetMeshPicker.SelectedItem is not TargetMeshChoice choice) return;

        string? problem = null;
        _target = SkeletalMeshReader.TryRead(_targetPackage, choice.ExportIndex, why => problem = why);
        _targetExportIndex = choice.ExportIndex;

        // A fit belongs to the model it was made against, so choosing a
        // different one puts the page back to having nothing to write.
        _outcome = null;
        ExportButton.IsEnabled = false;
        InstallButton.IsEnabled = false;

        if (_target is null)
        {
            _targetExportIndex = -1;
            TargetStatus.Text = $"That model could not be read: {problem}";
            ShowBones(null);
            return;
        }

        TargetLodPicker.ItemsSource = Enumerable
            .Range(0, _target.Lods.Count)
            .Select(i => i == 0 ? "Full detail" : $"Reduced {i}")
            .ToList();

        TargetLodPicker.SelectedIndex = 0;

        ShowBones(_target);

        StatusText.Text =
            $"{_target.Name} has {_target.Bones.Count:N0} bones. Import a model to fit to it.";
    }

    private void TargetLodPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        ShowSelectedView();
    }

    /// <summary>
    /// Lists the skeleton, indented by depth.
    /// </summary>
    /// <remarks>
    /// The workflow calls for checking the hierarchy and the bone count before
    /// running anything, which is the cheapest way to notice that the wrong
    /// model was picked.
    /// </remarks>
    private void ShowBones(SkeletalMesh? mesh)
    {
        _bones.Clear();

        if (mesh is null)
        {
            BoneSummary.Text = "No skeleton loaded.";
            return;
        }

        BoneSummary.Text = $"{mesh.Name} — {mesh.Bones.Count:N0} bones";

        var depth = new int[mesh.Bones.Count];

        for (int i = 0; i < mesh.Bones.Count; i++)
        {
            int parent = mesh.Bones[i].ParentIndex;

            depth[i] = parent >= 0 && parent < i ? depth[parent] + 1 : 0;

            _bones.Add(new BoneRow { Text = new string(' ', depth[i] * 2) + mesh.Bones[i].Name });
        }
    }

    // ---- Step two: the model to fit ----

    private async void ImportSourceButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };

            foreach (string extension in MeshFile.Extensions) picker.FileTypeFilter.Add(extension);

            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null) return;

            await ImportSourceAsync(file.Path);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Retarget.Import", ex);
            StatusText.Text = $"That model could not be imported: {ex.Message}";
        }
    }

    private async Task ImportSourceAsync(string path)
    {
        StatusText.Text = $"Reading {Path.GetFileName(path)}…";

        (SourceModel? model, string? problem) = await Task.Run(() =>
        {
            try
            {
                return (SourceModelBuilder.Build(MeshFile.Read(path)), (string?)null);
            }
            catch (Exception ex)
            {
                return ((SourceModel?)null, ex.Message);
            }
        });

        if (model is null)
        {
            SourceStatus.Text = $"Could not be read: {problem}";
            StatusText.Text = "That model could not be read.";
            return;
        }

        _source = model;
        _sourceName = Path.GetFileNameWithoutExtension(path);

        SourceStatus.Text =
            $"{_sourceName}\n" +
            $"{model.Geometry.Positions.Count:N0} vertices, {model.Geometry.TriangleCount:N0} triangles\n" +
            (model.HasSkeleton
                ? $"{model.Bones.Count:N0} bones, up to {model.MostInfluences} per vertex"
                : "no skeleton — its weights cannot be rebound by name");

        ShowSelectedView();

        StatusText.Text = _target is null
            ? "Model read. Now choose the package to fit it to."
            : "Model read. Press “Fit the model” when the options are right.";
    }

    private void ResetSourceButton_Click(object sender, RoutedEventArgs e)
    {
        _source = null;
        _outcome = null;
        _sourceName = string.Empty;

        ExportButton.IsEnabled = false;
        InstallButton.IsEnabled = false;

        SourceStatus.Text = "No model imported.";
        LogText.Text = "Nothing run yet.";

        ShowSelectedView();
    }

    // ---- Step three: fitting ----

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;

        if (ReferenceEquals(sender, FlipWindingCheck) && _outcome is not null)
            StatusText.Text = "Press “Fit the model” again for that to take effect.";
    }

    /// <summary>
    /// Fits the imported model to the target skeleton.
    /// </summary>
    /// <remarks>
    /// Carried out away from the interface. Run in place it froze the whole
    /// window — no viewport, no menu, and Windows reporting the application as
    /// not responding — with nothing to say whether it was working or stuck.
    /// </remarks>
    private async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        if (_source is null)
        {
            StatusText.Text = "Import a model first.";
            return;
        }

        if (_target is null)
        {
            StatusText.Text = "Choose the package and model to fit to first.";
            return;
        }

        var options = new RetargetOptions
        {
            KeepSourceWeights = KeepWeightsCheck.IsChecked == true,
            Shape = ShapePicker.SelectedIndex switch
            {
                1 => ShapeHandling.FitToRestPose,
                2 => ShapeHandling.Decide,
                _ => ShapeHandling.LeaveAlone,
            },
            FlipWinding = FlipWindingCheck.IsChecked == true,
        };

        SourceModel source = _source;
        SkeletalMesh target = _target;

        try
        {
            RunButton.IsEnabled = false;
            ExportButton.IsEnabled = false;
            InstallButton.IsEnabled = false;

            StatusText.Text = $"Fitting {_sourceName} to {target.Name}…";
            LogText.Text = "Working…";

            _outcome = await Task.Run(() => RetargetRun.Run(source, target, options));

            LogText.Text = string.Join(Environment.NewLine, _outcome.Log);

            FindingList.ItemsSource = _outcome.Findings.Select(FindingRow.From).ToList();

            ViewPicker.SelectedIndex = 1;
            ShowSelectedView();

            ExportButton.IsEnabled = true;
            InstallButton.IsEnabled = _targetExportIndex >= 0;

            StatusText.Text =
                $"{_sourceName} fitted to {_target.Name}. Nothing has been written to the game.";
        }
        catch (RetargetException ex)
        {
            // Refused on purpose, with a reason worth showing as it is.
            _outcome = null;
            ExportButton.IsEnabled = false;
            InstallButton.IsEnabled = false;
            LogText.Text = ex.Message;
            StatusText.Text = "The model was not fitted. See the panel on the right.";
        }
        catch (Exception ex)
        {
            _outcome = null;
            ExportButton.IsEnabled = false;
            InstallButton.IsEnabled = false;
            CrashLog.Write("Retarget.Run", ex);
            LogText.Text = ex.Message;
            StatusText.Text = "The retarget failed.";
        }
        finally
        {
            RunButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Writes the fitted model to a file the user picks.
    /// </summary>
    /// <remarks>
    /// The only thing on this page that writes anything, and it writes where
    /// the user chose — never into the game folder.
    /// </remarks>
    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_outcome is null || _target is null)
        {
            StatusText.Text = "Fit a model first.";
            return;
        }

        try
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeChoices.Add("Model", [".psk"]);
            picker.SuggestedFileName = $"{_sourceName}_on_{_target.Name}";

            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            StorageFile? file = await picker.PickSaveFileAsync();
            if (file is null) return;

            string path = file.Path;
            SkeletalMeshLod fitted = _outcome.After;

            // Written with the target's skeleton, because that is what the
            // model now follows: its weights name bones on that skeleton.
            IReadOnlyList<MeshBone> bones = _target.Bones;
            IReadOnlyList<string> materials = _source?.Materials ?? [];

            await Task.Run(() => PskWriter.Write(path, fitted, bones, materials));

            StatusText.Text = $"Saved to {path}. Nothing in the game was touched.";
        }
        catch (Exception ex)
        {
            CrashLog.Write("Retarget.Export", ex);
            StatusText.Text = $"Saving failed: {ex.Message}";
        }
    }

    // ---- Writing into the game ----

    /// <summary>
    /// Replaces the model inside the target package with the fitted one.
    /// </summary>
    /// <remarks>
    /// The only thing in the retarget that changes a file in the game folder,
    /// so it is deliberately the slowest path on the page: the new package is
    /// built and read back first, then exactly what would change is put in
    /// front of the user, and only an explicit confirmation commits it.
    /// </remarks>
    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_outcome is null || _target is null || _targetPackage is null || _targetExportIndex < 0)
        {
            StatusText.Text = "Fit a model first.";
            return;
        }

        Package package = _targetPackage;
        SkeletalMesh target = _target;
        int exportIndex = _targetExportIndex;
        SkeletalMeshLod fitted = _outcome.After;

        try
        {
            InstallButton.IsEnabled = false;
            StatusText.Text = "Checking the model can be written…";

            var geometry = new MeshGeometry
            {
                Positions = fitted.Positions,
                Normals = fitted.Normals,
                TexCoords = fitted.TexCoords,
                Influences = fitted.Influences,
                Indices = fitted.Indices,
                Sections = fitted.Sections,
                TangentFrames = fitted.TangentFrames,
            };

            // Built off the interface thread: it rewrites the whole package and
            // reads it back, which on a large one is not instant.
            (MeshInstallPlan? plan, string? refusal) = await Task.Run(() =>
            {
                try
                {
                    return (MeshInstaller.Plan(package, exportIndex, target, geometry), (string?)null);
                }
                catch (Exception ex) when (ex is MeshWriteException or PackageRebuildException)
                {
                    return ((MeshInstallPlan?)null, ex.Message);
                }
            });

            if (plan is null)
            {
                LogText.Text = refusal ?? "This model cannot be written into that package.";
                StatusText.Text = "Nothing was written. See the panel on the right.";
                InstallButton.IsEnabled = true;
                return;
            }

            if (!await ConfirmAsync(plan))
            {
                StatusText.Text = "Nothing was written.";
                InstallButton.IsEnabled = true;
                return;
            }

            StatusText.Text = $"Writing {plan.FileName}…";

            MeshInstallResult result = await MeshInstaller.CommitAsync(plan);

            // The file on disk is a different shape now, so what is held in
            // memory describes a package that no longer exists. Reading it
            // again also proves the file that landed can be opened.
            await LoadTargetPackageAsync(result.PackagePath, exportIndex);

            LogText.Text =
                $"{plan.ObjectName} in {plan.FileName} was replaced." + Environment.NewLine +
                $"{plan.VerticesAfter:N0} vertices, {plan.TrianglesAfter:N0} triangles." + Environment.NewLine +
                $"The original was backed up to:" + Environment.NewLine + result.BackupPath + Environment.NewLine +
                "Put it back at any time from the Backups page.";

            StatusText.Text = $"{plan.ObjectName} written into {plan.FileName}. The original is backed up.";
        }
        catch (Exception ex)
        {
            CrashLog.Write("Retarget.Install", ex);
            LogText.Text = ex.Message;
            StatusText.Text = "Writing failed. The game file was not changed.";
            InstallButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Puts the exact change in front of the user and waits for an answer.
    /// </summary>
    /// <remarks>
    /// Every number here was measured while building the package, not guessed,
    /// and the file is named in full — this is the last point at which the user
    /// can find out they had the wrong package selected.
    /// </remarks>
    private async Task<bool> ConfirmAsync(MeshInstallPlan plan)
    {
        var body = new StackPanel { Spacing = 8 };

        void Line(string text, bool strong = false) => body.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontWeight = strong
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal,
        });

        Line("This file will be changed:", strong: true);
        Line(plan.PackagePath);

        Line("This object inside it will be replaced:", strong: true);
        Line(plan.ObjectName);

        Line(
            $"{plan.VerticesBefore:N0} vertices and {plan.TrianglesBefore:N0} triangles become " +
            $"{plan.VerticesAfter:N0} vertices and {plan.TrianglesAfter:N0} triangles.");

        Line($"The file grows from {plan.FileSizeBefore:N0} to {plan.FileSizeAfter:N0} bytes.");

        if (plan.Morphs.Count > 0)
        {
            int lost = plan.Morphs.Sum(m => m.Lost);

            Line(
                $"{plan.Morphs.Count} of this character's powers reshape the model — " +
                string.Join(", ", plan.Morphs.Select(m => m.Name).Take(6)) +
                (plan.Morphs.Count > 6 ? ", and more" : string.Empty) +
                ". They name the vertices they move by number, so they are renumbered onto the new " +
                "model." +
                (lost > 0
                    ? $" {lost:N0} of {plan.Morphs.Sum(m => m.Before):N0} displacements have nowhere near " +
                      "enough to land on and are dropped; that part of the model will not reshape."
                    : " Every one of them found its place."));
        }

        if (plan.DetailLevels > 1)
        {
            Line(
                $"All {plan.DetailLevels} of its levels of detail are rewritten from this model. They are " +
                "not simplified, so it draws at full detail however far away it is.");
        }

        Line(
            "The original is copied into the backup vault before anything is written, and the new file " +
            "is swapped in as a whole. You can put it back from the Backups page.");

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Write this model into the game?",
            Content = new ScrollViewer { Content = body, MaxHeight = 420 },
            PrimaryButtonText = "Write it",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    // ---- Showing it ----

    /// <summary>
    /// Changing what happens to the shape means the fit has to be run again.
    /// </summary>
    private void ShapePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _outcome is null) return;

        StatusText.Text = "Press “Fit the model” again for that to take effect.";
    }

    private void ViewPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        ShowSelectedView();
    }

    private void ShowSelectedView()
    {
        if (!_renderer.Ready) return;

        SkeletalMeshLod? lod;
        string caption;

        switch (ViewPicker.SelectedIndex)
        {
            case 0:
                lod = _source?.Geometry;
                caption = _sourceName.Length > 0 ? $"{_sourceName}, as imported" : string.Empty;
                break;

            case 2:
                lod = TargetLod();
                caption = _target is not null ? $"{_target.Name}, the target's own model" : string.Empty;
                break;

            default:
                lod = _outcome?.After ?? _source?.Geometry;
                caption = _outcome is not null && _target is not null
                    ? $"{_sourceName} on {_target.Name}'s skeleton"
                    : _sourceName.Length > 0 ? $"{_sourceName}, not fitted yet" : string.Empty;
                break;
        }

        ViewCaption.Text = caption;

        if (lod is null || !lod.HasGeometry)
        {
            ShowViewportMessage(_source is null && _target is null
                ? "Choose a target package, then import a model to fit to it."
                : "Nothing to show for this view yet.");
            return;
        }

        _renderer.SetModel(lod);

        // Framed on the target where there is one, so the imported model and
        // the fitted result are seen at the size they will end up. With no
        // target yet, the imported model frames itself — otherwise it sits
        // wherever the camera happened to be and can be off screen entirely.
        if (_target is not null) _renderer.FrameModel(_target.Bounds);
        else FrameOn(lod);

        HideViewportMessage();
        RequestDraw();
    }

    /// <summary>
    /// Points the camera at a model whose size is not known in advance.
    /// </summary>
    /// <remarks>
    /// An imported model carries no stated bounds, so they are measured from
    /// its own vertices.
    /// </remarks>
    private void FrameOn(SkeletalMeshLod lod)
    {
        if (lod.Positions.Count == 0) return;

        var lowest = new System.Numerics.Vector3(float.MaxValue);
        var highest = new System.Numerics.Vector3(float.MinValue);

        foreach (System.Numerics.Vector3 position in lod.Positions)
        {
            lowest = System.Numerics.Vector3.Min(lowest, position);
            highest = System.Numerics.Vector3.Max(highest, position);
        }

        System.Numerics.Vector3 centre = (lowest + highest) * 0.5f;

        _renderer.Camera.Frame(centre, System.Numerics.Vector3.Distance(centre, highest));
    }

    private SkeletalMeshLod? TargetLod()
    {
        if (_target is null || _target.Lods.Count == 0) return null;

        int index = Math.Clamp(TargetLodPicker.SelectedIndex, 0, _target.Lods.Count - 1);
        return _target.Lods[index];
    }

    // ---- The viewport ----

    private void StartRenderer()
    {
        if (_rendererStarted) return;

        int width = (int)Viewport.ActualWidth;
        int height = (int)Viewport.ActualHeight;

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

    private void OnFrame(object? sender, object e)
    {
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

        if (_turning) _renderer.Camera.Rotate((float)-dx * 0.01f, (float)dy * 0.01f);
        else _renderer.Camera.Pan((float)-dx * 0.0015f, (float)dy * 0.0015f);

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

        _renderer.Camera.Zoom(delta > 0 ? 0.88f : 1.0f / 0.88f);
        RequestDraw();
    }

    private void ResetViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_target is not null) _renderer.FrameModel(_target.Bounds);
        RequestDraw();
    }

    private void ShowViewportMessage(string message)
    {
        ViewportMessage.Text = message;
        ViewportMessage.Visibility = Visibility.Visible;
    }

    private void HideViewportMessage() => ViewportMessage.Visibility = Visibility.Collapsed;
}
