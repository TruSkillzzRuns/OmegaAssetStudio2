using System.Text;
using System.Text.Json.Serialization;
using DDSLib.Constants;
using UpkManager.Models.UpkFile.Engine.Texture;
using UpkManager.Models.UpkFile.Tables;

namespace OmegaAssetStudio.TextureManager
{
    public enum ModResult
    {
        Success,
        NotMatch,
        TexutureNotFound,
        Reset
    }

    public class TextureManifest
    {
        public static TextureManifest Instance { get; private set; }

        public const string ManifestName = "TextureFileCacheManifest.bin";
        public string ManifestPath { get; private set; } = "";
        public string ManifestFilePath { get; private set; } = "";
        private TextureManifest() { }
        public SortedDictionary<TextureHead, TextureEntry> Entries { get; private set; } = [];

        public int LoadManifest(string filePath)
        {
            ManifestFilePath = filePath;
            ManifestPath = Path.GetDirectoryName(filePath) ?? "";
            Entries.Clear();

            using (var reader = new BinaryReader(File.Open(filePath, FileMode.Open)))
            {
                uint size = reader.ReadUInt32();

                for (uint i = 0; i < size; i++)
                {
                    try
                    {
                        if (reader.BaseStream.Position >= reader.BaseStream.Length)
                            break;

                        var entry = new TextureEntry(reader, i);
                        Entries.Add(entry.Head, entry);
                    }
                    catch (EndOfStreamException)
                    {
                        break;
                    }
                    catch (IOException)
                    {
                        break;
                    }
                }
            }

            string textureInfoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "TextureInfo.tsv");
            if (File.Exists(textureInfoPath))
                ApplyTextureInfoOverrides(textureInfoPath);

            return Entries.Count;
        }

        private void ApplyTextureInfoOverrides(string filePath)
        {
            using StreamReader reader = new(filePath);
            _ = reader.ReadLine();
            ILookup<Guid, TextureHead> guidLookup = Entries.Keys.ToLookup(static key => key.TextureGuid);

            while (!reader.EndOfStream)
            {
                string line = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                string[] parts = line.Split('\t');
                if (parts.Length < 5)
                    continue;

                if (!Guid.TryParse(parts[0].Trim(), out Guid guid))
                    continue;

                if (!int.TryParse(parts[2].Trim(), out int width) || !int.TryParse(parts[3].Trim(), out int height))
                    continue;

                if (!Enum.TryParse(parts[4].Trim(), ignoreCase: true, out FileFormat format))
                    continue;

                foreach (TextureHead key in guidLookup[guid])
                {
                    if (!Entries.TryGetValue(key, out TextureEntry entry))
                        continue;

                    entry.Data.OverrideMipMap.SizeX = width;
                    entry.Data.OverrideMipMap.SizeY = height;
                    entry.Data.OverrideMipMap.OverrideFormat = format;
                }
            }
        }

        public void SaveManifest(string filePath = null)
        {
            string outputPath = filePath ?? ManifestFilePath;
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new InvalidOperationException("Texture manifest path is not set.");

            // Atomic write: serialize to <path>.tmp, then File.Replace (or Move) over the target.
            // If the process is killed mid-write, the original manifest file is untouched and the
            // game still reads consistent offsets. The .bak created by EnsureBackupExists() still
            // exists as a secondary recovery point.
            string tempPath = outputPath + ".tmp";

            using (BinaryWriter writer = new(File.Open(tempPath, FileMode.Create, FileAccess.Write, FileShare.None)))
            {
                writer.Write((uint)Entries.Count);
                foreach (TextureEntry entry in Entries.Values)
                    entry.Write(writer);
                writer.Flush();
            }

            if (File.Exists(outputPath))
            {
                // File.Replace is atomic on the same volume and preserves ACLs; it also
                // automatically discards the destination's old contents.
                File.Replace(tempPath, outputPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, outputPath);
            }

            ManifestFilePath = outputPath;
            ManifestPath = Path.GetDirectoryName(outputPath) ?? "";
        }

        public TextureEntry GetTextureEntry(TextureHead head)
        {
            var matchedByName = Entries
                .Where(pair => string.Equals(pair.Key.TextureName, head.TextureName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var pair in matchedByName)
                if (pair.Key.TextureGuid == head.TextureGuid)
                    return pair.Value;

            foreach (var pair in Entries)
                if (pair.Key.TextureGuid == head.TextureGuid)
                    return pair.Value;

            foreach (var pair in matchedByName)
                return pair.Value;

            return null;
        }

        public TextureEntry GetTextureEntry(UTexture2D textureObject)
        {
            if (textureObject is null || Entries.Count == 0)
                return null;

            string textureName = textureObject.TextureFileCacheName?.Name ?? string.Empty;
            Guid textureGuid = textureObject.TextureFileCacheGuid.ToSystemGuid();

            if (string.IsNullOrWhiteSpace(textureName))
                return null;

            TextureEntry entry = GetTextureEntry(new TextureHead(textureName, textureGuid));
            if (entry is not null)
                return entry;

            return GetTextureEntry(new TextureHead(textureName, Guid.Empty));
        }

        public TextureEntry GetTextureEntryFromObject(FObject textureObject)
        {
            if (Entries.Count > 0)
            {
                var texture2D = textureObject.LoadObject<UTexture2D>();
                return GetTextureEntry(texture2D);
            }
            return null;
        }

        public static void Initialize()
        {
            Instance ??= new();
        }
    }

