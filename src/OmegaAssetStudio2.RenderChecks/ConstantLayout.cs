using System.Text.RegularExpressions;

namespace OmegaAssetStudio2.RenderChecks;

/// <summary>
/// Checks that a constant block laid out in C# matches how a shader will read
/// it.
/// </summary>
/// <remarks>
/// A shader packs constants in groups of four values. A group left short - two
/// padding entries where three were needed - leaves a hole, and everything
/// after it is read one slot early. Nothing warns about this: the code
/// compiles, the shader compiles, and the picture comes out wrong in a way that
/// looks like a lighting problem. It cost a session's worth of misdiagnosis
/// once already, so it is checked here instead.
/// </remarks>
internal static class ConstantLayout
{
    /// <summary>How many bytes each type takes in the block.</summary>
    private static readonly Dictionary<string, int> Sizes = new(StringComparer.Ordinal)
    {
        ["Matrix4x4"] = 64,
        ["Vector4"] = 16,
        ["Vector3"] = 12,
        ["Vector2"] = 8,
        ["float"] = 4,
        ["int"] = 4,
        ["uint"] = 4,
    };

    /// <summary>Types the shader will only read on a sixteen-byte boundary.</summary>
    private static readonly string[] MustBeAligned = ["Matrix4x4", "Vector4"];

    public static IReadOnlyList<string> Check(string sourcePath, string structName)
    {
        var complaints = new List<string>();

        if (!File.Exists(sourcePath))
        {
            complaints.Add($"{structName}: {sourcePath} is not there to read.");
            return complaints;
        }

        string source = File.ReadAllText(sourcePath);

        Match block = Regex.Match(
            source,
            @"struct\s+" + Regex.Escape(structName) + @"\s*\{(?<body>.*?)\n\}",
            RegexOptions.Singleline);

        if (!block.Success)
        {
            complaints.Add($"{structName}: could not be found in {Path.GetFileName(sourcePath)}.");
            return complaints;
        }

        string body = block.Groups["body"].Value;

        int offset = 0;

        foreach (Match field in Regex.Matches(body, @"public\s+(?<type>[A-Za-z0-9_]+)\s+(?<name>[A-Za-z0-9_]+)\s*;"))
        {
            string type = field.Groups["type"].Value;
            string name = field.Groups["name"].Value;

            if (!Sizes.TryGetValue(type, out int size)) continue;

            if (MustBeAligned.Contains(type) && offset % 16 != 0)
            {
                complaints.Add(
                    $"{structName}.{name} sits at byte {offset}, which is not a multiple of sixteen. " +
                    $"The shader will read it {16 - (offset % 16)} bytes further on, and every field after " +
                    "it as well. Pad the group before it out to four entries.");
            }

            offset += size;
        }

        if (offset % 16 != 0)
        {
            complaints.Add(
                $"{structName} is {offset} bytes, which is not a multiple of sixteen. " +
                $"Add {16 - (offset % 16)} bytes of padding to the end.");
        }

        // The declared size has to agree with the fields, because that is what
        // the buffer is actually made with.
        Match declared = Regex.Match(body, @"const\s+int\s+Size\s*=\s*(?<sum>[^;]+);");

        if (declared.Success)
        {
            int stated = Evaluate(declared.Groups["sum"].Value);

            if (stated != offset)
            {
                complaints.Add(
                    $"{structName}.Size says {stated} bytes but its fields come to {offset}. " +
                    "The buffer is made from Size, so the two have to agree.");
            }
        }

        return complaints;
    }

    /// <summary>Works out a sum written as products, such as (16 * 4) + (4 * 4).</summary>
    private static int Evaluate(string sum)
    {
        int total = 0;

        foreach (Match term in Regex.Matches(sum, @"\(?\s*(?<a>\d+)\s*\*\s*(?<b>\d+)\s*\)?"))
        {
            total += int.Parse(term.Groups["a"].Value) * int.Parse(term.Groups["b"].Value);
        }

        return total;
    }
}
