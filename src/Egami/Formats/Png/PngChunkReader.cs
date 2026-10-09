using System.Buffers.Binary;

namespace Egami.Formats.Png;

/// <summary>Chunk type codes as big-endian integers.</summary>
internal static class PngChunkType
{
    internal const uint Ihdr = 0x49484452;
    internal const uint Plte = 0x504C5445;
    internal const uint Idat = 0x49444154;
    internal const uint Iend = 0x49454E44;
    internal const uint Trns = 0x74524E53;
    internal const uint Gama = 0x67414D41;
    internal const uint Chrm = 0x6348524D;
    internal const uint Srgb = 0x73524742;
    internal const uint Iccp = 0x69434350;
    internal const uint Cicp = 0x63494350;
    internal const uint Phys = 0x70485973;
    internal const uint Text = 0x74455874;
    internal const uint Ztxt = 0x7A545874;
    internal const uint Itxt = 0x69545874;
    internal const uint Exif = 0x65584966;
    internal const uint Actl = 0x6163544C;
    internal const uint Fctl = 0x6663544C;
    internal const uint Fdat = 0x66644154;

    /// <summary>Chunks with no behavior in v1 that are still kept for round trips.</summary>
    internal static bool IsKeptAsIs(uint type)
    {
        return type is 0x624B4744 /* bKGD */ or 0x74494D45 /* tIME */ or 0x73424954 /* sBIT */ or 0x68495354 /* hIST */
            or 0x73504C54 /* sPLT */ or 0x6D444356 /* mDCV */ or 0x634C4C69 /* cLLi */ or 0x634C4C49 /* cLLI */;
    }

    /// <summary>Bit 5 of the first byte: lowercase means ancillary.</summary>
    internal static bool IsCritical(uint type)
    {
        return (type & 0x2000_0000) == 0;
    }

    /// <summary>Bit 5 of the last byte: lowercase means safe to copy when the image data changes.</summary>
    internal static bool IsSafeToCopy(uint type)
    {
        return (type & 0x20) != 0;
    }

    /// <summary>Each byte must be an ASCII letter (section 5.4).</summary>
    internal static bool IsValid(uint type)
    {
        for (int shift = 0; shift < 32; shift += 8)
        {
            uint c = (type >> shift) & 0xFF;
            if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')))
            {
                return false;
            }
        }

        return true;
    }

    internal static string Name(uint type)
    {
        Span<char> name = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            byte c = (byte)(type >> (24 - (8 * i)));
            name[i] = c is >= 0x20 and < 0x7F ? (char)c : '?';
        }

        return new string(name);
    }
}

/// <summary>Reads chunk headers, data and CRCs from an <see cref="EgamiInput"/>.</summary>
internal sealed class PngChunkReader(EgamiInput input, bool strict, IList<string> warnings)
{
    /// <summary>The largest chunk length the specification allows.</summary>
    private const uint MaxLength = int.MaxValue;

    internal EgamiInput Input => input;

    /// <summary>Gets the offset of the current chunk's length field.</summary>
    internal long ChunkOffset { get; private set; }

    /// <summary>Reads a chunk's length and type.</summary>
    /// <returns><see langword="false"/> if the data ended cleanly before another chunk.</returns>
    internal bool TryReadHeader(out uint length, out uint type)
    {
        ChunkOffset = input.Position;
        if (input.Peek(1).IsEmpty)
        {
            length = 0;
            type = 0;
            return false;
        }

        length = input.ReadUInt32BigEndian();
        type = input.ReadUInt32BigEndian();
        if (length > MaxLength)
        {
            throw input.Corrupt(ChunkOffset, $"The {PngChunkType.Name(type)} chunk length {length} is larger than 2^31-1.");
        }

        if (!PngChunkType.IsValid(type))
        {
            throw input.Corrupt(ChunkOffset + 4, "The chunk type is not four ASCII letters.");
        }

        return true;
    }

    /// <summary>Reads a chunk's data and CRC into a new array.</summary>
    /// <returns>The data, or <see langword="null"/> if an ancillary chunk's CRC was wrong in lenient mode.</returns>
    internal byte[]? ReadData(uint type, uint length)
    {
        byte[] data = new byte[length];
        input.ReadExactly(data);
        uint crc = Crc32.Update(CrcOfType(type), data);
        return CheckCrc(type, crc, PngChunkType.IsCritical(type)) ? data : null;
    }

    /// <summary>Skips a chunk's data, still checking its CRC.</summary>
    internal void Skip(uint type, uint length)
    {
        uint crc = CrcOfType(type);
        Span<byte> buffer = stackalloc byte[4096];
        while (length > 0)
        {
            Span<byte> part = buffer[..(int)Math.Min(length, (uint)buffer.Length)];
            input.ReadExactly(part);
            crc = Crc32.Update(crc, part);
            length -= (uint)part.Length;
        }

        CheckCrc(type, crc, PngChunkType.IsCritical(type));
    }

    /// <summary>Reads the stored CRC and compares it with <paramref name="computed"/>.</summary>
    /// <returns><see langword="true"/> if it matches; <see langword="false"/> for a bad ancillary CRC in lenient mode.</returns>
    internal bool CheckCrc(uint type, uint computed, bool critical)
    {
        long offset = input.Position;
        uint stored = input.ReadUInt32BigEndian();
        if (stored == computed)
        {
            return true;
        }

        string problem = $"The {PngChunkType.Name(type)} chunk CRC is 0x{stored:X8} but the data gives 0x{computed:X8}.";
        if (critical || strict)
        {
            throw input.Corrupt(offset, problem);
        }

        warnings.Add(problem + " The chunk was ignored.");
        return false;
    }

    /// <summary>Reports a problem that lenient mode recovers from.</summary>
    internal void Recoverable(long offset, string problem)
    {
        if (strict)
        {
            throw input.Corrupt(offset, problem);
        }

        warnings.Add(problem);
    }

    /// <summary>Returns the CRC of a chunk's four type bytes, where every chunk CRC starts.</summary>
    private static uint CrcOfType(uint type)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, type);
        return Crc32.Update(0, bytes);
    }
}
