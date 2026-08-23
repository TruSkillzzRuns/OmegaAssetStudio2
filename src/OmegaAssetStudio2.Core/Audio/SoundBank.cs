namespace OmegaAssetStudio2.Core.Audio;

/// <summary>
/// The name-to-number function the sound middleware uses.
/// </summary>
/// <remarks>
/// FNV-1 over the lowercase bytes of the name — the original, not the 1a
/// variant, which differs in the order of the multiply and the exclusive-or and
/// produces entirely different numbers. Nothing in a shipped game records the
/// names themselves, so this is the only way back from a number to a name: hash
/// a candidate and see whether it matches.
/// </remarks>
public static class SoundNameHash
{
    private const uint OffsetBasis = 0x811C9DC5u;
    private const uint Prime = 0x01000193u;

    public static uint Of(string name)
    {
        uint hash = OffsetBasis;

        foreach (char ch in name)
        {
            hash *= Prime;
            hash ^= (byte)char.ToLowerInvariant(ch);
        }

        return hash;
    }
}

/// <summary>
/// A sound bank's object graph, far enough to get from an event to the sounds
/// it plays.
/// </summary>
/// <remarks>
/// The chain is event → action → sound, with containers in between when an event
/// picks at random from several takes:
/// <code>
/// event (4)     → the actions it fires
/// action (3)    → the object it acts on
/// container (5, 6, 9) → its children
/// sound (2)     → the identifier of the audio itself
/// </code>
/// Only those four kinds are read. The rest — buses, effects, music — are
/// stepped over by their recorded length, so an unknown kind cannot derail the
/// walk.
/// </remarks>
public sealed class SoundBank
{
    private const byte SoundType = 2;
    private const byte ActionType = 3;
    private const byte EventType = 4;
    private const byte ContainerType = 5;
    private const byte SwitchContainerType = 6;
    private const byte LayerContainerType = 9;

    private static readonly byte[] HierarchyTag = "HIRC"u8.ToArray();

    private readonly Dictionary<uint, uint> _soundSources = [];
    private readonly Dictionary<uint, uint> _actionTargets = [];
    private readonly Dictionary<uint, List<uint>> _eventActions = [];
    private readonly Dictionary<uint, List<uint>> _containerChildren = [];

    /// <summary>Every object this bank declares, for checking children against.</summary>
    private readonly HashSet<uint> _ids = [];

    private SoundBank() { }

    /// <summary>Every event this bank declares.</summary>
    public IEnumerable<uint> Events => _eventActions.Keys;

    /// <summary>Every sound this bank declares, whether an event reaches it or not.</summary>
    public IEnumerable<uint> Sources => _soundSources.Values;

    /// <summary>How many containers this bank declares, and how many gave up their children.</summary>
    public (int Total, int WithChildren) Containers =>
        (_containerChildren.Count, _containerChildren.Count(c => c.Value.Count > 0));

    /// <summary>
    /// Reads a bank's object graph. Returns an empty bank rather than throwing
    /// when the bytes are not one, so a caller can walk a whole container
    /// without guarding each bank.
    /// </summary>
    public static SoundBank Read(ReadOnlySpan<byte> bytes)
    {
        var bank = new SoundBank();

        for (int at = 0; at + 8 <= bytes.Length;)
        {
            ReadOnlySpan<byte> tag = bytes.Slice(at, 4);
            int length = BitConverter.ToInt32(bytes.Slice(at + 4, 4));

            if (length < 0 || at + 8 + (long)length > bytes.Length) break;

            if (tag.SequenceEqual(HierarchyTag))
                bank.ReadHierarchy(bytes.Slice(at + 8, length));

            at += 8 + length;
        }

        return bank;
    }

