using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OmegaAssetStudio;
using OmegaAssetStudio.BackupManager;
using UpkManager.Models.UpkFile;
using UpkManager.Models.UpkFile.Tables;
using UpkManager.Repository;
using OmegaAssetStudio.WinUI.UpkInterop.CrossVersion;

namespace OmegaAssetStudio.WinUI.Modules.CharacterSwap;

// Phase 2 — Material Transplant.
//
// Path C in our agreed roadmap: skip the v894→v868 SkeletalMesh re-serializer
// (weeks of UE3 binary-format work) and ship the cheaper, higher-value piece
// first: add the new MaterialInstanceConstant + Texture2Ds + PhysicalMaterial
// + PhysicsAsset from the 1.53 source into the 1.52 target. The 1.52 mesh
// shape stays; what changes is the materials/textures applied to it.
//
// This first cut is intentionally CONSERVATIVE — it only EXTENDS target's
// tables; it does NOT yet rewire any existing target export's properties to
// point at the newly-added exports. So after this run, the new MIC/textures
// sit in the file as "orphan" content — nothing references them yet. The
// purpose is to validate the table-extension write path end-to-end before we
// attempt the trickier reference rewiring step.
//
// Successful smoke test for THIS turn = "file loads cleanly and the costume
// equips with target's original look". That proves we can grow the file
// without corrupting it. Next turn: wire the references so the new MIC and
// textures actually get used.
public sealed class Phase2MaterialExtender
{
    public sealed class Result
    {
        public string OutputPath { get; init; } = string.Empty;
        public string? BackupPath { get; init; }
        public int NamesAdded { get; init; }
        public int ImportsAdded { get; init; }
        public int ExportsAdded { get; init; }
        public long OutputBytes { get; init; }
        public List<string> Issues { get; } = new();
        public string Summary { get; init; } = string.Empty;
    }

