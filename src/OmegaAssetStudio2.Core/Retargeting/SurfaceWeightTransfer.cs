using System.Numerics;
using OmegaAssetStudio2.Core.Meshes;

namespace OmegaAssetStudio2.Core.Retargeting;

/// <summary>What happened when skinning was taken from a nearby surface.</summary>
public sealed record SurfaceTransferReport
{
    public required int VerticesBound { get; init; }
    public required int VerticesUnbound { get; init; }

    /// <summary>How far the average vertex was from the surface it took its skinning from.</summary>
    public required float AverageDistance { get; init; }

    /// <summary>How far the furthest one was.</summary>
    public required float LargestDistance { get; init; }

    public override string ToString() =>
        $"{VerticesBound:N0} bound, {VerticesUnbound:N0} unbound, " +
        $"{AverageDistance:0.##} away on average, {LargestDistance:0.##} at most";
}

/// <summary>Skinning taken from a nearby surface.</summary>
public sealed record SurfaceTransferResult
{
    public required IReadOnlyList<VertexInfluence> Influences { get; init; }
    public required SurfaceTransferReport Report { get; init; }
}

/// <summary>
/// Gives a model skinning by copying it from the nearest part of another model.
/// </summary>
/// <remarks>
/// This is for a model whose own bones cannot be used: one with no skeleton at
/// all, or whose bone names have nothing to do with the target's. Instead of
/// matching names, each vertex is bound the way the target's surface is bound
/// at the closest point to it.
/// <para>
/// It is the weaker of the two ways. A vertex takes whatever is nearest, so a
/// sleeve close to a body binds to the body, and anything far from the target's
/// surface — a cape held away from the back, a held weapon — is bound by
/// whatever happens to be nearest rather than by what it belongs to. How far
/// each vertex had to reach is reported, because that distance is the warning.
/// </para>
/// </remarks>
public static class SurfaceWeightTransfer
{
    /// <summary>Bones a vertex may end up following.</summary>
    private const int MaxInfluences = 4;

    /// <summary>
    /// Binds a model to a skeleton by copying the skinning of the nearest
    /// surface.
    /// </summary>
    /// <param name="positions">The model being bound.</param>
    /// <param name="target">The already-skinned model to copy from.</param>
    public static SurfaceTransferResult Apply(IReadOnlyList<Vector3> positions, SkeletalMeshLod target)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(target);

        var influences = new VertexInfluence[positions.Count];

        if (target.Indices.Count < 3 || target.Positions.Count == 0)
        {
            // Nothing to copy from. Every vertex is left unbound and said to be,
            // rather than bound to whatever the first bone happens to be.
            for (int i = 0; i < influences.Length; i++)
                influences[i] = new VertexInfluence { Bones = [], Weights = [] };

            return new SurfaceTransferResult
            {
                Influences = influences,
                Report = new SurfaceTransferReport
                {
                    VerticesBound = 0,
                    VerticesUnbound = positions.Count,
                    AverageDistance = 0f,
                    LargestDistance = 0f,
                },
            };
        }

        var grid = TriangleGrid.Build(target);

        int bound = 0;
        double totalDistance = 0;
        float largest = 0f;

        for (int v = 0; v < positions.Count; v++)
        {
            (int triangle, Vector3 point) = grid.Nearest(positions[v]);

            if (triangle < 0)
            {
                influences[v] = new VertexInfluence { Bones = [], Weights = [] };
                continue;
            }

            int at = triangle * 3;

            influences[v] = Blend(
                target,
                target.Indices[at], target.Indices[at + 1], target.Indices[at + 2],
                point);

            float distance = Vector3.Distance(positions[v], point);
            totalDistance += distance;
            if (distance > largest) largest = distance;

            bound++;
        }

