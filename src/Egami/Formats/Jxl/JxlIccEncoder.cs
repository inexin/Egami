using System.Buffers.Binary;

namespace Egami.Formats.Jxl;

/// <summary>
/// Writes an ICC profile in the codestream's compressed form (E.4), the inverse of <see cref="JxlIcc"/>: the header predicted
/// from the profile size, the tag list as commands, tagged elements as type commands, shuffled and predicted runs, and the
/// stream entropy coded in its 41 byte contexts. The transform is libjxl 0.12.0's PredictICC (enc_icc_codec.cc, see
/// THIRD-PARTY-NOTICES); the stream is coded with Egami's ANS coder rather than libjxl's prefix codes.
/// </summary>
internal static class JxlIccEncoder
{
    private const int HeaderSize = 128;
    private const uint SizeLimit = uint.MaxValue >> 2;

    private static readonly string[] TagStrings =
        ["cprt", "wtpt", "bkpt", "rXYZ", "gXYZ", "bXYZ", "kXYZ", "rTRC", "gTRC", "bTRC", "kTRC", "chad", "desc", "chrm", "dmnd", "dmdd", "lumi"];

    private static readonly string[] TypeStrings = ["XYZ ", "desc", "text", "mluc", "para", "curv", "sf32", "gbd "];

    internal static void Write(JxlBitWriter w, byte[] icc)
    {
        byte[] stream = Predict(icc);
        w.WriteU64((ulong)stream.Length);
        List<JxlToken> tokens = new(stream.Length);
        for (int i = 0; i < stream.Length; i++)
        {
            tokens.Add(new JxlToken(JxlIcc.Context(i, i > 0 ? stream[i - 1] : 0, i > 1 ? stream[i - 2] : 0), stream[i]));
        }

        List<List<JxlToken>> streams = [tokens];
        JxlEntropyCode code = JxlEntropyCode.Write(w, new JxlHistogramParams { Rle = true }, 41, streams);
        code.WriteTokens(w, streams[0]);
    }

