using System.Numerics;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Retargeting;
using OmegaAssetStudio2.Core.Textures;
using OmegaAssetStudio2.Core.Workspace;

namespace OmegaAssetStudio2.Core.Meshes;

/// <summary>
/// Puts the pieces a costume hangs on itself into the model, so they are drawn
/// with it.
/// </summary>
/// <remarks>
/// The pieces are folded into the model rather than drawn separately: their
/// corners are moved to where their socket sits and appended, and each becomes
/// another run of triangles with its own material. Everything that already
/// decides how a surface looks - what it reflects, whether it is cut away,
/// which side of it is drawn - then applies to them without being written
/// twice.
/// <para>
/// 182 of the 1,792 listed costumes hang something, 513 pieces between them,
/// and 492 of those name a socket their model declares.
/// </para>
/// </remarks>
public static class AttachmentMerger
{
    /// <summary>
    /// The model with its hung pieces added, and the surfaces to draw it with.
    /// Returns what it was given when the costume hangs nothing.
    /// </summary>
    public static (SkeletalMeshLod Lod, IReadOnlyList<MeshSurface> Surfaces) Merge(
        SkeletalMeshLod lod,
        SkeletalMesh mesh,
        IReadOnlyList<MeshAttachment> attachments,
        IReadOnlyList<MeshSocket> sockets,
        TextureReader reader,
        ObjectLocator? locator = null,
        IReadOnlyList<MeshSurface>? surfaces = null,
        string? colours = null)
    {
        ArgumentNullException.ThrowIfNull(lod);
        ArgumentNullException.ThrowIfNull(mesh);

        surfaces ??= [];

        if (attachments.Count == 0 || !lod.HasGeometry) return (lod, surfaces);

        SkeletonPose rest = SkeletonPose.Rest(mesh.Bones);

        var positions = new List<Vector3>(lod.Positions);
        var normals = new List<Vector3>(lod.Normals);
        var tangents = new List<Vector4>(lod.Tangents);
        var uvs = new List<Vector2>(lod.TexCoords);
        var indices = new List<int>(lod.Indices);
        var sections = new List<MeshSection>(lod.Sections);
        var painted = new List<MeshSurface>(surfaces);

        int slot = mesh.Materials.Count;

        foreach (MeshAttachment piece in attachments)
        {
            MeshSocket? where = null;

            foreach (MeshSocket socket in sockets)
            {
                if (!socket.Name.Equals(piece.Socket, StringComparison.OrdinalIgnoreCase)) continue;
                where = socket;
                break;
            }

            // A piece naming a socket the model does not have has nowhere to go,
            // and is left out rather than dropped at the feet.
            if (where is null) continue;

            Matrix4x4 place = Place(where, mesh, rest);

            int first = positions.Count;

            for (int i = 0; i < piece.Mesh.Positions.Count; i++)
            {
                positions.Add(Vector3.Transform(piece.Mesh.Positions[i], place));

                Vector3 normal = i < piece.Mesh.Normals.Count ? piece.Mesh.Normals[i] : Vector3.UnitZ;
                normals.Add(Vector3.Normalize(Vector3.TransformNormal(normal, place)));

                Vector4 tangent = i < piece.Mesh.Tangents.Count
                    ? piece.Mesh.Tangents[i]
                    : new Vector4(Vector3.UnitX, 1f);

                Vector3 along = Vector3.TransformNormal(new Vector3(tangent.X, tangent.Y, tangent.Z), place);
                tangents.Add(new Vector4(Vector3.Normalize(along), tangent.W));

                uvs.Add(i < piece.Mesh.TexCoords.Count ? piece.Mesh.TexCoords[i] : Vector2.Zero);
            }

            // The piece's own materials, resolved from the package it came
            // from, and numbered on after the model's.
            IReadOnlyList<MeshSurface> own = MeshSurfaceResolver.Resolve(
                piece.Source, piece.Mesh.Materials, reader, null, locator, colours);

            foreach (StaticMeshPart part in piece.Mesh.Parts)
            {
                int began = indices.Count;
                int count = part.TriangleCount * 3;

                for (int i = 0; i < count; i++)
                {
                    int at = part.FirstIndex + i;
                    if (at < 0 || at >= piece.Mesh.Indices.Count) break;

                    indices.Add(first + piece.Mesh.Indices[at]);
                }

                int added = indices.Count - began;
                if (added <= 0) continue;

                // Which of the piece's materials this run uses, matched by the
                // reference it names rather than by position, because a piece
                // can name the same material for more than one run.
                MeshSurface? surface = null;

                for (int i = 0; i < piece.Mesh.Materials.Count; i++)
                {
                    if (piece.Mesh.Materials[i] != part.Material) continue;

                    foreach (MeshSurface candidate in own)
                    {
                        if (candidate.MaterialIndex != i) continue;
                        surface = candidate;
                        break;
                    }

                    if (surface is not null) break;
                }

                sections.Add(new MeshSection
                {
                    MaterialIndex = slot,
                    ChunkIndex = 0,
                    BaseIndex = began,
                    TriangleCount = added / 3,
                });

                if (surface is not null) painted.Add(surface with { MaterialIndex = slot });

                slot++;
            }
        }

        return (lod with
        {
            Positions = positions,
            Normals = normals,
            Tangents = tangents,
            TexCoords = uvs,
            Indices = indices,
            Sections = sections,
            VertexCount = positions.Count,
        }, painted);
    }

    /// <summary>Where a socket sits, in the model's own space.</summary>
    private static Matrix4x4 Place(MeshSocket socket, SkeletalMesh mesh, SkeletonPose rest)
    {
        int bone = -1;

        for (int i = 0; i < mesh.Bones.Count; i++)
        {
            if (!mesh.Bones[i].Name.Equals(socket.Bone, StringComparison.OrdinalIgnoreCase)) continue;
            bone = i;
            break;
        }

        Matrix4x4 boneToModel = bone >= 0 && bone < rest.Count ? rest.BoneToModel[bone] : Matrix4x4.Identity;

        // The socket's own offset from the bone, in the bone's own axes. The
        // turn is stored the way this format stores angles, as sixteen-bit
        // steps of a full circle.
        Matrix4x4 offset =
            Matrix4x4.CreateScale(socket.Size == Vector3.Zero ? Vector3.One : socket.Size)
            * Matrix4x4.CreateFromYawPitchRoll(
                Turns(socket.Turn.Z), Turns(socket.Turn.Y), Turns(socket.Turn.X))
            * Matrix4x4.CreateTranslation(socket.Offset);

        return offset * boneToModel;
    }

    /// <summary>An angle in this format's own units, as radians.</summary>
    private static float Turns(float stored) => stored * MathF.Tau / 65536f;
}