        return new SurfaceTransferResult
        {
            Influences = influences,
            Report = new SurfaceTransferReport
            {
                VerticesBound = bound,
                VerticesUnbound = positions.Count - bound,
                AverageDistance = bound == 0 ? 0f : (float)(totalDistance / bound),
                LargestDistance = largest,
            },
        };
    }

    /// <summary>
    /// Mixes the skinning of a triangle's three corners by how close the point
    /// is to each.
    /// </summary>
    private static VertexInfluence Blend(SkeletalMeshLod target, int a, int b, int c, Vector3 point)
    {
        Vector3 weights = Barycentric(target.Positions[a], target.Positions[b], target.Positions[c], point);

        var gathered = new Dictionary<int, float>(MaxInfluences * 3);

        Add(gathered, target, a, weights.X);
        Add(gathered, target, b, weights.Y);
        Add(gathered, target, c, weights.Z);

        if (gathered.Count == 0) return new VertexInfluence { Bones = [], Weights = [] };

        // Only the strongest few are kept, because that is all the game's own
        // format stores; the rest are dropped and what remains made up to one.
        List<KeyValuePair<int, float>> strongest = gathered
            .OrderByDescending(g => g.Value)
            .Take(MaxInfluences)
            .ToList();

        float total = strongest.Sum(g => g.Value);

        var bones = new List<int>(strongest.Count);
        var strengths = new List<float>(strongest.Count);

        foreach ((int bone, float weight) in strongest)
        {
            bones.Add(bone);
            strengths.Add(total > 0.0001f ? weight / total : 1f / strongest.Count);
        }

        return new VertexInfluence { Bones = bones, Weights = strengths };
    }

    private static void Add(Dictionary<int, float> into, SkeletalMeshLod target, int vertex, float share)
    {
        if (share <= 0f || vertex < 0 || vertex >= target.Influences.Count) return;

        VertexInfluence influence = target.Influences[vertex];

        for (int i = 0; i < influence.Count; i++)
        {
            int bone = influence.Bones[i];
            into[bone] = into.GetValueOrDefault(bone) + (influence.Weights[i] * share);
        }
    }

    /// <summary>
    /// How much of a point belongs to each corner of the triangle holding it.
    /// </summary>
    private static Vector3 Barycentric(Vector3 a, Vector3 b, Vector3 c, Vector3 point)
    {
        Vector3 ab = b - a, ac = c - a, ap = point - a;

        float d00 = Vector3.Dot(ab, ab);
        float d01 = Vector3.Dot(ab, ac);
        float d11 = Vector3.Dot(ac, ac);
        float d20 = Vector3.Dot(ap, ab);
        float d21 = Vector3.Dot(ap, ac);

        float denominator = (d00 * d11) - (d01 * d01);

        // A triangle with no area gives no answer; its first corner takes it.
        if (MathF.Abs(denominator) < 1e-12f) return new Vector3(1f, 0f, 0f);

        float v = ((d11 * d20) - (d01 * d21)) / denominator;
        float w = ((d00 * d21) - (d01 * d20)) / denominator;

        return new Vector3(1f - v - w, v, w);
    }

    /// <summary>Nearest point on a triangle to a point in space.</summary>
    internal static Vector3 ClosestOnTriangle(Vector3 a, Vector3 b, Vector3 c, Vector3 point)
    {
        // Checked against each corner, each edge, then the face. Taken in that
        // order because a point outside the triangle is nearest to its rim, and
        // projecting straight onto the face would put it outside the triangle.
        Vector3 ab = b - a, ac = c - a, ap = point - a;

        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f) return a;

        Vector3 bp = point - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3) return b;

        float vc = (d1 * d4) - (d3 * d2);
        if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + (ab * (d1 / (d1 - d3)));

        Vector3 cp = point - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6) return c;

        float vb = (d5 * d2) - (d1 * d6);
        if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + (ac * (d2 / (d2 - d6)));

        float va = (d3 * d6) - (d5 * d4);
        if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
            return b + ((c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6))));

        float denominator = 1f / (va + vb + vc);

        return a + (ab * (vb * denominator)) + (ac * (vc * denominator));
    }
}

/// <summary>
/// The target's triangles sorted into boxes, so the nearest one can be found
/// without measuring against all of them.
/// </summary>
/// <remarks>
/// A character has tens of thousands of triangles and a model being bound has
/// tens of thousands of vertices. Comparing every pair would be hundreds of
/// millions of measurements; searching outward from the box a vertex sits in
/// reaches the answer after a handful.
/// </remarks>
internal sealed class TriangleGrid
{
    private readonly SkeletalMeshLod _target;
    private readonly Dictionary<(int X, int Y, int Z), List<int>> _boxes;
    private readonly float _boxSize;

    /// <summary>How far the filled boxes reach from the middle, in boxes.</summary>
    private readonly int _reach;

    private TriangleGrid(SkeletalMeshLod target, Dictionary<(int, int, int), List<int>> boxes, float boxSize)
    {
        _target = target;
        _boxes = boxes;
        _boxSize = boxSize;

        // How far the grid actually reaches, in boxes. Searching further than
        // this can never find anything, because there is nothing out there.
        _reach = boxes.Count == 0
            ? 1
            : Math.Max(
                Math.Max(
                    boxes.Keys.Max(k => Math.Abs(k.Item1)),
                    boxes.Keys.Max(k => Math.Abs(k.Item2))),
                boxes.Keys.Max(k => Math.Abs(k.Item3))) + 1;
    }

    public static TriangleGrid Build(SkeletalMeshLod target)
    {
        int triangles = target.Indices.Count / 3;

        var lowest = new Vector3(float.MaxValue);
        var highest = new Vector3(float.MinValue);

        foreach (Vector3 position in target.Positions)
        {
            lowest = Vector3.Min(lowest, position);
            highest = Vector3.Max(highest, position);
        }

        // Sized so a box holds a handful of triangles: too large and each search
        // measures against many, too small and the search walks many boxes.
        float span = Math.Max(0.001f, (highest - lowest).Length());
        float boxSize = Math.Max(0.001f, span / MathF.Cbrt(Math.Max(1, triangles)) * 2f);

        var boxes = new Dictionary<(int, int, int), List<int>>(triangles);

        for (int t = 0; t < triangles; t++)
        {
            int at = t * 3;

            Vector3 centre = (
                target.Positions[target.Indices[at]] +
                target.Positions[target.Indices[at + 1]] +
                target.Positions[target.Indices[at + 2]]) / 3f;

            (int, int, int) key = KeyOf(centre, boxSize);

            if (!boxes.TryGetValue(key, out List<int>? list))
            {
                list = [];
                boxes[key] = list;
            }

            list.Add(t);
        }

        return new TriangleGrid(target, boxes, boxSize);
    }

