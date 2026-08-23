namespace OmegaAssetStudio2.Core.Packages.Properties;

/// <summary>
/// The tagged properties of one serialised object, plus whatever binary payload
/// follows them.
/// </summary>
/// <remarks>
/// Property names are matched case-insensitively because the name table stores
/// them lower-cased.
/// </remarks>
public sealed class PropertyBag
{
    private readonly PropertyTag[] _tags;
    private readonly Dictionary<string, PropertyTag> _byName;

    internal PropertyBag(PropertyTag[] tags, int payloadOffset)
    {
        _tags = tags;
        PayloadOffset = payloadOffset;

        _byName = new Dictionary<string, PropertyTag>(tags.Length, StringComparer.OrdinalIgnoreCase);
        foreach (PropertyTag tag in tags)
        {
            // Array elements repeat the same name with a rising index. Keep the
            // first; callers that care use GetAll.
            _byName.TryAdd(tag.Name, tag);
        }
    }

    public IReadOnlyList<PropertyTag> Tags => _tags;

    /// <summary>
    /// Offset where the properties end and the object's binary payload begins —
    /// mip data for a texture, vertex data for a mesh.
    /// </summary>
    public int PayloadOffset { get; }

    public bool Contains(string name) => _byName.ContainsKey(name);

    public PropertyTag? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>All properties with this name, in order — for static arrays.</summary>
    public IEnumerable<PropertyTag> FindAll(string name) =>
        _tags.Where(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads an int property, or <paramref name="fallback"/> if absent.</summary>
    public int GetInt(string name, int fallback = 0)
    {
        PropertyTag? tag = Find(name);
        if (tag is null || tag.Value.Length < sizeof(int)) return fallback;
        return BitConverter.ToInt32(tag.Value.Span);
    }

    public float GetFloat(string name, float fallback = 0f)
    {
        PropertyTag? tag = Find(name);
        if (tag is null || tag.Value.Length < sizeof(float)) return fallback;
        return BitConverter.ToSingle(tag.Value.Span);
    }

    /// <summary>
    /// Reads a bool property. These carry their value in the tag rather than in
    /// a value block, so the size is zero and the reader stores the flag here.
    /// </summary>
    public bool GetBool(string name, bool fallback = false)
    {
        PropertyTag? tag = Find(name);
        if (tag is null || tag.Value.Length < 1) return fallback;
        return tag.Value.Span[0] != 0;
    }

    /// <summary>
    /// Reads a name- or enum-valued property, already resolved to text.
    /// </summary>
    public string GetName(string name, string fallback = "")
    {
        PropertyTag? tag = Find(name);
        if (tag is null) return fallback;

        // The reader resolves these into InnerName so callers do not need the
        // name table to interpret them.
        return string.IsNullOrEmpty(tag.InnerName) ? fallback : tag.InnerName;
    }

    /// <summary>Reads an object-reference property.</summary>
    public ObjectReference GetObject(string name)
    {
        PropertyTag? tag = Find(name);
        if (tag is null || tag.Value.Length < sizeof(int)) return ObjectReference.Null;
        return new ObjectReference(BitConverter.ToInt32(tag.Value.Span));
    }

    public override string ToString() => $"{_tags.Length} properties, payload at {PayloadOffset}";
}
