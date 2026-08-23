using System.Numerics;
using OmegaAssetStudio2.Core.Meshes;

namespace OmegaAssetStudio2.Core.Retargeting;

/// <summary>
/// Puts right the things that are commonly wrong with a model brought in from
/// a modelling tool.
/// </summary>
/// <remarks>
/// Every fault here was met on a real file. None of them is the user's mistake
/// so much as a disagreement between tools, and each has one sensible answer —
/// so the answer is applied rather than reported and left.
/// <list type="bullet">
///   <item>Weights that do not add to a whole, which shrinks a vertex toward
///   the model's origin when it is posed.</item>
///   <item>More bones on a vertex than the game stores, where the extra ones
///   are dropped and what remains has to be shared out again.</item>
///   <item>Triangles with no area, which draw nothing and can upset the
///   surface frames worked out around them.</item>
///   <item>Triangles naming a vertex the model does not have.</item>
/// </list>
/// Size, facing and vertices with no bone at all are put right by the retarget
/// itself, because each needs the target to measure against.
/// </remarks>
public static class ModelRepair
{
    /// <summary>The most bones the game stores for one vertex.</summary>
    private const int MostBones = 4;

    /// <summary>
    /// Repairs a model, describing anything it had to change.
    /// </summary>
    public static SkeletalMeshLod Apply(SkeletalMeshLod lod, ICollection<ModelFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(lod);
        ArgumentNullException.ThrowIfNull(findings);

        SkeletalMeshLod repaired = Weights(lod, findings);

        return Triangles(repaired, findings);
    }

    /// <summary>
    /// Softens the skin where it breaks abruptly from one vertex to the next.
    /// </summary>
    /// <remarks>
    /// A joint bends smoothly because the vertices across it share bones and
    /// hand over gradually. Where two neighbouring vertices follow entirely
    /// different bones the surface folds along that line instead — which is a
    /// crease at the knee, or a shoulder that comes apart.
    /// <para>
    /// Measured against the game's own model, which is the standard worth
    /// reaching rather than perfection: it breaks across 0.04% of its edges,
    /// where a model fitted from a modelling tool broke across 0.20%. Only
    /// vertices on such an edge are touched, and only by mixing in what their
    /// neighbours already say, so weights that were painted deliberately
    /// elsewhere are left exactly as they are.
    /// </para>
    /// </remarks>
    public static SkeletalMeshLod Soften(
        SkeletalMeshLod lod, int passes, ICollection<ModelFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(lod);

        var neighbours = Neighbours(lod);

        IReadOnlyList<VertexInfluence> influences = lod.Influences;

        int breaksBefore = Breaks(lod, influences);
        if (breaksBefore == 0) return lod;

        for (int pass = 0; pass < passes; pass++)
        {
            var next = influences.ToArray();

            for (int v = 0; v < influences.Count; v++)
            {
                if (!SitsOnABreak(v, influences, neighbours)) continue;

                var gathered = new Dictionary<int, float>();

                Add(gathered, influences[v], 1f);

                foreach (int n in neighbours[v]) Add(gathered, influences[n], 1f);

                var strongest = gathered.OrderByDescending(p => p.Value).Take(MostBones).ToList();
                float total = strongest.Sum(p => p.Value);

                if (total <= 0f) continue;

                next[v] = new VertexInfluence
                {
                    Bones = strongest.Select(p => p.Key).ToList(),
                    Weights = strongest.Select(p => p.Value / total).ToList(),
                };
            }

            influences = next;
        }

        int breaksAfter = Breaks(lod, influences);

        if (breaksAfter < breaksBefore)
        {
            findings.Add(new ModelFinding
            {
                Kind = FindingKind.Mended,
                What = $"The skin broke abruptly across {breaksBefore:N0} edges",
                Detail =
                    $"Softened to {breaksAfter:N0} by mixing in what the neighbouring vertices already " +
                    "say. Two vertices side by side following entirely different bones make the surface " +
                    "fold along that line — a crease at the knee, or a shoulder that comes apart — " +
                    "instead of bending. Only those places were touched.",
            });
        }

        return lod with { Influences = influences };
    }

    private static void Add(Dictionary<int, float> into, VertexInfluence influence, float say)
    {
        for (int i = 0; i < influence.Bones.Count; i++)
        {
            into.TryGetValue(influence.Bones[i], out float already);
            into[influence.Bones[i]] = already + (influence.Weights[i] * say);
        }
    }

    private static bool SitsOnABreak(
        int vertex, IReadOnlyList<VertexInfluence> influences, IReadOnlyList<List<int>> neighbours)
    {
        VertexInfluence mine = influences[vertex];
        if (mine.Bones.Count == 0) return false;

        foreach (int n in neighbours[vertex])
        {
            VertexInfluence theirs = influences[n];

            if (theirs.Bones.Count > 0 && !mine.Bones.Any(b => theirs.Bones.Contains(b))) return true;
        }

        return false;
    }

    private static int Breaks(SkeletalMeshLod lod, IReadOnlyList<VertexInfluence> influences)
    {
        int breaks = 0;

        for (int t = 0; t + 2 < lod.Indices.Count; t += 3)
        {
            for (int e = 0; e < 3; e++)
            {
                VertexInfluence a = influences[lod.Indices[t + e]];
                VertexInfluence b = influences[lod.Indices[t + ((e + 1) % 3)]];

                if (a.Bones.Count == 0 || b.Bones.Count == 0) continue;

                if (!a.Bones.Any(bone => b.Bones.Contains(bone))) breaks++;
            }
        }

        return breaks;
    }

