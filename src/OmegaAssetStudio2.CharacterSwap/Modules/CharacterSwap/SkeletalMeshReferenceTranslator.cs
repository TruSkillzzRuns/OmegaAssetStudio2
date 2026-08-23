using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UpkManager.Models.UpkFile;
using UpkManager.Models.UpkFile.Tables;
using UpkManager.Repository;
using OmegaAssetStudio.WinUI.UpkInterop.CrossVersion;

namespace OmegaAssetStudio.WinUI.Modules.CharacterSwap;

// Translates every FName / FObject reference inside a SkeletalMesh export
// body from source's index frame to target's. Works by:
//   1. Re-loading source's UPK with a RefRecorder attached to the header.
//   2. Re-parsing the SkeletalMesh export via the existing UpkManager
//      parser (which we already proved walks v868 and v894 with 100%
//      byte coverage). Every FName index read and every FObject ref read
//      gets its (offset, kind, rawValue) appended to the recorder.
//   3. For each recorded entry, translating the raw value via the
//      IndexTranslator (which knows source→target name index mapping +
//      source→target FObject mapping including aliases and future-add
//      targets) and rewriting the 4 bytes at that offset in a clone of
//      source's body.
//
// Output: a translated body byte buffer whose layout is identical to
// source but whose embedded references point at target's tables. Safe to
// splice into target's SkeletalMesh export slot (whether new or matched).
//
// Critical: the recorder offsets are relative to the body's start
// (UnrealObjectReader is a Splice of the package decompressed stream
// scoped to [SerialDataOffset, SerialDataOffset+SerialDataSize)). Those
// offsets line up directly with positions in source body bytes returned
// by UnrealObjectReader.GetBytes() (slice base = 0).
public sealed class SkeletalMeshReferenceTranslator
{
    public sealed class Result
    {
        public bool Success { get; init; }
        public byte[] Body { get; init; } = Array.Empty<byte>();
        public int NameRefsRewritten { get; init; }
        public int ObjectRefsRewritten { get; init; }
        public int NameRefsFailedTranslation { get; init; }
        public int ObjectRefsFailedTranslation { get; init; }
        public List<string> Issues { get; init; } = new();
    }

