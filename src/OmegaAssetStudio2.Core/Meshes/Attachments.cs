using System.Numerics;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Packages.Properties;
using OmegaAssetStudio2.Core.Workspace;

namespace OmegaAssetStudio2.Core.Meshes;

/// <summary>A named place on a model that something can be hung from.</summary>
public sealed record MeshSocket
{
    public required string Name { get; init; }

    /// <summary>The bone it follows.</summary>
    public required string Bone { get; init; }

    public Vector3 Offset { get; init; }
    public Vector3 Turn { get; init; }
    public Vector3 Size { get; init; } = Vector3.One;

    public override string ToString() => $"{Name} on {Bone}";
}

/// <summary>Something hung on a costume, and where it hangs.</summary>
public sealed record MeshAttachment
{
    /// <summary>The piece to draw.</summary>
    public required StaticMesh Mesh { get; init; }

    /// <summary>The package the piece came from, for finding its materials.</summary>
    public required Package Source { get; init; }

    /// <summary>The socket it hangs from, by name.</summary>
    public required string Socket { get; init; }

    public override string ToString() => $"{Mesh.Name} on {Socket}";
}

/// <summary>
/// Reads the pieces a costume hangs on itself and the places it hangs them.
/// </summary>
/// <remarks>
/// Several costumes have no model of their own. The holiday one is the plain
/// model with strings of lights hung on it: its package holds no skeletal mesh
/// at all, only static meshes, their materials, and a set of attachments naming
/// which particle system spawns which piece at which socket.
/// <para>
/// Every step of that is in the files. An attachment names a particle system;
/// the system's mesh module names the piece; the model's own socket list says
/// which bone the named socket follows and how far from it it sits.
/// </para>
/// </remarks>
public static class AttachmentReader
{
    /// <summary>What a costume says of a piece that is part of how it looks.</summary>
    private const string WhenWorn = "entity_on_appearance_change";

    /// <summary>The sockets a model declares.</summary>
    public static IReadOnlyList<MeshSocket> ReadSockets(Package package, int meshExport)
    {
        ArgumentNullException.ThrowIfNull(package);

        var sockets = new List<MeshSocket>();

        string outer = package.GetExportPath(meshExport);

        for (int i = 0; i < package.Exports.Count; i++)
        {
            if (!package.GetExportClassName(i)
                        .Equals("SkeletalMeshSocket", StringComparison.OrdinalIgnoreCase)) continue;

            if (!package.GetExportPath(i).StartsWith(outer, StringComparison.OrdinalIgnoreCase)) continue;

            PropertyBag? bag = package.TryReadProperties(i);
            if (bag is null) continue;

            string name = bag.GetName("SocketName");
            if (name.Length == 0) continue;

            sockets.Add(new MeshSocket
            {
                Name = name,
                Bone = bag.GetName("BoneName"),
                Offset = ReadVector(bag.Find("RelativeLocation"), Vector3.Zero),
                Turn = ReadVector(bag.Find("RelativeRotation"), Vector3.Zero),
                Size = ReadVector(bag.Find("RelativeScale"), Vector3.One),
            });
        }

        return sockets;
    }