    /// <summary>Which vertices each vertex shares a triangle edge with.</summary>
    private static List<int>[] Neighbours(SkeletalMeshLod lod)
    {
        var neighbours = new List<int>[lod.Influences.Count];
        for (int i = 0; i < neighbours.Length; i++) neighbours[i] = [];

        void Join(int a, int b)
        {
            if (a < 0 || b < 0 || a >= neighbours.Length || b >= neighbours.Length || a == b) return;

            if (!neighbours[a].Contains(b)) neighbours[a].Add(b);
            if (!neighbours[b].Contains(a)) neighbours[b].Add(a);
        }

        for (int t = 0; t + 2 < lod.Indices.Count; t += 3)
        {
            Join(lod.Indices[t], lod.Indices[t + 1]);
            Join(lod.Indices[t + 1], lod.Indices[t + 2]);
            Join(lod.Indices[t], lod.Indices[t + 2]);
        }

        return neighbours;
    }

    /// <summary>
    /// Shares each vertex's weights out again so they add to a whole, keeping
    /// only as many bones as the game stores.
    /// </summary>
    private static SkeletalMeshLod Weights(SkeletalMeshLod lod, ICollection<ModelFinding> findings)
    {
        var influences = new VertexInfluence[lod.Influences.Count];

        int tooMany = 0, notAWhole = 0;

        for (int v = 0; v < lod.Influences.Count; v++)
        {
            VertexInfluence influence = lod.Influences[v];

            if (influence.Bones.Count == 0)
            {
                influences[v] = influence;
                continue;
            }

            // Strongest first, so anything dropped is the least missed.
            var ordered = influence.Bones
                .Select((bone, i) => (Bone: bone, Weight: influence.Weights[i]))
                .Where(p => p.Weight > 0f)
                .OrderByDescending(p => p.Weight)
                .ToList();

            if (ordered.Count > MostBones)
            {
                tooMany++;
                ordered = ordered.Take(MostBones).ToList();
            }

            float total = ordered.Sum(p => p.Weight);

            if (total <= 0f)
            {
                influences[v] = new VertexInfluence { Bones = [], Weights = [] };
                continue;
            }

            if (MathF.Abs(total - 1f) > 0.001f) notAWhole++;

            influences[v] = new VertexInfluence
            {
                Bones = ordered.Select(p => p.Bone).ToList(),
                Weights = ordered.Select(p => p.Weight / total).ToList(),
            };
        }

        if (tooMany > 0)
        {
            findings.Add(new ModelFinding
            {
                Kind = FindingKind.Mended,
                What = $"{tooMany:N0} vertices followed more than {MostBones} bones",
                Detail =
                    $"The game stores {MostBones} for a vertex, so the {MostBones} strongest were kept and " +
                    "shared out again. The dropped ones were the weakest, so the difference is slight.",
            });
        }

        if (notAWhole > 0)
        {
            findings.Add(new ModelFinding
            {
                Kind = FindingKind.Mended,
                What = $"{notAWhole:N0} vertices had weights that did not add to a whole",
                Detail =
                    "Shared out again so they do. A vertex adding to less than a whole shrinks toward the " +
                    "model's origin when it is posed, and one adding to more stretches away from it.",
            });
        }

        return lod with { Influences = influences };
    }

    /// <summary>
    /// Drops triangles that draw nothing, and any naming a vertex that is not
    /// there.
    /// </summary>
    private static SkeletalMeshLod Triangles(SkeletalMeshLod lod, ICollection<ModelFinding> findings)
    {
        var kept = new List<int>(lod.Indices.Count);

        int outside = 0, flat = 0;

        for (int t = 0; t + 2 < lod.Indices.Count; t += 3)
        {
            int a = lod.Indices[t], b = lod.Indices[t + 1], c = lod.Indices[t + 2];

            if (a < 0 || b < 0 || c < 0 ||
                a >= lod.Positions.Count || b >= lod.Positions.Count || c >= lod.Positions.Count)
            {
                outside++;
                continue;
            }

            // A triangle with two corners in the same place, or three corners in
            // a line, covers no area at all.
            if (a == b || b == c || a == c ||
                Vector3.Cross(lod.Positions[b] - lod.Positions[a],
                              lod.Positions[c] - lod.Positions[a]).LengthSquared() < 1e-12f)
            {
                flat++;
                continue;
            }

            kept.Add(a);
            kept.Add(b);
            kept.Add(c);
        }

        if (outside > 0)
        {
            findings.Add(new ModelFinding
            {
                Kind = FindingKind.Mended,
                What = $"{outside:N0} triangles named a vertex the model does not have",
                Detail = "Dropped. They could not have been drawn, and would have been refused on writing.",
            });
        }

        if (flat > 0)
        {
            findings.Add(new ModelFinding
            {
                Kind = FindingKind.Mended,
                What = $"{flat:N0} triangles covered no area",
                Detail =
                    "Dropped. A triangle whose corners sit on top of each other or in a line draws nothing " +
                    "and has no direction of its own, which spoils the surface frames worked out around it.",
            });
        }

        return outside + flat > 0 ? lod with { Indices = kept } : lod;
    }
}
