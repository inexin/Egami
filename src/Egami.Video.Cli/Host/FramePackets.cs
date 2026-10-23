using System.Buffers.Binary;

namespace Egami.Video.Cli.Host;

/// <summary>
/// Motion JPEG packets as JPEG files, for <c>frames</c> to write them without decoding and encoding again: a packet that holds
/// one complete image (SOI to EOI) is that image's file. Many AVI cameras leave out the DHT segment and rely on the standard
/// Huffman tables of ITU-T T.81 Annex K.3; those tables are put back in before SOS, so the file is a JPEG any viewer opens.
/// </summary>
internal static class FramePackets
{
    private static readonly byte[] StandardDht = BuildStandardDht();

    /// <summary>
    /// The JPEG file in a Motion JPEG packet, or <see langword="null"/> when the packet is not one complete image (two field
    /// images of an interlaced frame, a cut-off image, junk before SOI); those frames are decoded instead. Bytes after EOI, such
    /// as padding, are left out.
    /// </summary>
    public static byte[]? Jpeg(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 4 || packet[0] != 0xFF || packet[1] != 0xD8)
        {
            return null;
        }

        // The marker segments up to SOS.
        int position = 2;
        bool hasDht = false;
        int sos = -1;
        while (position + 4 <= packet.Length && sos < 0)
        {
            if (packet[position] != 0xFF)
            {
                return null;
            }

            byte marker = packet[position + 1];
            if (marker == 0xFF)
            {
                position++;
                continue;
            }

            if (marker is 0xD8 or 0xD9)
            {
                return null;
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(packet[(position + 2)..]);
            if (length < 2 || position + 2 + length > packet.Length)
            {
                return null;
            }

            hasDht |= marker == 0xC4;
            if (marker == 0xDA)
            {
                sos = position;
            }

            position += 2 + length;
        }

        if (sos < 0)
        {
            return null;
        }

        // The entropy-coded data, and the segments between the scans of a progressive image, up to EOI.
        int end = -1;
        while (position + 1 < packet.Length)
        {
            int ff = packet[position..].IndexOf((byte)0xFF);
            if (ff < 0 || position + ff + 1 >= packet.Length)
            {
                break;
            }

            position += ff;
            byte next = packet[position + 1];
            if (next == 0x00 || next == 0xFF || next is >= 0xD0 and <= 0xD7)
            {
                position += next == 0xFF ? 1 : 2;
                continue;
            }

            if (next == 0xD9)
            {
                end = position + 2;
                break;
            }

            if (position + 4 > packet.Length)
            {
                break;
            }

            hasDht |= next == 0xC4;
            position += 2 + Math.Max(2, (int)BinaryPrimitives.ReadUInt16BigEndian(packet[(position + 2)..]));
        }

        // No EOI, or a second image after it (the other field): not one picture.
        if (end < 0 || packet[end..].IndexOf([(byte)0xFF, (byte)0xD8]) >= 0)
        {
            return null;
        }

        ReadOnlySpan<byte> image = packet[..end];
        if (hasDht)
        {
            return image.ToArray();
        }

        byte[] fixedImage = new byte[image.Length + StandardDht.Length];
        image[..sos].CopyTo(fixedImage);
        StandardDht.CopyTo(fixedImage.AsSpan(sos));
        image[sos..].CopyTo(fixedImage.AsSpan(sos + StandardDht.Length));
        return fixedImage;
    }

    private static byte[] BuildStandardDht()
    {
        // ITU-T T.81 Annex K.3, tables K.3 to K.6: the code length counts (16 bytes), then the symbol values.
        ReadOnlySpan<byte> dcLuminanceBits = [0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];
        ReadOnlySpan<byte> dcChrominanceBits = [0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];
        ReadOnlySpan<byte> dcValues = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
        ReadOnlySpan<byte> acLuminanceBits = [0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7D];
        ReadOnlySpan<byte> acLuminanceValues =
        [
            0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
            0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08, 0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0,
            0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0A, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x25, 0x26, 0x27, 0x28,
            0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
            0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
            0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
            0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7,
            0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5,
            0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2,
            0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
            0xF9, 0xFA,
        ];
        ReadOnlySpan<byte> acChrominanceBits = [0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77];
        ReadOnlySpan<byte> acChrominanceValues =
        [
            0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
            0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xA1, 0xB1, 0xC1, 0x09, 0x23, 0x33, 0x52, 0xF0,
            0x15, 0x62, 0x72, 0xD1, 0x0A, 0x16, 0x24, 0x34, 0xE1, 0x25, 0xF1, 0x17, 0x18, 0x19, 0x1A, 0x26,
            0x27, 0x28, 0x29, 0x2A, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
            0x49, 0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
            0x69, 0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
            0x88, 0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5,
            0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3,
            0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA,
            0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
            0xF9, 0xFA,
        ];

        List<byte> segment = [0xFF, 0xC4, 0, 0];
        Add(segment, 0x00, dcLuminanceBits, dcValues);
        Add(segment, 0x10, acLuminanceBits, acLuminanceValues);
        Add(segment, 0x01, dcChrominanceBits, dcValues);
        Add(segment, 0x11, acChrominanceBits, acChrominanceValues);
        int length = segment.Count - 2;
        segment[2] = (byte)(length >> 8);
        segment[3] = (byte)length;
        return [.. segment];

        static void Add(List<byte> segment, byte classAndId, ReadOnlySpan<byte> bits, ReadOnlySpan<byte> values)
        {
            segment.Add(classAndId);
            segment.AddRange(bits);
            segment.AddRange(values);
        }
    }
}

/// <summary>
/// Standard input read twice: the bytes a first look at the headers read are kept, and <see cref="Replay"/> returns them
/// followed by the rest of the input, so a pipe needs no temporary file.
/// </summary>
internal sealed class FrameReplayStream(Stream inner) : Stream
{
    private readonly MemoryStream _seen = new();

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => _seen.Length; set => throw new NotSupportedException(); }

    /// <summary>A stream of every byte read so far, then the rest of the input.</summary>
    public Stream Replay() => new Concatenated(_seen.ToArray(), inner);

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int read = inner.Read(buffer);
        _seen.Write(buffer[..read]);
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _seen.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed class Concatenated(byte[] prefix, Stream rest) : Stream
    {
        private int _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_offset < prefix.Length)
            {
                int count = Math.Min(buffer.Length, prefix.Length - _offset);
                prefix.AsSpan(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }

            return rest.Read(buffer);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                rest.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
