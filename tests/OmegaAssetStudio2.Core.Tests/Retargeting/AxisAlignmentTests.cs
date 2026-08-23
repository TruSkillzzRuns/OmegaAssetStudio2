using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OmegaAssetStudio2.Core.Retargeting;
using Xunit;

namespace OmegaAssetStudio2.Core.Tests.Retargeting;

/// <summary>
/// Checks that a model's idea of up is worked out from the skeletons rather
/// than assumed.
/// </summary>
/// <remarks>
/// The numbers here are taken from a real case: a character exported to a file
/// by a tool that calls Y up, fitted onto the same character in a game that
/// calls Z up. Every bone name matched and the model still came out mangled,
/// because nothing had noticed the two disagreed about which way was up.
/// </remarks>
public sealed class AxisAlignmentTests
{
    /// <summary>Joints as the file held them, and as the game holds the same ones.</summary>
    private static (List<Vector3> Source, List<Vector3> Target) RealCase()
    {
        (float X, float Y, float Z)[] fromFile =
        [
            (0f, 0f, 0f),
            (5.66f, 55.9f, 5.45f),      // g_r_hip
            (4.68f, 33.91f, 6.56f),     // g_r_knee
            (-0.54f, 8.02f, 7.44f),     // g_r_ankle
            (5.66f, 55.9f, -5.33f),     // g_l_hip
            (4.68f, 33.91f, -6.45f),    // g_l_knee
            (4.81f, 63.84f, 0.06f),     // g_spine01
        ];

        var source = fromFile.Select(p => new Vector3(p.X, p.Y, p.Z)).ToList();

        // The game holds the same joints with Y and Z the other way round.
        var target = fromFile.Select(p => new Vector3(p.X, p.Z, p.Y)).ToList();

        return (source, target);
    }

    [Fact]
    public void TheSwapBetweenAYUpFileAndAZUpGameIsFound()
    {
        (List<Vector3> source, List<Vector3> target) = RealCase();

        AxisAlignment alignment = AxisAligner.Find(source, target);

        Assert.False(alignment.IsIdentity);

        // Nothing left over: the joints land exactly on top of one another.
        Assert.True(alignment.Error < 0.001f,
            $"joints are still {alignment.Error:0.###} apart after rearranging.");

        // The average across these joints, one of which is the root and sits at
        // the origin in both. Checked so the case is known to start badly
        // wrong, or it would not be testing anything.
        Assert.True(alignment.ErrorBefore > 20f,
            $"the case only starts {alignment.ErrorBefore:0.##} apart, which is not far enough to test.");
    }

    [Fact]
    public void SwappingTwoAxesIsReportedAsTurningTheModelInsideOut()
    {
        // Swapping two axes mirrors the model. Left unsaid, the surface faces
        // inwards and the model renders inside out.
        (List<Vector3> source, List<Vector3> target) = RealCase();

        Assert.True(AxisAligner.Find(source, target).Mirrors);
    }

    [Fact]
    public void AModelAlreadyInTheGamesAxesIsLeftAlone()
    {
        (List<Vector3> source, _) = RealCase();

        AxisAlignment alignment = AxisAligner.Find(source, source);

        Assert.True(alignment.IsIdentity);
        Assert.False(alignment.Mirrors);
        Assert.True(alignment.Error < 0.001f);
    }

    [Fact]
    public void ATurnRatherThanASwapIsNotReportedAsMirroring()
    {
        // Turning a model a quarter turn about X keeps it the right way out.
        var source = new List<Vector3> { new(1, 2, 3), new(-4, 5, -6), new(7, -8, 9) };
        var target = source.Select(v => new Vector3(v.X, -v.Z, v.Y)).ToList();

        AxisAlignment alignment = AxisAligner.Find(source, target);

        Assert.True(alignment.Error < 0.001f);
        Assert.False(alignment.Mirrors);
    }

    [Fact]
    public void TheRearrangementIsDescribedInAxes()
    {
        (List<Vector3> source, List<Vector3> target) = RealCase();

        // Said in terms a person can check against their exporter's settings.
        Assert.Contains("become", AxisAligner.Find(source, target).Description);
    }

    [Fact]
    public void SkeletonsThatCannotBeLinedUpAtAllSayHowFarOutTheyAre()
    {
        // No rearrangement of the axes fits these, so the best is still poor —
        // and the caller can see that rather than being told it worked.
        var source = new List<Vector3> { new(1, 0, 0), new(0, 1, 0), new(0, 0, 1) };
        var target = new List<Vector3> { new(50, 3, 9), new(-7, 40, 2), new(4, 4, 90) };

        Assert.True(AxisAligner.Find(source, target).Error > 10f);
    }
}
