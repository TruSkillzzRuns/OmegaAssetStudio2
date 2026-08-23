using System;
using System.Collections.Generic;
using UpkManager.Models.UpkFile;
using UpkManager.Models.UpkFile.Tables;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor;

/// <summary>
/// Walks the raw tagged-property bytes of a UMaterialInstanceConstant export and records the
/// exact byte offsets of every editable parameter value (scalar / vector / texture reference).
///
/// This walker does not depend on the high-level UpkManager parser — it reads the same FName
/// indices directly out of the byte buffer, resolves them against the package's NameTable, and
/// captures the start offset of each ParameterValue tagged property payload. Those offsets are
/// then used to do in-place byte patches that are size-preserving (so no UPK header rebuild is
/// required, and UpkRepacker can splice the edited export back without touching anything else).
/// </summary>
public sealed class MaterialBytePatcher
{
    public sealed record ScalarOffset(int ValueOffset);
    public sealed record VectorOffset(int ValueOffset);
    public sealed record TextureOffset(int ValueOffset);

    public sealed class ParameterOffsets
    {
        public Dictionary<string, ScalarOffset> Scalars { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, VectorOffset> Vectors { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, TextureOffset> Textures { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int PropertyTableEndOffset { get; set; }
    }

    /// <summary>
    /// Walks <paramref name="exportBytes"/> (the raw UMaterialInstanceConstant export body)
    /// and records the byte offset of every Scalar/Vector/Texture ParameterValue payload.
    /// </summary>
    public ParameterOffsets Locate(byte[] exportBytes, UnrealHeader header)
    {
        ArgumentNullException.ThrowIfNull(exportBytes);
        ArgumentNullException.ThrowIfNull(header);

        ParameterOffsets offsets = new();

        // Export body layout:
        //   [int32 NetIndex] [tagged property table terminated by FName "None"] [post-property block]
        int pos = sizeof(int);

        while (pos + 8 <= exportBytes.Length)
        {
            if (!TryReadName(exportBytes, pos, header, out string propertyName))
                break;
            pos += 8;

            if (propertyName.Equals("None", StringComparison.OrdinalIgnoreCase))
                break;

            if (pos + 16 > exportBytes.Length)
                break;

            if (!TryReadName(exportBytes, pos, header, out string typeName))
                break;
            pos += 8;

            int propertySize = BitConverter.ToInt32(exportBytes, pos);
            pos += 4;
            // ArrayIndex (int32) — unused for our parameters
            pos += 4;

            // Type-specific tag header: StructProperty / ByteProperty have an extra 8-byte FName
            // (struct type / enum type) after the standard tag, before the value bytes.
            if (string.Equals(typeName, "StructProperty", StringComparison.Ordinal) ||
                string.Equals(typeName, "ByteProperty", StringComparison.Ordinal))
            {
                pos += 8;
            }

            int valueStart = pos;

            if (string.Equals(typeName, "ArrayProperty", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(propertyName, "ScalarParameterValues", StringComparison.OrdinalIgnoreCase))
                    WalkParameterArray(exportBytes, valueStart, propertySize, header, ParameterKind.Scalar, offsets);
                else if (string.Equals(propertyName, "VectorParameterValues", StringComparison.OrdinalIgnoreCase))
                    WalkParameterArray(exportBytes, valueStart, propertySize, header, ParameterKind.Vector, offsets);
                else if (string.Equals(propertyName, "TextureParameterValues", StringComparison.OrdinalIgnoreCase))
                    WalkParameterArray(exportBytes, valueStart, propertySize, header, ParameterKind.Texture, offsets);
            }

            // BoolProperty stores its value as a single byte after the tag header; propertySize is 0.
            // Every other property type reports its value byte count via propertySize.
            if (string.Equals(typeName, "BoolProperty", StringComparison.OrdinalIgnoreCase))
                pos += 1;
            else
                pos += propertySize;
        }

        offsets.PropertyTableEndOffset = pos;
        return offsets;
    }

    private enum ParameterKind { Scalar, Vector, Texture }

    private static void WalkParameterArray(byte[] bytes, int arrayStart, int arrayPayloadSize, UnrealHeader header, ParameterKind kind, ParameterOffsets offsets)
    {
        if (arrayStart + 4 > bytes.Length)
            return;

        int count = BitConverter.ToInt32(bytes, arrayStart);
        if (count <= 0)
            return;

        int arrayEnd = Math.Min(bytes.Length, arrayStart + arrayPayloadSize);
        int pos = arrayStart + 4;

        for (int i = 0; i < count && pos < arrayEnd; i++)
        {
            string? paramName = null;
            int? paramValueOffset = null;

            while (pos + 8 <= arrayEnd)
            {
                if (!TryReadName(bytes, pos, header, out string innerName))
                {
                    pos = arrayEnd;
                    break;
                }
                pos += 8;

                if (innerName.Equals("None", StringComparison.OrdinalIgnoreCase))
                    break;

                if (pos + 16 > arrayEnd)
                {
                    pos = arrayEnd;
                    break;
                }

                if (!TryReadName(bytes, pos, header, out string innerType))
                {
                    pos = arrayEnd;
                    break;
                }
                pos += 8;

                int innerSize = BitConverter.ToInt32(bytes, pos);
                pos += 4;
                // ArrayIndex
                pos += 4;

                if (string.Equals(innerType, "StructProperty", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(innerType, "ByteProperty", StringComparison.OrdinalIgnoreCase))
                {
                    pos += 8;
                }

                int innerValueStart = pos;

                if (string.Equals(innerName, "ParameterName", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryReadName(bytes, innerValueStart, header, out string resolvedParamName))
                        paramName = resolvedParamName;
                }
                else if (string.Equals(innerName, "ParameterValue", StringComparison.OrdinalIgnoreCase))
                {
                    paramValueOffset = innerValueStart;
                }

                if (string.Equals(innerType, "BoolProperty", StringComparison.OrdinalIgnoreCase))
                    pos += 1;
                else
                    pos += innerSize;
            }

            if (!string.IsNullOrWhiteSpace(paramName) && paramValueOffset.HasValue)
            {
                switch (kind)
                {
                    case ParameterKind.Scalar:
                        offsets.Scalars[paramName] = new ScalarOffset(paramValueOffset.Value);
                        break;
                    case ParameterKind.Vector:
                        offsets.Vectors[paramName] = new VectorOffset(paramValueOffset.Value);
                        break;
                    case ParameterKind.Texture:
                        offsets.Textures[paramName] = new TextureOffset(paramValueOffset.Value);
                        break;
                }
            }
        }
    }

    private static bool TryReadName(byte[] bytes, int pos, UnrealHeader header, out string name)
    {
        name = string.Empty;
        if (pos + 8 > bytes.Length)
            return false;

        int nameIndex = BitConverter.ToInt32(bytes, pos);
        int nameNumeric = BitConverter.ToInt32(bytes, pos + 4);

        if (nameIndex < 0 || nameIndex >= header.NameTable.Count)
            return false;

        string raw = header.NameTable[nameIndex].Name?.String ?? string.Empty;
        name = nameNumeric > 0 ? $"{raw}_{nameNumeric - 1}" : raw;
        return true;
    }
}
