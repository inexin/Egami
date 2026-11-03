using System.Buffers.Binary;
using System.Numerics;
using System.Text;

using Egami.Formats.Extended.Textures;

namespace Egami.Formats.Extended.Ktx;

/// <summary>Which KTX container a reader reads.</summary>
internal enum KtxContainer
{
    Ktx1,
    Ktx2,
}

/// <summary>Where one KTX 2 mip level lies in the file, from its level index entry.</summary>
internal readonly record struct KtxLevel(long Offset, long Length, long UncompressedLength);

/// <summary>
/// A KTX 1 or KTX 2 header read into one description: the size, layers, faces and mips, the format, where each level lies,
/// the orientation and swizzle, and the key/value pairs. Mip levels hold, in order, every layer, every face of it and every
/// depth slice of that, as both specifications lay them out.
/// </summary>
internal sealed class KtxTexture
{
    /// <summary>Gets the container.</summary>
    public required KtxContainer Container { get; init; }

    /// <summary>Gets the format.</summary>
    public required TextureFormat Format { get; init; }

    /// <summary>Gets the width of mip 0.</summary>
    public int Width { get; init; }

    /// <summary>Gets the height of mip 0; 1 for a 1D texture.</summary>
    public int Height { get; init; }

    /// <summary>Gets the depth of mip 0 of a volume texture, else 1.</summary>
    public int Depth { get; init; }

    /// <summary>Gets the number of array layers, at least 1.</summary>
    public int Layers { get; init; }

    /// <summary>Gets the number of faces of each layer: 6 for a cube map, else 1.</summary>
    public int Faces { get; init; }

    /// <summary>Gets the number of mip levels, at least 1.</summary>
    public int MipLevels { get; set; }

    /// <summary>Gets whether the file is an array texture (KTX 1 pads the faces of a cube map that is not one).</summary>
    public bool IsArray { get; init; }

    /// <summary>Gets the dimension.</summary>
    public KtxDimension Dimension { get; init; }

    /// <summary>Gets the Vulkan format number, or the one a KTX 1 OpenGL format matches, or 0.</summary>
    public int VkFormat { get; init; }

    /// <summary>Gets a KTX 1 file's OpenGL internal format, else 0.</summary>
    public int GlInternalFormat { get; init; }

    /// <summary>Gets the supercompression.</summary>
    public KtxSupercompression Supercompression { get; init; }

    /// <summary>Gets whether a KTX 1 file is big-endian.</summary>
    public bool BigEndian { get; init; }

    /// <summary>Gets the size of the units a big-endian KTX 1 file's pixel data must be byte-swapped in: 2 or 4, else 1.</summary>
    public int SwapUnit { get; init; } = 1;

    /// <summary>Gets the boundary each row of pixels is padded to: 4 in KTX 1's uncompressed data, else 1.</summary>
    public int RowAlign { get; init; } = 1;

    /// <summary>Gets where a KTX 1 file's first mip level (its imageSize word) starts.</summary>
    public long DataOffset { get; init; }

    /// <summary>Gets a KTX 2 file's level index, mip 0 first.</summary>
    public KtxLevel[] Levels { get; set; } = [];

    /// <summary>Gets the stored orientation in KTX 2's form, such as <c>rd</c>.</summary>
    public string Orientation { get; set; } = "rd";

    /// <summary>Gets the swizzle, such as <c>rgba</c>.</summary>
    public string Swizzle { get; set; } = "rgba";

