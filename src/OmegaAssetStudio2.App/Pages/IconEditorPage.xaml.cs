using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using OmegaAssetStudio2.App.Icons;
using OmegaAssetStudio2.App.Services;
using OmegaAssetStudio2.Core.Icons;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Packages.Properties;
using OmegaAssetStudio2.Core.Textures;
using OmegaAssetStudio2.Core.Workspace;

namespace OmegaAssetStudio2.App.Pages;

/// <summary>One icon in the grid. The picture loads only when the card is shown.</summary>
public sealed class IconCard : INotifyPropertyChanged
{
    private WriteableBitmap? _thumbnail;
    private string _placeholderGlyph = string.Empty;
    private bool _requested;

    public required TextureInfo Info { get; init; }
    public required string Name { get; init; }
    public required string PackageName { get; init; }

    public WriteableBitmap? Thumbnail
    {
        get => _thumbnail;
        private set { _thumbnail = value; Raise(); }
    }

    /// <summary>Shown instead of a picture when there is nothing to show.</summary>
    public string PlaceholderGlyph
    {
        get => _placeholderGlyph;
        private set { _placeholderGlyph = value; Raise(); }
    }

    /// <summary>Forces the next thumbnail request to decode again.</summary>
    public void InvalidateThumbnail()
    {
        _requested = false;
        Thumbnail = null;
    }

