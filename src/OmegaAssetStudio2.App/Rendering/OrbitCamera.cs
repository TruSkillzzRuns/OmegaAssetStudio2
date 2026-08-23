using System.Numerics;

namespace OmegaAssetStudio2.App.Rendering;

/// <summary>
/// A camera that circles a fixed point, which is how a model is inspected: the
/// subject stays put and the viewer walks around it.
/// </summary>
public sealed class OrbitCamera
{
    private const float MinimumPitch = -1.5f;   // just short of straight down
    private const float MaximumPitch = 1.5f;    // just short of straight up
    private const float MinimumDistance = 0.05f;

    /// <summary>
    /// Where the game itself puts its camera, so a model is seen the way it is
    /// seen in play.
    /// </summary>
    /// <remarks>
    /// Read out of the game rather than chosen. Its player camera keeps a list
    /// of offsets it can sit at, and the one the configuration selects by
    /// default is (-50, -50, 70.71): 45 degrees above the ground, turned 135
    /// degrees round from the x axis. The classic isometric view.
    /// <para>
    /// It matters for more than framing. The game's key light points almost
    /// straight down, because the game looks down at its characters; seen from
    /// the side, that light lands on the tops of shoulders and almost nothing
    /// else, and the lighting reads as flat however faithful its numbers are.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// Neither is the game's any more. Its own offset is in world space and its
    /// characters spin freely as they run, so which side of a model it lands on
    /// says nothing about which side is the front - taken literally it put the
    /// camera behind every model.
    /// <para>
    /// So the view is set where a model is best read instead: square in front
    /// of it, at a turn of nothing, which is where its face is - measured by
    /// drawing one at every quarter turn and looking. A few degrees above the
    /// ground rather than level, so the floor reads as a floor.
    /// </para>
    /// <para>
    /// It costs something. The game's key light points almost straight down,
    /// because the game looks down at its characters, and from here that light
    /// lands mostly on the tops of shoulders - so a model is lit flatter than
    /// it would be from above. The view was worth more than the shading.
    /// </para>
    /// </remarks>
    private const float GameYaw = 0f;             //    square in front of the model
    private const float GamePitch = 0.0872665f;   //    5 degrees above the ground

    private float _yaw = GameYaw;
    private float _pitch = GamePitch;

    /// <summary>The point the camera looks at and circles.</summary>
    public Vector3 Target { get; set; }

    /// <summary>How far back the camera sits.</summary>
    public float Distance { get; set; } = 300f;

    /// <summary>How far the model can be from the camera before it is clipped away.</summary>
    public float FarPlane { get; set; } = 10_000f;

    public void Rotate(float horizontal, float vertical)
    {
        _yaw += horizontal;

        // Clamped rather than wrapped: rolling past vertical flips the world
        // upside down, and there is no way for a viewer to tell that happened
        // except that every later drag goes the wrong way.
        _pitch = Math.Clamp(_pitch + vertical, MinimumPitch, MaximumPitch);
    }

    /// <summary>Moves in or out by a proportion, so it feels the same at any distance.</summary>
    public void Zoom(float factor) =>
        Distance = Math.Max(MinimumDistance, Distance * factor);

    /// <summary>Slides the point being looked at, in the camera's own plane.</summary>
    public void Pan(float right, float up)
    {
        Vector3 forward = Vector3.Normalize(Target - Position);
        Vector3 sideways = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, forward));
        Vector3 upwards = Vector3.Cross(forward, sideways);

        // Scaled by distance so a drag covers the same part of the screen
        // whether the camera is close in or far out.
        Target += ((sideways * right) + (upwards * up)) * Distance;
    }

    public Vector3 Position
    {
        get
        {
            float horizontal = Distance * MathF.Cos(_pitch);

            return Target + new Vector3(
                horizontal * MathF.Cos(_yaw),
                horizontal * MathF.Sin(_yaw),
                Distance * MathF.Sin(_pitch));
        }
    }

    /// <summary>Which way the camera is looking, for lighting as well as viewing.</summary>
    public Vector3 Direction => Vector3.Normalize(Target - Position);

    /// <summary>
    /// Frames a model: centres on it and backs off far enough to see all of it.
    /// </summary>
    public void Frame(Vector3 centre, float radius)
    {
        Target = centre;

        float safeRadius = radius > 0.001f ? radius : 1f;

        Distance = safeRadius * 2.6f;
        FarPlane = Math.Max(1000f, safeRadius * 40f);

        // Framing a model puts the view back where the game would have it.
        _yaw = GameYaw;
        _pitch = GamePitch;
    }

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Position, Target, Vector3.UnitZ);

    public Matrix4x4 Projection(float aspectRatio) =>
        Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 4f,
            aspectRatio > 0.01f ? aspectRatio : 1f,
            Math.Max(0.01f, Distance * 0.002f),
            FarPlane);
}
