using OmegaAssetStudio2.Core.Packages;

namespace OmegaAssetStudio2.Core.Materials;

/// <summary>A colour to write back to a material instance.</summary>
public readonly record struct ColourEdit(int ValueOffset, MaterialColour Colour);

/// <summary>A numeric value to write back to a material instance.</summary>
public readonly record struct ScalarEdit(int ValueOffset, float Value);

/// <summary>
/// Writes edited parameter values back into a package.
/// </summary>
/// <remarks>
/// Every edit replaces four bytes with four bytes, or sixteen with sixteen, so
/// nothing in the package moves. That is what makes this safe to apply directly
/// rather than needing the object to be rebuilt.
/// </remarks>
public static class MaterialParameterWriter
{
    /// <summary>
    /// Builds the patched bytes of an export with the given edits applied.
    /// </summary>
    public static byte[] BuildPatchedExport(
        Package package,
        int exportIndex,
        IReadOnlyList<ColourEdit> colours,
        IReadOnlyList<ScalarEdit> scalars)
    {
        byte[] data = package.GetExportData(exportIndex).ToArray();

        foreach (ColourEdit edit in colours)
        {
            Require(data, edit.ValueOffset, sizeof(float) * 4, "colour");

            BitConverter.GetBytes(edit.Colour.R).CopyTo(data, edit.ValueOffset);
            BitConverter.GetBytes(edit.Colour.G).CopyTo(data, edit.ValueOffset + 4);
            BitConverter.GetBytes(edit.Colour.B).CopyTo(data, edit.ValueOffset + 8);
            BitConverter.GetBytes(edit.Colour.A).CopyTo(data, edit.ValueOffset + 12);
        }

        foreach (ScalarEdit edit in scalars)
        {
            Require(data, edit.ValueOffset, sizeof(float), "value");
            BitConverter.GetBytes(edit.Value).CopyTo(data, edit.ValueOffset);
        }

        return data;
    }

    private static void Require(byte[] data, int offset, int length, string what)
    {
        if (offset < 0 || offset + length > data.Length)
        {
            throw new InvalidOperationException(
                $"A {what} edit at offset {offset} lies outside the {data.Length}-byte object. " +
                "The object was probably re-read since the parameters were listed.");
        }
    }

    /// <summary>
    /// Applies edits across a package and saves it, taking a backup and swapping
    /// the file in atomically.
    /// </summary>
    /// <param name="edits">Edits grouped by the export they belong to.</param>
    /// <returns>The path of the pristine backup protecting the original.</returns>
    public static async Task<string> SaveAsync(
        Package package,
        IReadOnlyDictionary<int, (IReadOnlyList<ColourEdit> Colours, IReadOnlyList<ScalarEdit> Scalars)> edits,
        CancellationToken cancellationToken = default)
    {
        var patches = new List<ExportPatch>(edits.Count);

        foreach ((int exportIndex, (IReadOnlyList<ColourEdit> colours, IReadOnlyList<ScalarEdit> scalars)) in edits)
            patches.Add(new ExportPatch(exportIndex, BuildPatchedExport(package, exportIndex, colours, scalars)));

        return await PackageWriter.SaveAsync(package, patches, cancellationToken).ConfigureAwait(false);
    }
}
