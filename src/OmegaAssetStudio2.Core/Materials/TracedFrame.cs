using OmegaAssetStudio2.Core.Workspace;

namespace OmegaAssetStudio2.Core.Materials;

/// <summary>
/// Which builds have their frame assembled the way their own compiled base
/// pass assembles it.
/// </summary>
/// <remarks>
/// Asked here and nowhere else. The viewport and the sweep that checks the
/// viewport have to answer this the same way or the sweep is guarding a
/// different renderer than the one that ships, so they read one list rather
/// than keeping one each.
/// <para>
/// This is not the same question as whether a surface's materials were read
/// from the build's own compiled shaders - that is MeshSurface.ReadFromShaders,
/// and it is decided per surface. A surface whose material could not be
/// resolved still draws, and it draws with its build's frame.
/// </para>
/// <para>
/// Each build read separately, in its own cache. 1.48.0.1712's ChBaseMaterial
/// base pass builds its frame the same way: the panorama times the mask's
/// reflectivity times ReflectionMult, all of it inside the block the surface
/// colour multiplies, the highlight added to that, the light colour over the
/// sum and the rim after it, with the ambient block scaled by
/// AmbientColorAndSkyFactor. Its hub sets a sky light of 194, 196, 255 at a
/// brightness of 0.06, the same as the other builds measured. 1.52.0.1700's
/// reads the same.
/// </para>
/// </remarks>
public static class TracedFrame
{
    private static readonly string[] Builds = ["1.53.0.203", "1.48.0.1712", "1.52.0.1700"];

    /// <summary>Whether this build's frame is assembled that way.</summary>
    public static bool Draws(string? build)
    {
        if (string.IsNullOrEmpty(build)) return false;

        foreach (string wanted in Builds)
        {
            if (GameClient.Reads(build, wanted)) return true;
        }

        return false;
    }
}
