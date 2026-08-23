using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UpkManager.Helpers;
using UpkManager.Models.UpkFile.Compression;
using OmegaAssetStudio.TextureManager;

namespace OmegaAssetStudio.WinUI.Modules.CharacterSwap;

// Loads raw mip bytes for a Texture2D export from a SOURCE game folder's
// TFC files. Used by the Texture2D body walker when transplanting textures
// from source -> target: we strip the source's TFC dependency by writing
// the actual mip bytes inline in the target UPK. After that the target
// engine reads mips straight out of the UPK with no TFC manifest/file
// dependency at all (matches how HUD/UI textures already work).
//
// Independent from the singleton TextureManifest.Instance — that one's
// pinned to the user's live game folder. This loader takes an explicit
// source UPK path and walks UP to find the source game's
// TextureFileCacheManifest.bin (usually in the parent cooked-data folder
// folder alongside the UPK).
public sealed class Phase2SourceTextureLoader
{
    public sealed class MipBytes
    {
        public int SizeX { get; init; }
        public int SizeY { get; init; }
        public byte[] Data { get; init; } = Array.Empty<byte>(); // raw uncompressed pixel bytes
    }

    private readonly TextureManifest _manifest = new TextureManifestProxy().GetInstance();

    // For source-side loading we need an instance of TextureManifest that's
    // independent of the global singleton. TextureManifest's constructor is
    // private, so we use a tiny reflection-free proxy via the static
    // Initialize + ResetEntries pattern. Simpler: just instantiate via the
    // private ctor via Activator (safe — TextureManifest.Initialize() does
    // the same thing for its singleton).
    private sealed class TextureManifestProxy
    {
        public TextureManifest GetInstance()
        {
            // TextureManifest has a private parameterless constructor; reuse
            // via Activator to keep this loader independent of the singleton
            // (which is tied to the live game folder).
            var ctor = typeof(TextureManifest).GetConstructor(
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                binder: null, types: Type.EmptyTypes, modifiers: null);
            if (ctor == null) throw new InvalidOperationException("TextureManifest has no parameterless constructor.");
            return (TextureManifest)ctor.Invoke(null);
        }
    }

    public string? ManifestPath { get; private set; }
    public string? ManifestFile { get; private set; }
    public int ManifestEntries { get; private set; }

    public bool TryLoadManifestFromUpkFolder(string sourceUpkPath, Action<string>? log = null)
    {
        string? folder = Path.GetDirectoryName(sourceUpkPath);
        for (int hops = 0; hops < 3 && folder != null; hops++)
        {
            string candidate = Path.Combine(folder, TextureManifest.ManifestName);
            if (File.Exists(candidate))
            {
                _manifest.LoadManifest(candidate);
                ManifestPath = folder;
                ManifestFile = candidate;
                ManifestEntries = _manifest.Entries.Count;
                log?.Invoke($"  source TFC manifest loaded: {candidate} ({ManifestEntries} entries)");
                return true;
            }
            folder = Path.GetDirectoryName(folder);
        }
        log?.Invoke($"  source TFC manifest NOT found near {sourceUpkPath}");
        return false;
    }

