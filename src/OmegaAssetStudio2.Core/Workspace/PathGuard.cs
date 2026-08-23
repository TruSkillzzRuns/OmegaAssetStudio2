namespace OmegaAssetStudio2.Core.Workspace;

/// <summary>
/// Containment check for every path that comes from outside the application —
/// archive entry names, manifest fields, anything a third party authored.
/// </summary>
/// <remarks>
/// Version 1's mod installer built output paths with
/// <c>Path.Combine(destinationRoot, entryName)</c> and no verification.
/// <c>Path.Combine</c> returns its second argument verbatim when that argument is
/// rooted, and it walks <c>".."</c> without complaint, so a crafted archive could
/// write anywhere the user could write. Nothing here resolves an untrusted
/// relative path without going through this class.
/// </remarks>
public static class PathGuard
{
    /// <summary>
    /// Resolves <paramref name="untrustedRelativePath"/> under <paramref name="rootDirectory"/>.
    /// Returns false if the result would land outside the root, or if the input is
    /// rooted, empty, or otherwise unusable.
    /// </summary>
    public static bool TryResolveWithin(
        string rootDirectory,
        string untrustedRelativePath,
        out string resolvedPath)
    {
        resolvedPath = string.Empty;

        if (string.IsNullOrWhiteSpace(rootDirectory)) return false;
        if (string.IsNullOrWhiteSpace(untrustedRelativePath)) return false;

        // Reject anything rooted outright rather than trying to relativize it —
        // "C:\Windows\..." and "\\server\share" are never legitimate here.
        if (Path.IsPathRooted(untrustedRelativePath)) return false;

        string normalizedRelative = untrustedRelativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        string root = Path.GetFullPath(rootDirectory);
        string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(root, normalizedRelative));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        // GetFullPath has already collapsed any "..", so a simple prefix test is
        // sound. Case-insensitive because Windows paths are.
        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            return false;

        resolvedPath = candidate;
        return true;
    }

    /// <summary>
    /// Same check, but throws with the offending value named. Use when a rejected
    /// path means the whole operation should stop.
    /// </summary>
    public static string ResolveWithinOrThrow(string rootDirectory, string untrustedRelativePath)
    {
        if (TryResolveWithin(rootDirectory, untrustedRelativePath, out string resolved))
            return resolved;

        throw new UnauthorizedAccessException(
            $"Path '{untrustedRelativePath}' resolves outside '{rootDirectory}' and was rejected.");
    }
}
