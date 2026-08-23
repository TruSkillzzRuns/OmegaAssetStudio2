namespace OmegaAssetStudio2.Core.Meshes;

/// <summary>One run of vertices, drawn by one section.</summary>
public sealed record PlannedChunk
{
    /// <summary>Where this run's vertices start in the rewritten buffer.</summary>
    public required int BaseVertexIndex { get; init; }

    /// <summary>The vertices, in the order they will be written.</summary>
    public required IReadOnlyList<int> Vertices { get; init; }

    /// <summary>Bones this run draws on; a vertex names them by position here.</summary>
    public required IReadOnlyList<int> BoneMap { get; init; }

    /// <summary>Vertices that follow exactly one bone. They come first.</summary>
    public required int RigidCount { get; init; }

    /// <summary>Vertices that follow several bones at once.</summary>
    public required int SoftCount { get; init; }

    /// <summary>The most bones any one vertex here follows.</summary>
    public required int MaxInfluences { get; init; }
}

/// <summary>How a model will be laid out: its runs, its sections, its triangles.</summary>
public sealed record MeshLayoutPlan
{
    public required IReadOnlyList<PlannedChunk> Chunks { get; init; }

    /// <summary>One section per run, in the same order.</summary>
    public required IReadOnlyList<MeshSection> Sections { get; init; }

    /// <summary>Triangle corners, renumbered against the rewritten buffer.</summary>
    public required IReadOnlyList<int> Indices { get; init; }

    /// <summary>Which original vertex each rewritten one came from.</summary>
    public required IReadOnlyList<int> VertexOrder { get; init; }

    public int VertexCount => VertexOrder.Count;
}

/// <summary>
/// Works out the layout a model has to be written in.
/// </summary>
/// <remarks>
/// Measured across 115 of the game's own character models: every single one
/// maps its sections to its runs of vertices one for one, no run ever draws on
/// more than 75 bones, and runs hold their singly-bound vertices before their
/// multiply-bound ones. Writing one run holding everything is not a shape that
/// appears anywhere in the shipped content, so this reproduces the shape that
/// does.
/// <para>
/// A run owns its vertices outright. Where two sections share a vertex it is
/// written into both, because a run addresses its vertices as a contiguous
/// stretch beginning at its own base — that is what lets a vertex name its
/// bones in a single byte.
/// </para>
/// </remarks>
public static class MeshLayoutPlanner
{
    /// <summary>
    /// The most bones one run may draw on.
    /// </summary>
    /// <remarks>
    /// Seventy-five is not a guess: across those 115 models the largest run
    /// draws on exactly 75 bones and none draws on more, which is the shape of
    /// a hard limit seen from the outside.
    /// </remarks>
    public const int MaxBonesPerChunk = 75;

    /// <summary>
    /// Divides a section's triangles into groups, none drawing on more bones
    /// than a run may address.
    /// </summary>
    /// <remarks>
    /// Triangles are taken in the order they are stored and added to the group
    /// in hand while they fit, so a model that already fits comes through as
    /// one group and is left exactly as it was. Neighbouring triangles share
    /// bones, so keeping their order keeps the groups few: one real model
    /// needing 79 bones divided into two runs rather than scattering.
    /// </remarks>
    private static List<List<int>> Split(MeshGeometry geometry, int firstCorner, int corners)
    {
        var groups = new List<List<int>>();

        var current = new List<int>();
        var bones = new HashSet<int>();

        for (int c = firstCorner; c + 2 < firstCorner + corners; c += 3)
        {
            var wanted = new HashSet<int>(bones);

            for (int corner = 0; corner < 3; corner++)
            {
                foreach (int bone in geometry.Influences[geometry.Indices[c + corner]].Bones)
                    wanted.Add(bone);
            }

            if (wanted.Count > MaxBonesPerChunk && current.Count > 0)
            {
                groups.Add(current);

                current = [];
                bones = [];

                for (int corner = 0; corner < 3; corner++)
                {
                    foreach (int bone in geometry.Influences[geometry.Indices[c + corner]].Bones)
                        bones.Add(bone);
                }
            }
            else
            {
                bones = wanted;
            }

            current.Add(c);
        }

        if (current.Count > 0) groups.Add(current);

        return groups.Count == 0 ? [[]] : groups;
    }

    /// <param name="existing">
    /// The runs the model already had, whose bone maps are reused where they
    /// still fit. Their order cannot be worked out from geometry alone — it is
    /// neither sorted nor the order the vertices mention them — so the only way
    /// to leave an untouched model untouched is to keep the list it came with.
    /// </param>
    public static MeshLayoutPlan Build(MeshGeometry geometry, IReadOnlyList<MeshChunk>? existing = null)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        IReadOnlyList<MeshSection> wanted = geometry.Sections.Count > 0
            ? geometry.Sections
            : [new MeshSection
               {
                   MaterialIndex = 0,
                   ChunkIndex = 0,
                   BaseIndex = 0,
                   TriangleCount = geometry.TriangleCount,
               }];