    public async Task EnsureThumbnailAsync(IconImageService images, string cookedPath)
    {
        if (_requested) return;
        _requested = true;

        WriteableBitmap? bitmap = await images.TryGetBitmapAsync(Info, cookedPath);
        if (bitmap is not null)
        {
            Thumbnail = bitmap;
            PlaceholderGlyph = string.Empty;
        }
        else
        {
            PlaceholderGlyph = Info.IsCacheBacked ? "in texture cache" : "no preview";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One row of the category tree.</summary>
public sealed class CategoryNode
{
    public required string Title { get; init; }

    /// <summary>Positions in the scan this row covers, itself and below.</summary>
    public required IReadOnlyList<int> Items { get; init; }

    public string CountLabel => Items.Count.ToString("N0");
}

public sealed partial class IconEditorPage : Page
{
    private readonly TextureCatalog _catalog = new();
    private readonly IconImageService _images = new();
    private readonly ObservableCollection<IconCard> _visible = [];

    private List<IconCard> _all = [];
    private IReadOnlyList<int>? _selectedItems;
    private CancellationTokenSource? _scanCancellation;
    private GameClient? _client;

    public IconEditorPage()
    {
        InitializeComponent();

        IconGrid.ItemsSource = _visible;
        CategorySplitter.ResizesPreviousColumn = true;

        ClientPicker.ClientChanged += (_, client) =>
        {
            _client = client;
            // Results and cached packages from one install mean nothing in another.
            _all = [];
            CategoryTree.RootNodes.Clear();
            _selectedItems = null;
            ShowAllButton.IsEnabled = false;
            _images.Clear();
            ApplyFilter();
            StatusText.Text = client is null
                ? "Add a game folder on the Home page first."
                : $"Ready to scan {client.DisplayName}.";
        };
        _client = ClientPicker.SelectedClient;
    }

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
        ScanBar.Value = 0;

        int skipped = 0;
        try
        {
            var progress = new Progress<TextureScanProgress>(p =>
            {
                ScanBar.Maximum = Math.Max(1, p.PackageCount);
                ScanBar.Value = p.PackagesScanned;
                StatusText.Text =
                    $"Scanning {p.PackagesScanned:N0} of {p.PackageCount:N0} packages — " +
                    $"{p.TexturesFound:N0} icons found";
            });

            IReadOnlyList<TextureInfo> found = await _catalog.ScanAsync(
                _client,
                fileFilter: "ICO__*.upk",
                progress: progress,
                onError: (_, _) => skipped++,
                cancellationToken: _scanCancellation.Token);

            _all = found
                .Select(info => new IconCard
                {
                    Info = info,
                    Name = info.Name,
                    PackageName = Path.GetFileNameWithoutExtension(info.PackagePath),
                })
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            BuildCategories();
            ApplyFilter();

            int previewable = _all.Count(c => !c.Info.IsCacheBacked);
            StatusText.Text = _all.Count == 0
                ? "No icons found. This install may keep them elsewhere."
                : $"Found {_all.Count:N0} icons; {previewable:N0} stored in their package." +
                  (skipped > 0 ? $" {skipped} package(s) could not be read." : string.Empty);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Scan stopped.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Scan failed: {ex.Message}";
            CrashLog.Write("IconEditor.Scan", ex);
        }
        finally
        {
            ScanButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            ScanBar.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _scanCancellation?.Cancel();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        string needle = SearchBox.Text.Trim();

        IEnumerable<IconCard> query = _selectedItems is null
            ? _all
            : _selectedItems.Select(index => _all[index]);

        if (needle.Length > 0)
        {
            query = query.Where(c =>
                c.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                c.PackageName.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        _visible.Clear();
        foreach (IconCard card in query) _visible.Add(card);

        CountText.Text = _all.Count == 0
            ? string.Empty
            : $"{_visible.Count:N0} shown of {_all.Count:N0}";

        ShowAllButton.IsEnabled = _selectedItems is not null;
    }

    /// <summary>
    /// Sorts the scan into the category tree. The work is done on the names
    /// alone, so it costs nothing next to the scan that produced them.
    /// </summary>
    private void BuildCategories()
    {
        CategoryTree.RootNodes.Clear();
        _selectedItems = null;

        if (_all.Count == 0) return;

        string[] names = _all.Select(card => card.Info.Name).ToArray();
        IconTreeNode root = IconTreeBuilder.Build(names, _client?.CookedPath);

        foreach (IconTreeNode child in root.Children)
            CategoryTree.RootNodes.Add(ToNode(child));
    }

    /// <summary>
    /// Builds the tree's own node for a category, carrying the category on it
    /// as Content. Every row is a node the tree made itself, so the row that
    /// reports as selected is always the row that was clicked.
    /// </summary>
    private static TreeViewNode ToNode(IconTreeNode source)
    {
        var node = new TreeViewNode
        {
            Content = new CategoryNode
            {
                Title = source.Title,
                Items = source.AllItems.ToArray(),
            },
        };

        foreach (IconTreeNode child in source.Children)
            node.Children.Add(ToNode(child));

        return node;
    }

    private void CategoryTree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        // Read the row from the event rather than from SelectedItem. The tree
        // wraps each bound row in a node of its own, and SelectedItem reports
        // that wrapper's neighbour once rows have been expanded - which showed
        // one category's icons under another category's name.
        CategoryNode? node = args.AddedItems.Count > 0
            ? AsCategory(args.AddedItems[0])
            : AsCategory(CategoryTree.SelectedNode);

        _selectedItems = node?.Items;
        ApplyFilter();
    }

    /// <summary>The row behind a selection, whether bound directly or wrapped.</summary>
    private static CategoryNode? AsCategory(object? selected) => selected switch
    {
        CategoryNode node => node,
        TreeViewNode wrapper => wrapper.Content as CategoryNode,
        _ => null,
    };

    private void ShowAllButton_Click(object sender, RoutedEventArgs e)
    {
        CategoryTree.SelectedNode = null;
        _selectedItems = null;
        ApplyFilter();
    }

    /// <summary>
    /// Decodes a card's picture only once it scrolls into view. A scan finds
    /// thousands of icons; decoding them all up front would stall the window.
    /// </summary>
    private void IconGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || _client is null) return;
        if (args.Item is not IconCard) return;

        string cookedPath = _client.CookedPath;
        args.RegisterUpdateCallback(async (_, callbackArgs) =>
        {
            if (callbackArgs.Item is IconCard card)
                await card.EnsureThumbnailAsync(_images, cookedPath);
        });
    }

    private async void IconGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IconGrid.SelectedItem is not IconCard card || _client is null)
        {
            PreviewImage.Source = null;
            PreviewMessage.Text = "Select an icon.";
            DetailName.Text = "Nothing selected";
            DetailAuthoring.Text = "Select an icon to see what size and format to author against.";
            DetailStorage.Text = string.Empty;
            DetailPath.Text = string.Empty;
            DetailFile.Text = string.Empty;
            DetailProperties.Text = "—";
            return;
        }

        TextureInfo info = card.Info;

        DetailName.Text = info.Name;
        DetailPath.Text = info.ObjectPath;
        DetailFile.Text = info.PackagePath;

        DetailAuthoring.Text =
            $"Author at {info.Width} x {info.Height}, {info.FormatName}" +
            (info.IsSrgb ? ", colour (sRGB)" : ", data (linear)") + ".";

        DetailStorage.Text = info.IsCacheBacked
            ? $"Full-size pixels are in the shared texture cache '{info.TextureCacheName}', which many " +
              "textures share. A replacement has to compress into the same space as the original."
            : "Pixels are stored inside this package."
              + (info.OriginalWidth != info.Width || info.OriginalHeight != info.Height
                  ? $" Originally authored at {info.OriginalWidth} x {info.OriginalHeight}."
                  : string.Empty);

        DetailProperties.Text = DescribeProperties(info);

        // Say up front whether this one can be changed, rather than letting the
        // user pick a file and only then be refused. Passing the content folder
        // lets textures stored in the shared cache be replaced too.
        ReplaceResult permission = TextureReplacer.CanReplace(
            Package.Open(info.PackagePath), info, _client.CookedPath);

        ReplaceButton.IsEnabled = permission.Succeeded;
        ReplaceMessage.Text = permission.Succeeded && info.IsCacheBacked
            ? permission.Message
            : permission.Succeeded ? string.Empty : permission.Message;

        PreviewImage.Source = null;
        PreviewMessage.Text = "Decoding...";

        WriteableBitmap? bitmap = await _images.TryGetBitmapAsync(info, _client.CookedPath);
        if (bitmap is not null)
        {
            PreviewImage.Source = bitmap;
            PreviewMessage.Text = string.Empty;
        }
        else
        {
            PreviewMessage.Text = info.IsCacheBacked
                ? "This icon's pixels are in the shared texture cache, which is not readable yet."
                : $"No preview: {info.FormatName} is not decoded yet.";
        }
    }

