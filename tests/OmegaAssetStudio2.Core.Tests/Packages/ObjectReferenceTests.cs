using System;
using OmegaAssetStudio2.Core.Packages;
using Xunit;

namespace OmegaAssetStudio2.Core.Tests.Packages;

public sealed class ObjectReferenceTests
{
    [Fact]
    public void ZeroIsNull()
    {
        var reference = new ObjectReference(0);

        Assert.True(reference.IsNull);
        Assert.False(reference.IsExport);
        Assert.False(reference.IsImport);
        Assert.Equal("null", reference.ToString());
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(1666, 1665)]
    public void PositiveIsAnExportOffsetByOne(int value, int expectedIndex)
    {
        var reference = new ObjectReference(value);

        Assert.True(reference.IsExport);
        Assert.Equal(expectedIndex, reference.ExportIndex);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(-2, 1)]
    [InlineData(-125, 124)]
    public void NegativeIsAnImportOffsetByOne(int value, int expectedIndex)
    {
        var reference = new ObjectReference(value);

        Assert.True(reference.IsImport);
        Assert.Equal(expectedIndex, reference.ImportIndex);
    }

    [Fact]
    public void AskingForTheWrongTableThrowsRatherThanReturningAWrongIndex()
    {
        // Silently returning the wrong index would resolve to a real but
        // unrelated object, which is far worse than failing.
        Assert.Throws<InvalidOperationException>(() => new ObjectReference(-1).ExportIndex);
        Assert.Throws<InvalidOperationException>(() => new ObjectReference(1).ImportIndex);
        Assert.Throws<InvalidOperationException>(() => ObjectReference.Null.ExportIndex);
        Assert.Throws<InvalidOperationException>(() => ObjectReference.Null.ImportIndex);
    }

    [Fact]
    public void DescribesItselfForDiagnostics()
    {
        Assert.Equal("export[0]", new ObjectReference(1).ToString());
        Assert.Equal("import[0]", new ObjectReference(-1).ToString());
    }
}
