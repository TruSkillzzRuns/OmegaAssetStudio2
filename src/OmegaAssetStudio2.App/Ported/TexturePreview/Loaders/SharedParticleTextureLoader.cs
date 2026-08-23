using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using DDSLib;
using UpkManager.Models.UpkFile;
using UpkManager.Models.UpkFile.Engine.Texture;
using UpkManager.Models.UpkFile.Objects;
using UpkManager.Models.UpkFile.Tables;
using UpkManager.Repository;

namespace OmegaAssetStudio.TexturePreview;

// Phase 2: resolves particle textures that live inside container packages
// (Engine.upk, Startup.upk, and per-game shared content) â€” the giant bundles that hold all
// shared VFX textures across the cooked-data dir.
//
// IMPORTANT design choice: this loader maintains its OWN UpkFileRepository
// instance and NEVER touches TextureManifest.Instance. The previous attempt to
// extend UpkTextureLoader.LoadFromObjectAsync hit a race against the manifest
// singleton (LoadManifest clears Entries while GetTextureEntry's lazy LINQ
// enumeration is still running â†’ "Collection was modified" â†’ mesh textures
// failed). This implementation reads inline-stored mip data directly from the
// UPK byte stream, skipping the manifest path entirely. TFC-streamed textures
// (the only ones that actually need the manifest) aren't resolved by this
// loader â€” particle VFX overwhelmingly use inline mips so coverage is high.
public sealed class SharedParticleTextureLoader
{
    private readonly UpkFileRepository _repo = new();
    private readonly ConcurrentDictionary<string, PackageIndex> _packageIndexes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TexturePreviewTexture?> _textureCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string>? _log;
    private string? _cookedDir;

    // Container packages that bundle shared VFX textures. Each is opened once
    // and its Texture2D export index lives in _packageIndexes forever.
    // - Universal UE3 containers ("Engine.upk", "Startup.upk") are part of
    //   the engine convention, not any game's trademark.
    // - Per-game container UPKs are AUTO-DISCOVERED at prewarm time by
    //   file size (see DiscoverGameContainerNames below).
    private static readonly string[] _universalContainers = { "Startup.upk", "Engine.upk" };

    // Discover the per-game container UPK(s) by scanning the cooked-data
    // dir for the largest top-level .upk files that don't follow a known
    // per-content-type prefix pattern (UC__ / ICO__ / etc.). These are the
    // game-specific shared-asset bundles. Returns up to 3 leaf names.
    private static System.Collections.Generic.List<string> DiscoverGameContainerNames(string cookedDir)
    {
        var result = new System.Collections.Generic.List<string>();
        try
        {
            foreach (var fi in new DirectoryInfo(cookedDir)
                .EnumerateFiles("*.upk", SearchOption.TopDirectoryOnly)
                .Where(f => !f.Name.Contains("__"))
                .OrderByDescending(f => f.Length)
                .Take(3))
            {
                result.Add(fi.Name);
            }
        }
        catch { }
        return result;
    }

    public SharedParticleTextureLoader(Action<string>? log = null)
    {
        _log = log;
    }

