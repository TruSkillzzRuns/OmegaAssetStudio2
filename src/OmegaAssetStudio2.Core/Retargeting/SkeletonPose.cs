using System.Numerics;
using OmegaAssetStudio2.Core.Meshes;

namespace OmegaAssetStudio2.Core.Retargeting;

/// <summary>
/// Where every bone of a skeleton sits, in the model's own space.
/// </summary>
/// <remarks>
/// A bone records where it sits relative to its parent, so its actual place is
/// only known once the chain above it has been walked. That walk is done once
/// here and kept, because everything else needs it repeatedly.
/// </remarks>
public sealed class SkeletonPose
{
    private SkeletonPose(IReadOnlyList<Matrix4x4> boneToModel, IReadOnlyList<Matrix4x4> modelToBone)
    {
        BoneToModel = boneToModel;
        ModelToBone = modelToBone;
    }

    /// <summary>Where each bone sits, in the model's space.</summary>
    public IReadOnlyList<Matrix4x4> BoneToModel { get; }

    /// <summary>The way back: model space into each bone's own space.</summary>
    public IReadOnlyList<Matrix4x4> ModelToBone { get; }

    public int Count => BoneToModel.Count;

    /// <summary>Where a bone sits, in the model's space.</summary>
    public Vector3 PositionOf(int bone) => BoneToModel[bone].Translation;

    /// <summary>
    /// Works out where every bone of a skeleton rests.
    /// </summary>
    /// <remarks>
    /// Bones are stored with their parents before them, so one pass down the
    /// list is enough. A bone whose parent comes after it would be built from a
    /// parent that is not ready; the skeletons this reads are checked for that
    /// elsewhere, and one that broke the rule is left where it is rather than
    /// silently built wrong.
    /// </remarks>
    public static SkeletonPose Rest(IReadOnlyList<MeshBone> bones)
    {
        ArgumentNullException.ThrowIfNull(bones);

        var boneToModel = new Matrix4x4[bones.Count];
        var modelToBone = new Matrix4x4[bones.Count];

        for (int i = 0; i < bones.Count; i++)
        {
            MeshBone bone = bones[i];

            Matrix4x4 local =
                Matrix4x4.CreateFromQuaternion(Normalised(bone.Orientation)) *
                Matrix4x4.CreateTranslation(bone.Position);

            int parent = bone.ParentIndex;

            // The root names itself as its own parent, and anything pointing
            // forward cannot be resolved, so both stand on their own.
            boneToModel[i] = parent >= 0 && parent < i
                ? local * boneToModel[parent]
                : local;

            modelToBone[i] = Matrix4x4.Invert(boneToModel[i], out Matrix4x4 inverse)
                ? inverse
                : Matrix4x4.Identity;
        }

        return new SkeletonPose(boneToModel, modelToBone);
    }

    /// <summary>
    /// Builds a pose from where each bone stood when the model was bound to it.
    /// </summary>
    /// <remarks>
    /// Preferred over walking the chain whenever a file records it. The chain
    /// gives where a bone sits in that file's node arrangement, which is
    /// however the scene was last left — not the pose the skin weights were
    /// authored against. Using the wrong one moves every vertex by roughly the
    /// size of the character while the bones still match perfectly, which is
    /// exactly how it was found.
    /// </remarks>
    public static SkeletonPose FromBindPoses(IReadOnlyList<Matrix4x4> boneToModel)
    {
        ArgumentNullException.ThrowIfNull(boneToModel);

        var modelToBone = new Matrix4x4[boneToModel.Count];

        for (int i = 0; i < boneToModel.Count; i++)
        {
            modelToBone[i] = Matrix4x4.Invert(boneToModel[i], out Matrix4x4 inverse)
                ? inverse
                : Matrix4x4.Identity;
        }

        return new SkeletonPose([.. boneToModel], modelToBone);
    }

    /// <summary>
    /// A rotation that is safe to build a transform from.
    /// </summary>
    /// <remarks>
    /// Stored rotations are very slightly off unit length, and a matrix built
    /// from one that is not unit length scales the bone as well as turning it —
    /// which shows up as a limb growing along its chain.
    /// </remarks>
    private static Quaternion Normalised(Quaternion rotation)
    {
        float length = rotation.Length();

        return length > 0.0001f ? Quaternion.Normalize(rotation) : Quaternion.Identity;
    }
}
