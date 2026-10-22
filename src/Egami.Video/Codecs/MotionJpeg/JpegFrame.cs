using System.Buffers.Binary;

namespace Egami.Video.Codecs.MotionJpeg;

/// <summary>
/// Finds the JPEG images in a Motion JPEG packet and fixes what Egami's JPEG decoder should not have to: junk around the
/// images, a missing DHT segment (many cameras rely on the standard tables of ITU-T T.81 Annex K.3), and the two field
/// images of an interlaced frame, flagged by an AVI1 APP0 segment.
/// </summary>
internal static class JpegFrame
{
    /// <summary>Field order from an AVI1 APP0 segment: 0 not interlaced, 1 odd (top) field first, 2 even (bottom) field first.</summary>
    public enum Polarity
    {
        None = 0,
        TopFirst = 1,
        BottomFirst = 2,
    }

    /// <summary>A DHT segment with the four standard tables of T.81 Annex K.3 (DC and AC, luminance and chrominance).</summary>
    public static ReadOnlySpan<byte> StandardHuffmanTables => s_standardDht;

    private static readonly byte[] s_standardDht = BuildStandardDht();

    /// <summary>One JPEG image inside a packet.</summary>
    public readonly record struct Image(int Start, int Length, bool HasDht, int SosOffset, Polarity Polarity);

    /// <summary>
    /// Finds up to two images: the first from the first SOI to its EOI, then a second one when another SOI follows (the
    /// second field of an interlaced frame). Returns how many were found.
    /// </summary>
    public static int Find(ReadOnlySpan<byte> packet, Span<Image> images)
    {
        int found = 0;
        int position = 0;
        while (found < images.Length && TryFindImage(packet, position, out Image image))
        {
            images[found++] = image;
            position = image.Start + image.Length;
        }

        return found;
    }

    /// <summary>Returns the image's bytes, with the standard DHT spliced in before SOS when the image has none.</summary>
    public static int CopyFixed(ReadOnlySpan<byte> packet, Image image, Span<byte> destination)
    {
        ReadOnlySpan<byte> bytes = packet.Slice(image.Start, image.Length);
        if (image.HasDht)
        {
            bytes.CopyTo(destination);
            return bytes.Length;
        }

        int split = image.SosOffset;
        bytes[..split].CopyTo(destination);
        s_standardDht.CopyTo(destination[split..]);
        bytes[split..].CopyTo(destination[(split + s_standardDht.Length)..]);
        return bytes.Length + s_standardDht.Length;
    }

    /// <summary>The size <see cref="CopyFixed"/> needs.</summary>
    public static int FixedLength(Image image) => image.Length + (image.HasDht ? 0 : s_standardDht.Length);

    private static bool TryFindImage(ReadOnlySpan<byte> data, int from, out Image image)
    {
        image = default;
        int soi = IndexOfMarker(data, from, 0xD8);
        if (soi < 0)
        {
            return false;
        }

        // Walk the marker segments up to SOS.
        int position = soi + 2;
        bool hasDht = false;
        Polarity polarity = Polarity.None;
        int sos = -1;
        while (position + 4 <= data.Length)
        {
            if (data[position] != 0xFF)
            {
                return false;
            }

            byte marker = data[position + 1];
            if (marker == 0xFF)
            {
                // Fill bytes before a marker.
                position++;
                continue;
            }

            if (marker == 0xD9)
            {
                return false;
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(data[(position + 2)..]);
            if (length < 2 || position + 2 + length > data.Length)
            {
                return false;
            }

            if (marker == 0xC4)
            {
                hasDht = true;
            }
            else if (marker == 0xE0 && length >= 7 && data.Slice(position + 4, 4).SequenceEqual("AVI1"u8))
            {
                polarity = data[position + 8] switch
                {
                    1 => Polarity.TopFirst,
                    2 => Polarity.BottomFirst,
                    _ => Polarity.None,
                };
            }
            else if (marker == 0xDA)
            {
                sos = position;
                position += 2 + length;
                break;
            }

            position += 2 + length;
        }

        if (sos < 0)
        {
            return false;
        }

        // Entropy-coded data runs to the next marker that is neither a stuffed zero nor a restart marker. Progressive files
        // have several scans, so carry on through further segments until EOI.
        while (position + 1 < data.Length)
        {
            int ff = data[position..].IndexOf((byte)0xFF);
            if (ff < 0 || position + ff + 1 >= data.Length)
            {
                break;
            }

            position += ff;
            byte next = data[position + 1];
            if (next == 0x00 || next == 0xFF || next is >= 0xD0 and <= 0xD7)
            {
                position += next == 0xFF ? 1 : 2;
                continue;
            }

            if (next == 0xD9)
            {
                int end = position + 2;
                image = new Image(soi, end - soi, hasDht, sos - soi, polarity);
                return true;
            }

            // Another segment (a DHT or SOS between progressive scans): skip it.
            if (position + 4 > data.Length)
            {
                break;
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(data[(position + 2)..]);
            hasDht |= next == 0xC4;
            position += 2 + Math.Max(2, length);
        }

        // No EOI: the image runs to the end of the packet, as in a truncated frame. Egami decides what it can use.
        image = new Image(soi, data.Length - soi, hasDht, sos - soi, polarity);
        return true;
    }

    private static int IndexOfMarker(ReadOnlySpan<byte> data, int from, byte marker)
    {
        for (int i = from; i + 1 < data.Length; i++)
        {
            int ff = data[i..].IndexOf((byte)0xFF);
            if (ff < 0)
            {
                return -1;
            }

            i += ff;
            if (i + 1 < data.Length && data[i + 1] == marker)
            {
                return i;
            }
        }

        return -1;
    }

    private static byte[] BuildStandardDht()
    {
        // ITU-T T.81 Annex K.3, tables K.3 to K.6: code length counts (16 bytes) then the symbol values.
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
        AddTable(segment, 0x00, dcLuminanceBits, dcValues);
        AddTable(segment, 0x10, acLuminanceBits, acLuminanceValues);
        AddTable(segment, 0x01, dcChrominanceBits, dcValues);
        AddTable(segment, 0x11, acChrominanceBits, acChrominanceValues);
        int length = segment.Count - 2;
        segment[2] = (byte)(length >> 8);
        segment[3] = (byte)length;
        return [.. segment];

        static void AddTable(List<byte> segment, byte classAndId, ReadOnlySpan<byte> bits, ReadOnlySpan<byte> values)
        {
            segment.Add(classAndId);
            segment.AddRange(bits);
            segment.AddRange(values);
        }
    }
}