    /// <summary>Gets the key/value pairs with text values, other than the orientation and swizzle.</summary>
    public Dictionary<string, string> KeyValues { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the alpha mode.</summary>
    public KtxAlphaMode AlphaMode { get; set; }

    /// <summary>Gets the KTX 2 descriptor's colour primaries (Khronos Data Format numbering), 0 when unspecified.</summary>
    public int Primaries { get; set; }

    /// <summary>Gets the KTX 2 descriptor's transfer function (Khronos Data Format numbering), 0 when unspecified.</summary>
    public int Transfer { get; set; }

    /// <summary>Gets what a lenient read changed to make sense of the header, for the warnings.</summary>
    public List<(string Id, string Message)> Repairs { get; } = [];

    /// <summary>Gets whether the values are sRGB-encoded.</summary>
    public bool IsSrgb => Format.Srgb || Transfer == 2;

    /// <summary>Gets whether the texture is a cube map.</summary>
    public bool IsCube => Faces == 6;

    /// <summary>Gets the number of surfaces at mip 0: layers × faces × depth slices.</summary>
    public int Surfaces => Layers * Faces * Depth;

    /// <summary>Gets whether x runs right to left in the file.</summary>
    public bool FlipX => Orientation.Length > 0 && Orientation[0] == 'l';

    /// <summary>Gets whether y runs bottom-up in the file.</summary>
    public bool FlipY => Orientation.Length > 1 && Orientation[1] == 'u';

    /// <summary>Gets whether the depth slices run out of the screen in the file.</summary>
    public bool FlipZ => Orientation.Length > 2 && Orientation[2] == 'o';

    /// <summary>Returns the width of mip <paramref name="mip"/>.</summary>
    public int MipWidth(int mip) => Math.Max(1, Width >> mip);

    /// <summary>Returns the height of mip <paramref name="mip"/>.</summary>
    public int MipHeight(int mip) => Math.Max(1, Height >> mip);

    /// <summary>Returns the depth of mip <paramref name="mip"/> of a volume texture, else 1.</summary>
    public int MipDepth(int mip) => Math.Max(1, Depth >> mip);

    /// <summary>Returns the stored bytes of one row of pixels, or of one row of blocks, at mip <paramref name="mip"/>.</summary>
    public long RowPitch(int mip) => Align(Format.RowBytes(MipWidth(mip)), RowAlign);

    /// <summary>Returns the stored rows of mip <paramref name="mip"/>: rows of blocks for the block formats.</summary>
    public int StoredRows(int mip) => Format.IsBlock ? (int)(((long)MipHeight(mip) + Format.BlockHeight - 1) / Format.BlockHeight) : MipHeight(mip);

    /// <summary>Returns the stored bytes of one 2D image (a face or depth slice) at mip <paramref name="mip"/>.</summary>
    public long ImageBytes(int mip) => checked(RowPitch(mip) * StoredRows(mip));

    /// <summary>Returns the bytes of all images of mip <paramref name="mip"/>, as a KTX 2 level holds them uncompressed.</summary>
    public long LevelImageBytes(int mip) => checked(ImageBytes(mip) * Layers * Faces * MipDepth(mip));

    /// <summary>Splits a surface into its layer, face and depth slice at mip 0.</summary>
    public (int Layer, int Face, int Slice) Split(int surface) => (surface / (Faces * Depth), surface / Depth % Faces, surface % Depth);

    /// <summary>
    /// Returns the index of the image surface <paramref name="surface"/> is at mip <paramref name="mip"/> among the level's
    /// images, and the depth slice of that mip; <paramref name="flipZ"/> counts slices from the other end.
    /// </summary>
    public (int Index, int Slice) Image(int surface, int mip, bool flipZ)
    {
        (int layer, int face, int slice) = Split(surface);
        int depth = MipDepth(mip);
        slice = Math.Min(slice >> mip, depth - 1);
        int stored = flipZ ? depth - 1 - slice : slice;
        return ((((layer * Faces) + face) * depth) + stored, slice);
    }

    /// <summary>Returns where a KTX 1 file's mip <paramref name="mip"/> starts: its imageSize word.</summary>
    public long Ktx1LevelStart(int mip)
    {
        long offset = DataOffset;
        for (int m = 0; m < mip; m++)
        {
            offset = checked(offset + 4 + Align(Ktx1LevelBytes(m), 4));
        }

        return offset;
    }

    /// <summary>Returns the bytes of a KTX 1 level after its imageSize word, cube padding included.</summary>
    public long Ktx1LevelBytes(int mip) => IsCube && !IsArray ? checked(6 * Align(ImageBytes(mip), 4)) : LevelImageBytes(mip);

    /// <summary>Returns the imageSize a KTX 1 level should state: one face of a cube map that is not an array, else the whole level.</summary>
    public long Ktx1ImageSize(int mip) => IsCube && !IsArray ? ImageBytes(mip) : LevelImageBytes(mip);

    /// <summary>Returns where image <paramref name="index"/> starts in a KTX 1 level, after its imageSize word.</summary>
    public long Ktx1ImageOffset(int mip, int index) => checked(index * (IsCube && !IsArray ? Align(ImageBytes(mip), 4) : ImageBytes(mip)));

    internal static long Align(long value, int to) => to <= 1 ? value : checked((value + to - 1) / to * to);

    /// <summary>Reads the key/value data both containers share: a length, the key, a NUL, the value, padding to 4 bytes.</summary>
    internal void ReadKeyValues(ReadOnlySpan<byte> data, bool bigEndian, bool strict, EgamiInput input, long offset)
    {
        int at = 0;
        while (data.Length - at >= 4)
        {
            uint size = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data[at..]) : BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
            at += 4;
            if (size > (uint)(data.Length - at))
            {
                if (strict)
                {
                    throw input.Corrupt(offset + at - 4, "A key/value pair runs past the key/value data.");
                }

                Repairs.Add(("KeyValueDamaged", "A key/value pair runs past the key/value data; the pairs after it are ignored."));
                return;
            }

            ReadOnlySpan<byte> pair = data.Slice(at, (int)size);
            at += (int)Align(size, 4);
            int nul = pair.IndexOf((byte)0);
            if (nul <= 0)
            {
                continue;
            }

            string key;
            try
            {
                key = Strict.GetString(pair[..nul]);
            }
            catch (DecoderFallbackException)
            {
                continue;
            }

            ReadOnlySpan<byte> value = pair[(nul + 1)..];
            if (value.Length > 0 && value[^1] == 0)
            {
                value = value[..^1];
            }

            if (value.Contains((byte)0))
            {
                continue;
            }

            string text;
            try
            {
                text = Strict.GetString(value);
            }
            catch (DecoderFallbackException)
            {
                continue;
            }

            switch (key)
            {
                case "KTXorientation":
                    SetOrientation(text);
                    break;
                case "KTXswizzle":
                    if (text.Length == 4 && text.All(c => "rgba01".Contains(c, StringComparison.Ordinal)))
                    {
                        Swizzle = text;
                    }
                    else
                    {
                        Repairs.Add(("SwizzleIgnored", $"The swizzle \"{text}\" is not four of r, g, b, a, 0 and 1; it is ignored."));
                    }

                    break;
                default:
                    KeyValues.TryAdd(key, text);
                    break;
            }
        }
    }