    // Re-loads source's UPK with the recorder enabled and translates
    // every recorded ref in srcExport's body via the provided translator.
    // sourceUpkPath: the path of the UPK that contained srcExport
    //                originally (needed because the export's underlying
    //                reader doesn't replay reads on demand — we re-parse)
    // skeletalmeshName: ObjectName of the SkeletalMesh export inside that
    //                UPK to translate.
    //
    // Two-pass translation:
    //   Pass 1: PropertyTagRewriter walks the property tag stream
    //     (NetIndex + all tags up to "None") and translates every
    //     embedded FName/FObject ref. This catches refs inside nested
    //     StructProperty values and inside ArrayProperty<TaggedStruct>
    //     items (LODInfo, AggGeom.SphylElems, etc.) — places where
    //     UpkManager's parser reads bytes opaquely and the recorder
    //     therefore can't observe FName positions.
    //   Pass 2: recorder-based translation for every (offset, kind, raw)
    //     captured during a UpkManager parse of the body. This covers
    //     FName / FObject reads in the binary tail past the property
    //     stream's "None" terminator (bone names, socket refs, materials
    //     array, clothing assets, etc.).
    // Both passes use the same translator, so overlapping translations
    // (e.g. names in the property stream that both passes hit) are
    // idempotent.
    public async Task<Result> TranslateAsync(
        string sourceUpkPath,
        string skeletalmeshName,
        IndexTranslator translator,
        Action<string>? log = null,
        IReadOnlyDictionary<int, int>? overrideMaterialsSrcToTgt = null)
    {
        if (string.IsNullOrWhiteSpace(sourceUpkPath)) throw new ArgumentException("source upk", nameof(sourceUpkPath));
        if (string.IsNullOrWhiteSpace(skeletalmeshName)) throw new ArgumentException("mesh name", nameof(skeletalmeshName));

        UpkFileRepository repo = new();
        var header = await repo.LoadUpkFile(sourceUpkPath).ConfigureAwait(false);
        await header.ReadHeaderAsync(null).ConfigureAwait(false);
        await header.ReadDependsTableAsync(null).ConfigureAwait(false);

        UnrealExportTableEntry? srcExport = null;
        foreach (var e in header.ExportTable)
        {
            if (string.Equals(e.ClassReferenceNameIndex?.Name, "skeletalmesh", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.ObjectNameIndex?.Name, skeletalmeshName, StringComparison.OrdinalIgnoreCase))
            { srcExport = e; break; }
        }
        if (srcExport is null)
            return new Result { Success = false, Issues = new List<string> { $"SkeletalMesh '{skeletalmeshName}' not found in source UPK." } };

        await header.ReadExportObjectAsync(srcExport, null).ConfigureAwait(false);
        try
        {
            // Parse source body via UpkManager so we can read bHasVertexColors
            // (and have a sanity-checked structure available for diagnostics).
            // We DO NOT rely on the recorder for translation any more — the
            // SkeletalMeshBodyWalker re-walks source bytes itself and emits
            // a translated output stream. This catches every FName/FObject
            // position (including those inside ArrayProperty value blobs that
            // UpkManager reads opaquely), and keeps binary mesh data byte-
            // exact since the walker never recurses into vertex/index buffers.
            await srcExport.ParseUnrealObject(skipProperties: false, skipParse: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new Result
            {
                Success = false,
                Issues = new List<string> { $"Parse threw: {ex.GetType().Name}: {ex.Message}" }
            };
        }

        byte[] srcBytes = srcExport.UnrealObjectReader?.GetBytes() ?? Array.Empty<byte>();

        // Pull bHasVertexColors off the parsed mesh — LODModel layout
        // depends on it (color vertex buffer present/absent).
        bool hasVertexColors = false;
        if (srcExport.UnrealObject is UpkManager.Models.UpkFile.Objects.IUnrealObject iuo
            && iuo.UObject is UpkManager.Models.UpkFile.Engine.Mesh.USkeletalMesh sm)
        {
            hasVertexColors = sm.bHasVertexColors;
        }

        var walker = new SkeletalMeshBodyWalker(srcBytes, header, translator, log);
        walker.SetMeshContext(hasVertexColors);
        walker.OverrideMaterialsSrcToTgt = overrideMaterialsSrcToTgt;
        try
        {
            walker.WalkSkeletalMeshBody();
        }
        catch (Exception ex)
        {
            return new Result
            {
                Success = false,
                Issues = new List<string>
                {
                    $"Walker threw at srcPos={walker.BytesConsumed}/{srcBytes.Length}: {ex.GetType().Name}: {ex.Message}"
                }
            };
        }

        byte[] outBytes = walker.GetBytes();
        log?.Invoke($"SkelMesh translator: walker consumed {walker.BytesConsumed}/{srcBytes.Length} bytes, emitted {outBytes.Length} bytes");
        log?.Invoke($"SkelMesh translator: names={walker.NameRefsRewritten}/{walker.NameRefsRewritten + walker.NameRefsFailedTranslation}, objects={walker.ObjectRefsRewritten}/{walker.ObjectRefsRewritten + walker.ObjectRefsFailedTranslation}");
        if (walker.BytesConsumed != srcBytes.Length)
        {
            return new Result
            {
                Success = false,
                Issues = new List<string>
                {
                    $"Walker consumed {walker.BytesConsumed} bytes but source body is {srcBytes.Length} — structural mismatch (v894/v868 layout drift?)",
                }.Concat(walker.Issues.Take(10)).ToList()
            };
        }

        return new Result
        {
            Success = true,
            Body = outBytes,
            NameRefsRewritten = walker.NameRefsRewritten,
            ObjectRefsRewritten = walker.ObjectRefsRewritten,
            NameRefsFailedTranslation = walker.NameRefsFailedTranslation,
            ObjectRefsFailedTranslation = walker.ObjectRefsFailedTranslation,
            Issues = walker.Issues,
        };
    }

    private static string DescribeRef(UnrealHeader header, int rawRef)
    {
        try
        {
            var entry = header.GetObjectTableEntry(rawRef);
            if (entry == null) return "(null entry)";
            string kind = rawRef > 0 ? "EXPORT" : "IMPORT";
            string cls = "?";
            if (entry is UnrealExportTableEntry exp)
                cls = exp.ClassReferenceNameIndex?.Name ?? "?";
            else if (entry is UnrealImportTableEntry imp)
                cls = imp.ClassNameIndex?.Name ?? "?";
            string path = entry.GetPathName();
            return $"{kind} {cls} '{path}'";
        }
        catch (Exception ex)
        {
            return $"(describe failed: {ex.GetType().Name})";
        }
    }

    private static void WriteInt32(byte[] buf, int off, int v)
    {
        buf[off    ] = (byte)( v        & 0xFF);
        buf[off + 1] = (byte)((v >>  8) & 0xFF);
        buf[off + 2] = (byte)((v >> 16) & 0xFF);
        buf[off + 3] = (byte)((v >> 24) & 0xFF);
    }
}
