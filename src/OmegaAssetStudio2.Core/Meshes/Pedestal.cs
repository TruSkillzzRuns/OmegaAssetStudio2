using System.Numerics;
using System.Drawing;
using System.Drawing.Imaging;
using OmegaAssetStudio2.Core.Retargeting;
using OmegaAssetStudio2.Core.Textures;
using SharpGLTF.Schema2;

namespace OmegaAssetStudio2.Core.Meshes;

/// <summary>A stand for a model to be shown on.</summary>
public sealed record PedestalMesh
{
    public required string Name { get; init; }

    public required IReadOnlyList<Vector3> Positions { get; init; }
    public required IReadOnlyList<Vector3> Normals { get; init; }
    public required IReadOnlyList<Vector4> Tangents { get; init; }
    public required IReadOnlyList<Vector2> TexCoords { get; init; }
    public required IReadOnlyList<int> Indices { get; init; }

    /// <summary>Its colour, where it has one.</summary>
    public TextureImage? Colour { get; init; }

    public TextureImage? NormalMap { get; init; }

    /// <summary>
    /// How polished and how mirror-like each part is, packed the way this
    /// tool's own materials pack it, so the same shader draws it.
    /// </summary>
    public TextureImage? Mask { get; init; }

    /// <summary>Which channel of the mask is the highlight, and which the reflection.</summary>
    public int GlossChannel { get; init; } = -1;
    public int ReflectChannel { get; init; } = -1;

    /// <summary>How wide it is across, and how thick.</summary>
    public required Vector3 Size { get; init; }

    /// <summary>Its highest point, ornaments and all.</summary>
    public required float TopHeight { get; init; }

    /// <summary>
    /// The height of the surface a model standing in the middle would rest on,
    /// for a model reaching out this far from the centre.
    /// </summary>
    /// <remarks>
    /// A stand is not one height, and both simpler answers are visibly wrong.
    /// Measured on the platform this was written against: its highest point is
    /// 18.84, the tip of a small dome in the middle, and standing a model there
    /// leaves it hanging in the air. Its broad outer deck is at 7, and standing
    /// a model there buries its feet, because the middle - where the model
    /// actually stands - is raised well above the deck.
    /// <para>
    /// So the question is asked of the part of the stand the model covers. Its
    /// height profile by distance from the middle runs 18.8 within 5 units, 15
    /// within 20, 13 within 30 and 12.1 within 50: a dome on a raised pad on a
    /// deck. Taking the 95th percentile rather than the highest point ignores
    /// the dome and any other ornament poking through, and answers with the pad
    /// the feet rest on.
    /// </para>
    /// </remarks>
    public float SurfaceWithin(float radius)
    {
        if (Positions.Count == 0) return TopHeight;
        if (radius <= 0f) return TopHeight;

        var inside = new List<float>();

        foreach (Vector3 position in Positions)
        {
            float reach = MathF.Sqrt((position.X * position.X) + (position.Y * position.Y));
            if (reach <= radius) inside.Add(position.Z);
        }

        // Nothing under the model at all - a stand narrower than the model, or
        // a ring with a hole in the middle - so its top is the best on offer.
        if (inside.Count == 0) return TopHeight;

        inside.Sort();
        return inside[Math.Clamp((int)(inside.Count * 0.95), 0, inside.Count - 1)];
    }

    public int TriangleCount => Indices.Count / 3;
}