    // One-time prewarm of the container package indexes. Runs in the
    // background so the first particle texture lookup doesn't pay the cost.
    // Safe to call multiple times â€” already-indexed packages are skipped.
    public async Task PrewarmAsync(string cookedDir)
    {
        if (string.IsNullOrEmpty(cookedDir) || !Directory.Exists(cookedDir)) return;
        _cookedDir = cookedDir;
        var containers = new System.Collections.Generic.List<string>(_universalContainers);
        containers.AddRange(DiscoverGameContainerNames(cookedDir));
        foreach (string container in containers)
        {
            string path = Path.Combine(cookedDir, container);
            if (!File.Exists(path)) continue;
            if (_packageIndexes.ContainsKey(path)) continue;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var header = await _repo.LoadUpkFile(path).ConfigureAwait(false);
                await header.ReadHeaderAsync(null).ConfigureAwait(false);
                var byPath = new Dictionary<string, UnrealExportTableEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (UnrealExportTableEntry e in header.ExportTable)
                {
                    string cls = e.ClassReferenceNameIndex?.Name ?? "";
                    if (!string.Equals(cls, "Texture2D", StringComparison.OrdinalIgnoreCase)) continue;
                    string p = e.GetPathName();
                    if (!string.IsNullOrEmpty(p)) byPath[p] = e;
                }
                sw.Stop();
                _packageIndexes[path] = new PackageIndex(header, byPath);
                _log?.Invoke($"[shared-tex] indexed {container}: {byPath.Count} Texture2D exports in {sw.Elapsed.TotalSeconds:F1}s");
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[shared-tex] failed to index {container}: {ex.Message}");
            }
        }
    }

    // Tries to find and decode a texture by its UPK path
    // (e.g. "vfx_shared_textures.textures.tex_elec_field"). Returns null if not
    // found in any container or if all available mips are TFC-streamed.
    public TexturePreviewTexture? TryResolveByPath(string importPath)
    {
        if (string.IsNullOrWhiteSpace(importPath)) return null;
        if (_textureCache.TryGetValue(importPath, out var cached)) return cached;

        try
        {
            UnrealExportTableEntry? hit = null;
            foreach (var (_, index) in _packageIndexes)
            {
                if (index.ByPath.TryGetValue(importPath, out var found)) { hit = found; break; }
                // Fallback short-name lookup (last dotted segment) â€” covers
                // layout drift between TargetClient's import path and the actual export path.
                int dot = importPath.LastIndexOf('.');
                if (dot >= 0 && dot < importPath.Length - 1)
                {
                    string tail = importPath.Substring(dot + 1);
                    foreach (var kv in index.ByPath)
                    {
                        if (kv.Key.EndsWith("." + tail, StringComparison.OrdinalIgnoreCase))
                        {
                            hit = kv.Value;
                            break;
                        }
                    }
                    if (hit is not null) break;
                }
            }
            if (hit is null)
            {
                _textureCache[importPath] = null;
                return null;
            }

            var preview = DecodeInlineTexture(hit);
            _textureCache[importPath] = preview;
            if (preview is not null)
                _log?.Invoke($"[shared-tex] resolved '{importPath}' ({preview.Width}x{preview.Height})");
            return preview;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[shared-tex] decode failed for '{importPath}': {ex.Message}");
            _textureCache[importPath] = null;
            return null;
        }
    }

    private TexturePreviewTexture? DecodeInlineTexture(UnrealExportTableEntry export)
    {
        // Force parse. We do this synchronously because the export is already
        // loaded into memory via the prewarm pass â€” no IO happens here.
        if (export.UnrealObject is null)
            export.ParseUnrealObject(false, false).GetAwaiter().GetResult();

        if (export.UnrealObject is not IUnrealObject u || u.UObject is not UTexture2D texture) return null;
        if (texture.Mips is null || texture.Mips.Count == 0) return null;

        // Pick the LARGEST inline (non-empty Data) mip â€” gives best texture
        // quality for our preview without falling back to TFC streaming.
        FTexture2DMipMap? bestMip = null;
        int bestIndex = -1;
        for (int i = 0; i < texture.Mips.Count; i++)
        {
            FTexture2DMipMap m = texture.Mips[i];
            if (m.Data is null || m.Data.Length == 0) continue;
            if (bestMip is null || (m.SizeX * m.SizeY > bestMip.SizeX * bestMip.SizeY))
            {
                bestMip = m;
                bestIndex = i;
            }
        }
        if (bestMip is null || bestIndex < 0) return null;

        Stream? stream;
        try { stream = texture.GetObjectStream(bestIndex); }
        catch { return null; }
        if (stream is null) return null;

        try
        {
            using var ddsStream = new MemoryStream();
            stream.CopyTo(ddsStream);
            ddsStream.Position = 0;
            var dds = new DdsFile();
            dds.Load(ddsStream);

            using Bitmap bmp = BitmapSourceToBitmap(dds.BitmapSource);
            byte[] rgba = ExtractRgba(bmp);

            return new TexturePreviewTexture
            {
                Name = export.ObjectNameIndex?.Name ?? "shared",
                SourcePath = "shared",
                SourceDescription = $"shared: {export.GetPathName()}",
                ExportPath = export.GetPathName(),
                RgbaPixels = rgba,
                Width = bmp.Width,
                Height = bmp.Height,
                Slot = TexturePreviewMaterialSlot.Diffuse,
            };
        }
        finally { stream.Dispose(); }
    }

    private static Bitmap BitmapSourceToBitmap(BitmapSource bs)
    {
        // Convert via PNG encode/decode round-trip â€” same pattern UpkTextureLoader
        // uses. Avoids importing WPF interop quirks for direct pixel marshaling.
        using var ms = new MemoryStream();
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bs));
        enc.Save(ms);
        ms.Position = 0;
        using Bitmap tmp = new(ms);
        return new Bitmap(tmp);
    }

    private static byte[] ExtractRgba(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] bgra = new byte[bmp.Width * bmp.Height * 4];
            Marshal.Copy(data.Scan0, bgra, 0, bgra.Length);
            // Swizzle B<->R so we get RGBA, which is what the particle shader expects.
            for (int i = 0; i < bgra.Length; i += 4)
                (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
            return bgra;
        }
        finally { bmp.UnlockBits(data); }
    }

    private sealed record PackageIndex(UnrealHeader Header, Dictionary<string, UnrealExportTableEntry> ByPath);
}