    /// <summary>Turns the profile into the stream E.4 decodes back: its size, the commands and the data (libjxl's PredictICC).</summary>
    internal static byte[] Predict(byte[] icc)
    {
        int size = icc.Length;
        if ((uint)size > SizeLimit)
        {
            throw new ArgumentException("The ICC profile is too large.", nameof(icc));
        }

        List<byte> result = [];
        List<byte> commands = [];
        List<byte> data = [];
        Varint(result, (ulong)size);

        // The header, less what its size and the bytes before predict.
        List<byte> header = [];
        for (int i = 0; i < HeaderSize && i < size; i++)
        {
            data.Add(unchecked((byte)(icc[i] - JxlIcc.PredictHeader(i, (uint)size, header))));
            header.Add(icc[i]);
        }

        if (size <= HeaderSize)
        {
            Varint(result, 0);
            result.AddRange(data);
            return [.. result];
        }

        List<uint> tagStarts = [];
        List<uint> tagSizes = [];
        Dictionary<long, int> tagMap = [];
        int pos = HeaderSize;
        if (pos + 4 <= size)
        {
            ulong numTags = Uint32(icc, pos);
            pos += 4;
            Varint(commands, numTags + 1);
            ulong prevStart = HeaderSize + (numTags * 12);
            uint prevSize = 0;
            for (ulong i = 0; i < numTags; i++)
            {
                if (pos + 12 > size)
                {
                    break;
                }

                string tag = Keyword(icc, pos);
                uint tagStart = Uint32(icc, pos + 4);
                uint tagSize = Uint32(icc, pos + 8);
                pos += 12;
                tagStarts.Add(tagStart);
                tagSizes.Add(tagSize);
                tagMap[tagStart] = tagStarts.Count - 1;
                int tagCode = 1;
                for (int j = 0; j < TagStrings.Length; j++)
                {
                    if (tag == TagStrings[j])
                    {
                        tagCode = j + 4;
                        break;
                    }
                }

                if (tag == "rTRC" && pos + 24 < size)
                {
                    bool ok = Keyword(icc, pos) == "gTRC" && Keyword(icc, pos + 12) == "bTRC";
                    for (int k = 0; ok && k < 8; k++)
                    {
                        ok = icc[pos - 8 + k] == icc[pos + 4 + k] && icc[pos - 8 + k] == icc[pos + 16 + k];
                    }

                    if (ok)
                    {
                        tagCode = 2;
                        pos += 24;
                        i += 2;
                    }
                }

                if (tag == "rXYZ" && pos + 24 < size)
                {
                    bool ok = Keyword(icc, pos) == "gXYZ" && Keyword(icc, pos + 12) == "bXYZ";
                    uint offsetG = Uint32(icc, pos + 4);
                    uint offsetB = Uint32(icc, pos + 16);
                    ok &= tagSize == 20 && Uint32(icc, pos + 8) == 20 && Uint32(icc, pos + 20) == 20;
                    ok &= offsetG == tagStart + 20 && offsetB == tagStart + 40;
                    if (ok)
                    {
                        tagCode = 3;
                        pos += 24;
                        i += 2;
                    }
                }

                int command = tagCode;
                if (prevStart + prevSize != tagStart)
                {
                    command |= 64;
                }

                uint predictedSize = tag is "rXYZ" or "gXYZ" or "bXYZ" or "kXYZ" or "wtpt" or "bkpt" or "lumi" ? 20 : prevSize;
                if (predictedSize != tagSize)
                {
                    command |= 128;
                }

                commands.Add((byte)command);
                if (tagCode == 1)
                {
                    data.AddRange(System.Text.Encoding.Latin1.GetBytes(tag));
                }

                if ((command & 64) != 0)
                {
                    Varint(commands, tagStart);
                }

                if ((command & 128) != 0)
                {
                    Varint(commands, tagSize);
                }

                prevStart = tagStart;
                prevSize = tagSize;
            }
        }

        // The end of the tag list.
        commands.Add(0);

        string current = string.Empty;
        long start = 0;
        long tagLength = 0;
        long clutStart = 0;
        bool Sane() => tagLength > 8 && tagLength < SizeLimit;
        int last0 = pos;
        while (pos <= size)
        {
            int last1 = pos;
            List<byte> commandsAdd = [];
            List<byte> dataAdd = [];
            if (pos > start + tagLength && tagLength < SizeLimit)
            {
                current = string.Empty;
            }

            if (tagMap.TryGetValue(pos, out int index) && pos + 4 <= size)
            {
                current = Keyword(icc, pos);
                start = tagStarts[index];
                tagLength = tagSizes[index];
                if (current == "mluc" && Sane() && pos + tagLength <= size && Uint32(icc, pos + 4) == 0)
                {
                    int num = (int)tagLength - 8;
                    commandsAdd.Add(16 + 3);
                    pos += 8;
                    commandsAdd.Add(2);
                    Varint(commandsAdd, (ulong)num);
                    int at = dataAdd.Count;
                    for (int i = 0; i < num; i++)
                    {
                        dataAdd.Add(icc[pos++]);
                    }

                    Unshuffle(dataAdd, at, num, 2);
                }

                if (current == "curv" && Sane() && pos + tagLength <= size && Uint32(icc, pos + 4) == 0)
                {
                    long num = tagLength - 8;
                    if (num > 16 && num < 1 << 28 && pos + num <= size && pos > 0)
                    {
                        commandsAdd.Add(16 + 5);
                        pos += 8;
                        commandsAdd.Add(4);
                        commandsAdd.Add((1 << 2) | (2 - 1));
                        Varint(commandsAdd, (ulong)num);
                        PredictAndShuffle(2, 2, 1, (int)num, icc, ref pos, dataAdd);
                    }
                }
            }

            if (current is "mAB " or "mBA ")
            {
                string sub = Keyword(icc, pos);
                if (pos + 12 < size && sub is "curv" or "vcgt" && Uint32(icc, pos + 4) == 0)
                {
                    long num = Uint32(icc, pos + 8) * 2L;
                    if (num > 16 && num < 1 << 28 && pos + 12 + num <= size)
                    {
                        pos += 12;
                        last1 = pos;
                        commandsAdd.Add(4);
                        commandsAdd.Add((1 << 2) | (2 - 1));
                        Varint(commandsAdd, (ulong)num);
                        PredictAndShuffle(2, 2, 1, (int)num, icc, ref pos, dataAdd);
                    }
                }

                if (pos == start + 24 && pos + 4 < size)
                {
                    clutStart = start + Uint32(icc, pos);
                }

                if (pos == clutStart && clutStart + 16 < size)
                {
                    int numi = icc[start + 8];
                    int numo = icc[start + 9];
                    int width = icc[clutStart + 16];
                    int stride = width * numo;
                    long num = (long)width * numo;
                    for (int i = 0; i < numi && clutStart + i < size; i++)
                    {
                        num *= icc[clutStart + i];
                    }

                    if (width is 1 or 2 && num > 64 && num < 1 << 28 && pos + num <= size && pos > stride * 4)
                    {
                        commandsAdd.Add(4);
                        byte flags = (byte)((1 << 2) | (width - 1) | (stride == width ? 0 : 16));
                        commandsAdd.Add(flags);
                        if ((flags & 16) != 0)
                        {
                            Varint(commandsAdd, (ulong)stride);
                        }

                        Varint(commandsAdd, (ulong)num);
                        PredictAndShuffle(stride, width, 1, (int)num, icc, ref pos, dataAdd);
                    }
                }
            }

            if (commandsAdd.Count == 0 && dataAdd.Count == 0 && current == "gbd " && Sane() && pos == start + 8 && pos + tagLength - 8 <= size && pos > 16)
            {
                int num = (int)tagLength - 8;
                commandsAdd.Add(4);
                commandsAdd.Add((0 << 2) | (4 - 1));
                Varint(commandsAdd, (ulong)num);
                PredictAndShuffle(4, 4, 0, num, icc, ref pos, dataAdd);
            }

            if (commandsAdd.Count == 0 && dataAdd.Count == 0 && pos + 20 <= size && Keyword(icc, pos) == "XYZ " && Uint32(icc, pos + 4) == 0)
            {
                commandsAdd.Add(10);
                pos += 8;
                for (int j = 0; j < 12; j++)
                {
                    dataAdd.Add(icc[pos++]);
                }
            }

            if (commandsAdd.Count == 0 && dataAdd.Count == 0 && pos + 8 <= size && Uint32(icc, pos + 4) == 0)
            {
                string sub = Keyword(icc, pos);
                for (int i = 0; i < TypeStrings.Length; i++)
                {
                    if (sub == TypeStrings[i])
                    {
                        commandsAdd.Add((byte)(16 + i));
                        pos += 8;
                        break;
                    }
                }
            }

            if (commandsAdd.Count != 0 || dataAdd.Count != 0 || pos == size)
            {
                if (last0 < last1)
                {
                    commands.Add(1);
                    Varint(commands, (ulong)(last1 - last0));
                    while (last0 < last1)
                    {
                        data.Add(icc[last0++]);
                    }
                }

                commands.AddRange(commandsAdd);
                data.AddRange(dataAdd);
                last0 = pos;
            }

            if (commandsAdd.Count == 0 && dataAdd.Count == 0)
            {
                pos++;
            }
        }

        Varint(result, (ulong)commands.Count);
        result.AddRange(commands);
        result.AddRange(data);
        return [.. result];
    }

