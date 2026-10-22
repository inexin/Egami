using System.Buffers.Binary;

namespace Egami.Video.Containers.Ogg;

/// <summary>An Ogg page (RFC 3533 section 6): its header fields, segment table and body.</summary>
internal sealed class OggPage
{
    public const int HeaderSize = 27;
    public const byte Continued = 1;
    public const byte BeginningOfStream = 2;
    public const byte EndOfStream = 4;

    public byte Flags;
    public long Granule;
    public uint Serial;
    public uint Sequence;
    public readonly byte[] Lacing = new byte[255];
    public int SegmentCount;
    public byte[] Body = new byte[1 << 16];
    public int BodyLength;

    /// <summary>Gets the file offset the page starts at.</summary>
    public long Position;

    public bool IsContinued => (Flags & Continued) != 0;

    public bool IsBeginning => (Flags & BeginningOfStream) != 0;

    public bool IsEnd => (Flags & EndOfStream) != 0;

    /// <summary>Gets the page's whole length in bytes.</summary>
    public int Length => HeaderSize + SegmentCount + BodyLength;

    /// <summary>
    /// Reads the next page, resynchronizing on "OggS" after damage. Returns false at the end of the data; a page whose CRC fails
    /// is skipped and counted in <paramref name="badPages"/>.
    /// </summary>
    public bool Read(MediaInput input, ref int badPages)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        Span<byte> zeroed = stackalloc byte[HeaderSize];
        while (true)
        {
            if (!FindCapture(input))
            {
                return false;
            }

            long position = input.Position - 4;
            "OggS"u8.CopyTo(header);
            if (!input.TryReadExactly(header[4..]))
            {
                return false;
            }

            if (header[4] != 0)
            {
                Retry(input, position);
                continue;
            }

            Position = position;
            Flags = header[5];
            Granule = BinaryPrimitives.ReadInt64LittleEndian(header[6..]);
            Serial = BinaryPrimitives.ReadUInt32LittleEndian(header[14..]);
            Sequence = BinaryPrimitives.ReadUInt32LittleEndian(header[18..]);
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(header[22..]);
            SegmentCount = header[26];
            if (!input.TryReadExactly(Lacing.AsSpan(0, SegmentCount)))
            {
                return false;
            }

            BodyLength = 0;
            for (int i = 0; i < SegmentCount; i++)
            {
                BodyLength += Lacing[i];
            }

            if (!input.TryReadExactly(Body.AsSpan(0, BodyLength)))
            {
                return false;
            }

            header.CopyTo(zeroed);
            zeroed[22..26].Clear();
            uint check = Crc(Crc(Crc(0, zeroed), Lacing.AsSpan(0, SegmentCount)), Body.AsSpan(0, BodyLength));
            if (check != crc)
            {
                badPages++;
                Retry(input, position);
                continue;
            }

            return true;
        }
    }

    /// <summary>After a bad page: on a seekable input look again one byte after its start; otherwise go on from here.</summary>
    private static void Retry(MediaInput input, long position)
    {
        if (input.CanSeek)
        {
            input.Seek(position + 1);
        }
    }

    /// <summary>Reads up to and including the next "OggS"; false at the end of the data.</summary>
    private static bool FindCapture(MediaInput input)
    {
        int matched = 0;
        while (matched < 4)
        {
            int b = input.PeekByte();
            if (b < 0)
            {
                return false;
            }

            input.ReadByte();
            matched = b == "OggS"[matched] ? matched + 1 : b == 'O' ? 1 : 0;
        }

        return true;
    }

    /// <summary>Writes the page with its CRC.</summary>
    public void Write(Stream output)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        "OggS"u8.CopyTo(header);
        header[4] = 0;
        header[5] = Flags;
        BinaryPrimitives.WriteInt64LittleEndian(header[6..], Granule);
        BinaryPrimitives.WriteUInt32LittleEndian(header[14..], Serial);
        BinaryPrimitives.WriteUInt32LittleEndian(header[18..], Sequence);
        header[26] = (byte)SegmentCount;
        uint crc = Crc(Crc(Crc(0, header), Lacing.AsSpan(0, SegmentCount)), Body.AsSpan(0, BodyLength));
        BinaryPrimitives.WriteUInt32LittleEndian(header[22..], crc);
        output.Write(header);
        output.Write(Lacing.AsSpan(0, SegmentCount));
        output.Write(Body.AsSpan(0, BodyLength));
    }

    /// <summary>Ogg's CRC-32: polynomial 0x04C11DB7, starting at 0, not reflected.</summary>
    public static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        uint[] table = s_table;
        foreach (byte b in data)
        {
            crc = (crc << 8) ^ table[(crc >> 24) ^ b];
        }

        return crc;
    }

    private static readonly uint[] s_table = BuildTable();

    private static uint[] BuildTable()
    {
        uint[] t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint r = i << 24;
            for (int k = 0; k < 8; k++)
            {
                r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
            }

            t[i] = r;
        }

        return t;
    }
}
