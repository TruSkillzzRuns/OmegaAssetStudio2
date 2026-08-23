using System.Text;

namespace OmegaAssetStudio2.Core.Retargeting;

/// <summary>Which side of the body a bone belongs to.</summary>
public enum BoneSide
{
    Unknown,
    Left,
    Right,
}

/// <summary>What part of the body a bone drives.</summary>
public enum BoneRegion
{
    Unknown,
    Root,
    Pelvis,
    Spine,
    Neck,
    Head,
    Face,
    Clavicle,
    Shoulder,
    Elbow,
    Wrist,
    Hand,
    Finger,
    Hip,
    Knee,
    Ankle,
    Ball,

    /// <summary>A bone that only spreads the twist of the joint above it.</summary>
    Twist,

    /// <summary>A control bone that poses others rather than moving skin.</summary>
    Control,

    /// <summary>A place something is hung from.</summary>
    Attachment,

    /// <summary>Clothing, hair, and anything else that hangs off the body.</summary>
    Trim,
}

/// <summary>
/// Reads what a bone's name says about it.
/// </summary>
/// <remarks>
/// <b>Every word below was taken from the game's own skeletons</b>, not from
/// what skeletons usually call things. That distinction matters, because this
/// game does not use the usual words: the joint at the top of the leg is
/// <c>hip</c> while the pelvis is its own bone, the middle finger is
/// <c>birdy</c>, and the forearm and clavicle are both spelled wrong
/// (<c>forarm</c>, <c>clavical</c>). Guessing produced a classifier that
/// mistook a dress for a leg.
/// <para>
/// Read across 115 characters: bones are named
/// <c>g_[l_|r_]&lt;part&gt;[number][_offset]</c>, and 80 of them appear on
/// every single character. Two characters of this game therefore match almost
/// entirely by name alone; the looser readings here exist for skeletons brought
/// in from elsewhere.
/// </para>
/// </remarks>
public static class BoneNames
{
    /// <summary>
    /// Leading words that say which rig a bone came from rather than what it
    /// is. <c>g_</c> is this game's own, on eleven thousand of the bones read;
    /// the rest are what other tools put in front.
    /// </summary>
    private static readonly string[] RigPrefixes =
    [
        "g_", "bip01_", "bip001_", "b_", "bone_", "bn_", "chr_", "def_",
        "skel_", "joint_", "jnt_", "mixamorig:", "valvebiped_",
    ];

    /// <summary>Removes a leading rig name, if there is one.</summary>
    public static string StripRig(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        string trimmed = name.Trim();

        foreach (string prefix in RigPrefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return trimmed[prefix.Length..];
        }

        return trimmed;
    }

    /// <summary>
    /// Reduces a name to its letters and digits, lower case, with the rig name
    /// and every separator gone.
    /// </summary>
    public static string Normalise(string name)
    {
        string stripped = StripRig(name);
        if (stripped.Length == 0) return string.Empty;

        var text = new StringBuilder(stripped.Length);

        foreach (char c in stripped)
        {
            if (char.IsLetterOrDigit(c)) text.Append(char.ToLowerInvariant(c));
        }

        return text.ToString();
    }

    /// <summary>
    /// Reduces a name to what it means: side, part, and any number. Two bones
    /// that describe the same joint reduce to the same text however
    /// differently they are spelled.
    /// </summary>
    public static string Describe(string name)
    {
        string raw = StripRig(name);
        string normalised = Normalise(name);

        if (normalised.Length == 0) return string.Empty;

        var parts = new List<string>(4);

        BoneSide side = SideOf(name);
        if (side != BoneSide.Unknown) parts.Add(side.ToString().ToLowerInvariant());

        BoneRegion region = RegionOf(normalised);
        if (region != BoneRegion.Unknown) parts.Add(region.ToString().ToLowerInvariant());

        // Which finger, which spine link. Without it every finger of a hand
        // reduces alike and they pair with one another at random.
        string? part = PartWord(normalised);
        if (part is not null) parts.Add(part);

        if (IsOffset(normalised)) parts.Add("offset");

        var digits = new StringBuilder();
        foreach (char c in normalised)
        {
            if (char.IsDigit(c)) digits.Append(c);
        }

        string number = digits.ToString().TrimStart('0');
        if (digits.Length > 0) parts.Add(number.Length > 0 ? number : "0");

        // A name that says nothing recognisable still has to compare as itself,
        // or every such bone would match every other.
        _ = raw;
        return parts.Count == 0 ? normalised : string.Join('_', parts);
    }

