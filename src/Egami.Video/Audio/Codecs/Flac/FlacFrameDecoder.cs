using System.Buffers.Binary;

namespace Egami.Video.Audio.Codecs.Flac;

/// <summary>
/// Decodes one FLAC frame (RFC 9639 section 9): the header, a subframe per channel (constant, verbatim, fixed or LPC
/// prediction over Rice-coded residuals, with wasted bits), stereo decorrelation and the CRC-16 footer.
/// </summary>
internal sealed class FlacFrameDecoder
{
    private int[][] _channels = [];
    private long[] _wide = [];
    private int[] _residual = [];

    /// <summary>Gets the decoded samples per channel, valid up to the last frame's block size.</summary>
    public int[][] Channels => _channels;

    /// <summary>Decodes a frame; returns its header. Throws <see cref="CorruptDataException"/> on any broken field.</summary>
    /// <param name="frame">The frame's bytes, from its sync code.</param>
    /// <param name="info">The stream's STREAMINFO, for fields that refer to it.</param>
    /// <param name="checkCrc">Whether to check the footer's CRC-16.</param>
    /// <param name="crcOk">Whether the CRC-16 matched (true when not checked).</param>
    /// <param name="length">The frame's length in bytes, footer included.</param>
    public FlacFrameHeader Decode(ReadOnlySpan<byte> frame, FlacStreamInfo? info, bool checkCrc, out bool crcOk, out int length)
    {
        FlacFrameHeader header = FlacFrameHeader.Parse(frame, info) ?? throw Corrupt("The frame header is broken or its CRC-8 does not match.");
        int n = header.BlockSize;
        if (info is not null && (header.Channels != info.Channels || header.BitsPerSample != info.BitsPerSample))
        {
            throw Corrupt($"The frame has {header.Channels} channels of {header.BitsPerSample} bits; the stream has {info.Channels} of {info.BitsPerSample}.");
        }

        if (_channels.Length < header.Channels || _channels[0].Length < n)
        {
            int capacity = Math.Max(n, _channels.Length > 0 ? _channels[0].Length : 0);
            _channels = new int[Math.Max(header.Channels, _channels.Length)][];
            for (int c = 0; c < _channels.Length; c++)
            {
                _channels[c] = new int[capacity];
            }
        }

        if (_residual.Length < n)
        {
            _residual = new int[n];
        }

        FlacBitReader reader = new(frame[header.Length..]);
        int wideChannel = -1;
        for (int c = 0; c < header.Channels; c++)
        {
            int bits = header.BitsPerSample + (header.Assignment switch
            {
                FlacChannels.LeftSide or FlacChannels.MidSide => c == 1 ? 1 : 0,
                FlacChannels.SideRight => c == 0 ? 1 : 0,
                _ => 0,
            });
            if (bits > 32)
            {
                if (_wide.Length < n)
                {
                    _wide = new long[n];
                }

                DecodeWideSubframe(ref reader, _wide.AsSpan(0, n), bits);
                wideChannel = c;
            }
            else
            {
                DecodeSubframe(ref reader, _channels[c].AsSpan(0, n), bits);
            }
        }

        reader.AlignToByte();
        int end = header.Length + reader.BytePosition;
        if (end + 2 > frame.Length)
        {
            throw Corrupt("The frame ends before its CRC-16.");
        }

        crcOk = !checkCrc || FlacCrc.Crc16(frame[..end]) == BinaryPrimitives.ReadUInt16BigEndian(frame[end..]);
        length = end + 2;
        Decorrelate(header, n, wideChannel);
        return header;
    }

    private static CorruptDataException Corrupt(string problem) => new("FLAC", -1, problem);

    private void Decorrelate(FlacFrameHeader header, int n, int wideChannel)
    {
        Span<int> a = _channels[0].AsSpan(0, n);
        Span<int> b = _channels.Length > 1 ? _channels[1].AsSpan(0, n) : default;
        ReadOnlySpan<long> wide = _wide.AsSpan(0, wideChannel >= 0 ? n : 0);
        switch (header.Assignment)
        {
            case FlacChannels.LeftSide:
                for (int i = 0; i < n; i++)
                {
                    b[i] = (int)(a[i] - (wideChannel == 1 ? wide[i] : b[i]));
                }

                break;
            case FlacChannels.SideRight:
                for (int i = 0; i < n; i++)
                {
                    a[i] = (int)((wideChannel == 0 ? wide[i] : a[i]) + b[i]);
                }

                break;
            case FlacChannels.MidSide:
                for (int i = 0; i < n; i++)
                {
                    long side = wideChannel == 1 ? wide[i] : b[i];
                    long mid = ((long)a[i] << 1) | (side & 1);
                    a[i] = (int)((mid + side) >> 1);
                    b[i] = (int)((mid - side) >> 1);
                }

                break;
        }
    }

