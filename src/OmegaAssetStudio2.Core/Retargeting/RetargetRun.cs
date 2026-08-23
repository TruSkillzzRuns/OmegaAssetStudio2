using System.Numerics;
using OmegaAssetStudio2.Core.Meshes;

namespace OmegaAssetStudio2.Core.Retargeting;

/// <summary>What to do with the shape of the model being fitted.</summary>
public enum ShapeHandling
{
    /// <summary>
    /// Move the shape onto the skeleton's rest pose only where the two
    /// disagree by more than a hair.
    /// </summary>
    Decide,

    /// <summary>
    /// Leave every vertex where it is and move only the skinning — for a model
    /// exported from this very character and brought back unaltered.
    /// </summary>
    LeaveAlone,

    /// <summary>
    /// Move the shape onto the skeleton's rest pose whatever the measurement
    /// says.
    /// </summary>
    /// <remarks>
    /// The game poses a model from its skeleton's own rest pose, so one
    /// standing in a different pose is drawn wrongly however right it looks
    /// sitting still. Deciding automatically cannot catch that: two rigs can
    /// hold every joint in the same place while facing them differently, which
    /// measures as agreement and renders on its side.
    /// </remarks>
    FitToRestPose,
}

/// <summary>How a retarget should be carried out.</summary>
public sealed record RetargetOptions
{
    /// <summary>
    /// What to do with the model's shape. Left alone by default.
    /// </summary>
    /// <remarks>
    /// Version 1's importer, which produces models this game draws correctly,
    /// moves no vertices at all: it reads the file, turns it into the game's
    /// space, looks each bone up by name, and writes it. Taking a model as it
    /// comes is therefore the behaviour to match, and moving it about is the
    /// exception.
    /// </remarks>
    public ShapeHandling Shape { get; init; } = ShapeHandling.LeaveAlone;

    /// <summary>Turn every triangle around, for a model that renders inside out.</summary>
    public bool FlipWinding { get; init; }

    /// <summary>
    /// Use the model's own skinning, rebound onto the target skeleton by bone
    /// name. Off, the model has to be bound some other way, which this cannot
    /// yet do.
    /// </summary>
    public bool KeepSourceWeights { get; init; } = true;
}

/// <summary>Everything a retarget produced, including why it did what it did.</summary>
public sealed record RetargetOutcome
{
    /// <summary>The model as it arrived.</summary>
    public required SkeletalMeshLod Before { get; init; }

    /// <summary>The model fitted to the target skeleton.</summary>
    public required SkeletalMeshLod After { get; init; }

    public required BoneMap Map { get; init; }
    public required TransferReport Transfer { get; init; }

    /// <summary>How far vertices moved, or null when the geometry was left alone.</summary>
    public ConformResult? Conform { get; init; }

    /// <summary>Things the user should know, in the order they happened.</summary>
    public required IReadOnlyList<string> Log { get; init; }

    /// <summary>What was found wrong with the model, and what was done about it.</summary>
    public IReadOnlyList<ModelFinding> Findings { get; init; } = [];
}

/// <summary>Thrown when a retarget cannot be carried out at all.</summary>
public sealed class RetargetException : Exception
{
    public RetargetException(string message) : base(message) { }
}

/// <summary>
/// Fits a model brought in from a file onto a skeleton from the game.
/// </summary>
/// <remarks>
/// This does not write anything. It produces the fitted model and a record of
/// what happened, so the result can be judged before any of it is kept.
/// </remarks>
public static class RetargetRun
{
    /// <summary>
    /// Runs a retarget.
    /// </summary>
    /// <param name="source">The model brought in.</param>
    /// <param name="target">The model whose skeleton is being fitted to.</param>
    /// <param name="options">How to carry it out.</param>
    public static RetargetOutcome Run(SourceModel source, SkeletalMesh target, RetargetOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);

        var log = new List<string>();
        var findings = new List<ModelFinding>();

        source = ToTargetScale(source, target, log, findings);

        // The faults a model can have on its own, before anything is measured
        // against the target.
        source = source with { Geometry = ModelRepair.Apply(source.Geometry, findings) };

        SkeletalMeshLod before = source.Geometry;

