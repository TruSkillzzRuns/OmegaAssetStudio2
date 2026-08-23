using System;
using OmegaAssetStudio2.Core.Workspace;
using Xunit;

namespace OmegaAssetStudio2.Core.Tests.Workspace;

public sealed class PathGuardTests
{
    private const string Root = @"C:\Games\Example\Data";

    [Theory]
    [InlineData("textures/hero.upk")]
    [InlineData(@"textures\hero.upk")]
    [InlineData("hero.upk")]
    [InlineData("a/b/c/deep.upk")]
    public void AcceptsPathsInsideTheRoot(string relative)
    {
        Assert.True(PathGuard.TryResolveWithin(Root, relative, out string resolved));
        Assert.StartsWith(Root, resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../escape.upk")]
    [InlineData("../../../../Windows/System32/evil.dll")]
    [InlineData(@"..\..\escape.upk")]
    [InlineData("textures/../../escape.upk")]
    public void RejectsTraversalOutOfTheRoot(string relative)
    {
        Assert.False(PathGuard.TryResolveWithin(Root, relative, out string resolved));
        Assert.Equal(string.Empty, resolved);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\evil.dll")]
    [InlineData(@"\\server\share\evil.dll")]
    [InlineData("/etc/passwd")]
    public void RejectsRootedPaths(string absolute)
    {
        // Path.Combine returns a rooted second argument verbatim. This is the
        // exact shape that made v1's archive extractor able to write anywhere.
        Assert.False(PathGuard.TryResolveWithin(Root, absolute, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void RejectsEmptyInput(string? relative)
    {
        Assert.False(PathGuard.TryResolveWithin(Root, relative!, out _));
    }

    [Fact]
    public void RejectsSiblingDirectoryWithSharedPrefix()
    {
        // "C:\Games\Example\DataBackup" starts with "C:\Games\Example\Data" as a
        // plain string. The separator-aware comparison must not be fooled.
        Assert.False(PathGuard.TryResolveWithin(Root, @"..\DataBackup\x.upk", out _));
    }

    [Fact]
    public void ThrowingOverloadNamesTheOffendingPath()
    {
        var ex = Assert.Throws<UnauthorizedAccessException>(
            () => PathGuard.ResolveWithinOrThrow(Root, "../escape.upk"));
        Assert.Contains("escape.upk", ex.Message);
    }
}