    private void DecodeSubframe(ref FlacBitReader reader, Span<int> samples, int bits)
    {
        (int type, int wasted) = SubframeHeader(ref reader);
        bits -= wasted;
        if (bits < 1)
        {
            throw Corrupt("A subframe has more wasted bits than sample bits.");
        }

        int n = samples.Length;
        switch (type)
        {
            case 0:
                samples.Fill(reader.ReadSigned(bits));
                break;
            case 1:
                for (int i = 0; i < n; i++)
                {
                    samples[i] = reader.ReadSigned(bits);
                }

                break;
            case >= 8 and <= 12:
                {
                    int order = type - 8;
                    WarmUp(ref reader, samples, order, bits);
                    Residual(ref reader, samples, order);
                    RestoreFixed(samples, order);
                    break;
                }

            case >= 32:
                {
                    int order = type - 31;
                    WarmUp(ref reader, samples, order, bits);
                    int precision = (int)reader.ReadBits(4) + 1;
                    int shift = reader.ReadSigned(5);
                    if (precision == 16 || shift < 0)
                    {
                        throw Corrupt(precision == 16 ? "An LPC subframe has the invalid precision code 15." : "An LPC subframe has a negative shift.");
                    }

                    Span<int> coefficients = stackalloc int[32];
                    for (int i = 0; i < order; i++)
                    {
                        coefficients[i] = reader.ReadSigned(precision);
                    }

                    Residual(ref reader, samples, order);
                    RestoreLpc(samples, coefficients[..order], shift);
                    break;
                }

            default:
                throw Corrupt($"Subframe type {type} is reserved.");
        }

        if (wasted > 0)
        {
            for (int i = 0; i < n; i++)
            {
                samples[i] <<= wasted;
            }
        }
    }

    private void DecodeWideSubframe(ref FlacBitReader reader, Span<long> samples, int bits)
    {
        (int type, int wasted) = SubframeHeader(ref reader);
        bits -= wasted;
        int n = samples.Length;
        switch (type)
        {
            case 0:
                samples.Fill(reader.ReadSigned64(bits));
                break;
            case 1:
                for (int i = 0; i < n; i++)
                {
                    samples[i] = reader.ReadSigned64(bits);
                }

                break;
            case >= 8 and <= 12:
            case >= 32:
                {
                    bool lpc = type >= 32;
                    int order = lpc ? type - 31 : type - 8;
                    if (order > n)
                    {
                        throw Corrupt($"The predictor order {order} is larger than the block of {n} samples.");
                    }

                    for (int i = 0; i < order; i++)
                    {
                        samples[i] = reader.ReadSigned64(bits);
                    }

                    Span<int> coefficients = stackalloc int[32];
                    int shift = 0;
                    if (lpc)
                    {
                        int precision = (int)reader.ReadBits(4) + 1;
                        shift = reader.ReadSigned(5);
                        if (precision == 16 || shift < 0)
                        {
                            throw Corrupt("An LPC subframe has an invalid precision or shift.");
                        }

                        for (int i = 0; i < order; i++)
                        {
                            coefficients[i] = reader.ReadSigned(precision);
                        }
                    }

                    Span<int> residual = _residual.AsSpan(0, n);
                    residual[..order].Clear();
                    Residual(ref reader, residual, order);
                    for (int i = order; i < n; i++)
                    {
                        long prediction = 0;
                        if (lpc)
                        {
                            for (int j = 0; j < order; j++)
                            {
                                prediction += (long)coefficients[j] * samples[i - 1 - j];
                            }

                            prediction >>= shift;
                        }
                        else
                        {
                            prediction = order switch
                            {
                                0 => 0,
                                1 => samples[i - 1],
                                2 => (2 * samples[i - 1]) - samples[i - 2],
                                3 => (3 * samples[i - 1]) - (3 * samples[i - 2]) + samples[i - 3],
                                _ => (4 * samples[i - 1]) - (6 * samples[i - 2]) + (4 * samples[i - 3]) - samples[i - 4],
                            };
                        }

                        samples[i] = residual[i] + prediction;
                    }

                    break;
                }

            default:
                throw Corrupt($"Subframe type {type} is reserved.");
        }

        if (wasted > 0)
        {
            for (int i = 0; i < n; i++)
            {
                samples[i] <<= wasted;
            }
        }
    }

