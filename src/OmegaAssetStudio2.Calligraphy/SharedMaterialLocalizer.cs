using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UpkManager.Models.UpkFile.Tables;
using UpkManager.Repository;

namespace OmegaAssetStudio.Cooked;

// Stage 1 (read-only) of "localize a shared VFX material so recoloring only
// affects one hero". Given the shared materials a skill's particle emitters
// reference (each living in a DIFFERENT host UPK), this builds the CLONE PLAN:
// the exact export closure that a full clone would copy into the hero's UPK.
//
// The closure is computed reliably from the export table: a cooked UE3
// material's sub-objects (MaterialExpression* nodes that hold the editable
// colors) are OUTERED to the material export, so the closure is "the material
// export + every export whose Outer chain reaches it". No fragile body-byte
// scanning is needed to enumerate it.
//
// Stage 2 (the writer) consumes this plan to RepackWithAddedImports/Exports the
// closure into the hero UPK and rebind the emitter's RequiredModule.Material.
// This class performs NO writes.
public sealed class SharedMaterialLocalizer
{
    private readonly UpkFileRepository _repo = new();

    public sealed record ClosureMember(string ObjectName, string ClassName, int TableIndex, int SerialSize);

    public sealed record MaterialPlan(
        string MaterialExportPath,
        string HostUpkPath,
        bool Found,
        bool HostIsMasterPackage,        // MarvelGame.upk — used game-wide, far riskier to localize
        string MaterialClass,
        int MaterialSerialSize,
        IReadOnlyList<ClosureMember> Closure,   // material + outered sub-objects (expressions, etc.)
        string Note);

    public sealed record LocalizePlan(
        string HeroUpkPath,
        IReadOnlyList<MaterialPlan> Materials)
    {
        public int TotalExportsToClone => Materials.Where(m => m.Found).Sum(m => m.Closure.Count);
        public bool AnyMasterPackage => Materials.Any(m => m.HostIsMasterPackage);
    }

    // Build a dry-run clone plan for the given shared material references.
    // heroUpkPath is the UPK that hosts the skill's particle systems (where the
    // localized copies + emitter rebinds will land in stage 2).
    public async Task<LocalizePlan> BuildPlanAsync(
        string heroUpkPath,
        IEnumerable<(string MaterialExportPath, string HostUpkPath)> sharedRefs)
    {
        var plans = new List<MaterialPlan>();

        // De-dupe by (material, host) — the same shared material is often used by
        // several emitters.
        var distinct = sharedRefs
            .Where(r => !string.IsNullOrWhiteSpace(r.MaterialExportPath) && !string.IsNullOrWhiteSpace(r.HostUpkPath))
            .GroupBy(r => r.MaterialExportPath + "|" + r.HostUpkPath, System.StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First());

        foreach (var (matPath, hostUpk) in distinct)
        {
            plans.Add(await BuildOneAsync(matPath, hostUpk).ConfigureAwait(false));
        }

        return new LocalizePlan(heroUpkPath, plans);
    }

    private async Task<MaterialPlan> BuildOneAsync(string materialExportPath, string hostUpkPath)
    {
        string hostName = Path.GetFileName(hostUpkPath);
        bool isMaster = hostName.StartsWith("MarvelGame", System.StringComparison.OrdinalIgnoreCase);

        if (!File.Exists(hostUpkPath))
            return new MaterialPlan(materialExportPath, hostUpkPath, false, isMaster, string.Empty, 0,
                System.Array.Empty<ClosureMember>(), $"host UPK not found: {hostName}");

        try
        {
            var header = await _repo.LoadUpkFile(hostUpkPath).ConfigureAwait(false);
            await header.ReadHeaderAsync(null).ConfigureAwait(false);

            // Resolve the material export. Match on full path first, then on leaf
            // name (the recolorizer sometimes only has the leaf).
            string leaf = materialExportPath.Contains('.')
                ? materialExportPath[(materialExportPath.LastIndexOf('.') + 1)..]
                : materialExportPath;

            UnrealExportTableEntry? mat =
                header.ExportTable.FirstOrDefault(e => string.Equals(e.GetPathName(), materialExportPath, System.StringComparison.OrdinalIgnoreCase))
             ?? header.ExportTable.FirstOrDefault(e => string.Equals(e.ObjectNameIndex?.Name, leaf, System.StringComparison.OrdinalIgnoreCase));

            if (mat is null)
                return new MaterialPlan(materialExportPath, hostUpkPath, false, isMaster, string.Empty, 0,
                    System.Array.Empty<ClosureMember>(), $"material export not found in {hostName}");

            int matIdx1 = mat.TableIndex;
            string matClass = mat.ClassReferenceNameIndex?.Name ?? "UMaterial";

            // Closure = material itself + every export whose Outer chain reaches it.
            var closure = new List<ClosureMember>
            {
                new(mat.ObjectNameIndex?.Name ?? leaf, matClass, matIdx1, SafeSerial(mat)),
            };
            foreach (var e in header.ExportTable)
            {
                if (ReferenceEquals(e, mat)) continue;
                if (OuterChainReaches(header, e, matIdx1))
                    closure.Add(new ClosureMember(
                        e.ObjectNameIndex?.Name ?? "?",
                        e.ClassReferenceNameIndex?.Name ?? "Object",
                        e.TableIndex,
                        SafeSerial(e)));
            }

            string note = isMaster
                ? "HOST IS THE GAME MASTER PACKAGE (MarvelGame.upk) — this material is used game-wide; localizing it is heavy and high-risk."
                : $"{closure.Count} export(s) would be copied into the hero UPK; texture refs stay as imports.";

            return new MaterialPlan(materialExportPath, hostUpkPath, true, isMaster, matClass,
                SafeSerial(mat), closure, note);
        }
        catch (System.Exception ex)
        {
            return new MaterialPlan(materialExportPath, hostUpkPath, false, isMaster, string.Empty, 0,
                System.Array.Empty<ClosureMember>(), $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // Walk an export's Outer chain (OuterReference is a 1-based export index, or
    // <= 0 at the package root) and report whether it reaches targetIndex1.
    private static bool OuterChainReaches(UpkManager.Models.UpkFile.UnrealHeader header, UnrealExportTableEntry e, int targetIndex1)
    {
        var cur = e;
        int guard = 0;
        while (cur is not null && guard++ < 128)
        {
            int outer = cur.OuterReference;
            if (outer <= 0) return false;
            if (outer == targetIndex1) return true;
            int idx = outer - 1;
            if (idx < 0 || idx >= header.ExportTable.Count) return false;
            cur = header.ExportTable[idx];
        }
        return false;
    }

    private static int SafeSerial(UnrealExportTableEntry e)
    {
        try { return e.UnrealObjectReader?.GetBytes()?.Length ?? 0; }
        catch { return 0; }
    }
}
