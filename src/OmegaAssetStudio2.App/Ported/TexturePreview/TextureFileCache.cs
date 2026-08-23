using DDSLib;

using UpkManager.Helpers;
using UpkManager.Models.UpkFile.Engine.Texture;

namespace OmegaAssetStudio.TextureManager
{
    public enum ImportType
    {
        New = 0,
        Add = 1,
        Replace = 2,
    }

    public enum WriteResult
    {
        Success,
        MipMapError,
        SizeReplaceError,
    }

    public class TextureFileCache
    {
        public static TextureFileCache Instance { get; private set; }
        public UTexture2D Texture2D { get; } = new();

        public TextureEntry Entry { get; private set; }
        public bool Loaded { get; private set; }
        public string LastLoadError { get; private set; } = string.Empty;

        private TextureFileCache() { }

        public static void Initialize()
        {
            Instance ??= new();
        }

        public void Reset()
        {
            Entry = null;
            LastLoadError = string.Empty;
        }

        public void LoadTextureCache()
        {
            if (Entry is null || Entry.Data is null)
            {
                Loaded = false;
                LastLoadError = "Texture cache entry is not set.";
                return;
            }

            string tfcPath = ResolveTfcPath(TextureManifest.Instance.ManifestPath, Entry.Data.TextureFileName);
            if (Entry.Data.Maps.Count == 0) return;

            if (LoadFromFile(tfcPath, Entry) && Texture2D.Mips.Count > 0)
            {
                LastLoadError = string.Empty;
                return;
            }

            Loaded = false;
            LastLoadError = $"Can't Load TFC: {Entry.Head.TextureName} | File: {tfcPath}";
        }

        public bool LoadFromFile(string filePath, TextureEntry entry, bool onlyFirst = false)
        {
            if (Loaded && Entry == entry) return true;

            if (!File.Exists(filePath)) return false;

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(fs);

            if (Entry != entry)
            {
                Texture2D.ResetMipMaps(entry.Data.Maps.Count);
                Loaded = false;
                Entry = entry;
            }

            foreach (var mipMap in entry.Data.Maps)
            {
                if (mipMap.Offset + mipMap.Size > fs.Length)
                    return false;

                fs.Seek(mipMap.Offset, SeekOrigin.Begin);
                byte[] textureData = reader.ReadBytes((int)mipMap.Size);
                var upkReader = ByteArrayReader.CreateNew(textureData, 0);
                var overrideMipMap = entry.Data.OverrideMipMap;

                Task.Run(() =>
                    Texture2D.ReadMipMapCache(upkReader, mipMap.Index, overrideMipMap)
                ).Wait();
                if (onlyFirst) break;
            }

            Loaded = entry.Data.Maps.Count == Texture2D.Mips.Count;

            return true;
        }

        public WriteResult WriteTexture(string texturePath, string textureCacheName, ImportType importType, DdsFile ddsHeader)
        {
            string tfcPath = ResolveTfcPath(texturePath, textureCacheName);

            using FileStream fs = importType switch
            {
                ImportType.New => new FileStream(tfcPath, FileMode.Create, FileAccess.Write),
                ImportType.Add => new FileStream(tfcPath, FileMode.Append, FileAccess.Write),
                ImportType.Replace => new FileStream(tfcPath, FileMode.Open, FileAccess.ReadWrite),
                _ => throw new ArgumentException("Invalid import type", nameof(importType))
            };

            // CRITICAL: the manifest's Maps[i] entries point into TFC at the
            // mip's actual UPK index (mipMap.Index). Highest-detail mips (e.g.
            // mip[0] = top) are often NothingToDo placeholders in the UPK and
            // therefore SKIPPED in Maps, so Maps[0].Index may be 1 (or 2/3/...).
            //
            // ddsHeader.MipMaps is a DENSE chain starting at the encoded
            // texture's top size (texture.SizeX) — index 0 = full size,
            // index 1 = half, index 2 = quarter, etc.
            //
            // We must pick ddsHeader.MipMaps[mipMap.Index], NOT the sequential
            // loop counter — otherwise every TFC mip slot receives data sized
            // for 1 level-of-detail too coarse than what the UPK declares,
            // and the engine fires "Detected data corruption [incorrect
            // uncompressed size]" the first time it tries to stream a non-top
            // mip → CTD. (Bug only surfaces for character textures where mip 0
            // is NothingToDo; UI textures with mip 0 in TFC happened to work.)
            //
            // Cache lookup against Texture2D.Mips uses sequential `index`
            // because LoadFromFile populated that list in the same order as
            // Entry.Data.Maps — they share the same length and ordering.
            if (Texture2D.Mips.Count == 0)
                return WriteResult.MipMapError;

            Texture2D.ResetCompressedChunks();
            byte[][] payloads = new byte[Entry.Data.Maps.Count][];
            bool requiresRelocation = importType == ImportType.Add;

            int index = 0;
            foreach (var mipMap in Entry.Data.Maps)
            {
                int ddsIdx = (int)mipMap.Index;
                if (ddsIdx < 0 || ddsIdx >= ddsHeader.MipMaps.Count)
                    return WriteResult.MipMapError;
                if (index >= Texture2D.Mips.Count)
                    return WriteResult.MipMapError;

                Texture2D.Mips[index].Data = ddsHeader.MipMaps[ddsIdx].MipMap;

                var data = Texture2D.WriteMipMapChache(index);

                if (data.Length == 0) return WriteResult.MipMapError;

                payloads[index] = data;
                if (importType == ImportType.Replace && data.Length > mipMap.Size)
                    requiresRelocation = true;

                index++;
            }

            if (importType == ImportType.Replace && !requiresRelocation)
            {
                for (int i = 0; i < Entry.Data.Maps.Count; i++)
                {
                    var mipMap = Entry.Data.Maps[i];
                    fs.Seek(mipMap.Offset, SeekOrigin.Begin);
                    mipMap.Offset = (uint)fs.Position;
                    fs.Write(payloads[i]);
                    mipMap.Size = (uint)payloads[i].Length;
                }
            }
            else
            {
                fs.Seek(0, SeekOrigin.End);
                for (int i = 0; i < Entry.Data.Maps.Count; i++)
                {
                    var mipMap = Entry.Data.Maps[i];
                    mipMap.Offset = (uint)fs.Position;
                    fs.Write(payloads[i]);
                    mipMap.Size = (uint)payloads[i].Length;
                }
            }

            Entry.Data.TextureFileName = textureCacheName;

            return WriteResult.Success;
        }

        public void SetEntry(TextureEntry entry, UTexture2D textureObject)
        {
            if (Loaded && Entry == entry) return;

            Entry = entry;
            Entry.Data.OverrideMipMap.SizeX = textureObject.SizeX;
            Entry.Data.OverrideMipMap.SizeY = textureObject.SizeY;
            Entry.Data.OverrideMipMap.OverrideFormat = UTexture2D.ParseFileFormat(textureObject.Format);
            Loaded = false;
            LastLoadError = string.Empty;
            Texture2D.ResetMipMaps(Entry.Data.Maps.Count);
        }


        private static string ResolveTfcPath(string rootPath, string? textureCacheName)
        {
            string normalized = (textureCacheName ?? string.Empty).Trim();
            if (normalized.EndsWith(".tfc", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(rootPath, normalized);
            return Path.Combine(rootPath, normalized + ".tfc");
        }
    }
}

