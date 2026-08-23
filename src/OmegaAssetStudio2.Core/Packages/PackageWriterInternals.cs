namespace OmegaAssetStudio2.Core.Packages;

/// <summary>
/// The parts of writing a package that more than one writer needs.
/// </summary>
internal static class PackageWriterInternals
{
    /// <summary>
    /// Rewrites the compression fields so the package reads as uncompressed.
    /// </summary>
    internal static void ClearCompression(byte[] headerBytes, PackageHeader header)
        => PackageWriter.ClearCompressionFieldsInternal(headerBytes, header);

    /// <summary>Where the compression flags sit within the header block.</summary>
    internal static int CompressionFlagsOffset(PackageHeader header)
        => PackageWriter.CompressionFlagsOffsetInternal(header);

    /// <summary>Checks that position really holds the compression fields.</summary>
    internal static void VerifyCompressionFields(byte[] headerBytes, PackageHeader header, int offset)
        => PackageWriter.Verify(headerBytes, header, offset);
}
