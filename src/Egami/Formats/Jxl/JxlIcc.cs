using System.Buffers.Binary;

namespace Egami.Formats.Jxl;

/// <summary>
/// The compressed ICC profile of ISO/IEC 18181-1 E.4: the entropy-coded stream with its 41 byte contexts, then the header
/// prediction, the tag list commands and the data commands that rebuild the profile. Translated from jxl-oxide's jxl-color
/// crate, checked against libjxl's icc_codec (see THIRD-PARTY-NOTICES).
/// </summary>
internal static class JxlIcc
{
    private static readonly string[] CommonTags =
        ["rTRC", "rXYZ", "cprt", "wtpt", "bkpt", "rXYZ", "gXYZ", "bXYZ", "kXYZ", "rTRC", "gTRC", "bTRC", "kTRC", "chad", "desc", "chrm", "dmnd", "dmdd", "lumi"];

    private static readonly string[] CommonData = ["XYZ ", "desc", "text", "mluc", "para", "curv", "sf32", "gbd "];

    /// <summary>Reads the entropy-coded stream and returns the profile it encodes, checking sizes against the metadata limit.</summary>
    internal static byte[] Read(JxlBitReader r, SafetyLimits limits)
    {
        ulong encodedSize = r.ReadU64();
        if (encodedSize > 1 << 28 || (long)encodedSize > limits.MaxMetadataBytes)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxMetadataBytes), limits.MaxMetadataBytes, (long)Math.Min(encodedSize, long.MaxValue));
        }

        JxlEntropyDecoder decoder = JxlEntropyDecoder.Read(r, 41);
        decoder.Begin(r);
        byte[] encoded = new byte[encodedSize];
        int b1 = 0;
        int b2 = 0;
        int header = (int)Math.Min(encodedSize, 18);
        for (int i = 0; i < encoded.Length; i++)
        {
            uint value = decoder.ReadUint(r, Context(i, b1, b2));
            if (value > 255)
            {
                throw r.Corrupt("The compressed ICC profile decodes a byte above 255.");
            }

            encoded[i] = (byte)value;
            b2 = b1;
            b1 = encoded[i];
            if (i + 1 == header)
            {
                // A quick check of the sizes before the rest is decoded.
                int at = 0;
                ulong output = Varint(encoded.AsSpan(0, header), ref at, r);
                ulong commands = Varint(encoded.AsSpan(0, header), ref at, r);
                if ((ulong)at + commands > encodedSize || output > 1 << 28 || output + 65536 < encodedSize)
                {
                    throw r.Corrupt("The compressed ICC profile's sizes are inconsistent.");
                }

                if ((long)output > limits.MaxMetadataBytes)
                {
                    throw new LimitExceededException(nameof(SafetyLimits.MaxMetadataBytes), limits.MaxMetadataBytes, (long)output);
                }
            }
        }

        decoder.Finish(r);
        return Decode(encoded, r);
    }

    /// <summary>Rebuilds the profile from the decoded stream (E.4.3 to E.4.5).</summary>
    internal static byte[] Decode(ReadOnlySpan<byte> stream, JxlBitReader r)
    {
        int at = 0;
        ulong outputSize = Varint(stream, ref at, r);
        ulong commandsSize = Varint(stream, ref at, r);
        if ((ulong)at + commandsSize > (ulong)stream.Length || outputSize > 1 << 28)
        {
            throw r.Corrupt("The ICC command stream is longer than the profile stream.");
        }

        ReadOnlySpan<byte> commands = stream.Slice(at, (int)commandsSize);
        ReadOnlySpan<byte> data = stream[(at + (int)commandsSize)..];
        int headerSize = (int)Math.Min(outputSize, 128);
        if (data.Length < headerSize)
        {
            throw r.Corrupt("The ICC data stream is shorter than the profile header.");
        }

        List<byte> output = new((int)outputSize);
        ReadOnlySpan<byte> headerData = data[..headerSize];
        for (int i = 0; i < headerSize; i++)
        {
            output.Add((byte)(Predict(i, (uint)outputSize, output) + headerData[i]));
        }

        data = data[headerSize..];
        if (outputSize <= 128)
        {
            return [.. output];
        }

        int cmd = 0;
        ulong tagCount = Varint(commands, ref cmd, r);
        if (tagCount > 0)
        {
            uint tags = (uint)(tagCount - 1);
            if ((outputSize - 128) / 12 < tags)
            {
                throw r.Corrupt("The ICC profile names too many tags.");
            }

            AddBigEndian(output, tags);
            uint previousStart = (tags * 12) + 128;
            uint previousSize = 0;
            while (cmd < commands.Length)
            {
                byte command = commands[cmd++];
                int tagCode = command & 63;
                if (tagCode == 0)
                {
                    break;
                }

                string tag;
                if (tagCode == 1)
                {
                    if (data.Length < 4)
                    {
                        throw r.Corrupt("The ICC data stream ends inside a tag name.");
                    }

                    tag = System.Text.Encoding.Latin1.GetString(data[..4]);
                    data = data[4..];
                }
                else if (tagCode <= 20)
                {
                    tag = CommonTags[tagCode - 2];
                }
                else
                {
                    throw r.Corrupt($"ICC tag code {tagCode} is unknown.");
                }

                uint start = (command & 64) == 0 ? previousStart + previousSize : (uint)Varint(commands, ref cmd, r);
                uint size = (command & 128) != 0 ? (uint)Varint(commands, ref cmd, r)
                    : tag is "rXYZ" or "gXYZ" or "bXYZ" or "kXYZ" or "wtpt" or "bkpt" or "lumi" ? 20 : previousSize;
                if ((ulong)start + size > outputSize)
                {
                    throw r.Corrupt("An ICC tag lies outside the profile.");
                }

                previousStart = start;
                previousSize = size;
                AddTag(output, tag, start, size);
                if (tagCode == 2)
                {
                    AddTag(output, "gTRC", start, size);
                    AddTag(output, "bTRC", start, size);
                }
                else if (tagCode == 3)
                {
                    AddTag(output, "gXYZ", start + size, size);
                    AddTag(output, "bXYZ", start + (size * 2), size);
                }
            }
        }

        while (cmd < commands.Length)
        {
            byte command = commands[cmd++];
            switch (command)
            {
                case 1:
                case 2:
                case 3:
                    {
                        int count = Count(Varint(commands, ref cmd, r), data, r);
                        ReadOnlySpan<byte> bytes = data[..count];
                        data = data[count..];
                        output.AddRange(command == 1 ? bytes.ToArray() : command == 2 ? Shuffle(bytes, 2) : Shuffle(bytes, 4));
                        break;
                    }

                case 4:
                    {
                        if (cmd >= commands.Length)
                        {
                            throw r.Corrupt("The ICC command stream ends inside a command.");
                        }

                        byte flags = commands[cmd++];
                        int width = (flags & 3) + 1;
                        int order = (flags >> 2) & 3;
                        if (width == 3 || order == 3)
                        {
                            throw r.Corrupt("An ICC predicted run has width 3 or order 3.");
                        }

                        int stride = width;
                        if ((flags & 16) != 0)
                        {
                            ulong s = Varint(commands, ref cmd, r);
                            stride = s < (ulong)width || s > int.MaxValue ? throw r.Corrupt("An ICC predicted run's stride is out of range.") : (int)s;
                        }

                        if ((long)stride * 4 >= output.Count)
                        {
                            throw r.Corrupt("An ICC predicted run reaches before the profile.");
                        }

                        int count = Count(Varint(commands, ref cmd, r), data, r);
                        byte[] bytes = width == 1 ? data[..count].ToArray() : Shuffle(data[..count], width);
                        data = data[count..];
                        Span<uint> previous = stackalloc uint[3];
                        for (int i = 0; i < count; i += width)
                        {
                            for (int j = 0; j <= order; j++)
                            {
                                int offset = output.Count - (stride * (j + 1));
                                uint value = 0;
                                for (int k = 0; k < width; k++)
                                {
                                    value = (value << 8) | output[offset + k];
                                }

                                previous[j] = value;
                            }

                            uint p = order switch
                            {
                                0 => previous[0],
                                1 => unchecked((2 * previous[0]) - previous[1]),
                                _ => unchecked((3 * (previous[0] - previous[1])) + previous[2]),
                            };
                            for (int j = 0; j < Math.Min(width, count - i); j++)
                            {
                                output.Add(unchecked((byte)(bytes[i + j] + (p >> (8 * (width - 1 - j))))));
                            }
                        }

                        break;
                    }

                case 10:
                    if (data.Length < 12)
                    {
                        throw r.Corrupt("The ICC data stream ends inside an XYZ value.");
                    }

                    output.AddRange("XYZ \0\0\0\0"u8.ToArray());
                    output.AddRange(data[..12].ToArray());
                    data = data[12..];
                    break;
                case >= 16 and <= 23:
                    output.AddRange(System.Text.Encoding.Latin1.GetBytes(CommonData[command - 16]));
                    output.AddRange([0, 0, 0, 0]);
                    break;
                default:
                    throw r.Corrupt($"ICC data command {command} is unknown.");
            }
        }

        if ((ulong)output.Count != outputSize)
        {
            throw r.Corrupt($"The ICC profile rebuilt to {output.Count} bytes; its header says {outputSize}.");
        }

        return [.. output];
    }

    private static int Context(int index, int b1, int b2)
    {
        if (index <= 128)
        {
            return 0;
        }

        int p1 = b1 switch
        {
            >= 'a' and <= 'z' or >= 'A' and <= 'Z' => 0,
            >= '0' and <= '9' or '.' or ',' => 1,
            0 or 1 => 2 + b1,
            <= 15 => 4,
            >= 241 and <= 254 => 5,
            255 => 6,
            _ => 7,
        };
        int p2 = b2 switch
        {
            >= 'a' and <= 'z' or >= 'A' and <= 'Z' => 0,
            >= '0' and <= '9' or '.' or ',' => 1,
            <= 15 => 2,
            >= 241 => 3,
            _ => 4,
        };
        return 1 + p1 + (8 * p2);
    }

    /// <summary>Predicts a header byte from the profile size and the bytes decoded so far, as libjxl's ICCPredictHeader does.</summary>
    private static byte Predict(int index, uint outputSize, List<byte> header)
    {
        switch (index)
        {
            case <= 3:
                return (byte)(outputSize >> (8 * (3 - index)));
            case 8:
                return 4;
            case >= 12 and <= 23:
                return (byte)"mntrRGB XYZ "[index - 12];
            case >= 36 and <= 39:
                return (byte)"acsp"[index - 36];
            case 70:
                return 246;
            case 71:
                return 214;
            case 73:
                return 1;
            case 78:
                return 211;
            case 79:
                return 45;
            case >= 80 and <= 83:
                return header[index - 76];
        }

        if (index is >= 41 and <= 43)
        {
            byte c = header[40];
            if (c == 'A')
            {
                return index == 43 ? (byte)'L' : (byte)'P';
            }

            if (c == 'M')
            {
                return index switch { 41 => (byte)'S', 42 => (byte)'F', _ => (byte)'T' };
            }

            if (c == 'S' && index >= 42)
            {
                if (header[41] == 'G')
                {
                    return index == 42 ? (byte)'I' : (byte)' ';
                }

                if (header[41] == 'U')
                {
                    return index == 42 ? (byte)'N' : (byte)'W';
                }
            }
        }

        return 0;
    }

    private static ulong Varint(ReadOnlySpan<byte> stream, ref int at, JxlBitReader r)
    {
        ulong value = 0;
        for (int shift = 0; shift < 63; shift += 7)
        {
            if (at >= stream.Length)
            {
                throw r.Corrupt("The ICC stream ends inside a number.");
            }

            byte b = stream[at++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                break;
            }
        }

        return value;
    }

    private static int Count(ulong count, ReadOnlySpan<byte> data, JxlBitReader r)
    {
        return count > (ulong)data.Length ? throw r.Corrupt("The ICC data stream is too short for a command.") : (int)count;
    }

    /// <summary>Undoes the byte transposition of 2 or 4 interleaved streams (E.4.5, Shuffle).</summary>
    private static byte[] Shuffle(ReadOnlySpan<byte> bytes, int width)
    {
        int len = bytes.Length;
        byte[] output = new byte[len];
        int n = 0;
        if (width == 2)
        {
            int height = len / 2;
            int odd = len % 2;
            for (int i = 0; i < height; i++)
            {
                output[n++] = bytes[i];
                output[n++] = bytes[i + height + odd];
            }

            if (odd != 0)
            {
                output[n] = bytes[height];
            }

            return output;
        }

        int step = len / 4;
        int wide = len % 4;
        for (int i = 0; i < step; i++)
        {
            int at = i;
            for (int k = 0; k < wide; k++)
            {
                output[n++] = bytes[at];
                at += step + 1;
            }

            for (int k = wide; k < 4; k++)
            {
                output[n++] = bytes[at];
                at += step;
            }
        }

        for (int k = 1; k <= wide; k++)
        {
            output[n++] = bytes[((step + 1) * k) - 1];
        }

        return output;
    }

    private static void AddTag(List<byte> output, string tag, uint start, uint size)
    {
        output.AddRange(System.Text.Encoding.Latin1.GetBytes(tag));
        AddBigEndian(output, start);
        AddBigEndian(output, size);
    }

    private static void AddBigEndian(List<byte> output, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        output.AddRange(bytes.ToArray());
    }
}