        // A model with no skeleton has no weights to rebind, whatever was
        // asked for, so it takes the only path that can work.
        bool byName = options.KeepSourceWeights && source.HasSkeleton;

        if (options.KeepSourceWeights && !source.HasSkeleton)
        {
            log.Add("This model carries no skeleton, so it is bound from the target's surface instead.");

            findings.Add(new ModelFinding
            {
                Kind = FindingKind.Warned,
                What = "The model brought no skeleton",
                Detail =
                    "It was bound from the nearest point on the target's own surface instead, which is a " +
                    "poor substitute: near a joint the nearest surface is often the wrong limb, so elbows " +
                    "tear and hands and feet smear. Export it again with its armature included.",
            });
        }

        if (!byName) return FromSurface(source, target, before, log) with { Findings = findings };

        BoneMap map = BoneMap.Build(source.Bones, target.Bones);

        log.Add($"Matched {map.Pairs.Count} of {source.Bones.Count} bones to the target's {target.Bones.Count}.");

        if (map.Pairs.Count < source.Bones.Count)
        {
            findings.Add(new ModelFinding
            {
                Kind = FindingKind.Warned,
                What = $"{source.Bones.Count - map.Pairs.Count} of the model's bones match nothing on the target",
                Detail =
                    "Whatever they hold is bound to the nearest bone above them instead. Bones the game " +
                    "does not have cannot be posed, so anything relying on them will not move as intended.",
            });
        }

        log.Add(source.BonesWithBindPose > 0
            ? $"Fitting from the bind pose the file records for {source.BonesWithBindPose} of " +
              $"{source.Bones.Count} bones."
            : "The file records no bind pose, so the bone chain is used instead.");

        if (map.Pairs.Count == 0)
        {
            throw new RetargetException(
                "No bone of this model matches any bone of the target skeleton. " +
                "The two are probably unrelated rigs.");
        }

        foreach (IGrouping<MatchQuality, BonePair> group in map.Pairs.GroupBy(p => p.Quality).OrderBy(g => g.Key))
            log.Add($"  {group.Count()} matched by {Describe(group.Key)}.");

        // Which way the model calls up. Measured from where the two skeletons
        // hold the same joints, because nothing in a model file settles it and
        // the tools that write them disagree.
        SkeletonPose targetRest = SkeletonPose.Rest(target.Bones);

        AxisAlignment axes = AxisAligner.Find(
            map.Pairs.Select(p => source.Pose.PositionOf(p.SourceIndex)).ToList(),
            map.Pairs.Select(p => targetRest.PositionOf(p.TargetIndex)).ToList());

        // Turned to face the way the target does, whatever was asked for its
        // shape. Reading a file already exchanges the two upright axes, which
        // is a fixed convention; what is left over is how the model was
        // arranged before it was saved, and modelling tools let that be set
        // several ways. One real file came back a quarter turn round — 76 by 26
        // across where the target is 32 by 76, the same measurements on
        // different axes — and no one should have to guess an exporter setting
        // to correct that.
        //
        // Only a clear improvement counts. A model already facing the right way
        // measures slightly better under some rearrangement or other by chance,
        // and turning it on that basis would spoil one that was already right.
        if (!axes.IsIdentity && axes.Error < axes.ErrorBefore * 0.5f)
        {
            log.Add(
                $"Axes differ: {axes.Description}. Joints were {axes.ErrorBefore:0.##} apart, " +
                $"{axes.Error:0.###} after rearranging.");

            if (axes.Mirrors)
            {
                log.Add(
                    "  That rearrangement turns the model inside out, so its surface is turned back " +
                    "the other way to compensate.");
            }

            findings.Add(new ModelFinding
            {
                Kind = FindingKind.Mended,
                What = "The model faced a different way from the skeleton",
                Detail =
                    $"Turned to match: {axes.Description}. Its joints sat {axes.ErrorBefore:0.#} away from " +
                    $"the target's and now sit {axes.Error:0.###} away.",
            });

            source = Realign(source, axes);
        }
        else
        {
            log.Add($"Axes agree: joints are {axes.ErrorBefore:0.##} apart as they stand.");
        }

        before = source.Geometry;

        TransferResult transfer = WeightTransfer.Apply(before, source.Bones, map);