    private static (int Type, int Wasted) SubframeHeader(ref FlacBitReader reader)
    {
        if (reader.ReadBit())
        {
            throw Corrupt("A subframe's padding bit is set.");
        }

        int type = (int)reader.ReadBits(6);
        int wasted = reader.ReadBit() ? reader.ReadUnary() + 1 : 0;
        return (type, wasted);
    }

    private static void WarmUp(ref FlacBitReader reader, Span<int> samples, int order, int bits)
    {
        if (order > samples.Length)
        {
            throw Corrupt($"The predictor order {order} is larger than the block of {samples.Length} samples.");
        }

        for (int i = 0; i < order; i++)
        {
            samples[i] = reader.ReadSigned(bits);
        }
    }

    /// <summary>Reads the Rice-coded residual into samples[order..].</summary>
    private static void Residual(ref FlacBitReader reader, Span<int> samples, int order)
    {
        int method = (int)reader.ReadBits(2);
        if (method > 1)
        {
            throw Corrupt($"Residual coding method {method} is reserved.");
        }

        int parameterBits = method == 0 ? 4 : 5;
        int escape = (1 << parameterBits) - 1;
        int partitionOrder = (int)reader.ReadBits(4);
        int partitions = 1 << partitionOrder;
        int n = samples.Length;
        if ((n & (partitions - 1)) != 0 || (n >> partitionOrder) < order)
        {
            throw Corrupt($"Partition order {partitionOrder} does not fit a block of {n} samples with predictor order {order}.");
        }

        int position = order;
        for (int p = 0; p < partitions; p++)
        {
            int count = (n >> partitionOrder) - (p == 0 ? order : 0);
            int parameter = (int)reader.ReadBits(parameterBits);
            Span<int> part = samples.Slice(position, count);
            if (parameter == escape)
            {
                int bits = (int)reader.ReadBits(5);
                for (int i = 0; i < count; i++)
                {
                    part[i] = reader.ReadSigned(bits);
                }
            }
            else
            {
                reader.ReadRice(part, parameter);
            }

            position += count;
        }
    }

    private static void RestoreFixed(Span<int> s, int order)
    {
        int n = s.Length;
        switch (order)
        {
            case 1:
                for (int i = 1; i < n; i++)
                {
                    s[i] += s[i - 1];
                }

                break;
            case 2:
                for (int i = 2; i < n; i++)
                {
                    s[i] = (int)(s[i] + (2L * s[i - 1]) - s[i - 2]);
                }

                break;
            case 3:
                for (int i = 3; i < n; i++)
                {
                    s[i] = (int)(s[i] + (3L * s[i - 1]) - (3L * s[i - 2]) + s[i - 3]);
                }

                break;
            case 4:
                for (int i = 4; i < n; i++)
                {
                    s[i] = (int)(s[i] + (4L * s[i - 1]) - (6L * s[i - 2]) + (4L * s[i - 3]) - s[i - 4]);
                }

                break;
        }
    }

    private static void RestoreLpc(Span<int> s, ReadOnlySpan<int> c, int shift)
    {
        int order = c.Length;
        int n = s.Length;
        if (order == 8)
        {
            // Order 8 is the most common at the default level; spelled out it avoids the inner loop.
            int c0 = c[0], c1 = c[1], c2 = c[2], c3 = c[3], c4 = c[4], c5 = c[5], c6 = c[6], c7 = c[7];
            for (int i = 8; i < n; i++)
            {
                long sum = ((long)c0 * s[i - 1]) + ((long)c1 * s[i - 2]) + ((long)c2 * s[i - 3]) + ((long)c3 * s[i - 4])
                    + ((long)c4 * s[i - 5]) + ((long)c5 * s[i - 6]) + ((long)c6 * s[i - 7]) + ((long)c7 * s[i - 8]);
                s[i] += (int)(sum >> shift);
            }

            return;
        }

        for (int i = order; i < n; i++)
        {
            long sum = 0;
            for (int j = 0; j < order; j++)
            {
                sum += (long)c[j] * s[i - 1 - j];
            }

            s[i] += (int)(sum >> shift);
        }
    }
}