    public class TextureEntry
    {
        public TextureHead Head;
        public TextureMipMaps Data;

        public TextureEntry()
        {
        }

        public TextureEntry(BinaryReader reader, uint index)
        {
            Head = new(reader, index);
            Data = new(this, reader);
        }

        public void Write(BinaryWriter writer)
        {
            Head.Write(writer);
            Data.Write(writer);
        }
    }

    public struct TextureHead : IComparable<TextureHead> 
    {
        public string TextureName { get; set; }
        public Guid TextureGuid { get; set; }

        [JsonIgnore]
        public uint HashIndex; // use HashIndex without the hash function

        public TextureHead(BinaryReader reader, uint index)
        {
            HashIndex = index;
            TextureName = ReadString(reader);
            TextureGuid = new Guid(reader.ReadBytes(16));
        }

        public TextureHead(string textureName, Guid guid) : this()
        {
            TextureName = textureName;
            TextureGuid = guid;
        }

        public override int GetHashCode()
        {
            // Deterministic case-insensitive name hash to avoid runtime-dependent string hash randomization.
            // Kept independent from HashIndex ordering used by the manifest table.
            const uint fnvOffset = 2166136261;
            const uint fnvPrime = 16777619;
            uint hash = fnvOffset;

            if (!string.IsNullOrWhiteSpace(TextureName))
            {
                for (int i = 0; i < TextureName.Length; i++)
                {
                    char c = char.ToLowerInvariant(TextureName[i]);
                    hash ^= c;
                    hash *= fnvPrime;
                }
            }

            return unchecked((int)hash);
        }

        public int CompareTo(TextureHead other)
        {
            return HashIndex.CompareTo(other.HashIndex);
        }

        public override bool Equals(object obj)
        {
            if (obj == null || GetType() != obj.GetType())
                return false;

            TextureHead other = (TextureHead)obj;
            return HashIndex == other.HashIndex;
        }

        public static string ReadString(BinaryReader reader)
        {
            uint length = reader.ReadUInt32();
            byte[] stringBytes = reader.ReadBytes((int)length);
            int nullIndex = Array.IndexOf(stringBytes, (byte)0);
            if (nullIndex >= 0)
                stringBytes = stringBytes[..nullIndex];
            return Encoding.UTF8.GetString(stringBytes);
        }

        public void Write(BinaryWriter writer)
        {
            WriteString(writer, TextureName);
            writer.Write(TextureGuid.ToByteArray());
        }

        public static void WriteString(BinaryWriter writer, string value)
        {
            byte[] stringBytes = Encoding.UTF8.GetBytes(value + '\0');
            writer.Write((uint)stringBytes.Length);
            writer.Write(stringBytes);
        }
    }

    public class TextureMipMap
    {
        [JsonIgnore]
        public uint Index;
        public uint Offset { get; set; }
        public uint Size { get; set; }        
        
        [JsonIgnore]
        public TextureEntry Entry;

        public TextureMipMap() { }

        public TextureMipMap(TextureMipMap map)
        {
            Index = map.Index;
            Offset = map.Offset;
            Size = map.Size;
        }

        public TextureMipMap(TextureEntry entry, BinaryReader reader)
        {
            Entry = entry;
            Index = reader.ReadUInt32();
            Offset = reader.ReadUInt32();
            Size = reader.ReadUInt32();
        }

        public override string ToString()
        {
            return Index.ToString();
        }

        public void Write(BinaryWriter writer)
        {
            writer.Write(Index);
            writer.Write(Offset);
            writer.Write(Size);
        }
    }

    public class TextureMipMaps
    {
        public string TextureFileName { get; set; }
        public List<TextureMipMap> Maps { get; set; }

        [JsonIgnore]
        public FTexture2DMipMap OverrideMipMap;

        public TextureMipMaps() { }

        public TextureMipMaps(TextureMipMaps data)
        {
            TextureFileName = data.TextureFileName;

            Maps = [];
            foreach (var map in data.Maps)
                Maps.Add(new(map));
        }

        public TextureMipMaps(TextureEntry entry, BinaryReader reader)
        {
            OverrideMipMap = new();
            TextureFileName = TextureHead.ReadString(reader);

            uint num = reader.ReadUInt32();

            Maps = [];
            for (uint i = 0; i < num; i++)
                Maps.Add(new(entry, reader));
        }

        public void Write(BinaryWriter writer)
        {
            TextureHead.WriteString(writer, TextureFileName);

            uint num = (uint)Maps.Count;
            writer.Write(num);

            foreach (var map in Maps)
                map.Write(writer);
        }

        public void SetData(TextureMipMaps updated)
        {
            int num = Maps.Count;
            for (int i = 0; i < num; i++)
            {
                Maps[i].Offset = updated.Maps[i].Offset;
                Maps[i].Size = updated.Maps[i].Size;
            }
        }
    }
}