    /// <summary>
    /// The pieces a costume's package hangs on it. Empty for a costume that has
    /// its own model and hangs nothing.
    /// </summary>
    public static IReadOnlyList<MeshAttachment> Read(Package package, ObjectLocator? locator = null)
    {
        ArgumentNullException.ThrowIfNull(package);

        locator ??= new ObjectLocator();

        var found = new List<MeshAttachment>();
        var already = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < package.Exports.Count; i++)
        {
            string kind = package.GetExportClassName(i);

            bool spawnsParticles = kind.Contains("fxparticle", StringComparison.OrdinalIgnoreCase);
            bool hangsAMesh = kind.Contains("meshattachment", StringComparison.OrdinalIgnoreCase);

            if (!spawnsParticles && !hangsAMesh) continue;

            PropertyBag? bag = package.TryReadProperties(i);
            if (bag is null || bag.Tags.Count == 0) continue;

            // Only the pieces that are part of how the costume looks.
            //
            // A costume's package declares every effect it can ever play, and
            // each says when it comes on. Across the listed costumes there are
            // 678 that say nothing, 459 that come on when the entity is killed,
            // 102 on a dodge, and others on hovering, fidgeting, taking damage
            // or being aggravated - none of which is a thing worn while
            // standing still. 188 come on when the entity's appearance changes,
            // and those are the ones that are worn.
            //
            // Drawing the rest put a spawning effect's ground quad behind one
            // costume as a grey slab.
            if (!bag.GetName("ActivationPoint")
                    .Equals(WhenWorn, StringComparison.OrdinalIgnoreCase)) continue;

            foreach (string socket in Sockets(bag, package.Names))
            {
                // A piece hung directly, or one a particle system draws.
                foreach ((StaticMesh mesh, Package source) in Pieces(package, bag, locator))
                {
                    // The same piece appears twice on several costumes, once
                    // for each way it can be turned on; drawing it twice would
                    // put two copies in the same place.
                    if (!already.Add(mesh.Name + "|" + socket)) continue;

                    found.Add(new MeshAttachment
                    {
                        Mesh = mesh,
                        Source = source,
                        Socket = socket,
                    });
                }
            }
        }

        return found;
    }

    /// <summary>The sockets an attachment names, or none.</summary>
    private static IEnumerable<string> Sockets(PropertyBag bag, NameTable names)
    {
        PropertyTag? spawn = bag.Find("SpawnSockets");

        if (spawn is not null)
        {
            foreach (StructArrayElement element in StructArray.ReadElements(spawn, names))
            {
                if (!element.Properties.GetBool("bEnabled", fallback: true)) continue;

                string named = element.Properties.GetName("SocketName");
                if (named.Length > 0) yield return named;
            }

            yield break;
        }

        string single = bag.GetName("SocketName");
        if (single.Length > 0) yield return single;
    }

    /// <summary>The pieces an attachment draws.</summary>
    private static IEnumerable<(StaticMesh Mesh, Package Source)> Pieces(
        Package package, PropertyBag bag, ObjectLocator locator)
    {
        ObjectReference direct = bag.GetObject("Mesh");

        if (!direct.IsNull)
        {
            StaticMesh? one = Piece(package, direct, locator, out Package? from);
            if (one is not null && from is not null) yield return (one, from);
        }

        ObjectReference system = bag.GetObject("ParticleSystemTemplate");
        if (system.IsNull) yield break;

        LocatedObject? holder = locator.TryLocate(package, system);
        if (holder is null) yield break;

        Package owner = holder.Value.Package;
        string outer = owner.GetExportPath(holder.Value.ExportIndex);

        for (int i = 0; i < owner.Exports.Count; i++)
        {
            if (!owner.GetExportClassName(i)
                      .Equals("ParticleModuleTypeDataMesh", StringComparison.OrdinalIgnoreCase)) continue;

            if (!owner.GetExportPath(i).StartsWith(outer, StringComparison.OrdinalIgnoreCase)) continue;

            PropertyBag? module = owner.TryReadProperties(i);
            ObjectReference drawn = module?.GetObject("Mesh") ?? default;

            if (drawn.IsNull) continue;

            StaticMesh? made = Piece(owner, drawn, locator, out Package? from);
            if (made is not null && from is not null) yield return (made, from);
        }
    }

    private static StaticMesh? Piece(
        Package package, ObjectReference reference, ObjectLocator locator, out Package? source)
    {
        source = null;

        LocatedObject? at = locator.TryLocate(package, reference);
        if (at is null) return null;

        if (!at.Value.Package.GetExportClassName(at.Value.ExportIndex)
                .Equals("StaticMesh", StringComparison.OrdinalIgnoreCase)) return null;

        source = at.Value.Package;
        return StaticMeshReader.TryRead(at.Value.Package, at.Value.ExportIndex);
    }

    private static Vector3 ReadVector(PropertyTag? tag, Vector3 fallback)
    {
        if (tag is null || tag.Value.Length < sizeof(float) * 3) return fallback;

        ReadOnlySpan<byte> value = tag.Value.Span;

        return new Vector3(
            BitConverter.ToSingle(value),
            BitConverter.ToSingle(value[4..]),
            BitConverter.ToSingle(value[8..]));
    }
}
