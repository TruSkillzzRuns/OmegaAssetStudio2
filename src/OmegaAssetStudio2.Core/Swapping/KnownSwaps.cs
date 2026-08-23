namespace OmegaAssetStudio2.Core.Swapping;

/// <summary>
/// The costume pairs already worked out, so they need not be found again.
/// </summary>
/// <remarks>
/// A costume cannot replace just any other. What it replaces has to be the same
/// character in a costume the older game already has, so that the class and the
/// bones the new parts expect are the ones that are there.
/// <para>
/// These are the pairs that have been settled. Anything not here can still be
/// done by naming the two files, which is what the tool does anyway - this is a
/// shortcut, not a limit.
/// </para>
/// </remarks>
public static class KnownSwaps
{
    public static readonly IReadOnlyList<SwapPair> All =
    [
        new() { Source = "Beast_90s", Chassis = "Beast_Astonishing" },
        new() { Source = "BlackBolt_InhumansTV", Chassis = "BlackBolt_ANAD" },
        new() { Source = "DoctorStrange_MovieEnhanced", Chassis = "DoctorStrange_Movie" },
        new() { Source = "Gambit_Classic_Jacketless", Chassis = "Gambit_Classic" },
        // Hulk_PlanetVU rather than Hulk_Revengers, which the entry below
        // already has - two costumes on one chassis means only the second is
        // installed. It is the richest of that character's free chassis (its own
        // base material and a full expression graph), and that base is not a skin one, so a two-sided tag
        // can be written there if the costume ever needs it.
        new() { Source = "Hulk_Ragnarok_Helmetless", Chassis = "Hulk_PlanetVU" },
        new() { Source = "Hulk_Ragnarok", Chassis = "Hulk_Revengers" },
        new() { Source = "Ironman_Mark46Helmetless", Chassis = "Ironman_Mark4" },
        new() { Source = "JeanGrey_Horseman", Chassis = "JeanGrey_90sXmen" },
        // Loki_VoteLoki rather than Loki_AgentOfAsgardVariant: it is the richer
        // chassis of the two (three shader instances, two base materials of its
        // own, fifteen textures) and it leaves the other to the entry below,
        // which had been sharing it.
        new() { Source = "Loki_FinalAct", Chassis = "Loki_VoteLoki" },
        new() { Source = "Loki_Ragnarok", Chassis = "Loki_AgentOfAsgardVariant" },
        // Loki_AgentOfAsgard rather than Loki_Fugitive. Fugitive was chosen for
        // what it could lend the costume - a hair shader and a base material of
        // its own - and on that count it is the better chassis. It was also the
        // one the costume stood still on.
        //
        // What separates the costumes that move from the ones that do not is not
        // settled. What is measured: the three that work all own the animation
        // set loki_teen_as, and Fugitive owns no animation set at all and
        // imports none. Loki_AgentOfAsgard owns loki_teen_as and is the only one
        // of the three not already spoken for, so it is what this is tried on.
        new() { Source = "Loki_SakaarVariant", Chassis = "Loki_AgentOfAsgard" },
        new() { Source = "Loki_Sakaar", Chassis = "Loki_Seige" },
        // The four themed costumes. Each goes onto a costume of the same
        // character that the older game has and nothing else is using. One of
        // them brings a sword, so it goes onto a costume that already carries
        // one: what places a prop is decided outside the costume package and
        // follows the chassis, as the hammer prop showed.
        //
        // The fourth is not here. Its shaders stand on a two-sided skin base,
        // and no costume of that character in the older game owns a base
        // material for a carried one to stand on - so its cut-out pieces draw
        // as solid shards whatever base they are given. Four ways were tried
        // and measured: the masked base and the masked skin base both left them
        // with no shader at all, the plain skin base drew them opaque, and
        // carrying its own base whole crashed the game on load.
        new() { Source = "Psylocke_Horseman", Chassis = "Psylocke_LadyMandarin" },
        new() { Source = "Storm_Horseman", Chassis = "Storm_Astonishing" },
        new() { Source = "Psylocke_Classic90sJacket", Chassis = "Psylocke_ClassicVU" },
        new() { Source = "Punisher_TVMarvelsPunisher", Chassis = "Punisher_TV" },
        new() { Source = "Storm_XTreme", Chassis = "Storm_AfricanGoddess" },
        // The chassis below. Its rig numbers its bones differently from this
        // costume's and its prop is a different hammer, so the hammer sits
        // beside the hand rather than in it. Nothing in the costume package
        // decides that - see the swapped pair below, which has the chassis
        // that does hold it properly.
        new() { Source = "Thor_RagnarokMovie", Chassis = "Thor_AgeOfUltron" },
        // Thor_ModernVU is the one 1.52 costume of that character on the same
        // skeleton as these newer ones, carrying the same prop mesh byte for
        // byte, and owning a two-sided base material already. The prop sits in
        // the hand here and nowhere else, so whichever of the two costumes
        // matters most gets it.
        new() { Source = "Thor_RoadWornSkullMovie", Chassis = "Thor_ModernVU" },
        new() { Source = "Wolverine_Xmen90s", Chassis = "Wolverine_XForceVU" },
    ];

    /// <summary>How a pair reads to someone choosing one.</summary>
    public static string Describe(SwapPair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);

        return $"{Spaced(pair.Source)}  →  replaces  {Spaced(pair.Chassis)}";
    }

    /// <summary>
    /// A costume's name with its parts separated, since they are written
    /// joined in the files themselves.
    /// </summary>
    private static string Spaced(string name) => name.Replace('_', ' ');
}