    private static readonly UTF8Encoding Strict = new(false, true);

    /// <summary>Takes KTXorientation in KTX 2's form (<c>rd</c>) or KTX 1's (<c>S=r,T=d</c>).</summary>
    private void SetOrientation(string text)
    {
        string letters = text.Contains('=', StringComparison.Ordinal)
            ? string.Concat(text.Split(',').Select(part => part.Trim()).Where(part => part.Length == 3 && part[1] == '=').Select(part => part[2]))
            : text;
        int dimensions = Dimension switch
        {
            KtxDimension.Texture1D => 1,
            KtxDimension.Texture3D => 3,
            _ => 2,
        };
        if (letters.Length < 1 || letters.Length > 3 || letters[0] is not ('r' or 'l')
            || (letters.Length > 1 && letters[1] is not ('d' or 'u')) || (letters.Length > 2 && letters[2] is not ('i' or 'o')))
        {
            Repairs.Add(("OrientationIgnored", $"The orientation \"{text}\" is not one KTX defines; the default is used."));
            return;
        }

        string defaults = Orientation;
        Orientation = letters.Length >= dimensions ? letters[..dimensions] : letters + defaults[letters.Length..];
    }

    /// <summary>Checks the counts both containers share and applies the limits, repairing a mip count the size cannot have.</summary>
    internal void Check(EgamiInput input, bool strict, long mipOffset)
    {
        input.CheckFrameCount((long)Layers * Faces * Depth);
        int maxMips = 1 + BitOperations.Log2((uint)Math.Max(Math.Max(Width, Height), Depth));
        if (MipLevels > maxMips)
        {
            if (strict)
            {
                throw input.Corrupt(mipOffset, $"The file claims {MipLevels} mip levels; a {Width}×{Height} texture has at most {maxMips}.");
            }

            MipLevels = maxMips;
            if (Levels.Length > maxMips)
            {
                Levels = Levels[..maxMips];
            }

            Repairs.Add(("MipCountRepaired", $"The file claims more mip levels than its size allows; {maxMips} were read."));
        }
    }

