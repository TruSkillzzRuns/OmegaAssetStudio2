namespace OmegaAssetStudio2.Core.Icons;

/// <summary>One node of the category tree.</summary>
public sealed class IconTreeNode
{
    public required string Title { get; init; }

    /// <summary>Children, already sorted. Empty on a leaf.</summary>
    public List<IconTreeNode> Children { get; } = [];

    /// <summary>
    /// Positions in the scanned list that belong to this node directly. A node
    /// with children usually has none of its own.
    /// </summary>
    public List<int> Items { get; } = [];

    /// <summary>Icons at this node and everywhere below it.</summary>
    public int Count { get; private set; }

    /// <summary>Every position at this node and below, for filtering the grid.</summary>
    public IEnumerable<int> AllItems
        => Items.Concat(Children.SelectMany(child => child.AllItems));

    internal void Recount()
    {
        foreach (IconTreeNode child in Children) child.Recount();

        Count = Items.Count + Children.Sum(child => child.Count);
    }
}

/// <summary>
/// Turns a scanned list of icon names into the tree the browser shows.
/// </summary>
public static class IconTreeBuilder
{
    /// <summary>
    /// Below this many icons, a catch-all section is not worth its own row and
    /// is folded into one. Real categories are far larger; the sections this
    /// catches are one-off names that would otherwise make hundreds of rows
    /// holding a single icon each.
    /// </summary>
    private const int SmallestWorthwhileSection = 8;

    private const string CatchAll = "Other";
    private const string Leftovers = "Unsorted";

    /// <summary>
    /// Builds the tree. <paramref name="objectNames"/> is indexed the same way
    /// as the caller's own list, and the nodes carry those positions back.
    /// </summary>
    public static IconTreeNode Build(IReadOnlyList<string> objectNames, string? cookedPath)
    {
        ClientDisplayNames display = ClientDisplayNames.FromCookedFolder(cookedPath);
        IconSubjects subjects = IconSubjects.Build(objectNames, display);

        var paths = new string[objectNames.Count][];
        var catchAllSizes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < objectNames.Count; i++)
        {
            paths[i] = IconTaxonomy.Classify(objectNames[i], subjects, display);

            if (paths[i].Length > 1 && paths[i][0] == CatchAll)
                catchAllSizes[paths[i][1]] = catchAllSizes.GetValueOrDefault(paths[i][1]) + 1;
        }

        var root = new IconTreeNode { Title = "All icons" };
        var lookup = new Dictionary<string, IconTreeNode>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < paths.Length; i++)
        {
            string[] path = paths[i];

            // A thin catch-all section joins the others rather than standing alone.
            if (path.Length > 1 && path[0] == CatchAll
                && catchAllSizes.GetValueOrDefault(path[1]) < SmallestWorthwhileSection)
                path = [CatchAll, Leftovers];

            IconTreeNode node = root;
            string key = string.Empty;

            foreach (string step in path)
            {
                key = key.Length == 0 ? step : $"{key}/{step}";

                if (!lookup.TryGetValue(key, out IconTreeNode? child))
                {
                    lookup[key] = child = new IconTreeNode { Title = step };
                    node.Children.Add(child);
                }

                node = child;
            }

            node.Items.Add(i);
        }

        Sort(root);
        root.Recount();

        return root;
    }

    /// <summary>
    /// Alphabetical, except that the catch-all sits last wherever it appears -
    /// it is the least useful row and should not head the list.
    /// </summary>
    private static void Sort(IconTreeNode node)
    {
        node.Children.Sort((left, right) =>
        {
            bool leftLast = left.Title is CatchAll or Leftovers;
            bool rightLast = right.Title is CatchAll or Leftovers;

            if (leftLast != rightLast) return leftLast ? 1 : -1;

            return string.Compare(left.Title, right.Title, StringComparison.OrdinalIgnoreCase);
        });

        foreach (IconTreeNode child in node.Children) Sort(child);
    }
}