        var chunks = new List<PlannedChunk>(wanted.Count);
        var sections = new List<MeshSection>(wanted.Count);
        var order = new List<int>(geometry.Positions.Count);
        var indices = new List<int>(geometry.Indices.Count);

        foreach (MeshSection section in wanted)
        {
            int sectionStart = section.BaseIndex;
            int sectionCorners = section.TriangleCount * 3;

            if (sectionStart < 0 || sectionCorners < 0 ||
                sectionStart + sectionCorners > geometry.Indices.Count)
            {
                throw new MeshWriteException(
                    $"A section claims {section.TriangleCount:N0} triangles from {section.BaseIndex:N0}, " +
                    $"which is more than the {geometry.TriangleCount:N0} the model has.");
            }

            // A run can only address so many bones, so a section drawing on
            // more than that is split into as many runs as it takes.
            List<List<int>> groups = Split(geometry, sectionStart, sectionCorners);

            bool wasSplit = groups.Count > 1;

            foreach (List<int> triangles in groups)
            {
            // Every vertex this section draws, kept in the order it already has
            // and then split so the singly-bound ones come first.
            //
            // The order matters more than it looks. A model already laid out
            // this way — every one the game ships is — comes back through here
            // unchanged, because its runs are already ascending stretches with
            // their rigid vertices at the front. Gathering them in the order the
            // triangles happen to mention them instead renumbered all 4,553
            // vertices of a costume that had not been altered at all, rewriting
            // 156,791 bytes to say the same thing.
            var mine = new HashSet<int>();

            foreach (int t in triangles)
            {
                mine.Add(geometry.Indices[t]);
                mine.Add(geometry.Indices[t + 1]);
                mine.Add(geometry.Indices[t + 2]);
            }

            List<int> inOrder = mine.Order().ToList();

            List<int> rigid = inOrder.Where(v => geometry.Influences[v].Count <= 1).ToList();
            List<int> soft = inOrder.Where(v => geometry.Influences[v].Count > 1).ToList();

            var ordered = new List<int>(rigid.Count + soft.Count);
            ordered.AddRange(rigid);
            ordered.AddRange(soft);

            int baseVertex = order.Count;

            // Where each of this run's vertices ended up, so its triangles can
            // be renumbered against the rewritten buffer.
            var placed = new Dictionary<int, int>(ordered.Count);

            for (int i = 0; i < ordered.Count; i++) placed[ordered[i]] = baseVertex + i;

            order.AddRange(ordered);

            // The run's own list where it had one, kept in its own order, with
            // anything the vertices now reach added on the end.
            var boneMap = new List<int>();
            var haveBone = new HashSet<int>();

            if (!wasSplit && existing is not null && chunks.Count < existing.Count)
            {
                foreach (int bone in existing[chunks.Count].BoneMap)
                {
                    if (haveBone.Add(bone)) boneMap.Add(bone);
                }
            }

            foreach (int v in ordered)
            {
                foreach (int bone in geometry.Influences[v].Bones)
                {
                    if (haveBone.Add(bone)) boneMap.Add(bone);
                }
            }

            // Carrying the old list over can push a run past what it may hold.
            // Bones nothing here follows are dropped first, since they cost
            // room without doing anything.
            if (boneMap.Count > MaxBonesPerChunk)
            {
                var reached = ordered.SelectMany(v => geometry.Influences[v].Bones).ToHashSet();

                boneMap = boneMap.Where(reached.Contains).ToList();
            }

            if (boneMap.Count == 0) boneMap.Add(0);

            if (boneMap.Count > MaxBonesPerChunk)
            {
                throw new MeshWriteException(
                    $"A run still draws on {boneMap.Count} bones after being split, which should not " +
                    "happen — a single triangle cannot need more than twelve.");
            }

            sections.Add(new MeshSection
            {
                MaterialIndex = section.MaterialIndex,
                ChunkIndex = chunks.Count,
                BaseIndex = indices.Count,
                TriangleCount = triangles.Count,
            });

            foreach (int t in triangles)
            {
                indices.Add(placed[geometry.Indices[t]]);
                indices.Add(placed[geometry.Indices[t + 1]]);
                indices.Add(placed[geometry.Indices[t + 2]]);
            }

            chunks.Add(new PlannedChunk
            {
                BaseVertexIndex = baseVertex,
                Vertices = ordered,
                BoneMap = boneMap,
                RigidCount = rigid.Count,
                SoftCount = soft.Count,
                MaxInfluences = ordered.Count == 0
                    ? 1
                    : ordered.Max(v => Math.Max(1, geometry.Influences[v].Count)),
            });
            }
        }

        if (order.Count > ushort.MaxValue + 1)
        {
            throw new MeshWriteException(
                $"Laid out in runs this model needs {order.Count:N0} vertices, and the game addresses " +
                $"them with two bytes, so it cannot hold more than {ushort.MaxValue + 1:N0}. Vertices " +
                "shared between sections have to be written into each.");
        }

        return new MeshLayoutPlan
        {
            Chunks = chunks,
            Sections = sections,
            Indices = indices,
            VertexOrder = order,
        };
    }
}
