using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Egami.Video.Audio.Codecs.Pcm;

/// <summary>Packs and unpacks interleaved PCM bytes into interleaved frame samples.</summary>
internal static class PcmSamples
{
    /// <summary>Returns the frame format that holds samples of a stored PCM type.</summary>
    public static SampleFormat FormatFor(bool isFloat, int bits) => isFloat
        ? bits == 64 ? SampleFormat.F64 : SampleFormat.F32
        : bits switch
        {
            <= 8 => SampleFormat.U8,
            <= 16 => SampleFormat.S16,
            <= 32 => SampleFormat.S32,
            _ => SampleFormat.S64,
        };

    /// <summary>Unpacks stored samples into an interleaved frame of <see cref="FormatFor"/>'s format.</summary>
    public static void Unpack(ReadOnlySpan<byte> data, bool isFloat, bool bigEndian, int bits, AudioFrame frame)
    {
        int bytes = (bits + 7) / 8;
        int count = frame.SampleCount * frame.ChannelCount;
        data = data[..(count * bytes)];
        if (isFloat)
        {
            if (bits == 64)
            {
                Span<double> d = frame.GetSpan<double>();
                if (bigEndian)
                {
                    for (int i = 0; i < count; i++)
                    {
                        d[i] = BinaryPrimitives.ReadDoubleBigEndian(data[(i * 8)..]);
                    }
                }
                else
                {
                    data.CopyTo(MemoryMarshal.AsBytes(d));
                }
            }
            else
            {
                Span<float> d = frame.GetSpan<float>();
                if (bigEndian)
                {
                    for (int i = 0; i < count; i++)
                    {
                        d[i] = BinaryPrimitives.ReadSingleBigEndian(data[(i * 4)..]);
                    }
                }
                else
                {
                    data.CopyTo(MemoryMarshal.AsBytes(d));
                }
            }

            return;
        }

        switch (bytes)
        {
            case 1:
                {
                    Span<byte> d = frame.GetSpan<byte>();
                    if (bigEndian)
                    {
                        // Big-endian 8-bit PCM (QuickTime twos, Matroska A_PCM/INT/BIG) is signed.
                        for (int i = 0; i < count; i++)
                        {
                            d[i] = (byte)(data[i] ^ 0x80);
                        }
                    }
                    else
                    {
                        data.CopyTo(d);
                    }

                    break;
                }

            case 2:
                {
                    Span<short> d = frame.GetSpan<short>();
                    if (bigEndian)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            d[i] = BinaryPrimitives.ReadInt16BigEndian(data[(i * 2)..]);
                        }
                    }
                    else
                    {
                        data.CopyTo(MemoryMarshal.AsBytes(d));
                    }

                    break;
                }

            case 3:
                {
                    Span<int> d = frame.GetSpan<int>();
                    for (int i = 0, j = 0; i < count; i++, j += 3)
                    {
                        int v = bigEndian ? (data[j] << 16) | (data[j + 1] << 8) | data[j + 2] : data[j] | (data[j + 1] << 8) | (data[j + 2] << 16);
                        d[i] = (v << 8) >> 8;
                    }

                    break;
                }

            case 4:
                {
                    Span<int> d = frame.GetSpan<int>();
                    if (bigEndian)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            d[i] = BinaryPrimitives.ReadInt32BigEndian(data[(i * 4)..]);
                        }
                    }
                    else
                    {
                        data.CopyTo(MemoryMarshal.AsBytes(d));
                    }

                    break;
                }

            default:
                {
                    // 5 to 8 bytes: sign-extended into 64 bits.
                    Span<long> d = frame.GetSpan<long>();
                    int shift = 64 - (bytes * 8);
                    for (int i = 0; i < count; i++)
                    {
                        ReadOnlySpan<byte> s = data.Slice(i * bytes, bytes);
                        long v = 0;
                        for (int k = 0; k < bytes; k++)
                        {
                            v |= (long)s[bigEndian ? bytes - 1 - k : k] << (8 * k);
                        }

                        d[i] = (v << shift) >> shift;
                    }

                    break;
                }
        }
    }

    /// <summary>Packs an interleaved frame of <see cref="FormatFor"/>'s format into stored bytes.</summary>
    public static void Pack(AudioFrame frame, bool isFloat, bool bigEndian, int bits, Span<byte> data)
    {
        int bytes = (bits + 7) / 8;
        int count = frame.SampleCount * frame.ChannelCount;
        data = data[..(count * bytes)];
        if (isFloat)
        {
            if (bits == 64)
            {
                ReadOnlySpan<double> s = frame.AsSpan<double>();
                if (bigEndian)
                {
                    for (int i = 0; i < count; i++)
                    {
                        BinaryPrimitives.WriteDoubleBigEndian(data[(i * 8)..], s[i]);
                    }
                }
                else
                {
                    MemoryMarshal.AsBytes(s).CopyTo(data);
                }
            }
            else
            {
                ReadOnlySpan<float> s = frame.AsSpan<float>();
                if (bigEndian)
                {
                    for (int i = 0; i < count; i++)
                    {
                        BinaryPrimitives.WriteSingleBigEndian(data[(i * 4)..], s[i]);
                    }
                }
                else
                {
                    MemoryMarshal.AsBytes(s).CopyTo(data);
                }
            }

            return;
        }

        switch (bytes)
        {
            case 1:
                {
                    ReadOnlySpan<byte> s = frame.AsSpan<byte>();
                    if (bigEndian)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            data[i] = (byte)(s[i] ^ 0x80);
                        }
                    }
                    else
                    {
                        s.CopyTo(data);
                    }

                    break;
                }

            case 2:
                {
                    ReadOnlySpan<short> s = frame.AsSpan<short>();
                    if (bigEndian)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            BinaryPrimitives.WriteInt16BigEndian(data[(i * 2)..], s[i]);
                        }
                    }
                    else
                    {
                        MemoryMarshal.AsBytes(s).CopyTo(data);
                    }

                    break;
                }

            case 3:
                {
                    ReadOnlySpan<int> s = frame.AsSpan<int>();
                    for (int i = 0, j = 0; i < count; i++, j += 3)
                    {
                        int v = s[i];
                        data[j + (bigEndian ? 2 : 0)] = (byte)v;
                        data[j + 1] = (byte)(v >> 8);
                        data[j + (bigEndian ? 0 : 2)] = (byte)(v >> 16);
                    }

                    break;
                }

            case 4:
                {
                    ReadOnlySpan<int> s = frame.AsSpan<int>();
                    if (bigEndian)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            BinaryPrimitives.WriteInt32BigEndian(data[(i * 4)..], s[i]);
                        }
                    }
                    else
                    {
                        MemoryMarshal.AsBytes(s).CopyTo(data);
                    }

                    break;
                }

            default:
                throw new NotSupportedException($"PCM of {bits} bits cannot be written.");
        }
    }
}
