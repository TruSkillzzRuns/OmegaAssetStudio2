using OmegaAssetStudio2.RenderChecks;

// Run against the viewport's source after it is built. Anything wrong here has
// already been wrong on screen once, so it fails the build rather than warning.

// Found by walking up from wherever this was built until the viewport's own
// project turns up beside it, rather than by counting folders. Counting gave
// the right answer from bin/Release and the wrong one from bin/x64/Release -
// and x64 is the only build the app ships from, so the default was broken for
// every real run and every caller had to name the folder by hand.
static string? AppFolderBesideThis()
{
    var at = new DirectoryInfo(AppContext.BaseDirectory);

    while (at is not null)
    {
        string beside = Path.Combine(at.FullName, "OmegaAssetStudio2.App");

        if (Directory.Exists(beside)) return beside;

        at = at.Parent;
    }

    return null;
}

string appFolder = args.Length > 0
    ? args[0]
    : AppFolderBesideThis()
      ?? throw new InvalidOperationException(
          "The viewport's project could not be found above " + AppContext.BaseDirectory
          + ". Name its folder as the first argument.");

string renderer = Path.GetFullPath(Path.Combine(appFolder, "Rendering", "ModelRenderer.cs"));
string shaders = Path.GetFullPath(Path.Combine(appFolder, "Rendering", "ModelShaders.cs"));

if (args.Length > 2 && args[1] == "--sweep")
{
    // Every model the mesh panel lists, drawn and graded. Written out so a
    // change can be judged against the whole roster instead of one screenshot.
    string source = OmegaAssetStudio2.RenderChecks.ShadingCheck.Extract(File.ReadAllText(shaders), "Source");

    // Optionally narrowed to one model, and optionally writing out what it
    // drew, so a fault can be looked at rather than only counted.
    foreach (string argument in args)
    {
        if (argument.StartsWith("only=")) OmegaAssetStudio2.RenderChecks.MeshSweep.Only = argument[5..];
        if (argument.StartsWith("shots=")) OmegaAssetStudio2.RenderChecks.MeshSweep.PictureFolder = argument[6..];

        if (argument.StartsWith("blame="))
        {
            // Render each model several ways - as it stands, and with one term
            // silenced at a time - so the marking can be attributed to a term
            // across the whole roster.
            OmegaAssetStudio2.RenderChecks.MeshSweep.Variants =
                [string.Empty, .. argument[6..].Split(';', StringSplitOptions.RemoveEmptyEntries)];
        }

        if (argument.StartsWith("without="))
        {
            foreach (string term in argument[8..].Split(','))
                OmegaAssetStudio2.RenderChecks.MeshSweep.Without.Add(term);
        }

        if (argument.StartsWith("turn="))
        {
            string[] where = argument[5..].Split(',');
            OmegaAssetStudio2.RenderChecks.MeshSweep.Around = float.Parse(where[0]) * MathF.PI / 180f;
            OmegaAssetStudio2.RenderChecks.MeshSweep.Above = float.Parse(where[1]) * MathF.PI / 180f;
        }

        if (argument.StartsWith("close="))
        {
            string[] parts = argument[6..].Split(',');
            OmegaAssetStudio2.RenderChecks.MeshSweep.Frame(int.Parse(parts[0]), float.Parse(parts[1]));
        }
    }

    IReadOnlyList<OmegaAssetStudio2.RenderChecks.MeshSweep.Verdict> drawn =
        OmegaAssetStudio2.RenderChecks.MeshSweep.Run(args[2], source);

    string report = args.Length > 3 ? args[3] : "sweep.tsv";

    File.WriteAllLines(report, drawn.Select(v => v.ToString()));

    Console.WriteLine();
    Console.WriteLine(drawn.Count + " models drawn, written to " + report);

    var counted = new Dictionary<string, int>();

    foreach (var one in drawn)
    {
        foreach (string fault in one.Faults)
        {
            string kind = System.Text.RegularExpressions.Regex.Replace(fault, "^[0-9]+ of [0-9]+", "some");
            counted[kind] = counted.GetValueOrDefault(kind) + 1;
        }
    }

    Console.WriteLine("   " + drawn.Count(v => v.Faults.Count == 0) + " came out clean");

    // What each silenced term did to the hard edges, across every model.
    IReadOnlyList<string> variants = OmegaAssetStudio2.RenderChecks.MeshSweep.Variants;

    if (variants.Count > 1)
    {
        Console.WriteLine();
        Console.WriteLine("   hard edges, and what silencing each term does to them:");

        long total = drawn.Where(v => v.Edges.Count > 0).Sum(v => (long)v.Edges[0]);

        Console.WriteLine("      as it stands".PadRight(34) + total.ToString("N0"));

        for (int i = 1; i < variants.Count; i++)
        {
            int index = i;

            var able = drawn.Where(v => v.Edges.Count > index).ToList();

            long after = able.Sum(v => (long)v.Edges[index]);

            // How many models lose most of their hard edges when this term goes
            // quiet. Those are the models the term is marking.
            int mostlyGone = able.Count(v => v.Edges[0] > 200 && v.Edges[index] < v.Edges[0] * 0.6);

            Console.WriteLine("      without " + variants[index].PadRight(26)
                              + after.ToString("N0").PadLeft(12)
                              + "   " + mostlyGone + " models lose most of theirs");
        }
    }

    foreach (var pair in counted.OrderByDescending(c => c.Value))
        Console.WriteLine("   " + pair.Value.ToString().PadLeft(6) + "  " + pair.Key);

    return 0;
}

if (args.Length > 1 && args[1] == "--facing") { OmegaAssetStudio2.RenderChecks.Facing.Run(); return 0; }

Console.WriteLine("Checking the viewport's shading");

var complaints = new List<string>();

// The block lives beside the shading now, so that the viewport and the
// sweep that grades every model fill the same one.
string constants = Path.GetFullPath(Path.Combine(
    appFolder, "..", "OmegaAssetStudio2.Core", "Materials", "ShadingConstants.cs"));

complaints.AddRange(ConstantLayout.Check(constants, "FrameConstants"));
complaints.AddRange(ConstantLayout.Check(renderer, "SceneConstants"));
complaints.AddRange(ShadingCheck.Check(shaders));

if (complaints.Count == 0)
{
    Console.WriteLine("  constants line up, shaders compile, shading unchanged");
    return 0;
}

foreach (string complaint in complaints) Console.Error.WriteLine($"  {complaint}");

return 1;
