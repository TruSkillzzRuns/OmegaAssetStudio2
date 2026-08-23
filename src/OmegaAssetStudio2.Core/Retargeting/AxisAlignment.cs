using System.Numerics;

namespace OmegaAssetStudio2.Core.Retargeting;

/// <summary>How a model's axes have to be rearranged to match the game's.</summary>
public sealed record AxisAlignment
{
    /// <summary>The rearrangement itself.</summary>
    public required Matrix4x4 Transform { get; init; }

    /// <summary>How far apart the skeletons still are afterwards, per bone.</summary>
    public required float Error { get; init; }

    /// <summary>How far apart they were before it.</summary>
    public required float ErrorBefore { get; init; }

    /// <summary>
    /// True when the rearrangement turns the model inside out, which happens
    /// whenever two axes are swapped rather than turned into one another. The
    /// surface then has to be turned back the other way or the model renders
    /// inside out.
    /// </summary>
    public required bool Mirrors { get; init; }

    /// <summary>True when the model was already in the game's axes.</summary>
    public bool IsIdentity => Transform.IsIdentity;

    /// <summary>How to say what it does, in axes.</summary>
    public required string Description { get; init; }

    public override string ToString() => Description;
}

/// <summary>
/// Works out how a model's idea of up and forward relates to the game's.
/// </summary>
/// <remarks>
/// Modelling tools disagree about which way is up, and a file records nothing
/// that settles it. What does settle it is the skeleton: when the same joints
/// are named on both sides, where each holds them says exactly how the two
/// coordinate systems relate.
/// <para>
/// Found by measurement, not assumption. Every way of rearranging three axes is
/// tried — twenty-four turns and the twenty-four mirrored ones — and the one
/// that puts the joints closest together wins. On a real case this went from
/// bones sitting ninety units apart to under a hundredth of a unit, which is
/// the difference between a mangled model and a correct one.
/// </para>
/// </remarks>
public static class AxisAligner
{
    /// <summary>
    /// Finds the rearrangement that best lines a model's skeleton up with the
    /// game's.
    /// </summary>
    /// <param name="source">Where the model holds each matched joint.</param>
    /// <param name="target">Where the game holds the same joints.</param>
    public static AxisAlignment Find(IReadOnlyList<Vector3> source, IReadOnlyList<Vector3> target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        int count = Math.Min(source.Count, target.Count);

        Matrix4x4 best = Matrix4x4.Identity;
        float bestError = float.MaxValue;

        float identityError = ErrorOf(Matrix4x4.Identity, source, target, count);

        foreach (Matrix4x4 candidate in Candidates())
        {
            float error = ErrorOf(candidate, source, target, count);

            if (error >= bestError) continue;

            bestError = error;
            best = candidate;
        }

        return new AxisAlignment
        {
            Transform = best,
            Error = bestError,
            ErrorBefore = identityError,
            Mirrors = best.GetDeterminant() < 0f,
            Description = Describe(best),
        };
    }

    private static float ErrorOf(Matrix4x4 transform, IReadOnlyList<Vector3> source, IReadOnlyList<Vector3> target, int count)
    {
        if (count == 0) return float.MaxValue;

        double total = 0;

        for (int i = 0; i < count; i++)
            total += Vector3.Distance(Vector3.Transform(source[i], transform), target[i]);

        return (float)(total / count);
    }

    /// <summary>
    /// Every way three axes can be rearranged: which axis each becomes, and
    /// whether it points the same way or the opposite.
    /// </summary>
    private static IEnumerable<Matrix4x4> Candidates()
    {
        int[][] orders =
        [
            [0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0],
        ];

        foreach (int[] order in orders)
        {
            for (int signs = 0; signs < 8; signs++)
            {
                float x = (signs & 1) == 0 ? 1f : -1f;
                float y = (signs & 2) == 0 ? 1f : -1f;
                float z = (signs & 4) == 0 ? 1f : -1f;

                var matrix = new Matrix4x4();
                Set(ref matrix, 0, order[0], x);
                Set(ref matrix, 1, order[1], y);
                Set(ref matrix, 2, order[2], z);
                matrix.M44 = 1f;

                yield return matrix;
            }
        }
    }

    /// <summary>Sends one axis to another, with a sign.</summary>
    private static void Set(ref Matrix4x4 matrix, int from, int to, float sign)
    {
        switch (from)
        {
            case 0:
                if (to == 0) matrix.M11 = sign; else if (to == 1) matrix.M12 = sign; else matrix.M13 = sign;
                break;
            case 1:
                if (to == 0) matrix.M21 = sign; else if (to == 1) matrix.M22 = sign; else matrix.M23 = sign;
                break;
            default:
                if (to == 0) matrix.M31 = sign; else if (to == 1) matrix.M32 = sign; else matrix.M33 = sign;
                break;
        }
    }

    private static string Describe(Matrix4x4 m)
    {
        if (m.IsIdentity) return "already in the game's axes";

        string x = Axis(m.M11, m.M12, m.M13);
        string y = Axis(m.M21, m.M22, m.M23);
        string z = Axis(m.M31, m.M32, m.M33);

        return $"the model's X, Y and Z become {x}, {y} and {z}";
    }

    private static string Axis(float toX, float toY, float toZ)
    {
        if (MathF.Abs(toX) > 0.5f) return toX > 0 ? "X" : "-X";
        if (MathF.Abs(toY) > 0.5f) return toY > 0 ? "Y" : "-Y";

        return toZ > 0 ? "Z" : "-Z";
    }
}