    /// <summary>Returns the exception for a texture format Egami does not read.</summary>
    internal static UnsupportedFormatException Unsupported(string container, string? family, string format)
    {
        return family is null
            ? new UnsupportedFormatException($"The {container} file's format ({format}) cannot be read.")
            : new UnsupportedFormatException($"{container} textures in {family} compression are not supported yet ({format}).");
    }
}

/// <summary>Reads a KTX 1.1 header, from the KTX File Format Specification 1.1.</summary>
internal static class Ktx1Header
{
    internal const int Length = 64;

    private static ReadOnlySpan<byte> Identifier => [0xAB, 0x4B, 0x54, 0x58, 0x20, 0x31, 0x31, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Returns whether the data starts with the KTX 1.1 identifier.</summary>
    public static bool Matches(ReadOnlySpan<byte> header) => header.StartsWith(Identifier);

    public static KtxTexture Read(EgamiInput input, bool strict)
    {
        byte[] h = new byte[Length];
        input.ReadExactly(h);
        if (!Matches(h))
        {
            throw input.Corrupt(0, "The KTX identifier is wrong.");
        }

        uint endianness = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(12));
        bool big = endianness switch
        {
            0x04030201 => false,
            0x01020304 => true,
            _ => throw input.Corrupt(12, $"The endianness word 0x{endianness:X8} is neither byte order."),
        };

        uint U(int offset) => big ? BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(offset)) : BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(offset));
        int I(int offset) => (int)Math.Min(U(offset), int.MaxValue);

        int glType = I(16);
        int glTypeSize = I(20);
        int glFormat = I(24);
        int glInternal = I(28);
        int width = I(36);
        int height = I(40);
        int depth = I(44);
        int arrayElements = I(48);
        int faces = I(52);
        int mips = I(56);
        uint keyValueBytes = U(60);
        if (width == 0 || (depth > 0 && height == 0))
        {
            throw input.Corrupt(36, $"The texture size {width}×{height}×{depth} is invalid.");
        }

        if (faces is not (1 or 6))
        {
            throw input.Corrupt(52, $"The face count {faces} is neither 1 nor 6.");
        }

        TextureFormat format = TextureFormat.FromGl(glInternal, glFormat, glType)
            ?? throw KtxTexture.Unsupported("KTX", TextureFormat.UnsupportedGlFamily(glInternal), $"OpenGL internal format 0x{glInternal:X4}");
        bool compressed = glType == 0 && glFormat == 0;
        if (compressed != format.IsBlock)
        {
            throw input.Corrupt(16, compressed ? "The format is uncompressed but the header gives no type or format." : "The format is compressed but the header gives a type or format.");
        }

        if (!compressed && glTypeSize is not (1 or 2 or 4))
        {
            throw input.Corrupt(20, $"The type size {glTypeSize} is not 1, 2 or 4.");
        }

        KtxDimension dimension = depth > 0 ? KtxDimension.Texture3D : height > 0 ? KtxDimension.Texture2D : KtxDimension.Texture1D;
        if (faces == 6 && dimension != KtxDimension.Texture2D)
        {
            throw input.Corrupt(52, "A cube map must be 2D.");
        }

        KtxTexture texture = new()
        {
            Container = KtxContainer.Ktx1,
            Format = format,
            Width = width,
            Height = Math.Max(height, 1),
            Depth = Math.Max(depth, 1),
            Layers = Math.Max(arrayElements, 1),
            Faces = faces,
            MipLevels = Math.Max(mips, 1),
            IsArray = arrayElements > 0,
            Dimension = dimension,
            VkFormat = format.Vk,
            GlInternalFormat = glInternal,
            BigEndian = big,
            SwapUnit = big && !compressed ? glTypeSize : 1,
            RowAlign = compressed ? 1 : 4,
            DataOffset = Length + (long)keyValueBytes,
        };
        texture.Orientation = dimension == KtxDimension.Texture3D ? "rdi" : "rd";
        input.CheckMetadataBytes(keyValueBytes);
        byte[] keyValues = new byte[keyValueBytes];
        input.ReadExactly(keyValues);
        texture.ReadKeyValues(keyValues, big, strict, input, Length);
        texture.Check(input, strict, 56);
        return texture;
    }
}