    // Classes whose matched body is taken from source WHOLE, without the
    // property-level merge.
    //
    // The merge exists to protect references: it keeps target's value wherever
    // source's would come out naming nothing. To do that it rebuilds the body
    // from parsed property spans and then appends TARGET's binary tail, since
    // for everything it was written for - components, pawn defaults, shader
    // instances - the tail is either absent or target's is the correct one.
    //
    // For a morph target that is exactly backwards. A morph target's entire
    // payload IS the binary tail: a list of per-vertex offsets, with almost no
    // properties in front of it. Merging one therefore produces source's
    // (empty) property list followed by target's offsets - which is target's
    // shape, unchanged, however the rest of the swap went. Verified on
    // One costume: with morphtarget re-translation enabled but the merge still
    // applied, all six claw shapes still came out at the older costume's 1,144
    // bytes rather than the newer costume's 1,424.
    //
    // Taking source's body whole is safe here precisely because the payload is
    // offsets rather than references - there is nothing in it that can name a
    // missing object, so there is nothing for the merge to protect.
    private static readonly HashSet<string> MergeKeepsSourceWholeClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "morphtarget",
    };

    // Default set of class names whose source-only exports we transplant for
    // the material-only path. SkeletalMesh is intentionally excluded — its
    // 360KB binary body would need a real v894→v868 re-serializer.
    // Narrow allowlist (v1.0.33+): ONLY visual-layer exports are carried
    // across. The previous broad list also included skeletalmesh /
    // skeletalmeshsocket / physicalmaterial / package / apexclothingasset,
    // but those rely on full-export-path dedup against the target — and
    // 1.53 source UPKs often rename or add their SkeletalMesh export, which
    // breaks the dedup and produces duplicate SkeletalMesh + duplicated
    // sockets in the output (a verified test case shipped 4 skeletalmesh +
    // 106 skeletalmeshsocket exports vs target's 2 + 53, causing
    // TArray-bounds asserts on avatar load + T-pose + wrong materials).
    // The narrow list keeps target's mesh / sockets / physics untouched
    // and only overlays the source's new MICs + textures, which is what
    //
    // To restore the broad behavior (if you need to transplant new
    // sockets / new mesh / new cape physics from source), uncomment the
    // additional entries below. Anything that depends on full-path dedup
    // will still suffer the duplication issue until smarter dedup is
    // implemented (see fix path "B" in dev notes).
    private static readonly HashSet<string> DefaultClassAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "materialinstanceconstant",
        "texture2d",
        "package",
        // v1.0.34: structural classes back in. The duplication bug that
        // from pickedExports deduping by FULL PATH only — source's
        // SkeletalMeshSocket
        // (whose Outer is source's SkeletalMesh) had a different path from
        // target's same-named socket (whose Outer is target's SkeletalMesh)
        // and got APPENDED on top of the same-name matched-override that
        // also replaced target's slot. Result: 53 source sockets +
        // 53 target sockets (still holding the matched-override body) =
        // 106 socket exports, all malformed. Fix below: extend the dedup
        // in pickedExports to ALSO skip when there's a target export with
        // matching class+name, regardless of path. Same source export now
        // ends up in matchedOverrides ONLY, not in pickedExports too.
        "skeletalmesh",
        "skeletalmeshsocket",
        "physicalmaterial",
        "apexclothingasset",
        // NOT here, and the attempt is worth recording so it is not repeated:
        // "morphtarget" and "morphtargetset".
        //
        // The problem they were meant to solve is real. A morph target is a
        // list of per-vertex offsets and only means anything against the mesh
        // it was authored for, so the older costume's shapes cannot drive the
        // carried mesh. One costume's blade shapes never retract for exactly
        // this reason: the six shapes (l_/r_ clawinner, clawmiddle, clawouter)
        // stayed bound to the older mesh while the component drew the newer
        // one.
        //
        // Carrying them made it worse. A set names its shapes through a flat
        // array of object references, and the rewriter leaves flat arrays
        // alone on purpose — it cannot tell an array's element size from the
        // bytes, so guessing there would corrupt every other array it met.
        // The carried set therefore kept the newer costume's raw indices, and
        // in the older package those slots are packages: the built set named
        // marvelgamecontent, uc__marvelplayer_wolverine_xforcevu_sf and
        // vfx_shared_materials_a as its six shapes. Meanwhile the shapes
        // themselves deduplicated by name into the older costume's slots and
        // kept the older data (1,144 bytes against the newer 1,424), so
        // nothing was gained either.
        //
        // Doing this properly needs the flat-array case handled for known
        // object-array properties, and the shapes replaced in place rather
        // than added alongside. Until then the claws stay out.
    };


    // Broad allowlist (legacy v1.0.32 behavior). Kept as a named constant so
    // a single line swap in `pickedExports` filtering re-enables it. Carries
    // the documented duplication bug for non-matching SkeletalMesh paths.
    private static readonly HashSet<string> BroadClassAllowlistLegacy = new(StringComparer.OrdinalIgnoreCase)
    {
        "materialinstanceconstant",
        // NOTE: base Material + materialexpression* + materialfunction were
        // briefly added to fix non-skin MIC parent nulls. Result was worse
        // than blue rendering — transparent costume — because the base
        // Material's expression graph references engine-intrinsic function
        // nodes (engine_materialfunctions02.math.* etc.) that 1.52's engine
        // binary doesn't expose under the same paths as 1.53. Picking the
        // graph leaves ~50% of refs null → renderer fallback to translucent.
        // Better solution: alias source's non-skin base material name to
        // 1.52's existing chbasematerial import (see DefaultAliases below).
        // 1.52's import points at a working v868 shader; the alias makes the
        // MIC parents resolve to it; the MIC's TextureParameter values pass
        // through with our transplanted textures overlaid.
        // NOTE: physics chain (physicsasset / physicsassetinstance /
        // rb_bodyinstance / rb_bodysetup / rb_constraintinstance /
        // rb_constraintsetup) is INTENTIONALLY EXCLUDED. Transplanting
        // them carries source's collision geometry (FKAggregateGeom with
        // BoxElems/SphereElems/SphylElems) through PropertyTagRewriter,
        // which doesn't deep-translate the nested struct + arrays
        // correctly — the rb_bodysetups end up with empty AggGeom and
        // the engine hard-segfaults the first time a throwable or power
        // tries to instantiate a physics shape (~12s after world entry).
        // Symptoms: "FKAggregateGeom: No geometries" + "URB_BodyInstance::
        // InitBody : Could not create new Shape" warnings then a bare
        // Fatal error with no error message.
        // Workaround: skip the physics chain entirely. Target's existing
        // PhysicsAsset for the costume stays referenced via the
        // matched SkeletalMeshComponent (its physicsasset[0] tag is on
        // CriticalPreferTargetNames) and powers / throwables use the
        // working v868 physics — no segfault. Cost: the new costume gets
        // target's slightly-different collision shape, which is invisible
        // to the player. Re-enable once we have a proper FKAggregateGeom
        // walker that preserves the per-bone shape arrays.
        "physicalmaterial",
        // Empty package wrapper export the 1.53 file uses for the new
        // content's package namespace. Cheap to add.
        "package",
        // SkeletalMesh — re-enabled. The v894 body's binary tail uses the
        // SAME structural layout as v868 (proven: existing parser walks
        // both with 100% byte coverage). The earlier freeze was caused by
        // untranslated FName/FObject references inside the binary tail,
        // not a format incompatibility. SkeletalMeshReferenceTranslator
        // now uses the recorder hooks on UnrealHeader to capture every
        // ref position during parse and rewrites them via IndexTranslator.
        "skeletalmesh",
        // SkeletalMesh sub-exports: the engine stores per-mesh sockets
        // (attachment points used by weapons/FX/etc.) as separate exports
        // whose Outer is the SkeletalMesh. The mesh body holds an FObject
        // array referencing them by ref idx, so they MUST be transplanted
        // alongside the mesh or the translator writes nulls into the
        // sockets array → crash on first access.
        "skeletalmeshsocket",
        // ApexClothingAsset is the cloth-physics container for capes/etc.
        // Same story: referenced from the mesh body's ClothingAssets array,
        // so it must come with the mesh.
        "apexclothingasset",
        // Texture2D: transplanted with mips INLINED via Texture2DBodyWalker
        // + Phase2SourceTextureLoader. The walker rewrites the body to clear
        // TextureFileCacheName/Guid and replaces each mip's bulk data with
        // inline-uncompressed pixel bytes pulled from source's .tfc. Result:
        // the new texture has zero TFC dependency and the target engine
        // reads pixels straight out of the UPK. Same approach the existing
        // HUD-texture inline-injection uses (TexturePreviewInjector).
        "texture2d",
        // NOTE: original blocker comment kept below for history. Replaced by
        // streams every loaded Texture2D's mip data from the TFC files
        // referenced by its TextureFileCacheGuid + TextureFileCacheName.
        // The source's textures point at 1.53 TFC files/offsets that don't
        // exist in 1.52's TFC, so the streaming layer reads garbage memory
        // and crashes the renderer thread (~6s after the costume equips).
        // Re-enable once we implement either:
        //   (a) TFC manifest+payload append (write the new mips into a 1.52
        //       TFC and add manifest entries), or
        //   (b) mip inlining (strip the TFC reference and embed mip data
        //       directly in the UPK body).
    };

    // Classes for which we treat "1 target instance + 1 source instance with
    // different names" as a match. Used to swap target's old MIC body for
    // source's new MIC body without renaming target's export slot. This is
    // what actually wires the visual upgrade — without it, source's MIC is
    // orphan content that nothing references, so the costume keeps target's
    // original look even when everything else succeeds.
    public static readonly HashSet<string> ClassSingletonMatchClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "materialinstanceconstant",
    };

    // Matched SAME-SIZE exports whose CLASS is in this set get translated
    // anyway (normally same-size matched exports are skipped as
    // "unchanged"). For SkeletalMeshComponent the body holds the actual
    // SkeletalMesh ObjectRef — without translating, target's existing
    // mesh import wins even when we've added a new SkeletalMesh export.
    public static readonly HashSet<string> ForceSameSizeMatchedTranslateClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "skeletalmeshcomponent",
    };

    // Matched-size-changed exports whose CLASS is in this set are NEVER
    // re-translated, even when the user enables matched-translate. Reason:
    // these exports sit in the physics chain and translating them often
    // re-points them at the newly-added (orphan) PhysicsAsset whose
    // DefaultInstance is null. The engine treats a broken physics setup as
    // "don't spawn the avatar" → invisible character. Until we wire physics
    // properly, keep target's working physics bodies in place.
    public static readonly HashSet<string> DefaultMatchedTranslateExcludeClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "physicsassetinstance",
        "rb_bodyinstance",
        "rb_bodysetup",
        "rb_constraintinstance",
        "rb_constraintsetup",
        // Animation chain: merging target's vanilla AnimSequence / AnimSet /
        // AnimNotify_* bodies with source's 1.53 bytes ends up writing
        // cross-version enum FName indices, compression-format bytes, and
        // object refs into target's anim exports. At world entry the engine
        // reads one of those merged exports as a UAnimSequence, hits a
        // KeyEncodingFormat / AnimationCompressionFormat byte that no longer
        // matches a known enum value, and trips appError("unknown or
        // unsupported animation format"). The new costume doesn't need its
        // source's anims — playback uses the Pawn class's animset references
        // which we keep target-side via SkeletalMeshComponent translation.
        "animsequence",
        "animset",
        "animnotify",
        "animnotify_akevent",
        "animnotify_playparticleeffect",
        "animnotify_trails",
        "animnotify_sound",
        "animnotify_footstep",
        "animnotify_viewshake",
        "animnotify_script",
        "animnotify_scripted",
        "animnotify_kismet",
        "animnotify_rumble",
        "marvelanimnotify_contactframe",
        "marvelanimnotify_footstep",
        "marvelanimnotify_jumpup",
        "marvelanimnotify_kneedown",
        "marvelanimnotify_landing",
        "marvelanimnotify_scuff",
        "animmetadata",
        "animmetadata_skelcontrol",
        // "morphtarget" is NOT excluded, and the reasoning is the opposite of
        // the anim entries above. See MergeKeepsSourceWholeClasses, which it
        // also appears in - the two go together and neither works alone.
        // the anim entries above.
        //
        // A morph target is a list of per-vertex offsets, so it is only valid
        // against the mesh it was authored for. The mesh being drawn after a
        // swap is the NEWER costume's, which this carries; the older costume's
        // shapes were authored against a mesh that is no longer there. Keeping
        // them is not the safe choice, it is the wrong one - the shape simply
        // never applies. Those blade shapes stay out for exactly this reason.
        //
        // Letting them re-translate puts the newer costume's offsets into the
        // older costume's shape slots, which is where the set already points
        // and where the component already looks. Nothing is added and nothing
        // is renamed, so none of the doubling that governs sockets applies.
        //
        // The set itself stays excluded, just below. Carrying one was tried
        // and is wrong: a set names its shapes through a flat object array
        // whose entries only translate when the shapes themselves were
        // carried, and carrying the shapes is what this avoids. The older
        // set already names the right slots.
        "morphtargetset",
        // MaterialFunction. Same reasoning as `material` below: target's
        // chbasematerial_v2-1 references shared functions like ch_filllightemissive,
        // ch_impactstargeting, reflection, mf_opacitycrawl, etc. Each of those
        // functions has an internal expression graph (FunctionExpressions array)
        // with FObject refs. When matched-translate rewrites a function's body
        // with source 1.53 bytes, the IndexTranslator nulls 1.53-only refs.
        // Result: the function has a broken expression graph. chbasematerial_v2-1
        // itself is preserved (above) but at *runtime* the engine traverses the
        // expression graph into these functions to compile the shader for a
        // newly-loaded MIC (one with bHasStaticPermutationResource=False).
        // The broken function graph produces a non-compilable shader → engine
        // falls back to debug rendering (bright-blue) on the skin path.
        // Costume MICs depend on this fallback shader compilation when the
        // chassis-based MIC drops its cached FMaterialResource tail, so any
        // function corruption shows up immediately on the new costume.
        "materialfunction",
        // Base Material (UMaterial). When target inlines chbasematerials_v2.*
        // as exports (certain "VU" series and similar "VU" costumes), matched-
        // translate rewrites target's pristine chbasematerial_v2-1 body with
        // source's 1.53 bytes. The merged body's expressions[] array re-points
        // half its refs at source-only MaterialExpression* exports / function
        // refs that don't exist in target; the body's MaterialFunctionInfos
        // entries pointing at engine-intrinsic MaterialFunctions get nulled.
        // The resulting shader fails compile checks at runtime — the engine
        // falls back to a TRANSLUCENT debug path on top of the original
        // opaque setup, so the avatar renders as a glassy ghost. The new
        // costume's MIC needs the base material's *shader* unchanged; the
        // visual changes live on the MIC's parameter values, not on the base.
        // Skipping the merge keeps target's working v868-compiled UMaterial
        // intact and the MIC's textures bind through it correctly.
        "material",
    };

    // Default alias map for AoA Colossus. Source (1.53) re-parented the
    // costume to <Hero>_<NewCostume>; target (1.52) still has the older
    // <Hero>_<OldCostume>. Without these substitutions every reference
    // through `colossus_modern.X` import paths fails to resolve in the live
    // game (mesh ref goes null → invisible character). With them, those
    // references map onto target's existing `colossus.X` imports.
    public static readonly Dictionary<string, string> DefaultAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        { "default__marvelplayer_colossus_modern", "default__marvelplayer_colossus" },
        { "marvelplayer_colossus_modern",          "marvelplayer_colossus" },
        // 1.53 renamed the shared skin base MIC from `chbasematerial_v2_skin`
        // (1.52) to `chbasematerial_v2-1_skin`. Without this alias every 1.53
        // costume MIC whose Parent points at the renamed skin base ends up
        // either pointing at a transplanted broken copy (whose own grandparent
        // is also a 1.53-only export that has no 1.52 equivalent) OR at null,
        // so the costume's body/cape renders untextured. Aliasing snaps the
        // Parent ref to the existing 1.52 import chain and the engine reuses
        // the working v868-compiled shader. (Verified via Parent-translation
        // logging on Storm_XTreme → Storm_AfricanGoddess swap: body+cape MICs
        // parent through `chbasematerial_v2-1_skin`, hair MIC parents through
        // an unchanged `hairshader` import and renders correctly.)
        { "chbasematerial_v2-1_skin",              "chbasematerial_v2_skin" },
        // Non-skin base material aliases. Empirically (UpkExportProbe on
        // e.g.): 1.52's basic costumes don't import chbasematerials_v2.X at
        // all — they import from the v1 package "chbasematerials" with the
        // bare name "chbasematerial" (no v2 / no -1 suffix). 1.53 reorganized
        // base materials under chbasematerials_v2.chbasematerial_v2-1 and
        // _dblsided. Need to rewrite BOTH the package and the leaf name to
        // map source paths to the destination costume actual existing import.
        // Single-segment aliases would corrupt other paths via substring
        // overlap, so use the longest-first ordering in ApplyAliases combined
        // with full-path entries to make each substitution unambiguous.
        { "chbasematerials_v2.chbasematerial_v2-1_dblsided", "chbasematerials.chbasematerial" },
        { "chbasematerials_v2.chbasematerial_v2-1",          "chbasematerials.chbasematerial" },
        // Fallback for V2-1 costume packs (e.g. some 1.53 donor costumes bundle their
        // own chbasematerials_v2.* EXPORTS inline). When target's matching
        // costume inlines the same package but DOES NOT carry the _dblsided
        // variant (verified: 1.52 certain "VU" series inlines chbasematerial_v2-1
        // but not chbasematerial_v2-1_dblsided), the V1 alias above maps to
        // a target path that ALSO doesn't exist → Parent resolves to null →
        // body MIC renders as the bright-blue engine debug fallback. The
        // INLINE-pair below targets the non-dblsided V2-1 export the target
        // DOES carry. Losing double-sided rendering is invisible on solid
        // character bodies (which is where this fires); a coat / cape that
        // genuinely needs both faces will need a per-asset override.
        { "chbasematerial_v2-1_dblsided",                    "chbasematerial_v2-1" },
        // The two-sided base material under its OTHER spelling. 1.53 packages
        // are not consistent: some name it chbasematerial_v2-1_dblsided, which
        // the entries above already cover, and some name it
        // chbasematerial_v2_dblsided with no -1. Nothing matched the second
        // form, so a cape or a cloak parented at it lost its shader and drew as
        // the flat blue the engine falls back to. Verified on
        // Thor_RagnarokMovie -> Thor_AgeOfUltron, whose cape came out untextured
        // while the rest of the costume was right.
        { "chbasematerials_v2.chbasematerial_v2_dblsided",   "chbasematerials.chbasematerial" },
        { "chbasematerial_v2_dblsided",                      "chbasematerial" },
        // And the base material under its BARE name, with no suffix at all.
        // 1.53 uses chbasematerial_v2-1 in most costumes and plain
        // chbasematerial_v2 in others; only the first was covered, so a costume
        // built on the second lost every parent it had. Iron Man is the case
        // that showed it - both of his shader instances parent here and both
        // came out naming nothing, which is the whole costume untextured
        // rather than a piece of it.
        //
        // This maps onto v2-1 rather than onto the older plain chbasematerial,
        // because v2-1 is what the 1.52 chassis itself is built on: its
        // chbasematerial_v2-1_skin parents at chbasematerials_v2.chbasematerial_v2-1
        // as a real export of its own. Staying inside the family the chassis
        // already uses keeps the parameter names the instance sets meaningful.
        { "chbasematerials_v2.chbasematerial_v2",            "chbasematerials_v2.chbasematerial_v2-1" },
        // The skin base, two-sided. Same family as chbasematerial_v2_skin and
        // the only difference is which faces are drawn, so an instance built on
        // it sets the same parameters by the same names.
        //
        // Without this its parent does not resolve, and an instance with no
        // resolvable parent falls to the value-only path - rebuilt on the
        // chassis's own shader, keeping the CHASSIS's reflection and
        // spec-colour maps over the costume's. Measured on
        // JeanGrey_Horseman, whose body came out looking muddy for exactly
        // that reason while the costume itself was carried correctly.
        //
        // Losing the second face is a shading difference on a body that is
        // mostly solid; wearing another costume's maps is not.
        { "chbasematerial_v2_skin_dblsided",                 "chbasematerial_v2_skin" },
        // Masked base material. 1.53 costumes that use alpha-cutout pieces
        // (scarves, sashes, face masks, loose cloth) parent those MICs at
        // `chbasematerials.chbasematerial_masked`, and they parent at it as an
        // INLINED EXPORT of their own package rather than as an import. That
        // matters: the import-borrowing pass only carries source IMPORTS that
        // target lacks, so an inlined-export parent has no route across at all
        // and translates to null — the MIC loses its shader and the piece
        // renders as the bright-blue engine fallback.
        //
        // 1.52 does have this material (verified: it is inlined in ~20 shipped
        // packages, among them Daredevil_Shadowlands and several NPCs), but it
        // is in none of the costume packages this tool targets, so there is no
        // local export or import to snap to. Aliasing it onto the plain base
        // material sends it down the exact route the body MIC already takes and
        // is proven to load — verified on Gambit_Shirtless -> Gambit_Classic,
        // where body_mat parents through import `chbasematerials.chbasematerial`
        // and renders correctly while scarves_mat parented at null.
        //
        // The cost is the masking itself: the piece draws opaque, so geometry
        // the alpha channel would have cut away stays visible. That is a
        // shading difference on pieces that are mostly solid anyway, against a
        // piece that otherwise does not draw at all.
        { "chbasematerials.chbasematerial_masked",           "chbasematerials.chbasematerial" },
        { "chbasematerial_masked",                           "chbasematerial" },
    };

    public async Task<Result> ExecuteAsync(
        string sourceUpkPath,
        string targetUpkPath,
        string outputPath,
        Action<string>? log = null,
        HashSet<string>? classAllowlist = null,
        // Optional: carry only these objects, and whatever hangs off them.
        //
        // The class list alone is too coarse when the source is another of the
        // older game's own costumes and only ONE of its materials is wanted.
        // Elektra holds the two-sided skin base every costume of that family
        // stands on - and twenty-three others besides, whose own references
        // reach objects that are not coming with them. Carried wholesale they
        // name things the new package does not hold, and the game refuses the
        // file. Named singly, only the wanted one and its own graph travel.
        IReadOnlySet<string>? onlyTheseRoots = null,
        IReadOnlyDictionary<string, string>? aliases = null,
        bool translateMatchedSizeChanged = true,
        bool mergeMatchedWithTarget = false,
        // Optional: rename target's NameTable entries in-place right after
        // load. Keys are existing strings in target's name table; values are
        // their replacements. Used by the batch sibling-chassis fallback:
        // a 1.53 costume with no 1.52 twin (e.g. a new variant with no 1.52 twin) is run with
        // a sibling 1.52 costume as the chassis (e.g. Hulk_Classic) plus a
        // rename map {"MarvelPlayer_Hulk_Classic" -> "MarvelPlayer_Hulk_Ragnarok",
        // "Default__MarvelPlayer_Hulk_Classic" -> "Default__MarvelPlayer_Hulk_Ragnarok"}.
        // Because every FName in the UPK references the name table by INDEX,
        // swapping the string at a slot transparently renames every reference
        // to that name across the whole file. The matched-export re-translation
        // then pairs source's MarvelPlayer_Hulk_Ragnarok class with the now-
        // renamed target export of the same name; output loads in-game under
        // the source's costume slot. Match is case-insensitive.
        IReadOnlyDictionary<string, string>? targetNameRenames = null)
    {
        if (string.IsNullOrWhiteSpace(sourceUpkPath)) throw new ArgumentException("source", nameof(sourceUpkPath));
        if (string.IsNullOrWhiteSpace(targetUpkPath)) throw new ArgumentException("target", nameof(targetUpkPath));
        if (string.IsNullOrWhiteSpace(outputPath))    throw new ArgumentException("output", nameof(outputPath));
        if (!File.Exists(sourceUpkPath))              throw new FileNotFoundException("source upk not found", sourceUpkPath);
        if (!File.Exists(targetUpkPath))              throw new FileNotFoundException("target upk not found", targetUpkPath);

        string sourceFull = Path.GetFullPath(sourceUpkPath);
        string targetFull = Path.GetFullPath(targetUpkPath);
        string outputFull = Path.GetFullPath(outputPath);
        if (string.Equals(targetFull, outputFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Output must differ from target.");
        if (string.Equals(sourceFull, outputFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Output must differ from source.");

        var allow = classAllowlist ?? DefaultClassAllowlist;

        // Auto-backup live target via the shared one-pristine-.bak policy.
        string? backupPath = null;
        if (File.Exists(targetFull))
        {
            backupPath = BackupFileHelper.CreateBackup(targetFull);
            log?.Invoke($"Phase 2: backup -> {backupPath}");

            // CRITICAL: if a pristine .bak already existed (= user installed
            // a previous swap output back to this slot), `targetFull` now
            // contains the PREVIOUS swap's accumulated additions. Reading
            // that as our "target" silently layers new picks on top of old
            // picks → duplicate "name=a" entries, stale untrimmed bodies,
            // and engine "Serial size mismatch" loads. Always restore the
            // live target from the pristine .bak before we read it so every
            // Phase 2 run operates on a clean baseline.
            string? existingBak = BackupFileHelper.FindExistingBackup(targetFull);
            if (existingBak != null && File.Exists(existingBak))
            {
                long liveLen = new FileInfo(targetFull).Length;
                long bakLen = new FileInfo(existingBak).Length;
                if (liveLen != bakLen)
                {
                    File.Copy(existingBak, targetFull, overwrite: true);
                    log?.Invoke($"Phase 2: restored live target from pristine .bak ({liveLen:N0} -> {bakLen:N0} bytes) to avoid layered swap");
                }
            }
        }

        log?.Invoke($"Phase 2: load source {sourceFull}");
        UpkFileRepository repo = new();
        var srcHeader = await repo.LoadUpkFile(sourceFull).ConfigureAwait(false);
        await srcHeader.ReadHeaderAsync(null).ConfigureAwait(false);
        log?.Invoke($"Phase 2: load target {targetFull}");
        var tgtHeader = await repo.LoadUpkFile(targetFull).ConfigureAwait(false);
        await tgtHeader.ReadHeaderAsync(null).ConfigureAwait(false);

        // Apply name-table renames before any downstream code reads names.
        // SetString preserves the name's INDEX slot, so every FName already
        // referencing it (export class, Default__ ref, property tags, etc.)
        // transparently picks up the new string without any further rewiring.
        if (targetNameRenames != null && targetNameRenames.Count > 0)
        {
            int renamed = 0;
            foreach (var entry in tgtHeader.NameTable)
            {
                string current = entry?.Name?.String ?? string.Empty;
                if (string.IsNullOrEmpty(current)) continue;
                foreach (var kv in targetNameRenames)
                {
                    if (string.Equals(current, kv.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        entry!.Name.SetString(kv.Value);
                        renamed++;
                        break;
                    }
                }
            }
            log?.Invoke($"Phase 2: target name-table renames applied: {renamed}/{targetNameRenames.Count}");
        }

        // 1. Pick the source-only exports we will transplant.
        // "Source-only" means: no target export shares this source export's
        // full path. We key by GetPathName() (full Outer chain) so that
        // sub-exports with generic names like 'skeletalmeshsocket_0' don't
        // get falsely matched against same-name sub-exports under a
        // different parent. The IndexTranslator uses the same key, so the
        // two stay consistent — a picker miss here means the translator
        // would also fail to map and write null into the body.
        var tgtPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in tgtHeader.ExportTable)
            tgtPaths.Add(e.GetPathName());
        // v1.0.34: ALSO dedup by class+name. The same-name matched-override
        // path below (line ~988) ALREADY replaces target's slot with source's
        // translated body when class+name match. Without this second dedup,
        // sources of those classes would ALSO be appended as new exports —
        // producing duplicates (verified case: 53→106 socket doubling on a
        var tgtClassNameKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in tgtHeader.ExportTable)
            tgtClassNameKeys.Add($"{e.ClassReferenceNameIndex?.Name}::{e.ObjectNameIndex?.Name}");
        // A socket's export name is not what identifies it. Sockets are named
        // skeletalmeshsocket_N where N is just a running count within the
        // package they were cooked into, so the same socket is
        // skeletalmeshsocket_0 in one costume and skeletalmeshsocket_100 in
        // another. What identifies it is its SocketName — socket_hat,
        // socket_r_hand, socket_origin — which is stable across costumes
        // because the attachment points are the same on every rig.
        //
        // Deduping sockets by export name therefore works only by luck, when
        // both packages happen to number from the same place. When they don't,
        // NOTHING matches and every source socket is appended beside the
        // target's own: verified as 51 -> 102 on one costume pair and
        // 53 -> 109 on a second, against 48 -> 48 on a third where both
        // packages happened to start at zero. That doubling
        // is what makes the engine assert on avatar load.
        var tgtSocketNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in tgtHeader.ExportTable)
        {
            if (!string.Equals(e.ClassReferenceNameIndex?.Name, "skeletalmeshsocket", StringComparison.OrdinalIgnoreCase))
                continue;
            string? socketName = ReadSocketName(tgtHeader, e);
            if (socketName != null) tgtSocketNames.Add(socketName);
        }
        var pickedExports = new List<UnrealExportTableEntry>();
        int dedupByName = 0;
        int dedupBySocket = 0;
        foreach (var s in srcHeader.ExportTable)
        {
            string cls = s.ClassReferenceNameIndex?.Name ?? string.Empty;
            string path = s.GetPathName();
            // A material whose path is already taken is still carried. The older
            // costume has its own chbasematerials_v2.chbasematerial_v2_skin and
            // the newer one keeps a different material under that same path, so
            // skipping it means the costume's shaders bind to the chassis's
            // material and the surface is drawn with the wrong thing. It is
            // re-homed below, under the costume's own group, which is what a
            bool materialWhosePathIsTaken =
                string.Equals(cls, "material", StringComparison.OrdinalIgnoreCase)
                && allow.Contains("material")
                && tgtPaths.Contains(path);

            if (tgtPaths.Contains(path) && !materialWhosePathIsTaken) continue;
            bool belongsToACarriedMesh = false;
            if (string.Equals(cls, "skeletalmeshsocket", StringComparison.OrdinalIgnoreCase))
            {
                // A socket that hangs off a mesh we are CARRYING comes with it,
                // whatever the target already has. It is that mesh's own
                // attachment point, the mesh names it by reference, and leaving
                // it behind is not neutral: the mesh comes across naming
                // nothing where it named a socket.
                //
                // Measured on Thor_RoadWornSkullMovie. Deduplicating these away
                // left the carried mesh resolving 12 of its 62 references,
                // against 62 of 62 when they are carried. The other fifty were
                // its sockets.
                //
                // The doubling this dedup exists to stop is a different case:
                // sockets belonging to a mesh we are NOT carrying, which the
                // target already has its own copies of. Those are still
                // deduplicated, just below.
                string socketOuter = OuterPathOf(path);
                if (socketOuter.Length > 0)
                {
                    foreach (var m in srcHeader.ExportTable)
                    {
                        if (!string.Equals(m.ClassReferenceNameIndex?.Name, "skeletalmesh", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!string.Equals(m.GetPathName(), socketOuter, StringComparison.OrdinalIgnoreCase)) continue;
                        // The mesh comes across unless the target already owns
                        // one of the same name, in which case it is merged
                        // rather than added and its sockets are target's.
                        belongsToACarriedMesh =
                            !tgtPaths.Contains(m.GetPathName()) &&
                            !tgtClassNameKeys.Contains($"{m.ClassReferenceNameIndex?.Name}::{m.ObjectNameIndex?.Name}") &&
                            allow.Contains("skeletalmesh");
                        break;
                    }
                }

                if (!belongsToACarriedMesh)
                {
                    string? socketName = ReadSocketName(srcHeader, s);
                    if (socketName != null && tgtSocketNames.Contains(socketName))
                    {
                        // Target already has this attachment point on a mesh of
                        // its own. Leave its socket in place rather than adding
                        // a second one for the same spot on the body.
                        dedupBySocket++;
                        continue;
                    }
                }
            }
            // An expression node's export name is a running count too -
            // materialexpressionscalarparameter_0 and so on - so a carried
            // material's own nodes collide by name with unrelated ones of the
            // chassis's and were being dropped here, exactly as a carried
            // mesh's sockets once were. A material that loses its nodes points
            // at nothing, and the transplant then refuses the material itself.
            // A node belonging to a material we are carrying comes with it.
            if (!belongsToACarriedMesh
                && cls.StartsWith("materialexpression", StringComparison.OrdinalIgnoreCase))
            {
                string nodeOuter = OuterPathOf(path);

                foreach (var m in srcHeader.ExportTable)
                {
                    if (!string.Equals(m.ClassReferenceNameIndex?.Name, "material", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!string.Equals(m.GetPathName(), nodeOuter, StringComparison.OrdinalIgnoreCase)) continue;

                    if (!allow.Contains("material")) break;

                    belongsToACarriedMesh = true; // same exemption, same reason
                    break;
                }
            }

            string classNameKey = $"{cls}::{s.ObjectNameIndex?.Name}";

            // A socket's export name is a running count, so a carried mesh's
            // socket collides with an unrelated one of target's by name alone
            // and was being dropped here after surviving the rule above. That
            // left the drawn mesh with six of its fifty-six attachment points -
            // the six target happened not to have - and a hammer with no
            // `socket_hammer` to hang on. It is paired by SocketName further
            // down, never by this name, so the collision means nothing.
            // A material whose path the chassis uses collides by name as well,
            // and being sent down the matched path would put the costume's
            // material into the chassis's slot rather than beside it. It is
            // carried and re-homed instead.
            if (tgtClassNameKeys.Contains(classNameKey) && !belongsToACarriedMesh && !materialWhosePathIsTaken)
            {
                // Target already has an export with this class+name — the
                // matched-override path will replace target's slot with our
                // translated body. Don't ALSO append source as a new export.
                dedupByName++;
                continue;
            }
            // Reverted: prefix-match for materialexpression* was added when
            // we tried to transplant the full base-material expression graph.
            // That strategy failed because the graph references engine-
            // intrinsic function nodes (engine_materialfunctions02.math.*)
            // that 1.52's engine binary doesn't expose under the same paths.
            // Result was a transparent costume. The aliasing approach (see
            // DefaultAliases) sidesteps the graph entirely by pointing MIC
            // parents at 1.52's existing working base material imports.
            if (!allow.Contains(cls)) continue;

            // Named roots, when the caller gave any: the object itself, or
            // something living underneath it.
            if (onlyTheseRoots is not null && onlyTheseRoots.Count > 0)
            {
                bool wanted = onlyTheseRoots.Any(rootPath =>
                    path.Equals(rootPath, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(rootPath + ".", StringComparison.OrdinalIgnoreCase));

                if (!wanted) continue;
            }
            pickedExports.Add(s);
        }
        // Every mesh the costume holds is carried, props included.
        //
        // Carrying only the largest was tried, on the reasoning that costumes
        // bringing a second mesh were the ones that failed to load. That
        // reasoning was wrong: the failures were the socket pairing below,
        // which was matching sockets by their running-count export name and
        // writing one attachment point's data into another's slot. Dropping a
        // mesh changed nothing about it — it failed identically, with the
        // same bad name index, either way.
        //
        // Leaving a prop behind is also actively harmful. The costume's
        // sockets and animations still name it, so the game is left reaching
        // for a mesh that is not there: one costume without its hammer and
        // another without its wings both hung, where the same costumes with the prop
        // carried and the sockets paired properly do not.

        log?.Invoke($"Phase 2: picked {pickedExports.Count} source-only exports to add (deduped {dedupByName} by class+name → matched-override path, {dedupBySocket} sockets by socket name)");

        // 2. Compute the set of names we need to add to target's NameTable.
        // Strategy: take the names referenced by each picked export's
        // ObjectName + ClassName + the names referenced by its body via the
        // property tag stream. We approximate "names referenced" as "names
        // that the IndexTranslator says are missing from target" — that's
        // conservative (adds names that may go unused) but always safe.
        var effectiveAliases = aliases ?? DefaultAliases;

        // Runtime alias correction for V2 inline-package targets.
        //
        // DefaultAliases maps `chbasematerials_v2.chbasematerial_v2-1[_dblsided]`
        // → `chbasematerials.chbasematerial` because 1.52's BASIC costumes
        // (Colossus_Classic, Storm_Classic, etc.) import the V1 base material
        // from the V1 package, not V2. But some 1.52 hero UPKs (notably
        // certain "VU" series and other "VU" costumes) INLINE the V2 base material
        // package as their own exports — for those targets, `chbasematerials.
        // chbasematerial` doesn't exist (no V1 import either), so the alias
        // points at nothing and the MIC's Parent ref translates to null →
        // engine renders the bright-blue debug fallback shader.
        //
        // Detect target's V2 inline-export situation and, when present, swap
        // the V1 redirects for V2 redirects. This keeps the basic-costume case
        // working (target without V2 → still maps to V1) while fixing the
        // VU-costume case (target with V2 inline → maps to V2-1).
        bool targetHasV2Base = false, targetHasV2Dblsided = false, targetHasSkinDblsided = false;
        foreach (var e in tgtHeader.ExportTable)
        {
            if (string.Equals(e.ClassReferenceNameIndex?.Name, "material", StringComparison.OrdinalIgnoreCase)
                && string.Equals(e.ObjectNameIndex?.Name, "chbasematerial_v2_skin_dblsided", StringComparison.OrdinalIgnoreCase))
                targetHasSkinDblsided = true;

            string p = e.GetPathName();
            if (string.Equals(p, "chbasematerials_v2.chbasematerial_v2-1",          StringComparison.OrdinalIgnoreCase)) targetHasV2Base = true;
            if (string.Equals(p, "chbasematerials_v2.chbasematerial_v2-1_dblsided", StringComparison.OrdinalIgnoreCase)) targetHasV2Dblsided = true;
        }

        if (targetHasV2Base && ReferenceEquals(effectiveAliases, DefaultAliases))
        {
            var patched = new Dictionary<string, string>(DefaultAliases, StringComparer.OrdinalIgnoreCase);
            patched["chbasematerials_v2.chbasematerial_v2-1"] = "chbasematerials_v2.chbasematerial_v2-1";
            patched["chbasematerials_v2.chbasematerial_v2-1_dblsided"] =
                targetHasV2Dblsided
                    ? "chbasematerials_v2.chbasematerial_v2-1_dblsided"
                    : "chbasematerials_v2.chbasematerial_v2-1";
            effectiveAliases = patched;
            log?.Invoke($"Phase 2: target inlines chbasematerials_v2 — V2 base aliases retargeted to inline paths (dblsided→{(targetHasV2Dblsided ? "dblsided" : "non-dblsided fallback")})");
        }

        // The two-sided skin base is aliased onto the plain skin one because the
        // older game was thought not to have it. Where the chassis DOES have
        // it - brought in from another of the older game's own costumes, which
        // is where it lives - the alias would send the shader past the real
        // thing to a base that does not draw its cutouts. So it stands down.
        if (targetHasSkinDblsided && ReferenceEquals(effectiveAliases, DefaultAliases))
        {
            var kept = new Dictionary<string, string>(DefaultAliases, StringComparer.OrdinalIgnoreCase);

            kept.Remove("chbasematerial_v2_skin_dblsided");
            effectiveAliases = kept;

            log?.Invoke("Phase 2: the chassis has the two-sided skin base itself, so the alias stands down");
        }

        // Re-home any carried material whose path the chassis already uses, so
        // both can live in the package and the costume's shaders bind to its
        // own. The costume's group is taken from a shader it carries, that
        // being where the newer costume keeps its own things.
        {
            int costumeGroupRef = 0;
            string costumeGroupName = string.Empty;

            foreach (var picked in pickedExports)
            {
                if (!string.Equals(picked.ClassReferenceNameIndex?.Name, "materialinstanceconstant", StringComparison.OrdinalIgnoreCase))
                    continue;

                int groupRef = picked.OuterReference;

                if (groupRef <= 0 || groupRef > srcHeader.ExportTable.Count) continue;

                var group = srcHeader.ExportTable[groupRef - 1];

                if (!string.Equals(group.ClassReferenceNameIndex?.Name, "package", StringComparison.OrdinalIgnoreCase))
                    continue;

                costumeGroupRef = groupRef;
                costumeGroupName = group.ObjectNameIndex?.Name ?? string.Empty;
                break;
            }

            if (costumeGroupRef != 0)
            {
                foreach (var picked in pickedExports)
                {
                    if (!string.Equals(picked.ClassReferenceNameIndex?.Name, "material", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string was = picked.GetPathName();

                    if (!tgtPaths.Contains(was)) continue;

                    picked.SetOuterReferenceForRehoming(costumeGroupRef);
                    log?.Invoke($"Phase 2: '{was}' is a path the chassis already uses; carried under '{costumeGroupName}' instead");
                }
            }
        }

        var translator = new IndexTranslator(srcHeader, tgtHeader, effectiveAliases);
        var namesToAdd = translator.NamesMissingFromTarget
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // A costume that wants two-sided rendering needs the word for it.
        // Step 6c below writes a `twosided` tag onto the base material the
        // costume's shaders are built on, and a tag can only name a name the
        // package holds. Queued here because names are settled long before
        // that step runs.
        bool costumeWantsTwoSided = srcHeader.ExportTable.Any(e =>
            string.Equals(e.ClassReferenceNameIndex?.Name, "material", StringComparison.OrdinalIgnoreCase)
            && (e.ObjectNameIndex?.Name ?? string.Empty)
                .Contains("dblsided", StringComparison.OrdinalIgnoreCase));

        if (costumeWantsTwoSided)
        {
            EnsureNameInToAdd("twosided", namesToAdd, tgtHeader);
            EnsureNameInToAdd("BoolProperty", namesToAdd, tgtHeader);
        }

        // Every carried object's OWN name is added, whatever the translator
        // concluded about it.
        //
        // The translator decides a name is present by looking it up THROUGH THE
        // ALIASES, and rightly - that is how a reference to a 1.53 base
        // material comes to point at the 1.52 one. But an object's own name is
        // written into the export table by a plain lookup, with no aliases, so
        // an aliased name is judged present, never added, and then not found.
        // The lookup falls back to index 0, and slot 0 in these packages is the
        // name "a".
        //
        // One costume is the case that showed it: its
        // chbasematerial_v2-1_skin is aliased to chbasematerial_v2_skin, so the
        // name was never added, and the carried shader instance arrived called
        // "a" with no parent. The same fallback is what once produced
        // forty-three sockets all named "a".
        foreach (var picked in pickedExports)
        {
            EnsureNameInToAdd(picked.ObjectNameIndex?.Name, namesToAdd, tgtHeader);
        }

        log?.Invoke($"Phase 2: {namesToAdd.Count} names to add (aliases active: {effectiveAliases.Count})");

        // ---- Cross-UPK inliner — DISCOVERY-ONLY PASS (milestone 1) ----
        // For every source import that the translator failed to resolve in
        // target, try to find the referenced object in another UPK inside
        // the source game's cooked-data folder. This pass only LOGS what
        // would be inlined; it does not modify the output. Validates the
        // resolver against real-world swap data before we wire inlining
        // into the export-table extender.
        var inliner = new CrossUpkInliner(
            Path.GetDirectoryName(sourceFull) ?? string.Empty,
            repo,
            msg => log?.Invoke(msg));
        var inlineResolved = new List<CrossUpkInliner.ResolvedExport>();
        var inlineUnresolved = new List<string>();
        foreach (var missingPath in translator.ImportsMissingFromTarget.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Skip pure-class imports (e.g. "Engine.SkeletalMesh") — these
            // are engine-builtin class refs the target's engine binary
            // already provides; they look like missing import paths but
            // they aren't actual cross-UPK content. Heuristic: imports
            // whose path has exactly one dot AND whose package segment is
            // "Core" / "Engine" / "GFxUI" / "EditorEngine" etc. are
            // engine intrinsics. We surface them in the log but don't
            // bother probing disk.
            int dotCount = missingPath.Count(c => c == '.');
            if (dotCount <= 1)
            {
                // 0 or 1 dot — likely class-only import or top-level package
                // ref. The inliner needs at least PackageName.ObjectChain to
                // attempt resolution.
                continue;
            }
            var resolved = await inliner.TryResolveAsync(missingPath).ConfigureAwait(false);
            if (resolved != null) inlineResolved.Add(resolved);
            else inlineUnresolved.Add(missingPath);
        }
        log?.Invoke($"Phase 2: cross-UPK inline discovery — {inlineResolved.Count} resolvable, {inlineUnresolved.Count} unresolvable (across {inliner.LoadedForeignUpkCount} foreign UPK(s))");
        if (inlineResolved.Count > 0)
        {
            log?.Invoke($"Phase 2: --- WOULD INLINE (first {Math.Min(20, inlineResolved.Count)}) ---");
            foreach (var r in inlineResolved.Take(20))
                log?.Invoke($"  WOULD-INLINE  {r.ForeignUpkName}.upk -> {r.Entry.ClassReferenceNameIndex?.Name ?? "?"} {r.ExportPathInForeign}");
        }
        if (inlineUnresolved.Count > 0)
        {
            log?.Invoke($"Phase 2: --- WOULD-INLINE UNRESOLVABLE (first {Math.Min(10, inlineUnresolved.Count)}) ---");
            foreach (var u in inlineUnresolved.Take(10))
                log?.Invoke($"  NO-FOREIGN-MATCH  {u}");
        }
        // Milestone 1 stops here — output unchanged. Next milestone wires
        // these resolved exports into the table extender so they actually
        // become exports in the output UPK.

        // 3. Pick which source imports to mirror into target. Same conservative
        // strategy: bring across every src import whose full path doesn't exist
        // on target's side. Hard-blocker imports (referencing classes that don't
        // exist in target's engine binary) are still added at the table level —
        // they'll just be unresolved at load time, which the engine tolerates
        // for IMPORTS as long as nothing references them at runtime.
        var importsToAdd = new List<UnrealImportTableEntry>();
        var tgtImportPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var imp in tgtHeader.ImportTable)
            tgtImportPaths.Add(ImportFullPath(tgtHeader, imp));
        foreach (var imp in srcHeader.ImportTable)
        {
            string path = ImportFullPath(srcHeader, imp);
            if (tgtImportPaths.Contains(path)) continue;
            // Alias fallback: if source's import path aliases onto an
            // existing target path, skip adding (the IndexTranslator already
            // resolves source ref → target ref via alias).
            string aliased = translator.ApplyAliases(path);
            if (!string.Equals(aliased, path, StringComparison.OrdinalIgnoreCase)
                && tgtImportPaths.Contains(aliased))
                continue;
            importsToAdd.Add(imp);
        }
        log?.Invoke($"Phase 2: {importsToAdd.Count} imports to add");

        // 3.5. Synthetic-import pass: for SOURCE exports of classes we can't
        // safely transplant as exports (Material body has a complex internal
        // expression graph that the property-tag rewriter doesn't enumerate
        // → dangling refs → engine "Bad export index" on load), build a
        // synthetic UnrealImportTableEntry pointing at the same path and
        // add it as a TARGET IMPORT instead. The engine's runtime linker
        // resolves imports by package-name + object-path against loaded
        // UPKs; if any other 1.52 UPK exports the same path (e.g.
        // chbasematerials_v2.chbasematerial_v2-1 lives in
        // UC__MarvelPlayer_<Hero>_<Variant>_SF.upk), the import resolves at
        // runtime and the dependent MIC's parent ref is non-null.
        //
        // What this fixes: non-skin-base costumes (e.g. armor / overcoat shells)
        // whose MIC chain roots at chbasematerial_v2-1 (the non-skin base
        // material) instead of chbasematerial_v2-1_skin (which has an existing
        // alias to chbasematerial_v2_skin and works via the existing
        // import-path lookup).
        //
        // Limitations:
        //   - Requires target's costume to ALREADY have the package outer
        //     (chbasematerials_v2) as an export. Verified on a known costume pair.
        //   - If no 1.52 UPK exports the synthetic path, the engine logs
        //     a "missing import" warning and the ref still goes null at
        //     runtime — same blue rendering, but the file at least loads.
        //   - Currently scoped to class=material; future extensions could
        //     add materialfunction (engine math nodes) etc.
        var syntheticImports = new List<UnrealImportTableEntry>();
        // Synthetic imports' OuterReference points at TARGET's export table
        // (not source's), so the standard ImportFullPath(srcHeader, ...)
        // walker produces a garbage path. Track each synthetic import's
        // INTENDED path here so futureImportIndex gets keyed correctly.
        var syntheticImportSrcPath = new Dictionary<UnrealImportTableEntry, string>();
        // Empty by design — `material` was moved into DefaultClassAllowlist
        // (gets transplanted as a regular export now alongside its full
        // expression-graph children). Kept the synthetic-import machinery
        // (UnrealImportTableEntry.SetOuterReferenceForSynthesis,
        // Phase2TableExtender outerRef>0 handling, ExtendedTranslator
        // futureImportByPath fallback) in case a future class needs the
        // synthetic-import path — proven correct at the file-encoding level
        // even though runtime resolution failed for material specifically.
        var syntheticUntransplantableClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Find target's existing chbasematerials_v2-style package exports so
        // we can build synthetic imports whose outer points at them. Key by
        // package export's name (e.g. "chbasematerials_v2").
        var tgtPackageExports = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int ti = 0; ti < tgtHeader.ExportTable.Count; ti++)
        {
            var tex = tgtHeader.ExportTable[ti];
            string tcls = tex.ClassReferenceNameIndex?.Name ?? string.Empty;
            if (string.Equals(tcls, "package", StringComparison.OrdinalIgnoreCase))
            {
                string tname = tex.ObjectNameIndex?.Name ?? string.Empty;
                if (!string.IsNullOrEmpty(tname) && !tgtPackageExports.ContainsKey(tname))
                    tgtPackageExports[tname] = ti + 1; // positive export ref
            }
        }
        int syntheticAddedCount = 0;
        int syntheticSkippedNoPackage = 0;
        foreach (var srcExp in srcHeader.ExportTable)
        {
            string sCls = srcExp.ClassReferenceNameIndex?.Name ?? string.Empty;
            if (!syntheticUntransplantableClasses.Contains(sCls)) continue;
            string sPath = srcExp.GetPathName();
            // Skip if already in target (export OR import path).
            if (tgtPaths.Contains(sPath)) continue;
            if (tgtImportPaths.Contains(sPath)) continue;
            // Derive the outer-package name (first segment of sPath).
            int firstDot = sPath.IndexOf('.');
            if (firstDot <= 0) continue;
            string pkgName = sPath.Substring(0, firstDot);
            if (!tgtPackageExports.TryGetValue(pkgName, out int outerExportRef))
            {
                syntheticSkippedNoPackage++;
                continue;
            }
            // Build the synthetic import. The FName indices reference SOURCE's
            // name table; SerializeAddedImports resolves them to target's
            // name table via ResolveNameIdx (and ensures missing names are
            // in namesToAdd).
            var synImp = new UnrealImportTableEntry();
            // PackageNameIndex: the engine package that owns the class.
            // For Material, that's "Core". Find or use source's existing
            // index for "Core".
            int coreSrcIdx = FindSrcNameIdx(srcHeader, "Core");
            int classSrcIdx = FindSrcNameIdx(srcHeader, sCls); // "material"
            int objSrcIdx = srcExp.ObjectNameIndex?.Index ?? -1;
            if (coreSrcIdx < 0 || classSrcIdx < 0 || objSrcIdx < 0)
            {
                syntheticSkippedNoPackage++;
                continue;
            }
            synImp.PackageNameIndex.SetNameTableIndex(srcHeader.NameTable[coreSrcIdx]);
            synImp.ClassNameIndex.SetNameTableIndex(srcHeader.NameTable[classSrcIdx]);
            synImp.ObjectNameIndex.SetNameTableIndex(srcHeader.NameTable[objSrcIdx]);
            synImp.SetOuterReferenceForSynthesis(outerExportRef); // positive = target export ref (translated via path in serializer)
            // Critical: the serializer reads imp's source name strings via
            // srcHeader.NameTable[idx]; ensure those strings are in namesToAdd
            // if target doesn't already have them.
            EnsureNameInToAdd(srcHeader.NameTable[coreSrcIdx]?.Name?.String, namesToAdd, tgtHeader);
            EnsureNameInToAdd(srcHeader.NameTable[classSrcIdx]?.Name?.String, namesToAdd, tgtHeader);
            EnsureNameInToAdd(srcHeader.NameTable[objSrcIdx]?.Name?.String, namesToAdd, tgtHeader);
            syntheticImports.Add(synImp);
            syntheticImportSrcPath[synImp] = sPath;
            syntheticAddedCount++;
        }
        // An alpha-cut shader needs a MASKED base - parented at a plain skin
        // one its cutout sheets draw as solid white, which is exactly what
        // one costume's headdress did. Where a carried shader is named for
        // alpha or masking, the chassis holds no masked base of its own, but it
        // DOES import the chbasematerials_v2 package, a masked-base import is
        // synthesized in the shape another chassis uses - material under the
        // package import - which provably resolves at runtime.
        var syntheticOuterTargetRefs = new Dictionary<UnrealImportTableEntry, int>();

        {
            // Only where the costume's own shaders stand on a masked base. A
            // shader named for alpha does not always want one: one costume's
            // ribbons are named alphamat but parent at a two-sided SKIN base,
            // and on a masked base they lost their shader and drew as flat
            // blue. Another's do parent at a masked base, and need it.
            bool costumeUsesAMaskedBase = srcHeader.ExportTable.Any(e =>
                string.Equals(e.ClassReferenceNameIndex?.Name, "material", StringComparison.OrdinalIgnoreCase)
                && (e.ObjectNameIndex?.Name ?? string.Empty)
                    .Contains("masked", StringComparison.OrdinalIgnoreCase))
                || srcHeader.ImportTable.Any(i =>
                string.Equals(i.ClassNameIndex?.Name, "material", StringComparison.OrdinalIgnoreCase)
                && (i.ObjectNameIndex?.Name ?? string.Empty)
                    .Contains("masked", StringComparison.OrdinalIgnoreCase));

            bool anyAlphaShader = costumeUsesAMaskedBase && pickedExports.Any(e =>
                string.Equals(e.ClassReferenceNameIndex?.Name, "materialinstanceconstant", StringComparison.OrdinalIgnoreCase)
                && ((e.ObjectNameIndex?.Name ?? string.Empty).Contains("alpha", StringComparison.OrdinalIgnoreCase)
                    || (e.ObjectNameIndex?.Name ?? string.Empty).Contains("masked", StringComparison.OrdinalIgnoreCase)));

            // Which masked base to stand the cutout shaders on. A masked base
            // comes in the same families as any other: a skin one and a plain
            // one, and they do not set the same parameters. Standing a skin
            // shader on the plain masked base leaves the parameters it sets
            // unread, so the piece loses its shader and draws as the engine's
            // flat blue - measured on that costume, whose ribbons came out blue and
            // white shards until they were given the skin masked base instead.
            //
            // So the family is taken from the base the costume itself parents
            // at, and the masked base of that same family is what gets
            // synthesized.
            bool costumeUsesSkinBase = srcHeader.ExportTable.Any(e =>
                string.Equals(e.ClassReferenceNameIndex?.Name, "material", StringComparison.OrdinalIgnoreCase)
                && (e.ObjectNameIndex?.Name ?? string.Empty)
                    .Contains("skin", StringComparison.OrdinalIgnoreCase));

            string maskedBaseName = costumeUsesSkinBase
                ? "chbasematerial_v2_skin_masked"
                : "chbasematerial_v2_masked";

            string maskedBasePath = $"chbasematerials_v2.{maskedBaseName}";

            bool targetHasMasked = tgtPaths.Contains(maskedBasePath)
                || tgtImportPaths.Contains(maskedBasePath);

            int groupImportRef = 0;

            for (int i = 0; i < tgtHeader.ImportTable.Count; i++)
            {
                var im = tgtHeader.ImportTable[i];

                if (!string.Equals(im.ClassNameIndex?.Name, "package", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(im.ObjectNameIndex?.Name, "chbasematerials_v2", StringComparison.OrdinalIgnoreCase)) continue;

                groupImportRef = -(i + 1);
                break;
            }

            if (anyAlphaShader && !targetHasMasked && groupImportRef != 0)
            {
                int engineIdx = FindSrcNameIdx(srcHeader, "engine");
                int materialIdx = FindSrcNameIdx(srcHeader, "material");
                int maskedIdx = FindOrTeachSrcName(srcHeader, maskedBaseName);

                if (engineIdx >= 0 && materialIdx >= 0 && maskedIdx >= 0)
                {
                    var masked = new UnrealImportTableEntry();

                    masked.PackageNameIndex.SetNameTableIndex(srcHeader.NameTable[engineIdx]);
                    masked.ClassNameIndex.SetNameTableIndex(srcHeader.NameTable[materialIdx]);
                    masked.ObjectNameIndex.SetNameTableIndex(srcHeader.NameTable[maskedIdx]);

                    syntheticOuterTargetRefs[masked] = groupImportRef;
                    syntheticImportSrcPath[masked] = maskedBasePath;

                    EnsureNameInToAdd(maskedBaseName, namesToAdd, tgtHeader);
                    importsToAdd.Add(masked);

                    log?.Invoke($"Phase 2: {maskedBaseName} synthesized for the alpha shaders, in the shape that resolves");
                }
            }
        }

        log?.Invoke($"Phase 2: synthetic-import pass — added {syntheticAddedCount}, skipped {syntheticSkippedNoPackage} (no package outer in target)");
        // Append synthetic imports to importsToAdd so they participate in the
        // standard serialization + future-index registration below.
        importsToAdd.AddRange(syntheticImports);

        // 4. Compute future indices so the translator + body rewriter can
        // refer to the planned additions by their post-extension indices.
        var futureNameIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < namesToAdd.Count; i++)
            futureNameIndex[namesToAdd[i]] = tgtHeader.NameTable.Count + i;
        var futureImportIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < importsToAdd.Count; i++)
        {
            int futureRef = -(tgtHeader.ImportTable.Count + i + 1);
            // Synthetic imports have OuterReference pointing at TARGET's
            // export table, so the standard srcHeader-based path walker
            // would produce garbage. Use the explicit pre-recorded path.
            string path = syntheticImportSrcPath.TryGetValue(importsToAdd[i], out var explicitPath)
                ? explicitPath
                : ImportFullPath(srcHeader, importsToAdd[i]);
            futureImportIndex[path] = futureRef;
        }
        var futureExportIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < pickedExports.Count; i++)
            futureExportIndex[pickedExports[i].GetPathName()] = tgtHeader.ExportTable.Count + i + 1;

        // 5. Build an EXTENDED translator that knows about the pending adds.
        var extended = new ExtendedTranslator(translator, futureNameIndex, futureImportIndex, futureExportIndex);
        var rewriter = new PropertyTagRewriter(extended.AsIndexTranslator());

        // 6. Translate each picked export's body. SkeletalMesh gets a
        // specialised path that uses the recorder hooks on UnrealHeader to
        // discover every FName + FObject ref position in the binary tail
        // and rewrite each via the IndexTranslator. PropertyTagRewriter
        // only walks the property-table prefix — for SkeletalMesh that
        // leaves ~300 references in the binary tail untranslated, which
        // crashes the engine when the mesh is actually used.
        var translatedBodies = new List<byte[]>(pickedExports.Count);
        // Parallel list to translatedBodies — for added exports that carry
        // inline bulk-data (Texture2D mips), records (offsetFieldInBody,
        // payloadStartInBody) so Phase2TableExtender can stamp the absolute
        // file offset into each mip header after final body layout.
        var translatedBodyPatches = new List<List<(int OffsetFieldInBody, int PayloadStartInBody)>>(pickedExports.Count);
        var issues = new List<string>();
        SkeletalMeshReferenceTranslator? meshTranslator = null;
        Phase2SourceTextureLoader? textureLoader = null;

        // Pre-resolve a singleton target MIC (same class, single instance in
        // both files) for the value-only-transplant fast path. When found,
        // we'll use its body as the chassis for the newly-appended MIC export
        // so the new MIC ships target's working FMaterialResource tail rather
        // than source's 1.53 baked shader bytes (which crash to bright-blue
        // on schema-divergent shader branches even when the property-stream
        // texture overrides resolve cleanly).
        UnrealExportTableEntry? singletonTargetMic = null;
        {
            var tgtMics = tgtHeader.ExportTable.Where(e =>
                string.Equals(e.ClassReferenceNameIndex?.Name, "materialinstanceconstant",
                              StringComparison.OrdinalIgnoreCase)).ToList();
            var srcMics = pickedExports.Where(e =>
                string.Equals(e.ClassReferenceNameIndex?.Name, "materialinstanceconstant",
                              StringComparison.OrdinalIgnoreCase)).ToList();
            if (tgtMics.Count == 1 && srcMics.Count >= 1)
            {
                singletonTargetMic = tgtMics[0];
                try { if (singletonTargetMic.UnrealObject is null) await singletonTargetMic.ParseUnrealObject(false, false).ConfigureAwait(false); } catch { }
                log?.Invoke($"Phase 2: value-only MIC chassis available — target MIC '{singletonTargetMic.ObjectNameIndex?.Name}' will host source MIC values");
            }
        }

        foreach (var s in pickedExports)
        {
            string cls = s.ClassReferenceNameIndex?.Name ?? string.Empty;
            string ctx = $"{cls}::{s.ObjectNameIndex?.Name}";
            byte[] srcBytes = s.UnrealObjectReader?.GetBytes() ?? Array.Empty<byte>();

            // MIC value-only path: parse source MIC; patch source's parameter
            // values onto a clone of target's MIC body. The resulting blob
            // ships with target's compatible shader tail + source's textures
            // and scalars. Skipped for source MICs that have NO matching
            // target MIC (e.g. weapon-only MICs like hulk_axe_matv2) — those
            // fall through to the generic body-translation path.
            // ...and only when this instance has no base material of its own to
            // stand on. Rebuilding it on the chassis's body costs it every
            // binding the chassis does not also have: measured on
            // JeanGrey_Horseman, where both carried instances came out as the
            // chassis's 2,882-byte body with values patched in, the horseman
            // body kept the CHASSIS's reflection and spec-colour textures, and
            // the face lost a diffuse. Her parents resolve perfectly well -
            // chbasematerial_v2_skin is in her chassis - so there was nothing
            // to gain by it.
            //
            // Where the parent genuinely cannot be resolved, this path is still
            // the only thing that gives the instance a shader that works, and
            // it runs as before.
            if (string.Equals(cls, "materialinstanceconstant", StringComparison.OrdinalIgnoreCase)
                && singletonTargetMic != null
                && !HasAParentTargetCanResolve(s, srcHeader, extended.AsIndexTranslator()))
            {
                try { if (s.UnrealObject is null) await s.ParseUnrealObject(false, false).ConfigureAwait(false); } catch { }
                var voResult = MicValueOnlyTransplant.Apply(
                    s, singletonTargetMic, srcHeader, tgtHeader,
                    srcRef => extended.AsIndexTranslator().TranslateObjectReference(srcRef));
                if (voResult.Success && voResult.PatchedBytes.Length > 0)
                {
                    // The PrefixLength on a value-patched body is target's
                    // own NetIndex prefix — already what we want. Zero out
                    // NetIndex per the same rule applied to other newly-
                    // added exports (-1 sentinel).
                    byte[] body = voResult.PatchedBytes;
                    if (body.Length >= 4) { body[0] = 0xFF; body[1] = 0xFF; body[2] = 0xFF; body[3] = 0xFF; }
                    translatedBodies.Add(body);
                    translatedBodyPatches.Add(new List<(int, int)>());
                    issues.Add($"{ctx}: VALUE-ONLY MIC transplant — chassis='{singletonTargetMic.ObjectNameIndex?.Name}', scalars patched={voResult.ScalarsPatched}, textures patched={voResult.TexturesPatched}");
                    foreach (var sk in voResult.SkippedSourceNames.Take(10)) issues.Add($"{ctx}:   skipped src param: {sk}");
                    foreach (var u in voResult.UntouchedTargetNames.Take(10)) issues.Add($"{ctx}:   kept tgt default: {u}");
                    foreach (var iss in voResult.Issues.Take(10)) issues.Add($"{ctx}:   issue: {iss}");
                    continue;
                }
                issues.Add($"{ctx}: value-only MIC transplant did not produce bytes; falling back to generic translation");
                foreach (var iss in voResult.Issues.Take(5)) issues.Add($"{ctx}:   fail: {iss}");
            }

            if (string.Equals(cls, "texture2d", StringComparison.OrdinalIgnoreCase))
            {
                // Lazy-init the source TFC loader (loads source's manifest
                // once on first texture).
                if (textureLoader == null)
                {
                    textureLoader = new Phase2SourceTextureLoader();
                    textureLoader.TryLoadManifestFromUpkFolder(sourceFull, log);
                }
                // Parse the source texture to access its TextureFileCacheName/Guid.
                try
                {
                    await s.ParseUnrealObject(skipProperties: false, skipParse: false).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    issues.Add($"{ctx}: parse failed: {ex.GetType().Name}: {ex.Message}; copying source bytes verbatim");
                    translatedBodies.Add((byte[])srcBytes.Clone());
                    translatedBodyPatches.Add(new List<(int, int)>());
                    continue;
                }
                UpkManager.Models.UpkFile.Engine.Texture.UTexture2D? tex = null;
                if (s.UnrealObject is UpkManager.Models.UpkFile.Objects.IUnrealObject iuoT
                    && iuoT.UObject is UpkManager.Models.UpkFile.Engine.Texture.UTexture2D t2d)
                {
                    tex = t2d;
                }
                List<Phase2SourceTextureLoader.MipBytes>? mipBytes = null;
                if (tex != null)
                    mipBytes = textureLoader.TryLoadMipsForTexture(tex, m => issues.Add($"{ctx}: {m}"));
                var texWalker = new Texture2DBodyWalker(srcBytes, srcHeader, translator, mipBytes, m => issues.Add($"{ctx}: {m}"));
                try
                {
                    texWalker.WalkTexture2DBody();
                }
                catch (Exception ex)
                {
                    issues.Add($"{ctx}: texture walker threw at srcPos={texWalker.BytesConsumed}/{srcBytes.Length}: {ex.GetType().Name}: {ex.Message}");
                    translatedBodies.Add((byte[])srcBytes.Clone());
                    translatedBodyPatches.Add(new List<(int, int)>());
                    continue;
                }
                byte[] texBody = texWalker.GetBytes();
                issues.Add($"{ctx}: Texture2D rewritten — names {texWalker.NameRefsRewritten}, objects {texWalker.ObjectRefsRewritten}, mips inlined {texWalker.MipsInlined}, bulk-patches {texWalker.BulkPatches.Count}");
                translatedBodies.Add(texBody);
                translatedBodyPatches.Add(texWalker.BulkPatches);
                continue;
            }

            if (string.Equals(cls, "skeletalmesh", StringComparison.OrdinalIgnoreCase))
            {
                meshTranslator ??= new SkeletalMeshReferenceTranslator();
                // BUG FIX (untextured mesh after transplant): source mesh's
                // Materials array references base parent UMaterials (not the
                // costume MICs). Those parent refs translate to null because
                // we only pick the MIC children into target. Override the
                // Materials array with the picked MIC target indices so the
                // mesh actually USES the new MICs (and thus their inlined
                // textures) instead of rendering with debug materials.
                // Build src→tgt map for each picked MIC (and parent UMaterial,
                // since source mesh.Materials slots may reference either).
                // Mesh.Materials slots reference SPECIFIC source exports by
                // their source FObject ref (positive int = export idx, 1-based).
                // A positional cycling override mis-wires slots (e.g. body slot
                // gets the cape MIC) AND drops any picked MIC whose source ref
                // doesn't appear in mesh.Materials. The map lets the walker
                // look up "for this exact source ref, what's the target ref?"
                // and fall back to identity translation for refs not in the
                // map.
                var micSrcToTgt = new Dictionary<int, int>();
                for (int pi = 0; pi < pickedExports.Count; pi++)
                {
                    var pe = pickedExports[pi];
                    string peCls = pe.ClassReferenceNameIndex?.Name ?? string.Empty;
                    bool isMaterialish =
                        string.Equals(peCls, "materialinstanceconstant", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(peCls, "material",                 StringComparison.OrdinalIgnoreCase);
                    if (!isMaterialish) continue;
                    int srcIdx0 = srcHeader.ExportTable.IndexOf(pe);
                    if (srcIdx0 < 0) continue;
                    int srcRef = srcIdx0 + 1; // UE3 FObject export ref is 1-based
                    if (futureExportIndex.TryGetValue(pe.GetPathName(), out int tgtRef))
                        micSrcToTgt[srcRef] = tgtRef;
                }
                // A slot whose material cannot come across at all is not left
                // naming nothing. A null slot draws the engine's blank default
                // - one costume's wings came out as white planes exactly
                // this way, their material being pure expression math the
                // transplant cannot carry. Such a slot is pointed at another
                // of the same mesh's carried shaders instead: the hair/cape
                // one where there is one, it being the flowing translucent-ish
                // of the set, else whichever resolves. Dressed in the wrong
                // material beats dressed in nothing.
                try
                {
                    if (s.UnrealObject is null) await s.ParseUnrealObject(false, false).ConfigureAwait(false);

                    if ((s.UnrealObject as UpkManager.Models.UpkFile.Objects.IUnrealObject)?.UObject
                        is UpkManager.Models.UpkFile.Engine.Mesh.USkeletalMesh srcMesh
                        && srcMesh.Materials is not null)
                    {
                        // The slots that resolve, and the ones that never will.
                        var resolving = new List<int>();
                        var lost = new List<int>();

                        foreach (var slot in srcMesh.Materials)
                        {
                            int slotRef = 0;

                            for (int ei = 0; ei < srcHeader.ExportTable.Count; ei++)
                            {
                                if (!ReferenceEquals(srcHeader.ExportTable[ei].ObjectNameIndex, slot)) continue;

                                slotRef = ei + 1;
                                break;
                            }

                            if (slotRef == 0) continue;

                            if (micSrcToTgt.ContainsKey(slotRef)
                                || extended.AsIndexTranslator().TranslateObjectReference(slotRef) != 0)
                                resolving.Add(slotRef);
                            else
                                lost.Add(slotRef);
                        }

                        // A mesh with no resolving slot of its own - a wings
                        // mesh whose single material is the one that cannot come
                        // - borrows from the whole carried set instead.
                        if (lost.Count > 0 && resolving.Count == 0)
                        {
                            foreach (var pe in pickedExports)
                            {
                                if (!string.Equals(pe.ClassReferenceNameIndex?.Name, "materialinstanceconstant", StringComparison.OrdinalIgnoreCase))
                                    continue;

                                int peIdx = srcHeader.ExportTable.IndexOf(pe);

                                if (peIdx < 0) continue;
                                if (!micSrcToTgt.ContainsKey(peIdx + 1)
                                    && extended.AsIndexTranslator().TranslateObjectReference(peIdx + 1) == 0) continue;

                                resolving.Add(peIdx + 1);
                            }
                        }

                        if (lost.Count > 0 && resolving.Count > 0)
                        {
                            int standIn = resolving[0];

                            foreach (int r in resolving)
                            {
                                string slotName = srcHeader.ExportTable[r - 1].ObjectNameIndex?.Name ?? string.Empty;

                                if (slotName.Contains("hair", StringComparison.OrdinalIgnoreCase)
                                    || slotName.Contains("cape", StringComparison.OrdinalIgnoreCase))
                                {
                                    standIn = r;
                                    break;
                                }
                            }

                            int standInTgt = micSrcToTgt.TryGetValue(standIn, out int mapped)
                                ? mapped
                                : extended.AsIndexTranslator().TranslateObjectReference(standIn);

                            foreach (int r in lost)
                            {
                                micSrcToTgt[r] = standInTgt;
                                issues.Add($"{ctx}: the slot for '{srcHeader.ExportTable[r - 1].ObjectNameIndex?.Name}' cannot be carried, so it wears '{srcHeader.ExportTable[standIn - 1].ObjectNameIndex?.Name}' instead of nothing");
                            }
                        }
                    }
                }
                catch
                {
                    // A mesh that cannot be examined keeps the old behaviour.
                }

                var meshResult = await meshTranslator.TranslateAsync(
                    sourceFull, s.ObjectNameIndex?.Name ?? string.Empty, translator, log,
                    overrideMaterialsSrcToTgt: micSrcToTgt.Count > 0 ? micSrcToTgt : null).ConfigureAwait(false);
                if (!meshResult.Success)
                {
                    issues.Add($"{ctx}: SkeletalMesh ref translator failed; falling back to untranslated bytes");
                    foreach (var w in meshResult.Issues.Take(4)) issues.Add($"{ctx}:     why: {w}");
                    translatedBodies.Add((byte[])srcBytes.Clone());
                }
                else
                {
                    translatedBodies.Add(meshResult.Body);
                    issues.Add($"{ctx}: SkeletalMesh refs rewritten — names {meshResult.NameRefsRewritten}/{meshResult.NameRefsRewritten + meshResult.NameRefsFailedTranslation}, objects {meshResult.ObjectRefsRewritten}/{meshResult.ObjectRefsRewritten + meshResult.ObjectRefsFailedTranslation}");
                    foreach (var w in meshResult.Issues.Where(x => x.Contains("Materials[", StringComparison.OrdinalIgnoreCase) || x.Contains("MIC map", StringComparison.OrdinalIgnoreCase))) issues.Add($"{ctx}:     warn: {w}");
                    foreach (var w in meshResult.Issues.Where(x => !x.Contains("Materials[", StringComparison.OrdinalIgnoreCase) && !x.Contains("MIC map", StringComparison.OrdinalIgnoreCase)).Take(3)) issues.Add($"{ctx}:     warn: {w}");
                }
                translatedBodyPatches.Add(new List<(int, int)>());
                continue;
            }

            // A shader instance whose parent is a material we are carrying keeps
            // the shaders baked into it. The usual reason for dropping them -
            // that the older game reads the newer game's baked shaders as
            // rubbish and should fall back to the parent's own compiled shader
            // instead - only holds when the parent HAS one. A material we write
            // afresh does not, so an instance stripped of its shaders and
            // pointed at it has nothing to draw with and comes out flat.
            // Measured on one costume: its body shader came out 2,670 bytes where
            var rewrite = rewriter.RewriteBody(srcBytes, ctx);
            byte[] addedBody;
            if (!rewrite.Success)
            {
                issues.Add($"{ctx}: translation failed; falling back to untranslated bytes (file may render this export incorrectly)");
                addedBody = (byte[])srcBytes.Clone();
            }
            else
            {
                addedBody = rewrite.Body;
                foreach (var w in rewrite.Issues.Take(3)) issues.Add($"{ctx}: {w}");
            }
            // Set NetIndex to INDEX_NONE (-1) for every newly-added export.
            // Source's NetIndex is a source-package-local value and target's
            // GenerationTable.NetObjectCount is 0, so any value (including 0)
            // triggers "AddNetObject ... invalid NetIndex N (max: 0)". UE3's
            // sentinel for "not a network object" is -1, not 0.
            if (addedBody.Length >= 4)
            {
                addedBody[0] = 0xFF; addedBody[1] = 0xFF; addedBody[2] = 0xFF; addedBody[3] = 0xFF;
            }

            // A shader instance left with no parent is given one the chassis
            // actually holds.
            //
            // The aliases map a costume's base material onto the older game's,
            // but which base a chassis has varies: most hold
            // chbasematerial_v2-1, one holds chbasematerial_v2_skin and no
            // v2-1 at all, and the alias for that name points at something its
            // package has neither as an export nor as a borrowing. No fixed
            // alias can be right for every chassis, and each one that is wrong
            // costs a costume its shader and renders it the engine's flat blue.
            //
            // So rather than name the base material, ask the chassis for one.
            // Preferring a skin base for an instance whose own name says skin
            // keeps the two families apart; otherwise any base it holds beats
            // none. This only ever replaces a null, so a parent that resolved
            // is never touched.
            if (string.Equals(cls, "materialinstanceconstant", StringComparison.OrdinalIgnoreCase))
            {
                addedBody = GiveItAParentIfItHasNone(
                    addedBody, s.ObjectNameIndex?.Name ?? string.Empty, tgtHeader, ctx, issues,
                    futureImportIndex);
            }

            translatedBodies.Add(addedBody);
            translatedBodyPatches.Add(new List<(int, int)>());
        }

        // 6b. Re-translate matched-size-changed exports. These exist on both
        // sides under the same (class, name) but with different SerialDataSize
        // — typically the costume's UClass meta and its Default__ subobject,
        // which hold the FObject refs to the MIC/mesh/physics. Source's body
        // refers to the newly-added exports (now that we just registered
        // them in target's tables via the extended translator). Overwriting
        // target's slot with the translated source body is what actually
        // wires the visual upgrade into the live costume.
        var srcByKey = new Dictionary<string, UnrealExportTableEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in srcHeader.ExportTable)
            srcByKey[$"{s.ClassReferenceNameIndex?.Name}::{s.ObjectNameIndex?.Name}"] = s;

        // Sockets are keyed by the attachment point they name instead, for the
        // same reason the picker deduplicates them that way: skeletalmeshsocket_N
        // is a running count within whichever package the socket was cooked
        // into, so pairing by that name pairs two sockets that have nothing to
        // do with each other. Measured on one costume: the target's socket_0 and
        // socket_1 were being given the source's socket_0 and socket_1, whose
        // SocketName and BoneName describe entirely different spots on the
        // body, and their bodies grew 120 -> 164 bytes in the process.
        //
        // Keyed this way a socket is only ever overwritten by the source's
        // socket for the same attachment point, and a target socket the source
        // has no counterpart for is left exactly as it was.
        var srcSocketBySocketName = new Dictionary<string, UnrealExportTableEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in srcHeader.ExportTable)
        {
            if (!string.Equals(s.ClassReferenceNameIndex?.Name, "skeletalmeshsocket", StringComparison.OrdinalIgnoreCase))
                continue;
            // A socket that is being carried in its own right must not ALSO be
            // written over target's socket of the same name. One source socket
            // landing in two slots is the doubling this whole pairing exists to
            // avoid. Target keeps its own copies, on its own mesh, untouched.
            if (pickedExports.Contains(s)) continue;

            string? socketName = ReadSocketName(srcHeader, s);
            if (socketName != null) srcSocketBySocketName[socketName] = s;
        }
        var matchedOverrides = new List<(int TgtIdx, byte[] Body)>();
        int matchedTranslated = 0;
        int matchedSkipped = 0;
        int matchedExcludedByClass = 0;
        int singletonMatched = 0;

        // Class-singleton matching: for each class in ClassSingletonMatchClasses,
        // if target has exactly 1 export of that class AND source has exactly 1
        // (with a different name — same-name would already match the regular
        // path), splice source's translated body into target's slot. The
        // target slot's name + index stays the same, so anything in target
        // that already referenced this slot now sees source's content.
        if (translateMatchedSizeChanged)
        {
            foreach (var className in ClassSingletonMatchClasses)
            {
                var tgtMatches = new List<(UnrealExportTableEntry E, int Idx)>();
                for (int i = 0; i < tgtHeader.ExportTable.Count; i++)
                {
                    var e = tgtHeader.ExportTable[i];
                    if (string.Equals(e.ClassReferenceNameIndex?.Name, className, StringComparison.OrdinalIgnoreCase))
                        tgtMatches.Add((e, i));
                }
                var srcMatches = srcHeader.ExportTable
                    .Where(e => string.Equals(e.ClassReferenceNameIndex?.Name, className, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (tgtMatches.Count != 1 || srcMatches.Count != 1) continue;
                if (string.Equals(tgtMatches[0].E.ObjectNameIndex?.Name, srcMatches[0].ObjectNameIndex?.Name,
                                   StringComparison.OrdinalIgnoreCase)) continue; // already a regular match
                var src = srcMatches[0];
                var tgt = tgtMatches[0];
                string ctx = $"singleton-{className}::{tgt.E.ObjectNameIndex?.Name}<-{src.ObjectNameIndex?.Name}";

                byte[] srcBytes = src.UnrealObjectReader?.GetBytes() ?? Array.Empty<byte>();
                var rewrite = rewriter.RewriteBody(srcBytes, ctx);
                if (!rewrite.Success)
                {
                    issues.Add($"{ctx}: singleton-match translation failed; skipped");
                    foreach (var rwIssue in rewrite.Issues.Take(5))
                        issues.Add($"{ctx}:     why: {rwIssue}");
                    continue;
                }
                byte[] finalBody = rewrite.Body;
                // Same prefix-overlay rationale as the matched-translate path:
                // keep target's NetIndex/ObjectArchetype, swap only the
                // property stream + binary tail.
                byte[] tgtBytesForPrefix2 = tgt.E.UnrealObjectReader?.GetBytes() ?? Array.Empty<byte>();
                int overlayLen2 = Math.Min(rewrite.PrefixLength, Math.Min(finalBody.Length, tgtBytesForPrefix2.Length));
                if (overlayLen2 > 0)
                {
                    Buffer.BlockCopy(tgtBytesForPrefix2, 0, finalBody, 0, overlayLen2);
                    issues.Add($"{ctx}: overlaid target's {overlayLen2}-byte prefix (preserves NetIndex/ObjectArchetype)");
                }
                if (mergeMatchedWithTarget)
                {
                    byte[] tgtBytes = tgt.E.UnrealObjectReader?.GetBytes() ?? Array.Empty<byte>();
                    finalBody = PropertyTableMerger.Merge(srcBytes, finalBody, tgtBytes, translator, out var mergeDiags);
                    foreach (var d in mergeDiags) issues.Add($"{ctx}: {d}");
                }
                matchedOverrides.Add((tgt.Idx, finalBody));
                singletonMatched++;
                log?.Invoke($"Phase 2: singleton-matched {className} '{tgt.E.ObjectNameIndex?.Name}' <- source '{src.ObjectNameIndex?.Name}'");
                // DIAG: emit via issues.Add so it lands in the report's
                // "Translation warnings" section (log?.Invoke doesn't appear
                // in the final report text).
                issues.Add($"[OVR-TRACE] singleton tgtIdx={tgt.Idx} tgtClass='{className}' tgtName='{tgt.E.ObjectNameIndex?.Name}' srcName='{src.ObjectNameIndex?.Name}' bodyLen={finalBody.Length} first16={BitConverter.ToString(finalBody, 0, Math.Min(16, finalBody.Length))}");
            }
        }
        if (translateMatchedSizeChanged)
        {
            for (int ti = 0; ti < tgtHeader.ExportTable.Count; ti++)
            {
                var t = tgtHeader.ExportTable[ti];
                string clsName = t.ClassReferenceNameIndex?.Name ?? string.Empty;
                UnrealExportTableEntry? s;
                if (string.Equals(clsName, "skeletalmeshsocket", StringComparison.OrdinalIgnoreCase))
                {
                    // Pair by the attachment point, never by the running count
                    // in the export name. See srcSocketBySocketName above.
                    string? tgtSocketName = ReadSocketName(tgtHeader, t);
                    if (tgtSocketName == null) continue;
                    if (!srcSocketBySocketName.TryGetValue(tgtSocketName, out s)) continue;
                }
                else
                {
                    string key = $"{clsName}::{t.ObjectNameIndex?.Name}";
                    if (!srcByKey.TryGetValue(key, out s)) continue;
                }
                // Same-size matched exports are normally skipped as
                // unchanged, but a few classes (SkeletalMeshComponent) need
                // translation anyway because their body holds critical
                // cross-version refs that must be remapped.
                if (s.SerialDataSize == t.SerialDataSize
                    && !ForceSameSizeMatchedTranslateClasses.Contains(clsName))
                    continue;
                if (DefaultMatchedTranslateExcludeClasses.Contains(clsName))
                {
                    matchedExcludedByClass++;
                    issues.Add($"matched-{clsName}::{t.ObjectNameIndex?.Name}: excluded from re-translation by class filter (physics chain protection)");
                    continue;
                }
                string ctx = $"matched-{t.ClassReferenceNameIndex?.Name}::{t.ObjectNameIndex?.Name}";
                byte[] srcBytes = s.UnrealObjectReader?.GetBytes() ?? Array.Empty<byte>();
                var rewrite = rewriter.RewriteBody(srcBytes, ctx);
                if (!rewrite.Success)
                {
                    issues.Add($"{ctx}: matched-size-change translation failed; keeping original target body");
                    foreach (var rwIssue in rewrite.Issues.Take(5))
                        issues.Add($"{ctx}:     why: {rwIssue}");
                    matchedSkipped++;
                    continue;
                }
                byte[] finalBody = rewrite.Body;
                // Overlay target's pre-property prefix bytes (NetIndex +
                // UComponent ObjectArchetype + etc.). Source's NetIndex is
                // valid only in source's package; copying it through breaks
                // the engine's net object registration. Target's NetIndex
                // is what the destination package's NetObjects table
                // already references.
                byte[] tgtBytesForPrefix = t.UnrealObjectReader?.GetBytes() ?? Array.Empty<byte>();
                int overlayLen = Math.Min(rewrite.PrefixLength, Math.Min(finalBody.Length, tgtBytesForPrefix.Length));
                if (overlayLen > 0)
                {
                    Buffer.BlockCopy(tgtBytesForPrefix, 0, finalBody, 0, overlayLen);
                    issues.Add($"{ctx}: overlaid target's {overlayLen}-byte prefix (preserves NetIndex/ObjectArchetype)");
                }
                if (mergeMatchedWithTarget && !MergeKeepsSourceWholeClasses.Contains(clsName))
                {
                    // Property-level merge: keep target's value where source's
                    // translation would null-out a ref (because the source
                    // export wasn't added to target — e.g. new SkeletalMesh).
                    // Also fills in target-only properties that source's
                    // smaller Default__ inherits from its parent class.
                    byte[] tgtBytes = t.UnrealObjectReader?.GetBytes() ?? Array.Empty<byte>();
                    finalBody = PropertyTableMerger.Merge(srcBytes, finalBody, tgtBytes, translator, out var mergeDiags);
                    foreach (var d in mergeDiags) issues.Add($"{ctx}: {d}");
                }
                matchedOverrides.Add((ti, finalBody));
                matchedTranslated++;
                foreach (var w in rewrite.Issues.Take(3)) issues.Add($"{ctx}: {w}");
                // DIAG: same trace as singleton path. Use issues.Add so it
                // appears in the report's warnings section.
                issues.Add($"[OVR-TRACE] matched tgtIdx={ti} tgtClass='{clsName}' tgtName='{t.ObjectNameIndex?.Name}' srcClass='{s.ClassReferenceNameIndex?.Name}' srcName='{s.ObjectNameIndex?.Name}' bodyLen={finalBody.Length} first16={BitConverter.ToString(finalBody, 0, Math.Min(16, finalBody.Length))}");
            }
            log?.Invoke($"Phase 2: matched-size-changed translated={matchedTranslated}, skipped={matchedSkipped}");
        }
        else
        {
            log?.Invoke("Phase 2: matched-size-changed translation DISABLED — target's original bodies kept (no visual upgrade wiring)");
        }

        // 6b. Point the target's morph target sets at the mesh that is actually
        // going to be drawn.
        //
        // A morph set names the mesh it belongs to in BaseSkelMesh, and the
        // engine will not apply a set whose BaseSkelMesh is not the mesh the
        // component is rendering — it treats the set as belonging to something
        // else and skips it. After a swap the drawn mesh is the newer
        // costume's, which we have just added, while the target's set still
        // names the older costume's mesh. So the set is skipped, and every
        // shape in it silently does nothing.
        //
        // That is why those blade shapes stay out. Giving the shapes the newer
        // costume's offsets (see MergeKeepsSourceWholeClasses) is necessary but
        // not sufficient: correct offsets in a set the engine never applies
        // still produce no movement.
        //
        // Only done when the swap carried exactly one mesh. With two or more
        // there is no way to tell from here which one the morphs belong to,
        // and guessing would bind them to a hammer or a pair of wings.
        {
            var carriedMeshes = pickedExports
                .Where(e => string.Equals(e.ClassReferenceNameIndex?.Name, "skeletalmesh", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (carriedMeshes.Count == 1
                && futureExportIndex.TryGetValue(carriedMeshes[0].GetPathName(), out int carriedMeshRef))
            {
                for (int ti = 0; ti < tgtHeader.ExportTable.Count; ti++)
                {
                    var t = tgtHeader.ExportTable[ti];
                    if (!string.Equals(t.ClassReferenceNameIndex?.Name, "morphtargetset", StringComparison.OrdinalIgnoreCase))
                        continue;
                    byte[] setBody = t.UnrealObjectReader?.GetBytes() ?? Array.Empty<byte>();
                    if (setBody.Length == 0) continue;

                    var (setSpans, _, _) = PropertyTableParser.Parse(setBody, tgtHeader.NameTable);
                    bool patched = false;
                    byte[] newSetBody = (byte[])setBody.Clone();

                    foreach (var span in setSpans)
                    {
                        if (!string.Equals(span.TagName, "BaseSkelMesh", StringComparison.OrdinalIgnoreCase)) continue;
                        if (span.ValueLen != 4) continue;

                        // The span's offset is relative to the span; find where
                        // that span begins in the body by matching its bytes.
                        int spanStart = IndexOfSpan(setBody, span);
                        if (spanStart < 0) continue;

                        BitConverter.GetBytes(carriedMeshRef)
                            .CopyTo(newSetBody, spanStart + span.ValueOffsetInSpan);
                        patched = true;
                    }

                    if (patched)
                    {
                        matchedOverrides.Add((ti, newSetBody));
                        issues.Add($"morphtargetset::{t.ObjectNameIndex?.Name}: base mesh pointed at the carried mesh '{carriedMeshes[0].ObjectNameIndex?.Name}' so its shapes are applied");
                    }
                }
            }
            else if (carriedMeshes.Count > 1)
            {
                issues.Add($"morph sets left alone: {carriedMeshes.Count} meshes were carried and which one the shapes belong to cannot be told from here");
            }
        }

        // 6c. Two-sided rendering for costumes that were authored with it.
        //
        // A cape is one sheet of geometry. Its inside is only drawn if the base
        // material the cape's shader is built on is two-sided, and that is a
        // plain tagged property: `twosided`, a BoolProperty carrying its value
        // in the tag itself, size 0. Measured in UC__MarvelAgent_Lizard_SF,
        // which holds both twins - chbasematerial_v2_dblsided has it at 0x14C
        // and chbasematerial_v2 does not have it at all. That is the whole
        // difference between the two.
        //
        // The newer costume expects it because its own base material is a
        // `_dblsided` one. That material cannot come across: borrowing it binds
        // to nothing (chbasematerials_v2 is a group export inside each costume
        // file, not a package of its own) and carrying it makes the costume
        // transparent (its expression graph reaches engine nodes the older game
        // does not expose under the same paths - which is why `material` is not
        // on the class allowlist). So instead of bringing the two-sided
        // material over, the tag is written onto the base material the older
        // costume already has and the carried shaders are already built on.
        //
        // The older game reaches the same look by modelling both faces of the
        // cape into the body mesh, so its own base materials are single-sided
        // and untouched by this until a costume that wants otherwise arrives.
        //
        // Turning it on for a closed body mesh costs a little fill rate and
        // looks the same, the back faces being hidden by the front ones.
        if (costumeWantsTwoSided)
        {
            int twoSidedNameIdx = FindTgtNameIdx(tgtHeader, futureNameIndex, "twosided");
            int boolTypeNameIdx = FindTgtNameIdx(tgtHeader, futureNameIndex, "BoolProperty");
            int alreadyTwoSided = 0, madeTwoSided = 0;
            var twoSidedRefByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            if (twoSidedNameIdx < 0 || boolTypeNameIdx < 0)
            {
                issues.Add("two-sided: the package could not name 'twosided'/'BoolProperty', so nothing was changed");
            }
            else
            {
                for (int ti = 0; ti < tgtHeader.ExportTable.Count; ti++)
                {
                    var t = tgtHeader.ExportTable[ti];

                    if (!string.Equals(t.ClassReferenceNameIndex?.Name, "material", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Only the character base materials — the ones a costume's
                    // shaders are built on. Effect materials in the same package
                    // decide their own sidedness and are left alone.
                    string tName = t.ObjectNameIndex?.Name ?? string.Empty;

                    if (!tName.StartsWith("chbasematerial", StringComparison.OrdinalIgnoreCase)) continue;

                    // Never the skin base. Writing the tag onto one costume's
                    // chbasematerial_v2_skin left it drawn flat and
                    // untextured, where the same tag on another's
                    // chbasematerial_v2_masked did no harm at all and gave it
                    // its cape back. Skin is what a body is built on, so when it
                    // goes wrong the whole costume goes with it. A cape hangs off
                    // a base of its own, and that is the one to write on.
                    if (tName.Contains("skin", StringComparison.OrdinalIgnoreCase)) continue;

                    // One is enough. The shaders that want two sides are pointed
                    // at whichever base is written on, so writing on more of them
                    // only widens what can go wrong.
                    if (madeTwoSided > 0) break;

                    byte[] matBody = t.UnrealObjectReader?.GetBytes() ?? Array.Empty<byte>();

                    if (matBody.Length == 0) continue;

                    var (matSpans, matTail, matPrefixLen) =
                        PropertyTableParser.Parse(matBody, tgtHeader.NameTable);

                    if (matSpans.Count == 0)
                    {
                        issues.Add($"two-sided: '{tName}' could not be read as a property table, so it was left alone");
                        continue;
                    }

                    if (matSpans.Any(s => string.Equals(s.TagName, "twosided", StringComparison.OrdinalIgnoreCase)))
                    {
                        // The chassis already owns a two-sided base. That is the
                        // one to build on, and nothing here needs changing.
                        alreadyTwoSided++;
                        twoSidedRefByName[tName] = ti + 1;
                        continue;
                    }

                    // Only supply one where the chassis has none. Modern VU, for
                    // instance, ships chbasematerial_v2_dblsided already.
                    if (alreadyTwoSided > 0 || TargetAlreadyHasATwoSidedBase(tgtHeader)) continue;

                    using var rebuilt = new MemoryStream();

                    if (matPrefixLen > 0) rebuilt.Write(matBody, 0, matPrefixLen);

                    foreach (var s in matSpans) rebuilt.Write(s.Bytes, 0, s.Bytes.Length);

                    // name(4) + numeric(4) + type(4) + numeric(4) + size(4) +
                    // arrayIndex(4) + the value, one byte, in the tag itself.
                    rebuilt.Write(BitConverter.GetBytes(twoSidedNameIdx), 0, 4);
                    rebuilt.Write(BitConverter.GetBytes(0), 0, 4);
                    rebuilt.Write(BitConverter.GetBytes(boolTypeNameIdx), 0, 4);
                    rebuilt.Write(BitConverter.GetBytes(0), 0, 4);
                    rebuilt.Write(BitConverter.GetBytes(0), 0, 4);
                    rebuilt.Write(BitConverter.GetBytes(0), 0, 4);
                    rebuilt.WriteByte(1);

                    int noneNameIdx = FindTgtNameIdx(tgtHeader, futureNameIndex, "None");
                    rebuilt.Write(BitConverter.GetBytes(noneNameIdx < 0 ? 0 : noneNameIdx), 0, 4);
                    rebuilt.Write(BitConverter.GetBytes(0), 0, 4);

                    if (matTail.Length > 0) rebuilt.Write(matTail, 0, matTail.Length);

                    matchedOverrides.Add((ti, rebuilt.ToArray()));
                    madeTwoSided++;
                    twoSidedRefByName[tName] = ti + 1; // positive = export ref
                    issues.Add($"material::{tName}: made two-sided, so the inside faces of the costume are drawn");
                }

                // Tagging the material is only half of it. A shader that was
                // built on a `_dblsided` base in the newer game does not land
                // on it here - the alias sends it to the older game's plain
                // base material, which is an IMPORT, an object in some other
                // file that cannot be tagged. So the shaders that wanted a
                // two-sided base are pointed at the local one that now is.
                int repointed = 0;

                if (twoSidedRefByName.Count > 0)
                {
                    for (int pi = 0; pi < pickedExports.Count && pi < translatedBodies.Count; pi++)
                    {
                        var picked = pickedExports[pi];

                        if (!string.Equals(picked.ClassReferenceNameIndex?.Name, "materialinstanceconstant", StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (!SourceParentWantedTwoSided(picked, srcHeader)) continue;

                        // Prefer a skin base for a skin shader, as the rest of
                        // this file does; otherwise whichever we tagged.
                        bool wantsSkin = (picked.ObjectNameIndex?.Name ?? string.Empty)
                            .Contains("skin", StringComparison.OrdinalIgnoreCase);

                        int chosen = 0;
                        string chosenName = string.Empty;

                        foreach (var kv in twoSidedRefByName)
                        {
                            bool isSkin = kv.Key.Contains("skin", StringComparison.OrdinalIgnoreCase);

                            if (chosen == 0 || isSkin == wantsSkin) { chosen = kv.Value; chosenName = kv.Key; }
                            if (isSkin == wantsSkin) break;
                        }

                        if (chosen == 0) continue;

                        byte[] micBody = translatedBodies[pi];
                        var (micSpans, _, _) = PropertyTableParser.Parse(micBody, tgtHeader.NameTable);

                        foreach (var span in micSpans)
                        {
                            if (!string.Equals(span.TagName, "Parent", StringComparison.OrdinalIgnoreCase)) continue;
                            if (span.ValueLen != 4) continue;

                            int spanStart = IndexOfSpan(micBody, span);

                            if (spanStart < 0) continue;

                            BitConverter.GetBytes(chosen).CopyTo(micBody, spanStart + span.ValueOffsetInSpan);
                            repointed++;
                            issues.Add($"materialinstanceconstant::{picked.ObjectNameIndex?.Name}: built on '{chosenName}', the two-sided base, as it was in the newer costume");
                            break;
                        }
                    }
                }

                log?.Invoke($"Phase 2: two-sided — {madeTwoSided} base material(s) turned two-sided, {alreadyTwoSided} already were, {repointed} shader(s) repointed at one");
            }
        }

        // 7. Build the output file bytes.
        log?.Invoke("Phase 2: writing extended UPK bytes");
        byte[] originalTargetBytes = await File.ReadAllBytesAsync(targetFull).ConfigureAwait(false);
        // Decompress if necessary so the offsets we patch refer to a flat byte
        // stream the engine can load directly. (UpkRepacker uses this same path
        // for compressed packages.)
        if (tgtHeader.CompressedChunks.Count > 0)
            originalTargetBytes = UpkRepacker.PrepareDecompressedHeaderBytes(originalTargetBytes, tgtHeader);

        byte[] outBytes = Phase2TableExtender.Build(
            originalTargetBytes,
            tgtHeader,
            namesToAdd,
            importsToAdd,
            pickedExports,
            translatedBodies,
            futureNameIndex,
            srcHeader,
            matchedOverrides,
            translatedBodyPatches,
            syntheticImportOuterTargetRefs: syntheticOuterTargetRefs.Count > 0 ? syntheticOuterTargetRefs : null);

        string? outDir = Path.GetDirectoryName(outputFull);
        if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
            Directory.CreateDirectory(outDir);
        await File.WriteAllBytesAsync(outputFull, outBytes).ConfigureAwait(false);
        long outLen = new FileInfo(outputFull).Length;

        var sb = new StringBuilder();
        sb.AppendLine("Phase 2 Material Transplant complete.");
        sb.AppendLine($"  Source : {sourceFull}");
        sb.AppendLine($"  Target : {targetFull}");
        sb.AppendLine($"  Output : {outputFull}  ({outLen:N0} bytes)");
        sb.AppendLine($"  Backup : {backupPath ?? "(target didn't exist)"}");
        sb.AppendLine();
        sb.AppendLine("-- Extension stats --");
        sb.AppendLine($"  Names   : {tgtHeader.NameTable.Count,5}  +{namesToAdd.Count}  -> {tgtHeader.NameTable.Count + namesToAdd.Count}");
        sb.AppendLine($"  Imports : {tgtHeader.ImportTable.Count,5}  +{importsToAdd.Count}  -> {tgtHeader.ImportTable.Count + importsToAdd.Count}");
        sb.AppendLine($"  Exports : {tgtHeader.ExportTable.Count,5}  +{pickedExports.Count}  -> {tgtHeader.ExportTable.Count + pickedExports.Count}");
        sb.AppendLine($"  Matched-size-changed re-translated : {matchedTranslated}  (skipped {matchedSkipped})");
        sb.AppendLine($"  Class-singleton matches (e.g. MIC body swap) : {singletonMatched}");
        sb.AppendLine();
        sb.AppendLine("-- Picked source-only exports --");
        foreach (var s in pickedExports)
            sb.AppendLine($"  {s.ClassReferenceNameIndex?.Name,-40}  {s.ObjectNameIndex?.Name,-50}  {s.SerialDataSize,8} bytes");
        sb.AppendLine();
        sb.AppendLine($"-- Synthetic-import pass --");
        sb.AppendLine($"  Synthetic imports added : {syntheticAddedCount}");
        sb.AppendLine($"  Skipped (no target package outer) : {syntheticSkippedNoPackage}");
        if (syntheticAddedCount > 0)
        {
            foreach (var syn in syntheticImports)
            {
                string p = syntheticImportSrcPath.TryGetValue(syn, out var sp) ? sp : "(?)";
                sb.AppendLine($"  SYN-IMPORT  {syn.ClassNameIndex?.Name,-15}  {p}  outerRefTgt={syn.OuterReference}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("-- Smoke test goal --");
        sb.AppendLine("  This output APPENDS the new MIC/textures/physics to target. It does NOT yet");
        sb.AppendLine("  rewire any existing export to USE them — the new exports are orphan content.");
        sb.AppendLine("  Success criterion for this turn: file loads + costume equips with TARGET's");
        sb.AppendLine("  original look. That proves table extension works. Next turn: wire references");
        sb.AppendLine("  so the new MIC and textures actually get applied to the mesh.");

        // -- Cross-UPK inline discovery report (milestone 1: log-only) --
        sb.AppendLine();
        sb.AppendLine("-- Cross-UPK inline discovery (milestone 1: discovery only, output unchanged) --");
        sb.AppendLine($"  Resolvable foreign refs   : {inlineResolved.Count}");
        sb.AppendLine($"  Unresolvable foreign refs : {inlineUnresolved.Count}");
        sb.AppendLine($"  Foreign UPKs loaded       : {inliner.LoadedForeignUpkCount}");
        if (inlineResolved.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  -- WOULD INLINE (top 60) --");
            foreach (var r in inlineResolved.Take(60))
                sb.AppendLine($"    {r.ForeignUpkName,-30}  {r.Entry.ClassReferenceNameIndex?.Name,-30}  {r.ExportPathInForeign}");
            if (inlineResolved.Count > 60)
                sb.AppendLine($"    ... ({inlineResolved.Count - 60} more)");
        }
        if (inlineUnresolved.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  -- UNRESOLVABLE (foreign UPK not on disk OR export path doesn't match) --");
            foreach (var u in inlineUnresolved.Take(30))
                sb.AppendLine($"    {u}");
            if (inlineUnresolved.Count > 30)
                sb.AppendLine($"    ... ({inlineUnresolved.Count - 30} more)");
        }
        if (issues.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"-- Translation warnings ({issues.Count}) --");
            foreach (var i in issues) sb.AppendLine($"  {i}");
        }

        var resultObj = new Result
        {
            OutputPath = outputFull,
            BackupPath = backupPath,
            NamesAdded = namesToAdd.Count,
            ImportsAdded = importsToAdd.Count,
            ExportsAdded = pickedExports.Count,
            OutputBytes = outLen,
            Summary = sb.ToString(),
        };
        // Mirror the per-export warnings into result.Issues so callers
        // (batch runner) can filter for the smoking-gun lines without
        // having to re-parse the summary string. Issues list is a
        // get-only init-allowed property; populate in-place via AddRange.
        resultObj.Issues.AddRange(issues);
        return resultObj;
    }

    // Wrapper that lets PropertyTagRewriter resolve through the extended
    // translation tables (target NameTable + planned additions).
    private sealed class ExtendedTranslator
    {
        private readonly IndexTranslator baseTranslator;
        private readonly Dictionary<string, int> futureNameByText;
        private readonly Dictionary<string, int> futureImportByPath;
        private readonly Dictionary<string, int> futureExportByPath;

        public ExtendedTranslator(
            IndexTranslator baseTranslator,
            Dictionary<string, int> futureNameByText,
            Dictionary<string, int> futureImportByPath,
            Dictionary<string, int> futureExportByPath)
        {
            this.baseTranslator = baseTranslator;
            this.futureNameByText = futureNameByText;
            this.futureImportByPath = futureImportByPath;
            this.futureExportByPath = futureExportByPath;
        }

        // Builds a NEW IndexTranslator whose Name/Import/Export maps fold in
        // the future additions. We piggyback on the existing class by
        // mutating the maps via reflection-free re-assignment is impossible
        // (the arrays are immutable in shape). Easiest path: subclass-style
        // wrapper via internal access — but for now, build a delegating
        // adapter by patching the existing maps in place (they're public
        // int[] / List<string> on IndexTranslator).
        public IndexTranslator AsIndexTranslator()
        {
            // Patch the existing base translator's maps in-place: replace -1
            // entries with their future indices where the source name now
            // exists in the planned-additions set.
            for (int i = 0; i < baseTranslator.NameMap.Length; i++)
            {
                if (baseTranslator.NameMap[i] >= 0) continue;
                string srcName = baseTranslator.Source.NameTable[i]?.Name?.String ?? string.Empty;
                if (futureNameByText.TryGetValue(srcName, out int futureIdx))
                    baseTranslator.NameMap[i] = futureIdx;
            }
            for (int i = 0; i < baseTranslator.ImportMap.Length; i++)
            {
                if (baseTranslator.ImportMap[i] != 0) continue;
                string path = ImportFullPath(baseTranslator.Source, baseTranslator.Source.ImportTable[i]);
                if (futureImportByPath.TryGetValue(path, out int futureRef))
                    baseTranslator.ImportMap[i] = futureRef;
            }
            for (int i = 0; i < baseTranslator.ExportMap.Length; i++)
            {
                if (baseTranslator.ExportMap[i] != 0) continue;
                string path = baseTranslator.Source.ExportTable[i].GetPathName();
                if (futureExportByPath.TryGetValue(path, out int futureRef))
                {
                    baseTranslator.ExportMap[i] = futureRef;
                    continue;
                }
                // Cross-map fallback: source's export may have a corresponding
                // planned IMPORT addition (the synthetic-import path used for
                // material classes that can't be safely transplanted as
                // exports). futureImportByPath holds negative refs to those.
                if (futureImportByPath.TryGetValue(path, out int futureImportRef))
                {
                    baseTranslator.ExportMap[i] = futureImportRef;
                    continue;
                }

                // And the same two lookups again under the alias table. The
                // base translator already alias-matches source exports, but
                // only against the export/import tables target ALREADY has —
                // it is built before the borrowing pass exists, so an alias
                // whose destination is one of the imports we are about to ADD
                // can never match there. This is the only place those two
                // facts meet, and without it such an alias is silently inert.
                //
                // Concrete case: 1.53 costumes parent alpha-cutout pieces at
                // `chbasematerials.chbasematerial_masked`, held as an inlined
                // EXPORT of their own package. 1.52 costume packages have no
                // such export and no such import, so the base translator's
                // alias pass finds nothing; but the plain base material IS
                // being added as a borrowing (source imports it for the body
                // MIC), so the alias resolves here and nowhere else. Verified
                // on Gambit_Shirtless -> Gambit_Classic: scarves_mat parented
                // at null before, at the base material after.
                string aliasedPath = baseTranslator.ApplyAliases(path);
                if (!string.Equals(aliasedPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    if (futureExportByPath.TryGetValue(aliasedPath, out int aliasedExportRef))
                    {
                        baseTranslator.ExportMap[i] = aliasedExportRef;
                        continue;
                    }
                    if (futureImportByPath.TryGetValue(aliasedPath, out int aliasedImportRef))
                        baseTranslator.ExportMap[i] = aliasedImportRef;
                }
            }
            return baseTranslator;
        }
    }

    // Where a parsed span begins within the body it came from. The parser
    // hands back each span's bytes but not its offset, and patching a value in
    // place needs the offset.
    private static int IndexOfSpan(byte[] body, PropertyTagSpan span)
    {
        byte[] want = span.Bytes;
        if (want.Length == 0 || want.Length > body.Length) return -1;

        for (int at = 0; at + want.Length <= body.Length; at++)
        {
            bool same = true;
            for (int k = 0; k < want.Length; k++)
            {
                if (body[at + k] != want[k]) { same = false; break; }
            }
            if (same) return at;
        }
        return -1;
    }

    // Whether a shader instance names a base material that the target can
    // resolve - through the aliases, and through anything queued to be added.
    // False when it names nothing, or names something with no counterpart.
    private static bool HasAParentTargetCanResolve(
        UnrealExportTableEntry mic, UnrealHeader srcHeader, IndexTranslator translator)
    {
        try
        {
            byte[] body = mic.UnrealObjectReader?.GetBytes() ?? Array.Empty<byte>();
            if (body.Length == 0) return false;

            var (spans, _, _) = PropertyTableParser.Parse(body, srcHeader.NameTable);

            foreach (var span in spans)
            {
                if (!string.Equals(span.TagName, "Parent", StringComparison.OrdinalIgnoreCase)) continue;
                if (span.ValueLen != 4) return false;

                int srcRef = BitConverter.ToInt32(span.Bytes, span.ValueOffsetInSpan);

                return srcRef != 0 && translator.TranslateObjectReference(srcRef) != 0;
            }
        }
        catch
        {
            // A body that will not read is treated as having no usable parent,
            // which sends it down the value-only path exactly as before.
        }

        return false;
    }

    // Gives a shader instance whose Parent came out null a base material the
    // target actually holds. Returns the body unchanged when the parent
    // resolved, when there is no Parent property, or when the target has no
    // base material to offer.
    private static byte[] GiveItAParentIfItHasNone(
        byte[] body, string instanceName, UnrealHeader tgtHeader, string ctx, List<string> issues,
        IReadOnlyDictionary<string, int>? futureImports = null)
    {
        try
        {
            var (spans, _, _) = PropertyTableParser.Parse(body, tgtHeader.NameTable);

            foreach (var span in spans)
            {
                if (!string.Equals(span.TagName, "Parent", StringComparison.OrdinalIgnoreCase)) continue;
                if (span.ValueLen != 4) return body;

                int start = IndexOfSpan(body, span);
                if (start < 0) return body;
                if (BitConverter.ToInt32(body, start + span.ValueOffsetInSpan) != 0) return body;

                bool wantsSkin = instanceName.Contains("skin", StringComparison.OrdinalIgnoreCase);
                int chosen = 0;
                string chosenName = string.Empty;

                for (int i = 0; i < tgtHeader.ExportTable.Count; i++)
                {
                    var e = tgtHeader.ExportTable[i];

                    if (!string.Equals(e.ClassReferenceNameIndex?.Name, "material", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string name = e.ObjectNameIndex?.Name ?? string.Empty;
                    if (name.IndexOf("chbasematerial", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    bool isSkin = name.Contains("skin", StringComparison.OrdinalIgnoreCase);

                    // First acceptable one wins, but a skin base is preferred
                    // for a skin instance and a non-skin base otherwise.
                    if (chosen == 0 || isSkin == wantsSkin)
                    {
                        chosen = i + 1;
                        chosenName = name;
                        if (isSkin == wantsSkin) break;
                    }
                }

                // An alpha-cut shader is given a MASKED base above all else -
                // on a plain one its cutouts draw as solid white sheets. The
                // masked import may be one being added right now, known only
                // by its future index.
                bool wantsMask = instanceName.Contains("alpha", StringComparison.OrdinalIgnoreCase)
                    || instanceName.Contains("masked", StringComparison.OrdinalIgnoreCase);

                if (chosen == 0 && wantsMask && futureImports is not null)
                {
                    foreach (string masked in new[]
                    {
                        "chbasematerials_v2.chbasematerial_v2_skin_masked",
                        "chbasematerials_v2.chbasematerial_v2_masked",
                    })
                    {
                        if (!futureImports.TryGetValue(masked, out int maskedRef)) continue;

                        chosen = maskedRef;
                        chosenName = masked[(masked.IndexOf('.') + 1)..] + " (borrowed)";
                        break;
                    }
                }

                // Some chassis hold no base material at all - every costume of
                // one character in the older game borrows one instead. Their own
                // shaders are built on that borrowing and draw correctly, so it
                // will do here too. Only looked at when there is nothing local.
                if (chosen == 0)
                {
                    for (int i = 0; i < tgtHeader.ImportTable.Count; i++)
                    {
                        var im = tgtHeader.ImportTable[i];

                        if (!string.Equals(im.ClassNameIndex?.Name, "material", StringComparison.OrdinalIgnoreCase))
                            continue;

                        string name = im.ObjectNameIndex?.Name ?? string.Empty;

                        if (name.IndexOf("chbasematerial", StringComparison.OrdinalIgnoreCase) < 0) continue;

                        bool isSkin = name.Contains("skin", StringComparison.OrdinalIgnoreCase);

                        if (chosen == 0 || isSkin == wantsSkin)
                        {
                            chosen = -(i + 1); // negative: a borrowing, not an export
                            chosenName = name + " (borrowed)";

                            if (isSkin == wantsSkin) break;
                        }
                    }
                }

                if (chosen == 0)
                {
                    issues.Add($"{ctx}: has no parent and the chassis offers no base material to give it one");
                    return body;
                }

                byte[] patched = (byte[])body.Clone();
                BitConverter.GetBytes(chosen).CopyTo(patched, start + span.ValueOffsetInSpan);

                issues.Add($"{ctx}: had no parent, so it was built on the chassis's '{chosenName}'");

                return patched;
            }
        }
        catch
        {
            // A body that will not read is left exactly as it is.
        }

        return body;
    }

    // What holds a thing, taken from its full path: everything before the last
    // dot. Empty when it holds nothing above it.
    private static string OuterPathOf(string path)
    {
        int stop = path.LastIndexOf('.');

        return stop > 0 ? path[..stop] : string.Empty;
    }

    // The SocketName a SkeletalMeshSocket carries, which is what actually
    // identifies the attachment point. Returns null when the body cannot be
    // read or holds no such property, and the caller then falls back to the
    // ordinary class+name rule.
    private static string? ReadSocketName(UnrealHeader header, UnrealExportTableEntry entry)
    {
        try
        {
            byte[] body = entry.UnrealObjectReader?.GetBytes() ?? Array.Empty<byte>();
            if (body.Length == 0) return null;

            var (spans, _, _) = PropertyTableParser.Parse(body, header.NameTable);
            foreach (var s in spans)
            {
                if (!string.Equals(s.TagName, "SocketName", StringComparison.OrdinalIgnoreCase)) continue;
                if (s.ValueLen < 8) return null;

                int nameIdx = BitConverter.ToInt32(s.Bytes, s.ValueOffsetInSpan);
                int numeric = BitConverter.ToInt32(s.Bytes, s.ValueOffsetInSpan + 4);
                if (nameIdx < 0 || nameIdx >= header.NameTable.Count) return null;

                string baseName = header.NameTable[nameIdx]?.Name?.String ?? string.Empty;
                if (baseName.Length == 0) return null;

                // FName is a base string plus a numeric suffix; both matter,
                // since socket_l_hand and socket_l_hand_1 are different spots.
                return numeric > 0 ? $"{baseName}_{numeric - 1}" : baseName;
            }
        }
        catch
        {
            // A socket whose body will not read is left to the ordinary rule.
        }
        return null;
    }

    // Helper for synthetic-import construction: find a name in source's name
    // table by string. Returns -1 if not present (caller skips synthesis).

    // Whether the older costume already owns a base material that is two-sided,
    // in which case nothing needs to be written onto any of its materials.
    private static bool TargetAlreadyHasATwoSidedBase(UnrealHeader tgtHeader)
    {
        foreach (var t in tgtHeader.ExportTable)
        {
            if (!string.Equals(t.ClassReferenceNameIndex?.Name, "material", StringComparison.OrdinalIgnoreCase))
                continue;

            byte[] body = t.UnrealObjectReader?.GetBytes() ?? Array.Empty<byte>();

            if (body.Length == 0) continue;

            var (spans, _, _) = PropertyTableParser.Parse(body, tgtHeader.NameTable);

            if (spans.Any(s => string.Equals(s.TagName, "twosided", StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    // Whether a shader was built, in the newer costume, on a two-sided base.
    // Read off the source object's own Parent tag against the source package.
    private static bool SourceParentWantedTwoSided(UnrealExportTableEntry picked, UnrealHeader srcHeader)
    {
        byte[] body = picked.UnrealObjectReader?.GetBytes() ?? Array.Empty<byte>();

        if (body.Length == 0) return false;

        var (spans, _, _) = PropertyTableParser.Parse(body, srcHeader.NameTable);

        foreach (var span in spans)
        {
            if (!string.Equals(span.TagName, "Parent", StringComparison.OrdinalIgnoreCase)) continue;
            if (span.ValueLen != 4) continue;

            int spanStart = IndexOfSpan(body, span);

            if (spanStart < 0) continue;

            int r = BitConverter.ToInt32(body, spanStart + span.ValueOffsetInSpan);

            if (r <= 0 || r > srcHeader.ExportTable.Count) return false;

            return (srcHeader.ExportTable[r - 1].ObjectNameIndex?.Name ?? string.Empty)
                .Contains("dblsided", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    // A name's index in the package as it will be once the additions are in:
    // what target already holds, else what is queued to be added.
    private static int FindTgtNameIdx(
        UnrealHeader tgtHeader,
        IReadOnlyDictionary<string, int> futureNameIndex,
        string name)
    {
        for (int i = 0; i < tgtHeader.NameTable.Count; i++)
            if (string.Equals(tgtHeader.NameTable[i]?.Name?.String, name, StringComparison.OrdinalIgnoreCase))
                return i;

        return futureNameIndex.TryGetValue(name, out int future) ? future : -1;
    }

    // The index of a word in the source's table, teaching it the word when it
    // does not know it. The costume never says "chbasematerial_v2_masked", but
    // the import synthesized for its alpha shaders must.
    private static int FindOrTeachSrcName(UnrealHeader srcHeader, string name)
    {
        int have = FindSrcNameIdx(srcHeader, name);

        if (have >= 0) return have;

        var entry = new UnrealNameTableEntry { TableIndex = srcHeader.NameTable.Count };

        entry.Name.SetString(name);
        srcHeader.NameTable.Add(entry);

        return entry.TableIndex;
    }

    private static int FindSrcNameIdx(UnrealHeader srcHeader, string name)
    {
        if (string.IsNullOrEmpty(name)) return -1;
        for (int i = 0; i < srcHeader.NameTable.Count; i++)
        {
            string s = srcHeader.NameTable[i]?.Name?.String ?? string.Empty;
            if (string.Equals(s, name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    // Ensures a name string is staged for addition to target's name table.
    // No-op if target already has the name OR if the name is already in the
    // additions list. Used by the synthetic-import flow to make sure the
    // package/class/object name strings referenced by synthetic imports
    // resolve when SerializeAddedImports calls ResolveNameIdx.
    private static void EnsureNameInToAdd(string? name, List<string> namesToAdd, UnrealHeader tgtHeader)
    {
        if (string.IsNullOrEmpty(name)) return;
        // Already in target?
        for (int i = 0; i < tgtHeader.NameTable.Count; i++)
        {
            string s = tgtHeader.NameTable[i]?.Name?.String ?? string.Empty;
            if (string.Equals(s, name, StringComparison.OrdinalIgnoreCase)) return;
        }
        // Already queued?
        for (int i = 0; i < namesToAdd.Count; i++)
            if (string.Equals(namesToAdd[i], name, StringComparison.OrdinalIgnoreCase)) return;
        namesToAdd.Add(name);
    }

    internal static string ImportFullPath(UnrealHeader header, UnrealImportTableEntry import)
    {
        string name = import.ObjectNameIndex?.Name ?? "(noName)";
        int outerRef = import.OuterReference;
        if (outerRef == 0) return name;
        var outer = header.GetObjectTableEntry(outerRef);
        if (outer is null) return name;
        return ImportOuterPath(header, outer) + "." + name;
    }
    private static string ImportOuterPath(UnrealHeader header, UnrealObjectTableEntryBase entry)
    {
        if (entry is UnrealImportTableEntry imp)
        {
            string nm = imp.ObjectNameIndex?.Name ?? "(noName)";
            int outerRef = imp.OuterReference;
            if (outerRef == 0) return nm;
            var outer = header.GetObjectTableEntry(outerRef);
            return outer is null ? nm : ImportOuterPath(header, outer) + "." + nm;
        }
        if (entry is UnrealExportTableEntry exp)
            return exp.GetPathName();
        return "(?)";
    }
}