    private static (int X, int Y, int Z) KeyOf(Vector3 position, float boxSize) => (
        (int)MathF.Floor(position.X / boxSize),
        (int)MathF.Floor(position.Y / boxSize),
        (int)MathF.Floor(position.Z / boxSize));

    /// <summary>
    /// The triangle nearest a point, and the nearest place on it.
    /// </summary>
    /// <remarks>
    /// Searched in widening rings of boxes, stopping once a ring cannot hold
    /// anything closer than what has already been found. Without that check a
    /// nearer triangle one ring out would be missed.
    /// </remarks>
    public (int Triangle, Vector3 Point) Nearest(Vector3 point)
    {
        (int x, int y, int z) = KeyOf(point, _boxSize);

        int best = -1;
        Vector3 bestPoint = Vector3.Zero;
        float bestDistance = float.MaxValue;

        // A handful of rings, no more. Walking outwards is only worth it while
        // the answer is close: ring 8 is already four and a half thousand
        // boxes, and a point far outside the grid — a model that came back a
        // hundred times too large, say — never reaches anything however far it
        // walks. Beyond this the whole surface is measured instead, which is
        // one pass over the triangles and cannot run away.
        int furthest = Math.Min(8, _reach);

        for (int ring = 0; ring <= furthest; ring++)
        {
            // Anything in this ring is at least this far away, so once the ring
            // starts further out than the best answer, there is nothing left.
            if (best >= 0 && (ring - 1) * _boxSize > bestDistance) break;

            bool any = false;

            for (int dx = -ring; dx <= ring; dx++)
            for (int dy = -ring; dy <= ring; dy++)
            for (int dz = -ring; dz <= ring; dz++)
            {
                // Only the shell of the ring; everything inside was searched.
                if (ring > 0 && Math.Abs(dx) != ring && Math.Abs(dy) != ring && Math.Abs(dz) != ring) continue;

                if (!_boxes.TryGetValue((x + dx, y + dy, z + dz), out List<int>? triangles)) continue;

                any = true;

                foreach (int t in triangles)
                {
                    int at = t * 3;

                    Vector3 on = SurfaceWeightTransfer.ClosestOnTriangle(
                        _target.Positions[_target.Indices[at]],
                        _target.Positions[_target.Indices[at + 1]],
                        _target.Positions[_target.Indices[at + 2]],
                        point);

                    float distance = Vector3.Distance(point, on);

                    if (distance >= bestDistance) continue;

                    bestDistance = distance;
                    bestPoint = on;
                    best = t;
                }
            }

            _ = any;
        }

        // A vertex far from everything — a weapon held away from the body, or a
        // model placed to one side — outruns the widening search. It still has
        // a nearest triangle, so it is found the slow way rather than left
        // following nothing, which would collapse it to the origin.
        return best >= 0 ? (best, bestPoint) : NearestAnywhere(point);
    }

    /// <summary>The nearest triangle of all of them, measured one by one.</summary>
    private (int Triangle, Vector3 Point) NearestAnywhere(Vector3 point)
    {
        int triangles = _target.Indices.Count / 3;

        int best = -1;
        Vector3 bestPoint = Vector3.Zero;
        float bestDistance = float.MaxValue;

        for (int t = 0; t < triangles; t++)
        {
            int at = t * 3;

            Vector3 on = SurfaceWeightTransfer.ClosestOnTriangle(
                _target.Positions[_target.Indices[at]],
                _target.Positions[_target.Indices[at + 1]],
                _target.Positions[_target.Indices[at + 2]],
                point);

            float distance = Vector3.Distance(point, on);
            if (distance >= bestDistance) continue;

            bestDistance = distance;
            bestPoint = on;
            best = t;
        }

        // Nothing near enough to be found by walking outwards, so the surface
        // is measured in full. This is what happens to a point that sits well
        // outside the model, and it is bounded by the number of triangles.
        return best >= 0 ? (best, bestPoint) : Everywhere(point);
    }

    /// <summary>The nearest point on the whole surface, measured directly.</summary>
    private (int Triangle, Vector3 Point) Everywhere(Vector3 point)
    {
        int best = -1;
        Vector3 bestPoint = Vector3.Zero;
        float bestDistance = float.MaxValue;

        int triangles = _target.Indices.Count / 3;

        for (int t = 0; t < triangles; t++)
        {
            int at = t * 3;

            Vector3 on = SurfaceWeightTransfer.ClosestOnTriangle(
                _target.Positions[_target.Indices[at]],
                _target.Positions[_target.Indices[at + 1]],
                _target.Positions[_target.Indices[at + 2]],
                point);

            float distance = Vector3.DistanceSquared(on, point);
            if (distance >= bestDistance) continue;

            bestDistance = distance;
            bestPoint = on;
            best = t;
        }

        return (best, bestPoint);
    }
}