    // Returns the list of mip bytes for a Texture2D, in the same order as
    // the texture's Mips array. Each entry has the decompressed pixel data
    // plus the mip dimensions. Returns null if the texture isn't in the
    // source manifest (i.e. its mips are stored inline in the source UPK,
    // not in a TFC — caller should handle that path separately).
    public List<MipBytes>? TryLoadMipsForTexture(
        UpkManager.Models.UpkFile.Engine.Texture.UTexture2D texture,
        Action<string>? log = null)
    {
        if (string.IsNullOrEmpty(ManifestPath)) return null;
        // STRICT GUID lookup: TextureManifest.GetTextureEntry has a fallback
        // path that returns the first entry sharing the TFC bucket name
        // (e.g. "chartextures") when the GUID doesn't match. That's fine for
        // editor previews but CATASTROPHIC for cross-version mip
        // transplant: a cape texture whose GUID happens not to match the
        // manifest silently gets the body texture's mips, and the costume
        // renders with the body's pixels stretched over the cape mesh. We
        // do our own GUID-only match here so a miss is loud (logged + null
        // return) instead of silent wrong-pixel-data.
        var requestedGuid = texture.TextureFileCacheGuid.ToSystemGuid();
        TextureEntry? entry = null;
        int matchCount = 0;
        var matchedNames = new List<string>();
        foreach (var pair in _manifest.Entries)
        {
            if (pair.Key.TextureGuid == requestedGuid)
            {
                matchCount++;
                matchedNames.Add(pair.Key.TextureName ?? "(no name)");
                if (entry == null) entry = pair.Value;
            }
        }
        if (entry == null)
        {
            log?.Invoke($"  source manifest has NO entry for guid={requestedGuid} (texture '{texture.TextureFileCacheName?.Name}') — strict GUID match (no fallback)");
            return null;
        }
        if (matchCount > 1)
            log?.Invoke($"  WARN: manifest has {matchCount} entries with guid={requestedGuid}: [{string.Join(", ", matchedNames)}] — using first");
        else
            log?.Invoke($"  manifest entry for guid={requestedGuid}: bucket='{matchedNames[0]}', maps={entry.Data.Maps.Count}, first-offset=0x{(entry.Data.Maps.Count>0 ? entry.Data.Maps[0].Offset : 0):X}");
        string tfcPath = Path.Combine(ManifestPath, entry.Data.TextureFileName + ".tfc");
        if (!File.Exists(tfcPath))
        {
            log?.Invoke($"  source TFC file not found: {tfcPath}");
            return null;
        }
        var result = new List<MipBytes>();
        using var fs = new FileStream(tfcPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(fs);
        var overrideMip = entry.Data.OverrideMipMap;
        foreach (var manifestMip in entry.Data.Maps)
        {
            if (manifestMip.Offset + manifestMip.Size > fs.Length)
            {
                log?.Invoke($"  source TFC mip out of bounds: off={manifestMip.Offset} size={manifestMip.Size} fileLen={fs.Length}");
                continue;
            }
            fs.Seek(manifestMip.Offset, SeekOrigin.Begin);
            byte[] chunkBytes = reader.ReadBytes((int)manifestMip.Size);
            var bar = ByteArrayReader.CreateNew(chunkBytes, 0);
            var header = new UnrealCompressedChunkHeader();
            try
            {
                header.ReadCompressedChunkHeader(bar, 1, 0, 0);
            }
            catch (Exception ex)
            {
                log?.Invoke($"  mip[{manifestMip.Index}] header parse failed: {ex.GetType().Name}: {ex.Message}");
                continue;
            }
            byte[]? pixels = null;
            try
            {
                var decompressed = Task.Run(() => header.DecompressChunk()).Result;
                pixels = decompressed?.GetBytes();
            }
            catch (Exception ex)
            {
                log?.Invoke($"  mip[{manifestMip.Index}] decompress failed: {ex.GetType().Name}: {ex.Message}");
                continue;
            }
            if (pixels == null || pixels.Length == 0)
            {
                log?.Invoke($"  mip[{manifestMip.Index}] yielded 0 bytes after decompress");
                continue;
            }
            int width, height;
            if (overrideMip != null && overrideMip.SizeX > 0)
            {
                int shift = (int)manifestMip.Index;
                if (shift < 0) { width = overrideMip.SizeX << -shift; height = overrideMip.SizeY << -shift; }
                else { width = overrideMip.SizeX >> shift; height = overrideMip.SizeY >> shift; }
            }
            else
            {
                // Best effort: derive from raw byte count for the first mip,
                // halve dimensions per subsequent index.
                width = texture.SizeX >> (int)manifestMip.Index;
                height = texture.SizeY >> (int)manifestMip.Index;
            }
            if (width < 1) width = 1;
            if (height < 1) height = 1;
            result.Add(new MipBytes { SizeX = width, SizeY = height, Data = pixels });
        }
        log?.Invoke($"  source TFC '{Path.GetFileName(tfcPath)}': loaded {result.Count} mip(s) for '{texture.TextureFileCacheName?.Name}'");
        return result;
    }
}