    /// <summary>
    /// Works out which side a bone is on.
    /// </summary>
    /// <remarks>
    /// This game puts the side in its own word, straight after the rig name:
    /// <c>g_l_elbow</c>, <c>g_r_birdy2</c>. Read as whole words rather than as
    /// letters anywhere in the name, or <c>g_pelvis</c> ends in an "s" and
    /// <c>g_l_ball</c> would be read twice over.
    /// </remarks>
    public static BoneSide SideOf(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return BoneSide.Unknown;

        string stripped = StripRig(name).ToLowerInvariant();

        foreach (string word in stripped.Split(['_', '-', ' ', '.', ':'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (word is "l" or "lt" or "lf" or "left") return BoneSide.Left;
            if (word is "r" or "rt" or "rg" or "right") return BoneSide.Right;
        }

        if (stripped.Contains("left", StringComparison.Ordinal)) return BoneSide.Left;
        if (stripped.Contains("right", StringComparison.Ordinal)) return BoneSide.Right;

        return BoneSide.Unknown;
    }

    /// <summary>The game's five finger words, in the order a hand has them.</summary>
    private static readonly string[] Fingers = ["thumb", "index", "birdy", "ring", "pinky"];

    /// <summary>Which finger or which link, when the part comes in several.</summary>
    private static string? PartWord(string normalisedName)
    {
        foreach (string finger in Fingers)
        {
            if (normalisedName.Contains(finger, StringComparison.Ordinal)) return finger;
        }

        return null;
    }

    /// <summary>
    /// True for the paired bone that carries a joint's rest offset. The game
    /// ships these beside many joints — <c>g_spine01_offset</c>,
    /// <c>g_l_hip_offset</c> — and they are not the joint itself.
    /// </summary>
    public static bool IsOffset(string normalisedName) =>
        normalisedName.EndsWith("offset", StringComparison.Ordinal);

    /// <summary>Works out which part of the body a bone drives.</summary>
    public static BoneRegion RegionOf(string normalisedName)
    {
        string n = normalisedName;
        if (n.Length == 0) return BoneRegion.Unknown;

        // Clothing and hair first. This game rigs a dress with bones named
        // after the joints they follow — "l_dressfront_hip", "r_dressback_ankle"
        // — so read for body parts they pass as a hip and an ankle. Bound as
        // such they drag a skirt onto somebody's leg. Nineteen of one
        // character's bones read as real joints until this went in front.
        if (IsTrim(n)) return BoneRegion.Trim;

        // Then the bones that pose others rather than move skin.
        if (Mentions(n, "ikbase", "ikeffector", "iktarget", "ikpole")) return BoneRegion.Control;
        if (Mentions(n, "twist", "twst", "roll")) return BoneRegion.Twist;
        if (Mentions(n, "attach", "socket", "weapon", "throwable", "prop")) return BoneRegion.Attachment;

        // Fingers before the hand they hang off, or "hand" claims them all.
        foreach (string finger in Fingers)
        {
            if (n.Contains(finger, StringComparison.Ordinal)) return BoneRegion.Finger;
        }

        // The face before the head, for the same reason.
        if (Mentions(n, "eyelid", "eyebrow", "eye", "jaw", "lip", "tongue", "cheek", "nose", "brow"))
            return BoneRegion.Face;

        // Arm, from the body outwards. The game spells the forearm "forarm"
        // and the clavicle "clavical"; both are matched as written.
        if (Mentions(n, "clavical", "clavicle", "clav")) return BoneRegion.Clavicle;
        if (Mentions(n, "shoulder", "upperarm", "uparm", "bicep")) return BoneRegion.Shoulder;
        if (Mentions(n, "elbow", "forarm", "forearm", "lowerarm")) return BoneRegion.Elbow;
        if (Mentions(n, "wrist")) return BoneRegion.Wrist;
        if (Mentions(n, "palm", "hand")) return BoneRegion.Hand;

        // Leg, from the body outwards. In this game the top of the leg is the
        // "hip" and the pelvis is a separate bone above it — the opposite way
        // round from how the words are usually used.
        if (Mentions(n, "hip", "thigh", "upleg", "upperleg")) return BoneRegion.Hip;
        if (Mentions(n, "knee", "calf", "shin", "lowerleg")) return BoneRegion.Knee;
        if (Mentions(n, "ankle", "foot")) return BoneRegion.Ankle;
        if (Mentions(n, "ball", "toe")) return BoneRegion.Ball;

        // The spine, and the pelvis at the bottom of it.
        if (Mentions(n, "head", "skull")) return BoneRegion.Head;
        if (Mentions(n, "neck")) return BoneRegion.Neck;
        if (Mentions(n, "spine", "chest", "torso", "breast", "ribcage")) return BoneRegion.Spine;
        if (Mentions(n, "pelvis", "hips", "crotch")) return BoneRegion.Pelvis;
        if (Mentions(n, "root", "armature", "reference")) return BoneRegion.Root;

        return BoneRegion.Unknown;
    }

    /// <summary>
    /// True for a bone that moves clothing, hair, or another loose part rather
    /// than the body itself.
    /// </summary>
    public static bool IsTrim(string normalisedName) =>
        Mentions(normalisedName,
            "dress", "skirt", "cape", "cloak", "coat", "robe", "sash", "scarf",
            "tail", "belt", "strap", "ribbon", "cloth", "hair", "bang", "fur",
            "tassel", "banner", "flap", "loin", "wing", "antenna", "tentacle");

    /// <summary>True for a bone that only spreads the twist of the joint above it.</summary>
    public static bool IsTwist(string normalisedName) =>
        Mentions(normalisedName, "twist", "twst", "roll");

    private static bool Mentions(string value, params string[] words)
    {
        foreach (string word in words)
        {
            if (value.Contains(word, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