/// <summary>
/// Loads a stand from a model file and the picture files beside it.
/// </summary>
/// <remarks>
/// Written against what these files actually are rather than against a format
/// in the abstract. A stand exported from a modelling or generating tool
/// arrives as one mesh with a set of maps named after it - colour, normal,
/// metallic, roughness - and those last two are exactly what this tool's own
/// shader wants, once they are packed into the one texture it reads them from.
/// <para>
/// Nothing here is specific to one stand: any model file with maps named the
/// same way loads, and one with no maps at all still loads and is drawn plain.
/// </para>
/// </remarks>
public static class PedestalLoader
{
    /// <summary>
    /// Word in a picture's file name that says what the picture is. Ordered so
    /// that the more specific names are tested first - a file ending
    /// "_texture_normal" is a normal map, not a colour map, even though it also
    /// contains "texture".
    /// </summary>
    private static readonly string[] NormalWords = ["normal", "_norm", "nrm", "bump"];
    private static readonly string[] RoughnessWords = ["roughness", "rough", "gloss"];
    private static readonly string[] MetallicWords = ["metallic", "metalness", "metal", "specular"];

    /// <summary>
    /// Reads a stand from a model file.
    /// </summary>
    /// <remarks>
    /// glTF and GLB are read directly rather than through the general model
    /// reader, because they carry their pictures inside the file and say plainly
    /// which is which. Every other format is read the general way, and its
    /// pictures are looked for beside it.
    /// <para>
    /// This matters in practice: exporting a stand from a modelling tool to FBX
    /// commonly writes the mesh but only references to its pictures, leaving
    /// them behind. The same stand exported to GLB brought its two 2048-pixel
    /// textures with it in a file a tenth the size.
    /// </para>
    /// </remarks>
    public static PedestalMesh Load(string modelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        string extension = Path.GetExtension(modelPath);

        if (extension.Equals(".glb", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gltf", StringComparison.OrdinalIgnoreCase))
        {
            return LoadGltf(modelPath);
        }

        ImportedMesh imported = FbxReader.Read(modelPath);

        // Drawn corners, not shared points: the file gives a texture coordinate
        // and a surface frame per corner, and collapsing them onto shared points
        // would lose the seams the stand was authored with.
        int count = imported.WedgePoints.Count;

        var positions = new Vector3[count];
        var normals = new Vector3[count];
        var tangents = new Vector4[count];
        var texCoords = new Vector2[count];

        var low = new Vector3(float.MaxValue);
        var high = new Vector3(float.MinValue);

        for (int i = 0; i < count; i++)
        {
            int point = imported.WedgePoints[i];

            Vector3 position = point >= 0 && point < imported.Positions.Count
                ? imported.Positions[point]
                : Vector3.Zero;

            positions[i] = position;
            low = Vector3.Min(low, position);
            high = Vector3.Max(high, position);

            normals[i] = At(imported.Normals, i, Vector3.UnitZ);
            texCoords[i] = At(imported.TexCoords, i, Vector2.Zero);

            Vector3 tangent = At(imported.Tangents, i, Vector3.UnitX);
            Vector3 bitangent = At(imported.Bitangents, i, Vector3.UnitY);

            // Which way round the third axis runs, worked out from the two the
            // file gives rather than assumed one way.
            float handedness = Vector3.Dot(Vector3.Cross(normals[i], tangent), bitangent) < 0f ? -1f : 1f;

            tangents[i] = new Vector4(tangent, handedness);
        }

        BringLevel(positions, normals, tangents, imported.Indices);

        // The turn moves the stand, so where it now sits has to be asked again.
        low = new Vector3(float.MaxValue);
        high = new Vector3(float.MinValue);

        foreach (Vector3 position in positions)
        {
            low = Vector3.Min(low, position);
            high = Vector3.Max(high, position);
        }

        var settle = new Vector3((low.X + high.X) * 0.5f, (low.Y + high.Y) * 0.5f, 0f);

        if (settle != Vector3.Zero)
        {
            for (int i = 0; i < positions.Length; i++) positions[i] -= settle;

            low -= settle;
            high -= settle;
        }

        string folder = Path.GetDirectoryName(modelPath) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(modelPath);

        TextureImage? colour = FindPicture(folder, stem, null);
        TextureImage? normalMap = FindPicture(folder, stem, NormalWords);
        TextureImage? roughness = FindPicture(folder, stem, RoughnessWords);
        TextureImage? metallic = FindPicture(folder, stem, MetallicWords);

        TextureImage? mask = Pack(roughness, metallic);

        return new PedestalMesh
        {
            Name = stem,
            Positions = positions,
            Normals = normals,
            Tangents = tangents,
            TexCoords = texCoords,
            Indices = imported.Indices,
            Colour = colour,
            NormalMap = normalMap,
            Mask = mask,

            // Packed below as red for the highlight and blue for the reflection,
            // matching how the game's own masks name their channels.
            GlossChannel = mask is null ? -1 : 0,
            ReflectChannel = mask is null ? -1 : 2,

            Size = high - low,
            TopHeight = high.Z,
        };
    }

    /// <summary>
    /// Reads a stand from a glTF or GLB file, pictures and all.
    /// </summary>
    private static PedestalMesh LoadGltf(string modelPath)
    {
        ModelRoot model = ModelRoot.Load(modelPath);

        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var tangents = new List<Vector4>();
        var texCoords = new List<Vector2>();
        var indices = new List<int>();

        Material? material = null;

        // Walked through the scene's nodes rather than over the meshes alone,
        // because a mesh is placed by the node that carries it. Reading the
        // vertex data on its own ignores that placement: this file's only node
        // moves its mesh by more than half its own width, which would have put
        // the stand beside the model instead of under it.
        foreach (Node node in model.DefaultScene?.VisualChildren.SelectMany(Descend) ?? [])
        {
            if (node.Mesh is null) continue;

            Matrix4x4 placement = node.WorldMatrix;

            foreach (MeshPrimitive primitive in node.Mesh.Primitives)
            {
                material ??= primitive.Material;

                IList<Vector3>? points = primitive.GetVertexAccessor("POSITION")?.AsVector3Array();
                if (points is null || points.Count == 0) continue;

                IList<Vector3>? givenNormals = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array();
                IList<Vector4>? givenTangents = primitive.GetVertexAccessor("TANGENT")?.AsVector4Array();
                IList<Vector2>? givenUvs = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();

                int start = positions.Count;

                for (int i = 0; i < points.Count; i++)
                {
                    positions.Add(Upright(Vector3.Transform(points[i], placement)));

                    normals.Add(givenNormals is not null && i < givenNormals.Count
                        ? Upright(Vector3.TransformNormal(givenNormals[i], placement))
                        : Vector3.UnitZ);

                    if (givenTangents is not null && i < givenTangents.Count)
                    {
                        Vector4 tangent = givenTangents[i];

                        tangents.Add(new Vector4(
                            Upright(Vector3.TransformNormal(new Vector3(tangent.X, tangent.Y, tangent.Z), placement)),
                            tangent.W));
                    }
                    else
                    {
                        tangents.Add(new Vector4(Vector3.UnitX, 1f));
                    }

                    texCoords.Add(givenUvs is not null && i < givenUvs.Count ? givenUvs[i] : Vector2.Zero);
                }

                foreach ((int a, int b, int c) in primitive.GetTriangleIndices())
                {
                    indices.Add(start + a);
                    indices.Add(start + b);
                    indices.Add(start + c);
                }
            }
        }

        BringLevel(positions, normals, tangents, indices);

        var low = new Vector3(float.MaxValue);
        var high = new Vector3(float.MinValue);

        foreach (Vector3 position in positions)
        {
            low = Vector3.Min(low, position);
            high = Vector3.Max(high, position);
        }

        if (positions.Count == 0) low = high = Vector3.Zero;

        // Centred sideways on itself. Where a stand sits in the file it was
        // made in is its author's business; under the model is where it needs
        // to be here, and the fitting places it by its middle.
        var sideways = new Vector3((low.X + high.X) * 0.5f, (low.Y + high.Y) * 0.5f, 0f);

        if (sideways != Vector3.Zero)
        {
            for (int i = 0; i < positions.Count; i++) positions[i] -= sideways;

            low -= sideways;
            high -= sideways;
        }

        TextureImage? colour = ChannelPicture(material, "BaseColor");
        TextureImage? normalMap = ChannelPicture(material, "Normal");
        TextureImage? metallicRoughness = ChannelPicture(material, "MetallicRoughness");

        TextureImage? mask = FromMetallicRoughness(metallicRoughness);

        return new PedestalMesh
        {
            Name = Path.GetFileNameWithoutExtension(modelPath),
            Positions = positions,
            Normals = normals,
            Tangents = tangents,
            TexCoords = texCoords,
            Indices = indices,
            Colour = colour,
            NormalMap = normalMap,
            Mask = mask,
            GlossChannel = mask is null ? -1 : 0,
            ReflectChannel = mask is null ? -1 : 2,
            Size = high - low,
            TopHeight = high.Z,
        };
    }

    /// <summary>
    /// Turns a stand so its deck lies flat.
    /// </summary>
    /// <remarks>
    /// A stand is a thing to put a model on, so its deck should be level, and a
    /// stand that arrives tilted is a mistake rather than a style. One measured
    /// here was 12.9 degrees off, climbing from 0.12 to 0.25 across its width;
    /// standing a model on it left the model leaning.
    /// <para>
    /// Which way "up" is on a given stand is decided by area, not by counting:
    /// the faces are grouped by the direction they point and the largest group
    /// by area wins. On that stand the winning group was 47% of everything
    /// facing upwards, so the deck decides, and its rims, ramps and fittings do
    /// not get a vote. An average of all of them would have landed between the
    /// deck and its rims and levelled neither.
    /// </para>
    /// </remarks>
    private static void BringLevel(
        IList<Vector3> positions, IList<Vector3> normals, IList<Vector4> tangents, IReadOnlyList<int> indices)
    {
        Vector3 deck = DominantUpward(positions, indices);

        float straightUp = Math.Clamp(Vector3.Dot(deck, Vector3.UnitZ), -1f, 1f);
        float off = MathF.Acos(straightUp);

        // Below about half a degree there is nothing to correct, and turning
        // the stand anyway would only lose precision.
        if (off < 0.009f) return;

        Vector3 axis = Vector3.Cross(deck, Vector3.UnitZ);
        if (axis.LengthSquared() < 1e-10f) return;

        Matrix4x4 turn = Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(axis), off);

        for (int i = 0; i < positions.Count; i++)
            positions[i] = Vector3.Transform(positions[i], turn);

        for (int i = 0; i < normals.Count; i++)
            normals[i] = Vector3.TransformNormal(normals[i], turn);

        for (int i = 0; i < tangents.Count; i++)
        {
            Vector4 tangent = tangents[i];

            tangents[i] = new Vector4(
                Vector3.TransformNormal(new Vector3(tangent.X, tangent.Y, tangent.Z), turn),
                tangent.W);
        }
    }

    /// <summary>
    /// The direction the largest single family of upward-facing surface points
    /// in, weighted by how much of the stand it covers.
    /// </summary>
    private static Vector3 DominantUpward(IList<Vector3> positions, IReadOnlyList<int> indices)
    {
        var clusters = new Dictionary<(int, int), (double Area, Vector3 Sum)>();

        for (int i = 0; i + 2 < indices.Count; i += 3)
        {
            int ia = indices[i], ib = indices[i + 1], ic = indices[i + 2];

            if (ia < 0 || ib < 0 || ic < 0) continue;
            if (ia >= positions.Count || ib >= positions.Count || ic >= positions.Count) continue;

            Vector3 a = positions[ia];
            Vector3 cross = Vector3.Cross(positions[ib] - a, positions[ic] - a);

            float size = cross.Length();
            if (size <= 0f) continue;

            Vector3 face = cross / size;

            // Only surfaces facing broadly upwards: a stand's sides say nothing
            // about which way it should sit.
            if (Vector3.Dot(face, Vector3.UnitZ) < 0.5f) continue;

            // Grouped to about two degrees, so one flat deck stays one family
            // rather than splintering across rounding.
            var key = ((int)MathF.Round(face.X * 30f), (int)MathF.Round(face.Y * 30f));

            clusters.TryGetValue(key, out (double Area, Vector3 Sum) seen);
            clusters[key] = (seen.Area + size, seen.Sum + (face * size));
        }

        if (clusters.Count == 0) return Vector3.UnitZ;

        Vector3 winner = Vector3.UnitZ;
        double most = -1;

        foreach ((double area, Vector3 sum) in clusters.Values)
        {
            if (area <= most) continue;
            most = area;
            winner = sum;
        }

        return winner.LengthSquared() < 1e-10f ? Vector3.UnitZ : Vector3.Normalize(winner);
    }

    /// <summary>A node and everything hanging off it.</summary>
    private static IEnumerable<Node> Descend(Node node)
    {
        yield return node;

        foreach (Node child in node.VisualChildren)
        {
            foreach (Node deeper in Descend(child)) yield return deeper;
        }
    }

    /// <summary>
    /// Turns a glTF direction or position into this tool's own footing.
    /// </summary>
    /// <remarks>
    /// glTF holds up as Y, and everything here holds up as Z. Without this a
    /// stand arrives lying on its side.
    /// </remarks>
    private static Vector3 Upright(Vector3 value) => new(value.X, -value.Z, value.Y);

    /// <summary>The picture a material binds to one of its channels, if any.</summary>
    private static TextureImage? ChannelPicture(Material? material, string channelKey)
    {
        if (material is null) return null;

        foreach (MaterialChannel channel in material.Channels)
        {
            if (!channel.Key.Equals(channelKey, StringComparison.OrdinalIgnoreCase)) continue;

            SharpGLTF.Memory.MemoryImage? image = channel.Texture?.PrimaryImage?.Content;
            if (image is null || !image.Value.IsValid) return null;

            return TryReadPicture(image.Value.Content.ToArray());
        }

        return null;
    }

    /// <summary>
    /// Repacks glTF's combined metallic-roughness picture into the mask this
    /// tool's shader reads.
    /// </summary>
    /// <remarks>
    /// glTF fixes the layout: green is roughness and blue is metallic. Ours
    /// wants how polished a surface is rather than how rough, so the green
    /// channel is inverted on the way across.
    /// </remarks>
    private static TextureImage? FromMetallicRoughness(TextureImage? combined)
    {
        if (combined is null) return null;

        var packed = new byte[combined.Width * combined.Height * 4];

        for (int i = 0; i < combined.Width * combined.Height; i++)
        {
            byte roughness = combined.Rgba[(i * 4) + 1];
            byte metallic = combined.Rgba[(i * 4) + 2];

            packed[(i * 4) + 0] = (byte)(255 - roughness);
            packed[(i * 4) + 1] = 0;
            packed[(i * 4) + 2] = metallic;
            packed[(i * 4) + 3] = 255;
        }

        return new TextureImage(combined.Width, combined.Height, packed);
    }

    /// <summary>
    /// Builds the one packed mask the shader reads, from the separate roughness
    /// and metallic pictures these files come with.
    /// </summary>
    /// <remarks>
    /// Roughness is inverted on the way in: the shader asks how polished a
    /// surface is, and roughness answers the opposite question.
    /// </remarks>
    private static TextureImage? Pack(TextureImage? roughness, TextureImage? metallic)
    {
        TextureImage? shape = roughness ?? metallic;
        if (shape is null) return null;

        int width = shape.Width;
        int height = shape.Height;
        var packed = new byte[width * height * 4];

        for (int i = 0; i < width * height; i++)
        {
            byte rough = Sample(roughness, i, width, height, 128);
            byte metal = Sample(metallic, i, width, height, 0);

            packed[(i * 4) + 0] = (byte)(255 - rough);
            packed[(i * 4) + 1] = 0;
            packed[(i * 4) + 2] = metal;
            packed[(i * 4) + 3] = 255;
        }

        return new TextureImage(width, height, packed);
    }

    /// <summary>
    /// One channel of a picture at a position given in another picture's grid,
    /// so two maps of different sizes can still be packed together.
    /// </summary>
    private static byte Sample(TextureImage? image, int at, int width, int height, byte fallback)
    {
        if (image is null || image.Width <= 0 || image.Height <= 0) return fallback;

        int x = at % width;
        int y = at / width;

        int sx = image.Width == width ? x : x * image.Width / Math.Max(1, width);
        int sy = image.Height == height ? y : y * image.Height / Math.Max(1, height);

        int index = ((sy * image.Width) + sx) * 4;
        return index >= 0 && index < image.Rgba.Length ? image.Rgba[index] : fallback;
    }

    /// <summary>
    /// Finds the picture beside the model whose name carries one of the given
    /// words, or - when given none - the one that carries none of them, which
    /// is the colour.
    /// </summary>
    private static TextureImage? FindPicture(string folder, string stem, string[]? words)
    {
        if (!Directory.Exists(folder)) return null;

        string[] extensions = [".png", ".jpg", ".jpeg", ".tga", ".bmp"];
        string? best = null;

        foreach (string path in Directory.EnumerateFiles(folder))
        {
            string name = Path.GetFileNameWithoutExtension(path);

            if (!extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;
            if (!name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
                && !stem.StartsWith(name, StringComparison.OrdinalIgnoreCase)) continue;

            bool mentions = Mentions(name, NormalWords) || Mentions(name, RoughnessWords) || Mentions(name, MetallicWords);

            if (words is null)
            {
                // The colour map is the one that does not announce itself as
                // something else.
                if (mentions) continue;
                best = path;
                break;
            }

            if (!Mentions(name, words)) continue;
            best = path;
            break;
        }

        return best is null ? null : TryReadPicture(best);
    }

    private static bool Mentions(string name, string[] words)
    {
        foreach (string word in words)
        {
            if (name.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// Reads a picture file into plain pixels, or null when it cannot be read.
    /// A stand with a missing map is still worth showing.
    /// </summary>
    private static TextureImage? TryReadPicture(string path)
    {
        try
        {
            using var bitmap = new Bitmap(path);
            return FromBitmap(bitmap);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>As above, for a picture that arrived inside a model file.</summary>
    private static TextureImage? TryReadPicture(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            using var bitmap = new Bitmap(stream);
            return FromBitmap(bitmap);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static TextureImage FromBitmap(Bitmap bitmap)
    {
        {

            int width = bitmap.Width;
            int height = bitmap.Height;

            BitmapData locked = bitmap.LockBits(
                new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            var rgba = new byte[width * height * 4];

            try
            {
                unsafe
                {
                    for (int y = 0; y < height; y++)
                    {
                        byte* row = (byte*)locked.Scan0 + (y * locked.Stride);

                        for (int x = 0; x < width; x++)
                        {
                            int to = ((y * width) + x) * 4;

                            // Stored blue first; the card is handed red first.
                            rgba[to + 0] = row[(x * 4) + 2];
                            rgba[to + 1] = row[(x * 4) + 1];
                            rgba[to + 2] = row[(x * 4) + 0];
                            rgba[to + 3] = row[(x * 4) + 3];
                        }
                    }
                }
            }
            finally
            {
                bitmap.UnlockBits(locked);
            }

            return new TextureImage(width, height, rgba);
        }
    }

    private static T At<T>(IReadOnlyList<T> values, int index, T fallback) =>
        index >= 0 && index < values.Count ? values[index] : fallback;
}