    private static void Varint(List<byte> output, ulong value)
    {
        while (value > 127)
        {
            output.Add((byte)((value & 127) | 128));
            value >>= 7;
        }

        output.Add((byte)value);
    }

    private static uint Uint32(byte[] icc, long pos) => pos + 4 > icc.Length ? 0 : BinaryPrimitives.ReadUInt32BigEndian(icc.AsSpan((int)pos, 4));

    private static string Keyword(byte[] icc, long pos) => pos + 4 > icc.Length ? string.Empty : System.Text.Encoding.Latin1.GetString(icc, (int)pos, 4);

    /// <summary>Transposes width interleaved streams into one after the other (libjxl's Unshuffle).</summary>
    private static void Unshuffle(List<byte> data, int start, int size, int width)
    {
        int height = (size + width - 1) / width;
        byte[] result = new byte[size];
        int s = 0;
        int j = 0;
        for (int i = 0; i < size; i++)
        {
            result[j] = data[start + i];
            j += height;
            if (j >= size)
            {
                j = ++s;
            }
        }

        for (int i = 0; i < size; i++)
        {
            data[start + i] = result[i];
        }
    }

    /// <summary>Appends each byte less its linear prediction from the bytes a stride and more before, then unshuffles them.</summary>
    private static void PredictAndShuffle(int stride, int width, int order, int num, byte[] icc, ref int pos, List<byte> result)
    {
        if (pos == 0 || ((pos - 1) >> 2) < stride || pos < stride * 4 || pos + num > icc.Length)
        {
            throw new InvalidOperationException("An ICC prediction reached outside the profile.");
        }

        int begin = result.Count;
        for (int i = 0; i < num; i++)
        {
            result.Add(unchecked((byte)(icc[pos + i] - LinearPredict(icc, pos, i, stride, width, order))));
        }

        pos += num;
        if (width > 1)
        {
            Unshuffle(result, begin, num, width);
        }
    }

    private static byte LinearPredict(byte[] data, int start, int i, int stride, int width, int order)
    {
        int pos = start + i;
        if (width == 1)
        {
            return (byte)Predict(data[pos - stride], data[pos - (stride * 2)], data[pos - (stride * 3)], order);
        }

        if (width == 2)
        {
            int p = start + (i & ~1);
            uint p1 = (uint)((data[p - stride] << 8) + data[p - stride + 1]);
            uint p2 = (uint)((data[p - (stride * 2)] << 8) + data[p - (stride * 2) + 1]);
            uint p3 = (uint)((data[p - (stride * 3)] << 8) + data[p - (stride * 3) + 1]);
            uint pred = Predict(p1, p2, p3, order) & 0xFFFF;
            return (i & 1) != 0 ? (byte)(pred & 255) : (byte)((pred >> 8) & 255);
        }

        int q = start + (i & ~3);
        uint q1 = Bounded(data, pos, q - stride);
        uint q2 = Bounded(data, pos, q - (stride * 2));
        uint q3 = Bounded(data, pos, q - (stride * 3));
        uint value = Predict(q1, q2, q3, order);
        return (byte)((value >> ((3 - (i & 3)) * 8)) & 255);
    }

    /// <summary>A big-endian 32-bit value at <paramref name="offset"/>, or 0 when it reaches past <paramref name="limit"/>.</summary>
    private static uint Bounded(byte[] data, int limit, int offset) => offset < 0 || offset + 4 > limit ? 0 : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));

    private static uint Predict(uint p1, uint p2, uint p3, int order) => unchecked(order switch
    {
        0 => p1,
        1 => (2 * p1) - p2,
        _ => (3 * p1) - (3 * p2) + p3,
    });
}