    private async void ReplaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (IconGrid.SelectedItem is not IconCard card || _client is null) return;

        TextureInfo info = card.Info;

        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            foreach (string extension in ImageFileLoader.SupportedExtensions)
                picker.FileTypeFilter.Add(extension);

            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null) return;

            ReplaceButton.IsEnabled = false;
            ReplaceMessage.Text = "Reading image...";

            LoadedImage? source = await ImageFileLoader.TryLoadAsync(file.Path);
            if (source is null)
            {
                ReplaceMessage.Text = "That file could not be read as an image.";
                ReplaceButton.IsEnabled = true;
                return;
            }

            ReplaceMessage.Text = "Writing...";

            // Re-open rather than reusing a cached copy: the write goes through
            // the file on disk, and a stale in-memory package would be written
            // back over any change made since it was loaded.
            _images.Clear();
            Package package = Package.Open(info.PackagePath);

            ReplaceResult result = await TextureReplacer.ReplaceAsync(
                package, info, source.Rgba, source.Width, source.Height, _client.CookedPath);

            ReplaceMessage.Text = result.Message;
            StatusText.Text = result.Message;

            if (result.Succeeded)
            {
                // Show what actually landed in the package, not the source file.
                card.InvalidateThumbnail();
                await card.EnsureThumbnailAsync(_images, _client.CookedPath);

                WriteableBitmap? updated = await _images.TryGetBitmapAsync(info, _client.CookedPath);
                if (updated is not null)
                {
                    PreviewImage.Source = updated;
                    PreviewMessage.Text = string.Empty;
                }
            }
        }
        catch (Exception ex)
        {
            ReplaceMessage.Text = $"Replace failed: {ex.Message}";
            CrashLog.Write("IconEditor.Replace", ex);
        }
        finally
        {
            ReplaceButton.IsEnabled = true;
        }
    }

    /// <summary>Reads the object's full property list for the detail panel.</summary>
    private static string DescribeProperties(TextureInfo info)
    {
        try
        {
            Package package = Package.Open(info.PackagePath);
            PropertyBag? properties = package.TryReadProperties(info.ExportIndex);
            if (properties is null) return "This object's properties could not be read.";

            IEnumerable<string> lines = properties.Tags.Select(tag =>
            {
                string value = tag.TypeName.ToLowerInvariant() switch
                {
                    "intproperty" => BitConverter.ToInt32(tag.Value.Span).ToString(),
                    "floatproperty" => BitConverter.ToSingle(tag.Value.Span).ToString("0.###"),
                    "boolproperty" => tag.Value.Span[0] != 0 ? "true" : "false",
                    "byteproperty" or "nameproperty" => tag.InnerName,
                    _ => $"({tag.Size} bytes)",
                };
                return $"{tag.Name,-24} {value}";
            });

            return string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            CrashLog.Write("IconEditor.DescribeProperties", ex);
            return $"Could not read properties: {ex.Message}";
        }
    }
}