        transfer = transfer with
        {
            Influences = Anchored(before, transfer.Influences, target, log, findings),
        };

        MissingBones(source, target, findings);

        // Last, once every vertex has its bones settled: soften the places
        // where the skin still breaks from one vertex to the next.
        transfer = transfer with
        {
            Influences = ModelRepair
                .Soften(before with { Influences = transfer.Influences }, passes: 2, findings)
                .Influences,
        };

        log.Add(
            $"Skinning moved: {transfer.Report.VerticesKept:N0} kept, " +
            $"{transfer.Report.VerticesRerouted:N0} rerouted, {transfer.Report.VerticesDropped:N0} left loose.");

        foreach ((string bone, float weight) in transfer.Report.ReroutedFrom.OrderByDescending(r => r.Value).Take(8))
            log.Add($"  weight from {bone} went to the nearest bone above it ({weight:0.#}).");

        // Once the axes agree, the two skeletons may already hold every joint
        // in the same place — which is what a model exported from this same
        // character looks like. There is then nothing to fit, and fitting it
        // anyway is not harmless: the two rigs can hold a joint in the same
        // place while facing it differently, and conforming through those
        // frames turns the model about every joint. Measured on a real file,
        // that turned a correct model into a mangled one.
        float fits = JointDisagreement(source.Pose, targetRest, map);
        float allowed = Math.Max(0.01f, target.Bounds.Radius * 0.01f);

        bool alreadyFits = fits <= allowed;

        bool leaveShapeAlone = options.Shape switch
        {
            ShapeHandling.LeaveAlone => true,
            ShapeHandling.FitToRestPose => false,
            _ => alreadyFits,
        };

        log.Add(options.Shape switch
        {
            ShapeHandling.LeaveAlone =>
                $"Shape left alone, as asked. Joints are {fits:0.###} apart.",

            ShapeHandling.FitToRestPose =>
                $"Shape fitted to the skeleton's rest pose, as asked. Joints were {fits:0.###} apart " +
                (alreadyFits
                    ? "— close enough that this would have been skipped if left to decide."
                    : "beforehand."),

            _ when alreadyFits =>
                $"The model already sits on this skeleton: joints agree to within {fits:0.###}, so its " +
                "shape is left exactly as it is. If it renders on its side or bent in the game, choose " +
                "to fit it to the rest pose instead — two rigs can hold every joint in the same place " +
                "while facing them differently.",

            _ => $"Joints are {fits:0.###} apart, so the shape is fitted to the skeleton's rest pose.",
        });

        if (leaveShapeAlone)
        {
            return new RetargetOutcome
            {
                Before = before,
                After = Turned(before with { Influences = transfer.Influences }, options, log),
                Map = map,
                Transfer = transfer.Report,
                Log = log,
                Findings = findings,
            };
        }

        // The model's own pose comes from what its file recorded as the bind
        // pose; the target's is worked out from its bone chain, which is the
        // only thing the game's format stores.
        ConformResult conformed = MeshConform.Apply(
            before, transfer.Influences,
            source.Pose,
            targetRest,
            map);

        log.Add(
            $"Geometry fitted: vertices moved {conformed.AverageMove:0.##} on average, " +
            $"{conformed.LargestMove:0.##} at most.");

        log.AddRange(CompareSkeletons(source, target, map));

