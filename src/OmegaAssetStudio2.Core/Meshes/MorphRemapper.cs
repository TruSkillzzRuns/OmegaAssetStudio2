using System.Numerics;

namespace OmegaAssetStudio2.Core.Meshes;

/// <summary>What happened when a set of displacements was renumbered.</summary>
public sealed record MorphRemapReport
{
    public required string Name { get; init; }

    /// <summary>Displacements the model being replaced recorded.</summary>
    public required int Before { get; init; }

    /// <summary>Displacements written, which may be more where vertices split.</summary>
    public required int After { get; init; }

    /// <summary>Displacements with nowhere near enough to land on.</summary>
    public required int Lost { get; init; }

    /// <summary>
    /// Displacements that would have moved a vertex already moved by another,
    /// and were dropped rather than applied twice.
    /// </summary>
    public int Doubled { get; init; }

    /// <summary>How far the furthest one had to reach, in the model's own units.</summary>
    public required float FurthestReach { get; init; }
}

/// <summary>
/// Renumbers a power's displacements onto a rewritten model.
/// </summary>
/// <remarks>
/// Displacements name the vertices they move by number, and rewriting a model
/// renumbers its vertices — a run owns its own copy of anything two runs share,
/// so a model can come out with more vertices than it went in with. Left alone,
/// every displacement lands on a vertex it was never meant for: the model
/// stands correctly at rest and tears itself apart the moment the power fires.
/// <para>
/// They are matched by where the vertex was rather than by its number, because
/// number is exactly what has changed. A vertex that split into several copies
/// gets a displacement for each, or only part of the surface would move.
/// </para>
/// </remarks>
public static class MorphRemapper
{
    /// <summary>
    /// Renumbers one set of displacements from the model being replaced onto
    /// the model going in.
    /// </summary>
    /// <param name="original">The model being replaced, whose numbering they use.</param>
    /// <param name="written">Where each vertex of the new model sits.</param>
    /// <param name="target">The displacements to renumber.</param>
    public static (IReadOnlyList<MorphLevel> Levels, MorphRemapReport Report) Apply(
        SkeletalMeshLod original, IReadOnlyList<Vector3> written, MorphTarget target)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(written);
        ArgumentNullException.ThrowIfNull(target);

        // How far a vertex may have shifted and still be the same vertex. Taken
        // from the model's own size, so it holds whatever units it is in: an
        // edited costume sits within a whisker of the body it replaces, while
        // an unrelated model does not, and should lose the displacement rather
        // than have it land somewhere arbitrary.
        float reach = Span(original.Positions) * 0.02f;

        // Grouped by where they sit, not by which original each is nearest to.
        // A model splits a vertex wherever the surface needs two texture
        // coordinates in the same place, so several originals share a position
        // exactly — and asking each new vertex which original is nearest hands
        // them all to whichever came first, leaving the rest with nothing. On a
        // model that had not changed at all that lost a quarter of every power's
        // displacements.
        var atPlace = new Dictionary<(int, int, int), List<int>>();

        for (int v = 0; v < written.Count; v++)
        {
            (int, int, int) key = Key(written[v]);

            if (!atPlace.TryGetValue(key, out List<int>? here))
            {
                here = [];
                atPlace[key] = here;
            }

            here.Add(v);
        }

        var byPlace = new Dictionary<int, List<int>>();

        for (int o = 0; o < original.Positions.Count; o++)
        {
            if (atPlace.TryGetValue(Key(original.Positions[o]), out List<int>? exact))
            {
                byPlace[o] = exact;
                continue;
            }

            // Nothing in the same place, so the model has been edited here.
            // The nearest vertex takes it, if anything is near enough at all.
            int nearest = Nearest(written, original.Positions[o], out float distance);

            if (nearest >= 0 && distance <= reach) byPlace[o] = [nearest];
        }

        var levels = new List<MorphLevel>(target.Levels.Count);

        int before = 0, after = 0, lost = 0, doubled = 0;
        float furthest = 0f;

        foreach (MorphLevel level in target.Levels)
        {
            var moved = new List<MorphDelta>(level.Deltas.Count);

            // A vertex may be displaced once and once only. A model splits a
            // vertex wherever the surface needs a second texture coordinate in
            // the same place, and each copy carries its own displacement saying
            // the same thing — so handing every copy's displacement to every
            // vertex sharing that place moves it two, three, four times over.
            // That is a hand that shatters into spikes rather than closing, and
            // a power that bursts the model apart rather than inflating it.
            var already = new HashSet<int>();

            foreach (MorphDelta delta in level.Deltas)
            {
                before++;

                if (delta.Vertex < 0 || delta.Vertex >= original.Positions.Count)
                {
                    lost++;
                    continue;
                }

                // Every copy of that vertex moves, or the surface splits along
                // whichever seam the runs happened to fall on.
                if (byPlace.TryGetValue(delta.Vertex, out List<int>? copies))
                {
                    bool any = false;

                    foreach (int copy in copies)
                    {
                        if (!already.Add(copy)) continue;

                        moved.Add(delta with { Vertex = copy });
                        after++;
                        any = true;

                        furthest = MathF.Max(
                            furthest, Vector3.Distance(original.Positions[delta.Vertex], written[copy]));
                    }

                    if (!any) doubled++;
                }
                else
                {
                    lost++;
                }
            }

            // Put back in vertex order before writing. The game walks a shape's
            // displacements and the model's vertices together in one pass, each
            // list moving forward only, so it finds a displacement only while
            // both are climbing. Handed them out of order it stops finding them
            // and the rest of the shape does nothing.
            //
            // Renumbering breaks the order that the game's own files arrive in.
            // A vertex that split into copies contributes all of its copies at
            // the point its original displacement is read, and those copy
            // numbers belong wherever the split halves ended up - which is
            // rarely next in line. Measured on one import: the shipped
            // shapes are all in order, and the renumbered ones broke as early
            // as the second displacement (54 then 25), so the claws kept the
            // first two or three offsets and ignored the rest. That is a claw
            // that never retracts.
            moved.Sort(static (a, b) => a.Vertex.CompareTo(b.Vertex));

            levels.Add(new MorphLevel { Deltas = moved, BaseVertexCount = written.Count });
        }

        return (levels, new MorphRemapReport
        {
            Name = target.Name,
            Before = before,
            After = after,
            Lost = lost,
            Doubled = doubled,
            FurthestReach = furthest,
        });
    }

    /// <summary>
    /// A place, rounded finely enough that the same vertex written twice lands
    /// in the same bucket and a genuinely different one does not.
    /// </summary>
    private static (int, int, int) Key(Vector3 place) => (
        (int)MathF.Round(place.X * 1000f),
        (int)MathF.Round(place.Y * 1000f),
        (int)MathF.Round(place.Z * 1000f));

    private static int Nearest(IReadOnlyList<Vector3> among, Vector3 place, out float distance)
    {
        int nearest = -1;
        float best = float.MaxValue;

        for (int i = 0; i < among.Count; i++)
        {
            float d = Vector3.DistanceSquared(place, among[i]);
            if (d >= best) continue;

            best = d;
            nearest = i;
        }

        distance = nearest < 0 ? float.MaxValue : MathF.Sqrt(best);

        return nearest;
    }

    private static float Span(IReadOnlyList<Vector3> positions)
    {
        if (positions.Count == 0) return 0f;

        var lowest = new Vector3(float.MaxValue);
        var highest = new Vector3(float.MinValue);

        foreach (Vector3 position in positions)
        {
            lowest = Vector3.Min(lowest, position);
            highest = Vector3.Max(highest, position);
        }

        return (highest - lowest).Length();
    }
}
