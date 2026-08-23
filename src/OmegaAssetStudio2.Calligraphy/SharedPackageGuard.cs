using System;
using System.IO;

namespace OmegaAssetStudio.Cooked;

// Refuses recolor writes against packages that are SHARED across heroes or
// game-wide — so a "recolor a skill for a single hero" never leaks edits into
// other heroes' content or into the game's master package.
//
// Categories of package this guard blocks:
//   • Master package           — MarvelGame.upk (used by EVERY hero/skill)
//   • Shared base material libs — chBaseMaterials*.upk, chResourceMeters*.upk
//   • Engine / editor libs      — EngineMaterials.upk, EditorMaterials.upk, etc.
//   • Explicit shared FX libs   — vfx_shared*.upk
//   • OTHER heroes' VFX libs    — vfx_<otherHero>.upk when we know the current hero
//
// Allowed (the per-hero write surface):
//   • The skill's own UPK              UC__Power<…>_SF.upk
//   • Its sibling effect packages       UC__MarvelConditionEffect_*_SF.upk,
//                                       UC__MarvelEntity_Hotspot_*_SF.upk,
//                                       UC__MarvelProjectile_*_SF.upk
//   • The current hero's VFX library    vfx_<currentHero>.upk (+ vfx_<currentHero>_*.upk)
//
// Truly shared content that legitimately needs per-hero recoloring should be
// localized via the "clone into hero UPK + rebind" path (SharedMaterialLocalizer),
// not blanket-written through this guard.
public static class SharedPackageGuard
{
    public sealed record GuardResult(bool Allowed, string Reason);

    // Hard-block list: package leaf names that are NEVER safe to write,
    // regardless of which hero is being recolored.
    private static readonly string[] HardBlockedLeaves =
    {
        "marvelgame.upk",
        "enginematerials.upk",
        "editormaterials.upk",
    };

    // Hard-block prefixes: leaf NAMES that start with these are shared libraries.
    private static readonly string[] HardBlockedPrefixes =
    {
        "chbasematerials",   // chbasematerials.upk, chbasematerials_v2.upk, etc.
        "chresourcemeters",
        "charresourcemeters",
        "vfx_shared",
        "vfx_global",
        "vfx_common",
    };

    public static GuardResult IsSafeToWrite(string upkPath, string? currentHeroToken)
    {
        if (string.IsNullOrWhiteSpace(upkPath))
            return new GuardResult(false, "empty path");

        string leaf = Path.GetFileName(upkPath).ToLowerInvariant();

        // 1) Master package — never. This is the game's content backbone.
        if (leaf.StartsWith("marvelgame", StringComparison.Ordinal))
            return new GuardResult(false, "master package (MarvelGame.upk) — used game-wide");

        // 2) Hard-block leaves.
        foreach (string b in HardBlockedLeaves)
            if (leaf == b) return new GuardResult(false, $"shared library ({leaf})");
        foreach (string p in HardBlockedPrefixes)
            if (leaf.StartsWith(p, StringComparison.Ordinal))
                return new GuardResult(false, $"shared library (matches '{p}*')");

        // 3) Other heroes' VFX libs. Pattern: vfx_<hero>.upk / vfx_<hero>_<suffix>.upk.
        //    Allow vfx_<currentHero>* always; block vfx_<otherHero>* only when we
        //    KNOW the current hero (otherwise we can't distinguish).
        if (leaf.StartsWith("vfx_", StringComparison.Ordinal))
        {
            // Strip "vfx_" + ".upk" → token. "vfx_thor.upk" → "thor",
            // "vfx_thor_e.upk" → "thor_e" (kept whole — we match by prefix below).
            string token = leaf.Substring(4);
            if (token.EndsWith(".upk", StringComparison.Ordinal))
                token = token.Substring(0, token.Length - 4);

            if (!string.IsNullOrEmpty(currentHeroToken))
            {
                string heroLow = currentHeroToken.ToLowerInvariant();
                // Match if the VFX-lib token starts with the hero token (so
                // "vfx_thor.upk" and "vfx_thor_e.upk" both match hero "thor",
                // but "vfx_storm.upk" does NOT match "thor").
                if (token.StartsWith(heroLow, StringComparison.Ordinal))
                    return new GuardResult(true, "vfx lib for current hero");
                return new GuardResult(false, $"vfx lib for different hero ('{token}', current is '{heroLow}')");
            }
            // Unknown current hero → be conservative and allow vfx_* writes
            // since they're at least scoped to a single hero (just not verified
            // to be THIS hero). Recolorizer always passes the token though.
        }

        // 4) Per-power / per-hero / condition / hotspot / projectile UPKs:
        //    the legitimate write surface — allow.
        return new GuardResult(true, "per-power / per-hero package");
    }
}