    private void ReadHierarchy(ReadOnlySpan<byte> section)
    {
        if (section.Length < 4) return;

        // Kept aside: a container's children can only be recognised once every
        // object in the bank is known, and containers come before some of them.
        var containerBodies = new Dictionary<uint, byte[]>();

        int count = BitConverter.ToInt32(section[..4]);
        int at = 4;

        for (int i = 0; i < count && at + 5 <= section.Length; i++)
        {
            byte type = section[at];
            int size = BitConverter.ToInt32(section.Slice(at + 1, 4));

            int bodyStart = at + 5;
            if (size < 4 || bodyStart + (long)size > section.Length) break;

            uint id = BitConverter.ToUInt32(section.Slice(bodyStart, 4));
            ReadOnlySpan<byte> body = section.Slice(bodyStart + 4, size - 4);

            _ids.Add(id);

            switch (type)
            {
                case SoundType:
                    // Plugin identifier, then how it is stored, then the
                    // identifier of the audio itself.
                    if (body.Length >= 9) _soundSources[id] = BitConverter.ToUInt32(body.Slice(5, 4));
                    break;

                case ActionType:
                    // What kind of action, then what it acts on.
                    if (body.Length >= 6) _actionTargets[id] = BitConverter.ToUInt32(body.Slice(2, 4));
                    break;

                case EventType:
                    _eventActions[id] = ReadIdentifiers(body);
                    break;

                case ContainerType:
                case SwitchContainerType:
                case LayerContainerType:
                    containerBodies[id] = body.ToArray();
                    break;
            }

            at = bodyStart + size;
        }

        ResolveChildren(containerBodies);
    }

    /// <summary>
    /// Reads an event's list of actions.
    /// </summary>
    /// <remarks>
    /// The count is a four-byte number in the version this game ships and a
    /// single byte in later ones. Both are tried, longest first, and a count
    /// that does not fit the remaining bytes is rejected — so a misread is
    /// caught rather than producing a list of rubbish identifiers.
    /// </remarks>
    private static List<uint> ReadIdentifiers(ReadOnlySpan<byte> body)
    {
        var ids = new List<uint>();
        if (body.Length < 4) return ids;

        int count = BitConverter.ToInt32(body[..4]);
        int start = 4;

        if (count < 0 || count > 64 || 4 + ((long)count * 4) > body.Length)
        {
            count = body[0];
            start = 1;

            if (count > 64 || start + ((long)count * 4) > body.Length) return ids;
        }

        for (int i = 0; i < count; i++)
            ids.Add(BitConverter.ToUInt32(body.Slice(start + (i * 4), 4)));

        return ids;
    }

    /// <summary>
    /// Finds each container's children.
    /// </summary>
    /// <remarks>
    /// The children are a count followed by that many identifiers, but not at a
    /// fixed place and not at the end: what precedes them depends on how the
    /// container is set up, and a container that plays its children in a chosen
    /// order stores that order after them.
    /// <para>
    /// So the body is scanned for a block that could be it, and each candidate
    /// is tested against something a coincidence cannot satisfy: every
    /// identifier in it must be an object this same bank declares. Requiring
    /// the block to end at the end of the body instead — the obvious rule —
    /// found the children of only 10 of one character's 70 containers, and 1 of
    /// another's 96.
    /// </para>
    /// </remarks>
    private void ResolveChildren(Dictionary<uint, byte[]> bodies)
    {
        foreach ((uint id, byte[] body) in bodies)
        {
            List<uint> best = [];

            for (int at = 0; at + 4 <= body.Length; at++)
            {
                int count = BitConverter.ToInt32(body, at);

                if (count <= 0 || count > 1024) continue;
                if (at + 4 + ((long)count * 4) > body.Length) continue;
                if (count <= best.Count) continue;

                var children = new List<uint>(count);
                bool all = true;

                for (int i = 0; i < count; i++)
                {
                    uint child = BitConverter.ToUInt32(body, at + 4 + (i * 4));

                    if (!_ids.Contains(child)) { all = false; break; }

                    children.Add(child);
                }

                if (all) best = children;
            }

            _containerChildren[id] = best;
        }
    }

    /// <summary>
    /// Every sound an event can play, including each take of a random one.
    /// </summary>
    public IEnumerable<uint> SoundsOf(uint eventId)
    {
        if (!_eventActions.TryGetValue(eventId, out List<uint>? actions)) yield break;

        foreach (uint action in actions)
        {
            if (!_actionTargets.TryGetValue(action, out uint target)) continue;

            foreach (uint source in Follow(target, depth: 0))
                yield return source;
        }
    }

    private IEnumerable<uint> Follow(uint id, int depth)
    {
        // Containers nest, and a malformed bank could point one at itself.
        if (depth > 8) yield break;

        if (_soundSources.TryGetValue(id, out uint source))
        {
            yield return source;
            yield break;
        }

        if (!_containerChildren.TryGetValue(id, out List<uint>? children)) yield break;

        foreach (uint child in children)
            foreach (uint reached in Follow(child, depth + 1))
                yield return reached;
    }
}
