using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OmegaAssetStudio.Calligraphy;

// Generates a single markdown reference of every hero, every visible skill,
// and every recolorable color slot the Skill Recolor pipeline can touch.
// Pipes the EXACT same catalog methods Skill Recolor uses at runtime
// (CollectSkillColorsAsync + CollectHeroPlayerColorsAsync) so the report
// reflects what Apply would actually see.
//
// Output is informational only and must live OUTSIDE the project per
// CLAUDE.md — the caller writes to %USERPROFILE%\Desktop\OmegaAssetStudio_Docs.
public static class HeroSkillRecolorReportBuilder
{
    public sealed record Progress(string Stage, int HeroIndex, int HeroTotal, string HeroLabel, string SkillLabel);

    public static async Task<string> BuildAsync(
        HeroSkillCatalog catalog,
        string cookedDir,
        IProgress<Progress>? progress,
        CancellationToken ct)
    {
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));
        if (string.IsNullOrWhiteSpace(cookedDir) || !Directory.Exists(cookedDir))
            throw new ArgumentException("Cooked directory missing or invalid.", nameof(cookedDir));

        var sb = new StringBuilder();
        sb.AppendLine("# Hero Skill Recolor Reference");
        sb.AppendLine();
        sb.AppendLine("Auto-generated index of every recolorable color slot Skill Recolor can write, per hero/skill.");
        sb.AppendLine();
        sb.AppendLine($"- Generated: {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"- Cooked dir: `{cookedDir}`");
        sb.AppendLine();
        sb.AppendLine("## How to read this");
        sb.AppendLine();
        sb.AppendLine("Each skill section lists every editable color slot the writer would offer. The **Affects** column is the slot's owner — the material it lives on, or the particle emitter that uses it. The **Source UPK** column is which file gets saved when the slot is recolored. `Cross-pkg` means the slot is patched via the surgical allowlist (won't blanket-rewrite a shared library).");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        var heroes = await catalog.EnumerateHeroesAsync(cookedDir).ConfigureAwait(false);

        // Group by base token so each hero appears once even if they have
        // multiple costume variant UPKs.
        var grouped = heroes
            .GroupBy(h => h.Token, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.First().DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Table of contents up front so a long report stays navigable.
        sb.AppendLine("## Heroes");
        sb.AppendLine();
        for (int i = 0; i < grouped.Count; i++)
        {
            string disp = grouped[i].First().DisplayName.Split('—')[0].Trim();
            string anchor = AnchorOf(disp);
            sb.AppendLine($"{i + 1}. [{disp}](#{anchor})");
        }
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        for (int hi = 0; hi < grouped.Count; hi++)
        {
            ct.ThrowIfCancellationRequested();
            var group = grouped[hi];
            string token = group.First().RawToken;
            string heroDisplay = group.First().DisplayName.Split('—')[0].Trim();
            progress?.Report(new Progress("hero", hi + 1, grouped.Count, heroDisplay, string.Empty));

            sb.AppendLine($"## {heroDisplay}");
            sb.AppendLine();
            sb.AppendLine($"- Token: `{token}`");
            string[] variants = group
                .Where(h => !string.IsNullOrEmpty(h.Variant))
                .Select(h => h.Variant)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            sb.AppendLine(variants.Length == 0
                ? "- Variants: (base only)"
                : $"- Variants: {string.Join(", ", variants.Select(v => $"`{v}`"))}");
            sb.AppendLine();

            // Skills for this hero token.
            IReadOnlyList<PowerEntry> skills;
            try { skills = await catalog.GetSkillsAsync(token).ConfigureAwait(false); }
            catch { skills = Array.Empty<PowerEntry>(); }

            if (skills.Count == 0)
            {
                sb.AppendLine("_No skills detected for this hero token._");
                sb.AppendLine();
                continue;
            }

            // Hero-wide pass once per hero, dedup against per-skill entries below.
            IReadOnlyList<HeroSkillCatalog.SkillColorEntry> heroWide;
            try { heroWide = await catalog.CollectHeroPlayerColorsAsync(token, cookedDir).ConfigureAwait(false); }
            catch { heroWide = Array.Empty<HeroSkillCatalog.SkillColorEntry>(); }

            sb.AppendLine($"_Hero-wide pass also reaches **{heroWide.Count}** slot(s) across player/costume/per-power UPKs (added on top of each skill's own slots when you Apply)._");
            sb.AppendLine();

            for (int si = 0; si < skills.Count; si++)
            {
                ct.ThrowIfCancellationRequested();
                PowerEntry power = skills[si];
                string skillLabel = string.IsNullOrEmpty(power.DisplayName) ? (power.PowerName ?? "(unnamed)") : power.DisplayName;
                progress?.Report(new Progress("skill", hi + 1, grouped.Count, heroDisplay, skillLabel));

                sb.AppendLine($"### {skillLabel}");
                sb.AppendLine();
                if (!string.IsNullOrEmpty(power.PowerUnrealClassName))
                    sb.AppendLine($"- Class: `{power.PowerUnrealClassName}`");

                PowerVfxResolver.ResolvedVfx? vfx = null;
                try { vfx = await catalog.ResolveSkillVfxAsync(power, cookedDir).ConfigureAwait(false); }
                catch { /* leave null */ }

                if (vfx is null || vfx.Bindings.Count == 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("_No PowerFX bindings for this skill._");
                    sb.AppendLine();
                    continue;
                }

                var sourceUpks = vfx.Bindings
                    .Where(b => !string.IsNullOrEmpty(b.SourceUpkFullPath))
                    .Select(b => Path.GetFileName(b.SourceUpkFullPath!))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(u => u, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (sourceUpks.Count > 0)
                    sb.AppendLine($"- Bound UPKs: {string.Join(", ", sourceUpks.Select(u => $"`{u}`"))}");

                IReadOnlyList<HeroSkillCatalog.SkillColorEntry> perSkill;
                try { perSkill = await catalog.CollectSkillColorsAsync(vfx).ConfigureAwait(false); }
                catch { perSkill = Array.Empty<HeroSkillCatalog.SkillColorEntry>(); }

                if (perSkill.Count == 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("_No editable color slots reachable from this skill's bindings._");
                    sb.AppendLine();
                    continue;
                }

                sb.AppendLine($"- Editable color slots from this skill alone: **{perSkill.Count}**");
                sb.AppendLine();
                EmitEntriesTable(sb, perSkill);
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void EmitEntriesTable(StringBuilder sb, IReadOnlyList<HeroSkillCatalog.SkillColorEntry> entries)
    {
        sb.AppendLine("| # | Kind | Param | Affects | Current RGB | Source UPK | Cross-pkg |");
        sb.AppendLine("|---|------|-------|---------|-------------|------------|-----------|");
        int idx = 1;
        foreach (var e in entries.OrderBy(x => x.Kind).ThenBy(x => x.OwnerLabel, StringComparer.OrdinalIgnoreCase))
        {
            string rgb = $"`({e.CurrentColor.X:F3}, {e.CurrentColor.Y:F3}, {e.CurrentColor.Z:F3})`";
            string upkLeaf = string.IsNullOrEmpty(e.SourceUpkPath) ? "—" : "`" + Path.GetFileName(e.SourceUpkPath) + "`";
            string crosspkg = e.IsCrossPackage ? "yes" : "";
            string param = string.IsNullOrEmpty(e.ParameterName) ? "—" : "`" + Escape(e.ParameterName) + "`";
            string affects = string.IsNullOrEmpty(e.OwnerLabel) ? "—" : Escape(e.OwnerLabel);
            sb.AppendLine($"| {idx} | {e.Kind} | {param} | {affects} | {rgb} | {upkLeaf} | {crosspkg} |");
            idx++;
        }
    }

    private static string Escape(string s) => s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    private static string AnchorOf(string title)
    {
        var sb = new StringBuilder(title.Length);
        foreach (char c in title.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c == ' ' || c == '-' || c == '_') sb.Append('-');
        }
        return sb.ToString();
    }
}