        return new RetargetOutcome
        {
            Before = before,
            After = Turned(
                before with
                {
                    Positions = conformed.Positions,
                    Normals = conformed.Normals,
                    Influences = transfer.Influences,
                },
                options, log),
            Map = map,
            Transfer = transfer.Report,
            Conform = conformed,
            Log = log,
            Findings = findings,
        };
    }

    /// <summary>
    /// Turns the surface the other way, when asked.
    /// </summary>
    /// <remarks>
    /// Done here rather than while reading the file, so ticking the box and
    /// pressing fit again is enough. Applied at import it did nothing at all
    /// until the model was read in a second time, which reads as an option that
    /// does not work.
    /// <para>
    /// Both the winding and the directions are reversed. Winding alone decides
    /// which side is drawn, but leaving the directions facing the old way lights
    /// the surface as though it still faced that way.
    /// </para>
    /// </remarks>
    private static SkeletalMeshLod Turned(SkeletalMeshLod lod, RetargetOptions options, List<string> log)
    {
        if (!options.FlipWinding) return lod;

        var indices = new int[lod.Indices.Count];

        for (int i = 0; i + 2 < lod.Indices.Count; i += 3)
        {
            indices[i] = lod.Indices[i];
            indices[i + 1] = lod.Indices[i + 2];
            indices[i + 2] = lod.Indices[i + 1];
        }

        log.Add($"Surface turned the other way: {lod.Indices.Count / 3:N0} triangles reversed.");

        // The surface frames say which way the surface faces and which way
        // round it turns, so they are turned over with it. Reversing the
        // triangles alone leaves the model lit as though it still faced the
        // other way.
        var frames = lod.TangentFrames.ToArray();

        for (int at = 0; at + 7 < frames.Length; at += 8)
        {
            frames[at + 4] = Opposite(frames[at + 4]);
            frames[at + 5] = Opposite(frames[at + 5]);
            frames[at + 6] = Opposite(frames[at + 6]);
            frames[at + 7] = Opposite(frames[at + 7]);
        }

        return lod with
        {
            Indices = indices,
            Normals = lod.Normals.Select(n => -n).ToList(),
            TangentFrames = frames,
        };
    }

    /// <summary>One packed direction byte, pointing the other way.</summary>
    private static byte Opposite(byte value) => (byte)(255 - value);

    /// <summary>
    /// Gives a vertex with nothing pulling on it the bones of its nearest
    /// neighbour that has some.
    /// </summary>
    /// <remarks>
    /// A vertex has to name a bone: the format gives it four slots and no way
    /// to say "none". Left to fall back on whichever bone happens to be first,
    /// it is dragged to that bone the moment the model is posed — which is what
    /// a hand stretched into a spike, or two legs merged to a point, actually
    /// is. Taking the skinning of the nearest vertex that has some keeps it
    /// with the part of the body it belongs to.
    /// <para>
    /// One real file arrived with 657 of its 4,040 vertices unweighted, so this
    /// is not a rare case worth ignoring.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The skinning the model being replaced has around a given place.
    /// </summary>
    /// <remarks>
    /// Several nearby vertices are blended rather than the single nearest one
    /// taken. A joint has surfaces that sit close together and belong to
    /// different bones — the front and back of a knee are a centimetre apart
    /// and bend opposite ways — so copying whichever one happens to be nearest
    /// makes the skinning jump from vertex to vertex, and the joint creases in
    /// hard lines instead of bending. Blending by nearness spreads the change
    /// out the way a hand-painted weight does.
    /// </remarks>
    private static VertexInfluence Borrowed(Vector3 place, SkeletalMeshLod original)
    {
        const int neighbours = 6;

        Span<int> nearest = stackalloc int[neighbours];
        Span<float> distances = stackalloc float[neighbours];

        int found = 0;

        for (int o = 0; o < original.Positions.Count; o++)
        {
            float distance = Vector3.DistanceSquared(place, original.Positions[o]);

            // Kept in order, so the worst of the set is always the last.
            if (found == neighbours && distance >= distances[neighbours - 1]) continue;

            int at = Math.Min(found, neighbours - 1);

            while (at > 0 && distances[at - 1] > distance)
            {
                distances[at] = distances[at - 1];
                nearest[at] = nearest[at - 1];
                at--;
            }

            distances[at] = distance;
            nearest[at] = o;

            if (found < neighbours) found++;
        }

        if (found == 0) return new VertexInfluence { Bones = [], Weights = [] };

        var gathered = new Dictionary<int, float>();

        for (int i = 0; i < found; i++)
        {
            VertexInfluence neighbour = original.Influences[nearest[i]];

            // Nearer counts for more. The small addition keeps a vertex sitting
            // exactly on another from counting for everything.
            float say = 1f / (MathF.Sqrt(distances[i]) + 0.01f);

            for (int b = 0; b < neighbour.Bones.Count; b++)
            {
                gathered.TryGetValue(neighbour.Bones[b], out float already);
                gathered[neighbour.Bones[b]] = already + (neighbour.Weights[b] * say);
            }
        }

        var strongest = gathered.OrderByDescending(p => p.Value).Take(4).ToList();
        float total = strongest.Sum(p => p.Value);

        if (total <= 0f) return original.Influences[nearest[0]];

        return new VertexInfluence
        {
            Bones = strongest.Select(p => p.Key).ToList(),
            Weights = strongest.Select(p => p.Value / total).ToList(),
        };
    }

    /// <summary>
    /// Names the bones the model being replaced leans on that the new one does
    /// not have.
    /// </summary>
    /// <remarks>
    /// Weight on a bone that is not there has to go somewhere else, and the
    /// part of the body it held stops bending where it should. One real file
    /// came back without a single leg bone, and the legs moved as one piece
    /// with the hips and never planted on the ground.
    /// </remarks>
    private static void MissingBones(
        SourceModel source, SkeletalMesh target, ICollection<ModelFinding> findings)
    {
        if (target.HighestDetail is not { } original) return;

        var leaned = original.Influences
            .SelectMany(i => i.Bones)
            .Distinct()
            .Where(b => b >= 0 && b < target.Bones.Count)
            .Select(b => target.Bones[b].Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var brought = source.Bones.Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        string[] missing = leaned.Where(n => !brought.Contains(n)).Order(StringComparer.Ordinal).ToArray();

        if (missing.Length == 0) return;

        findings.Add(new ModelFinding
        {
            Kind = FindingKind.Warned,
            What = $"{missing.Length} bones the model being replaced uses are not in this file",
            Detail =
                string.Join(", ", missing.Take(14)) +
                (missing.Length > 14 ? ", and more. " : ". ") +
                "Whatever they held has been bound from the model being replaced instead, which keeps it " +
                "moving, but keeping those bones in the modelling tool would be better.",
        });
    }

    private static IReadOnlyList<VertexInfluence> Anchored(
        SkeletalMeshLod lod, IReadOnlyList<VertexInfluence> influences, SkeletalMesh target,
        List<string> log, ICollection<ModelFinding> findings)
    {
        var loose = new List<int>();

        for (int v = 0; v < influences.Count; v++)
        {
            if (influences[v].Bones.Count == 0) loose.Add(v);
        }

        if (loose.Count == 0) return influences;

        // Taken from the model being replaced wherever it can be. Its weights
        // are already numbered against this very skeleton, and a costume sits
        // in much the same place as the body it covers — so a vertex with
        // nothing pulling on it gets the skinning the original had at that
        // spot, legs and all. Borrowing from elsewhere in the imported model
        // only spreads whatever weights it does have.
        if (target.HighestDetail is { HasGeometry: true } original)
        {
            var fromOriginal = influences.ToArray();

            foreach (int v in loose)
                fromOriginal[v] = Borrowed(lod.Positions[v], original);

            log.Add(
                $"{loose.Count:N0} of {influences.Count:N0} vertices arrived with no bone pulling on them, " +
                "and took the skinning of the model they replace at the same spot.");

            findings.Add(new ModelFinding
            {
                Kind = FindingKind.Mended,
                What = $"{loose.Count:N0} of {influences.Count:N0} vertices had no bone pulling on them",
                Detail =
                    "Each took the skinning of the model being replaced around the same spot, blended over " +
                    "its nearest few vertices so joints bend smoothly rather than creasing. Left alone " +
                    "they would be dragged to whichever bone came first, stretching hands, feet and neck " +
                    "to points.",
            });

            return fromOriginal;
        }

        int[] anchored = Enumerable.Range(0, influences.Count)
            .Where(v => influences[v].Bones.Count > 0)
            .ToArray();

        if (anchored.Length == 0)
        {
            log.Add(
                $"None of this model's {influences.Count:N0} vertices carry any weights, so none of it " +
                "can be bound by name.");

            findings.Add(new ModelFinding
            {
                Kind = FindingKind.Warned,
                What = "No vertex carries any weight",
                Detail =
                    "Nothing here can bind this model by bone name. Export it again from the modelling " +
                    "tool with its armature and its vertex groups.",
            });

            return influences;
        }

        var filled = influences.ToArray();

        foreach (int v in loose)
        {
            Vector3 position = lod.Positions[v];

            int nearest = anchored[0];
            float best = float.MaxValue;

            foreach (int candidate in anchored)
            {
                float distance = Vector3.DistanceSquared(position, lod.Positions[candidate]);
                if (distance >= best) continue;

                best = distance;
                nearest = candidate;
            }

            filled[v] = influences[nearest];
        }

        log.Add(
            $"{loose.Count:N0} of {influences.Count:N0} vertices arrived with no bone pulling on them, " +
            "and were given the skinning of their nearest neighbour.");

        findings.Add(new ModelFinding
        {
            Kind = FindingKind.Warned,
            What = $"{loose.Count:N0} of {influences.Count:N0} vertices had no bone pulling on them",
            Detail =
                "Each was given the skinning of its nearest neighbour, which keeps it with the part of the " +
                "body it belongs to. Left alone they would be dragged to whichever bone came first, " +
                "stretching hands, feet and neck to points. Weighting them in the modelling tool would be " +
                "better than this guess.",
        });

        return filled;
    }

    /// <summary>
    /// Brings a model that came back a different size back to the target's.
    /// </summary>
    /// <remarks>
    /// Modelling tools disagree about what one unit means. Blender measures in
    /// metres and a file records centimetres, so a model that made the trip out
    /// and back through it returns a hundred times too large even with its
    /// scale left at one — and nothing downstream can tell that from a model
    /// that is genuinely enormous.
    /// <para>
    /// Only a wild difference is corrected. A costume can legitimately be a
    /// little larger or smaller than the one it replaces, and quietly resizing
    /// those would flatten a real difference the user asked for.
    /// </para>
    /// </remarks>
    private static SourceModel ToTargetScale(
        SourceModel source, SkeletalMesh target, List<string> log, ICollection<ModelFinding> findings)
    {
        if (source.Geometry.Positions.Count == 0) return source;

        // Measured from the target's own vertices, not from the size it states
        // about itself. The stated figure describes a box drawn around the
        // model with room to spare — on one real costume it is 174 tall where
        // the vertices span 112 — and scaling to it makes every model that goes
        // through here half again too large.
        if (target.HighestDetail is not { HasGeometry: true } targetGeometry) return source;

        float came = Span(source.Geometry.Positions);
        float wanted = Span(targetGeometry.Positions);

        if (came < 0.0001f || wanted < 0.0001f) return source;

        float ratio = wanted / came;

        // Half again either way is the widest a costume plausibly differs by.
        if (ratio is > 0.66f and < 1.5f) return source;

        log.Add(
            $"The model came in {came:0.#} across where the target is {wanted:0.#}, so it is resized by " +
            $"{ratio:0.####}.");

        findings.Add(new ModelFinding
        {
            Kind = FindingKind.Mended,
            What = $"The model was {(ratio > 1f ? "too small" : "too large")} by about {Math.Max(ratio, 1f / ratio):0.#} times",
            Detail =
                $"Resized to match the target: it measured {came:0.#} corner to corner where the target " +
                $"measures {wanted:0.#}. Modelling tools disagree about what one unit means.",
        });

        return Resized(source, ratio);
    }

    /// <summary>How far a set of points reaches, corner to corner.</summary>
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

    /// <summary>Every part of a model made larger or smaller by the same amount.</summary>
    private static SourceModel Resized(SourceModel source, float ratio)
    {
        var positions = source.Geometry.Positions.Select(p => p * ratio).ToList();

        var pose = new Matrix4x4[source.Pose.Count];

        for (int b = 0; b < pose.Length; b++)
        {
            Matrix4x4 boneToModel = source.Pose.BoneToModel[b];
            boneToModel.Translation *= ratio;

            pose[b] = boneToModel;
        }

        return source with
        {
            Geometry = source.Geometry with { Positions = positions },
            Bones = source.Bones.Select(b => b with { Position = b.Position * ratio }).ToList(),
            Pose = SkeletonPose.FromBindPoses(pose),
        };
    }

    /// <summary>
    /// How far apart the two skeletons hold the joints they share.
    /// </summary>
    private static float JointDisagreement(SkeletonPose source, SkeletonPose target, BoneMap map)
    {
        float worst = 0f;

        foreach (BonePair pair in map.Pairs)
        {
            if (pair.SourceIndex >= source.Count || pair.TargetIndex >= target.Count) continue;

            worst = Math.Max(
                worst,
                Vector3.Distance(source.PositionOf(pair.SourceIndex), target.PositionOf(pair.TargetIndex)));
        }

        return worst;
    }

    /// <summary>
    /// Rearranges a model's axes to match the game's.
    /// </summary>
    /// <remarks>
    /// Everything moves together: the vertices, the directions the surface
    /// faces, and the pose the skinning was measured against. Moving any one of
    /// them without the others is worse than moving none.
    /// <para>
    /// A rearrangement that swaps two axes rather than turning one into another
    /// mirrors the model, which turns its surface inside out. The triangles are
    /// turned back to compensate, so the model still faces outwards.
    /// </para>
    /// </remarks>
    private static SourceModel Realign(SourceModel source, AxisAlignment axes)
    {
        Matrix4x4 transform = axes.Transform;

        var positions = new Vector3[source.Geometry.Positions.Count];
        for (int i = 0; i < positions.Length; i++)
            positions[i] = Vector3.Transform(source.Geometry.Positions[i], transform);

        var normals = new Vector3[source.Geometry.Normals.Count];
        for (int i = 0; i < normals.Length; i++)
        {
            Vector3 turned = Vector3.TransformNormal(source.Geometry.Normals[i], transform);
            float length = turned.Length();

            normals[i] = length > 0.0001f ? turned / length : Vector3.UnitZ;
        }

        IReadOnlyList<int> indices = source.Geometry.Indices;

        if (axes.Mirrors)
        {
            var turnedAround = new int[indices.Count];

            for (int i = 0; i + 2 < indices.Count; i += 3)
            {
                turnedAround[i] = indices[i];
                turnedAround[i + 1] = indices[i + 2];
                turnedAround[i + 2] = indices[i + 1];
            }

            indices = turnedAround;
        }

        var pose = new Matrix4x4[source.Pose.Count];
        for (int i = 0; i < pose.Length; i++) pose[i] = source.Pose.BoneToModel[i] * transform;

        return source with
        {
            Geometry = source.Geometry with
            {
                Positions = positions,
                Normals = normals,
                Indices = indices,
            },
            Pose = SkeletonPose.FromBindPoses(pose),
        };
    }

    /// <summary>
    /// Says how the two skeletons differ, in terms that name the cause.
    /// </summary>
    /// <remarks>
    /// When a model is fitted to a skeleton its bones all match by name and it
    /// still moves a long way, the two skeletons disagree about something
    /// wholesale — how large the character is, which way is up, or where the
    /// origin sits. Which of those it is cannot be told from the movement
    /// figure alone, so it is measured and named here rather than guessed at.
    /// </remarks>
    private static IEnumerable<string> CompareSkeletons(SourceModel source, SkeletalMesh target, BoneMap map)
    {
        SkeletonPose sourcePose = source.Pose;
        SkeletonPose targetPose = SkeletonPose.Rest(target.Bones);

        var pairs = map.Pairs
            .Where(p => p.SourceIndex < sourcePose.Count && p.TargetIndex < targetPose.Count)
            .ToList();

        if (pairs.Count < 3) yield break;

        // How far apart the two skeletons hold the same joints, measured from
        // each one's own middle so that a shifted origin does not disguise
        // itself as every bone being wrong.
        var fromMiddle = new List<(float Source, float Target)>(pairs.Count);

        Vector3 sourceMiddle = Middle(pairs.Select(p => sourcePose.PositionOf(p.SourceIndex)));
        Vector3 targetMiddle = Middle(pairs.Select(p => targetPose.PositionOf(p.TargetIndex)));

        foreach (BonePair pair in pairs)
        {
            fromMiddle.Add((
                Vector3.Distance(sourcePose.PositionOf(pair.SourceIndex), sourceMiddle),
                Vector3.Distance(targetPose.PositionOf(pair.TargetIndex), targetMiddle)));
        }

        float sourceSpread = fromMiddle.Sum(f => f.Source) / fromMiddle.Count;
        float targetSpread = fromMiddle.Sum(f => f.Target) / fromMiddle.Count;

        float offset = Vector3.Distance(sourceMiddle, targetMiddle);

        yield return
            $"Skeletons compared: the model's is {sourceSpread:0.#} across, the target's {targetSpread:0.#}, " +
            $"their middles {offset:0.#} apart.";

        if (targetSpread > 0.001f && sourceSpread > 0.001f)
        {
            float scale = sourceSpread / targetSpread;

            if (scale is < 0.9f or > 1.1f)
                yield return $"  The model is {scale:0.##} times the size of the target skeleton.";
        }

        if (offset > targetSpread * 0.25f)
            yield return "  They sit in different places, so the model is built around a different origin.";

        // Where each holds the same joint, once size and place are set aside.
        // A large angle here is the two disagreeing about which way is up.
        BonePair? furthest = null;
        float worst = 0f;

        foreach (BonePair pair in pairs)
        {
            Vector3 fromSource = sourcePose.PositionOf(pair.SourceIndex) - sourceMiddle;
            Vector3 fromTarget = targetPose.PositionOf(pair.TargetIndex) - targetMiddle;

            if (fromSource.Length() < 0.001f || fromTarget.Length() < 0.001f) continue;

            float angle = Angle(Vector3.Normalize(fromSource), Vector3.Normalize(fromTarget));

            if (angle <= worst) continue;

            worst = angle;
            furthest = pair;
        }

        if (furthest is { } bone && worst > 20f)
        {
            yield return
                $"  They point differently: {bone.SourceName} lies {worst:0} degrees away from where the " +
                "target holds it, so the two disagree about which way the character faces or which way is up.";
        }
    }

    private static Vector3 Middle(IEnumerable<Vector3> points)
    {
        var total = Vector3.Zero;
        int count = 0;

        foreach (Vector3 point in points)
        {
            total += point;
            count++;
        }

        return count == 0 ? Vector3.Zero : total / count;
    }

    private static float Angle(Vector3 a, Vector3 b) =>
        MathF.Acos(Math.Clamp(Vector3.Dot(a, b), -1f, 1f)) * 180f / MathF.PI;

    /// <summary>
    /// Binds the model by copying the skinning of the target's nearest surface.
    /// </summary>
    /// <remarks>
    /// The model keeps its shape: nothing here knows how its bones relate to
    /// the target's, so there is no basis on which to move a vertex. It is
    /// bound where it stands, which is only right if it was already built to sit
    /// on this character.
    /// </remarks>
    private static RetargetOutcome FromSurface(
        SourceModel source, SkeletalMesh target, SkeletalMeshLod before, List<string> log)
    {
        if (target.HighestDetail is not { HasGeometry: true } surface)
        {
            throw new RetargetException(
                "The target has no geometry to copy skinning from, so binding by nearest surface cannot work.");
        }

        SurfaceTransferResult transfer = SurfaceWeightTransfer.Apply(before.Positions, surface);

        log.Add($"Bound from the target's surface: {transfer.Report}.");

        if (transfer.Report.VerticesUnbound > 0)
            log.Add($"  {transfer.Report.VerticesUnbound:N0} found nothing near enough and follow nothing.");

        // How far vertices had to reach is the warning worth giving: a model
        // sitting on the character binds within a whisker, one placed elsewhere
        // binds to whatever happened to be closest.
        if (transfer.Report.LargestDistance > target.Bounds.Radius * 0.25f)
        {
            log.Add(
                $"  Some vertices were {transfer.Report.LargestDistance:0.#} from the target, which is far " +
                $"against its own size of {target.Bounds.Radius:0.#}. Check the model sits on the character.");
        }

        return new RetargetOutcome
        {
            Before = before,
            After = before with { Influences = transfer.Influences },
            Map = BoneMap.Build(source.Bones, target.Bones),
            Transfer = new TransferReport
            {
                VerticesKept = transfer.Report.VerticesBound,
                VerticesRerouted = 0,
                VerticesDropped = transfer.Report.VerticesUnbound,
                ReroutedFrom = new Dictionary<string, float>(),
            },
            Log = log,
        };
    }

    private static string Describe(MatchQuality quality) => quality switch
    {
        MatchQuality.Exact => "the same name",
        MatchQuality.SameName => "the same name spelled differently",
        MatchQuality.SameJoint => "being the same joint",
        _ => "hand",
    };
}
