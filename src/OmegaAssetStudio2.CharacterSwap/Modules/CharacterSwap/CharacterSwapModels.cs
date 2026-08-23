using System.Collections.Generic;

namespace OmegaAssetStudio.WinUI.Modules.CharacterSwap;

// Per-export transfer feasibility for a Character Swap operation.
//
// Goal: take a character UPK from a newer game version (source) and make it
// drop-in usable as a replacement for an existing character UPK in an older
// game version (target). The analysis tells us, per export, whether a byte
// copy is sufficient or whether a real per-class re-serializer is required.
public enum SwapFeasibility
{
    // Source export not present in target — the target version would gain a
    // new object. Usually safe (just appended) as long as classes resolve.
    AddNew,

    // Target export not present in source — would orphan or have to be
    // preserved from the original target file.
    KeepFromTarget,

    // Both sides have it. Class is one we know is version-stable, so byte
    // copy from source into a v868-shaped envelope should work.
    DirectCopyViable,

    // Both sides have it but the class is known-format-changed between
    // versions. Requires a real per-class re-serializer (tasks #14–16).
    NeedsReserializer,

    // Both sides have it. Same package version on both → trivial swap.
    SameVersionSwap,
}

public sealed class CharacterSwapEntry
{
    public string ObjectName { get; init; } = string.Empty;
    public string ClassName { get; init; } = string.Empty;
    public SwapFeasibility Feasibility { get; init; }
    public int SourceSize { get; init; }
    public int TargetSize { get; init; }
    public string Notes { get; init; } = string.Empty;
}

public sealed class CharacterSwapReport
{
    public string SourceUpkPath { get; init; } = string.Empty;
    public string TargetUpkPath { get; init; } = string.Empty;
    public ushort SourceVersion { get; init; }
    public ushort TargetVersion { get; init; }
    public bool VersionMismatch => SourceVersion != TargetVersion;

    public int SourceExportCount { get; init; }
    public int TargetExportCount { get; init; }
    public int SourceNameCount { get; init; }
    public int TargetNameCount { get; init; }
    public int SourceImportCount { get; init; }
    public int TargetImportCount { get; init; }

    public List<CharacterSwapEntry> Entries { get; } = [];

    public int AddNewCount { get; set; }
    public int KeepFromTargetCount { get; set; }
    public int DirectCopyViableCount { get; set; }
    public int NeedsReserializerCount { get; set; }
    public int SameVersionSwapCount { get; set; }

    // Class names that source uses but target's name table does not contain.
    // These are the actual hard blockers — even a perfect re-serializer
    // can't materialize a class the target engine doesn't know.
    public List<string> SourceClassesMissingFromTarget { get; } = [];

    // Per-class roll-up: count of exports per class for source vs target.
    // Useful for spotting "source has 5 textures, target has 3" type drift.
    public List<ClassPopulation> ClassPopulations { get; } = [];

    public string SummaryText { get; set; } = string.Empty;
    public string Recommendation { get; set; } = string.Empty;
}

public sealed class ClassPopulation
{
    public string ClassName { get; init; } = string.Empty;
    public int SourceCount { get; init; }
    public int TargetCount { get; init; }
    public bool VersionStable { get; init; }
}