/// <summary>Reads a KTX 2.0 header, level index, data format descriptor and key/value data, from the KTX 2.0 Specification.</summary>
internal static class Ktx2Header
{
    internal const int Length = 80;

    private static ReadOnlySpan<byte> Identifier => [0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Returns whether the data starts with the KTX 2.0 identifier.</summary>
    public static bool Matches(ReadOnlySpan<byte> header) => header.StartsWith(Identifier);

    public static KtxTexture Read(EgamiInput input, bool strict)
    {
        byte[] h = new byte[Length];
        input.ReadExactly(h);
        if (!Matches(h))
        {
            throw input.Corrupt(0, "The KTX 2 identifier is wrong.");
        }

        uint U(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(offset));
        int I(int offset) => (int)Math.Min(U(offset), int.MaxValue);

        int vk = I(12);
        int width = I(20);
        int height = I(24);
        int depth = I(28);
        int layers = I(32);
        int faces = I(36);
        int levels = I(40);
        uint scheme = U(44);
        uint dfdOffset = U(48);
        uint dfdLength = U(52);
        uint kvdOffset = U(56);
        uint kvdLength = U(60);
        if (width == 0 || (depth > 0 && height == 0))
        {
            throw input.Corrupt(20, $"The texture size {width}×{height}×{depth} is invalid.");
        }

        if (faces is not (1 or 6))
        {
            throw input.Corrupt(36, $"The face count {faces} is neither 1 nor 6.");
        }

        KtxDimension dimension = depth > 0 ? KtxDimension.Texture3D : height > 0 ? KtxDimension.Texture2D : KtxDimension.Texture1D;
        if (faces == 6 && dimension != KtxDimension.Texture2D)
        {
            throw input.Corrupt(36, "A cube map must be 2D.");
        }

        if (scheme > 3)
        {
            throw new UnsupportedFormatException($"The KTX 2 file's supercompression scheme {scheme} is not one Egami knows.");
        }

        int levelCount = Math.Max(levels, 1);
        int maxMips = 1 + BitOperations.Log2((uint)Math.Max(Math.Max(width, height), depth));
        if (levelCount > maxMips && strict)
        {
            throw input.Corrupt(40, $"The file claims {levelCount} mip levels; a {width}×{height} texture has at most {maxMips}.");
        }

        // The level index of every level the file claims is read, so the descriptor after it is found where the file has it.
        input.CheckMetadataBytes((long)levelCount * 24);
        byte[] index = new byte[levelCount * 24];
        input.ReadExactly(index);
        KtxLevel[] entries = new KtxLevel[levelCount];
        for (int m = 0; m < levelCount; m++)
        {
            ReadOnlySpan<byte> e = index.AsSpan(m * 24);
            entries[m] = new KtxLevel(Long(e), Long(e[8..]), Long(e[16..]));
        }

        // The descriptor and key/value data follow the index, in that order; a reader that cannot seek skips forward to each.
        byte[] dfd = Block(input, dfdOffset, dfdLength, "data format descriptor");
        (int model, int primaries, int transfer, bool premultiplied, string? problem) = Descriptor(dfd);
        if (problem is not null && strict)
        {
            throw input.Corrupt(dfdOffset, problem);
        }

        TextureFormat format = (vk, (KtxSupercompression)scheme) switch
        {
            (_, KtxSupercompression.BasisLZ) => throw KtxTexture.Unsupported("KTX 2", "Basis Universal ETC1S (BasisLZ)", $"VkFormat {vk}"),
            (0, _) when model == 166 => throw KtxTexture.Unsupported("KTX 2", "Basis Universal UASTC", "VK_FORMAT_UNDEFINED"),
            (0, _) => throw new UnsupportedFormatException($"The KTX 2 file's format is undefined (VkFormat 0, colour model {model})."),
            _ => TextureFormat.FromVk(vk) ?? throw KtxTexture.Unsupported("KTX 2", TextureFormat.UnsupportedVkFamily(vk), $"VkFormat {vk}"),
        };

        KtxTexture texture = new()
        {
            Container = KtxContainer.Ktx2,
            Format = format,
            Width = width,
            Height = Math.Max(height, 1),
            Depth = Math.Max(depth, 1),
            Layers = Math.Max(layers, 1),
            Faces = faces,
            MipLevels = levelCount,
            IsArray = layers > 0,
            Dimension = dimension,
            VkFormat = vk,
            Supercompression = (KtxSupercompression)scheme,
            Levels = entries,
            Primaries = primaries,
            Transfer = transfer,
            AlphaMode = premultiplied ? KtxAlphaMode.Premultiplied : KtxAlphaMode.Straight,
        };
        texture.Orientation = dimension == KtxDimension.Texture3D ? "rdi" : "rd";
        if (problem is not null)
        {
            texture.Repairs.Add(("DescriptorDamaged", problem + " Its colour space is ignored."));
        }

        if (kvdLength > 0)
        {
            byte[] kvd = Block(input, kvdOffset, kvdLength, "key/value data");
            texture.ReadKeyValues(kvd, false, strict, input, kvdOffset);
        }

        texture.Check(input, strict, 40);
        return texture;
    }

    private static long Long(ReadOnlySpan<byte> bytes) => (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(bytes), long.MaxValue);

    /// <summary>Reads a block the index points at, skipping forward to it.</summary>
    private static byte[] Block(EgamiInput input, uint offset, uint length, string what)
    {
        if (offset < input.Position)
        {
            throw input.Corrupt(input.Position, $"The {what} lies before the data read so far.");
        }

        input.CheckMetadataBytes(length);
        input.Skip(offset - input.Position);
        byte[] block = new byte[length];
        input.ReadExactly(block);
        return block;
    }

    /// <summary>
    /// Reads the basic descriptor block of a data format descriptor (Khronos Data Format Specification, section 5): its
    /// colour model, primaries, transfer function and whether alpha is premultiplied. Returns a problem when it is malformed.
    /// </summary>
    private static (int Model, int Primaries, int Transfer, bool Premultiplied, string? Problem) Descriptor(ReadOnlySpan<byte> dfd)
    {
        if (dfd.Length < 4 + 24)
        {
            return (0, 0, 0, false, "The data format descriptor is too short for its basic block.");
        }

        uint total = BinaryPrimitives.ReadUInt32LittleEndian(dfd);
        uint word0 = BinaryPrimitives.ReadUInt32LittleEndian(dfd[4..]);
        uint word1 = BinaryPrimitives.ReadUInt32LittleEndian(dfd[8..]);
        int blockSize = (int)(word1 >> 16);
        if (total != dfd.Length || (word0 & 0x1FFFF) != 0 || (word0 >> 17) != 0 || blockSize < 24 || blockSize > dfd.Length - 4)
        {
            return (0, 0, 0, false, "The data format descriptor does not start with a basic descriptor block.");
        }

        return (dfd[12], dfd[13], dfd[14], (dfd[15] & 1) != 0, null);
    }
}
