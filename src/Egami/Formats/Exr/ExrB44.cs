using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Formats.Exr;

/// <summary>
/// B44 and B44A: each half channel's lines in 4 × 4 blocks of 14 bytes (a start value and 6-bit differences scaled by a
/// shift), or 3 bytes when the block is flat (B44A); float and uint channels stored as they are, one channel after another.
/// Translated from OpenEXR's internal_b44.c (BSD-3-Clause; see THIRD-PARTY-NOTICES).
/// </summary>
internal static class ExrB44
{
    /// <summary>The table from a perceptually linear channel's stored values back to half: 8 × ln(x), as OpenEXR builds it.</summary>
    private static readonly Lazy<ushort[]> s_logTable = new(BuildLogTable);

    public static bool Decompress(in ExrBlock block, ReadOnlySpan<byte> source, Span<byte> destination)
    {
        ExrChannelInfo[] channels = block.Part.Channels;
        if (BitConverter.IsLittleEndian && Array.TrueForAll(channels, c => c.Type == ExrPixelType.Half && c.XSampling == 1 && c.YSampling == 1))
        {
            return DecompressHalves(block, source, destination);
        }

        ushort[] planarBuffer = ArrayPool<ushort>.Shared.Rent((destination.Length / 2) + 2);
        try
        {
            Span<byte> scratch = MemoryMarshal.AsBytes(planarBuffer.AsSpan(0, (destination.Length / 2) + 1))[..destination.Length];
            int inPos = 0, at = 0;
            Span<ushort> s = stackalloc ushort[16];
            foreach (ExrChannelInfo channel in channels)
            {
                int nx = ExrBlock.Sampled(block.X0, block.X1, channel.XSampling);
                int ny = ExrBlock.Sampled(block.Y0, block.Y1, channel.YSampling);
                int bytes = nx * ny * channel.Bytes;
                if (bytes == 0)
                {
                    continue;
                }

                if (channel.Type != ExrPixelType.Half)
                {
                    if (inPos + bytes > source.Length)
                    {
                        return false;
                    }

                    source.Slice(inPos, bytes).CopyTo(scratch[at..]);
                    inPos += bytes;
                    at += bytes;
                    continue;
                }

                Span<ushort> plane = MemoryMarshal.Cast<byte, ushort>(scratch.Slice(at, bytes));
                for (int y = 0; y < ny; y += 4)
                {
                    for (int x = 0; x < nx; x += 4)
                    {
                        if (inPos + 3 > source.Length)
                        {
                            return false;
                        }

                        if (source[inPos + 2] >= (13 << 2))
                        {
                            Unpack3(source.Slice(inPos, 3), s);
                            inPos += 3;
                        }
                        else
                        {
                            if (inPos + 14 > source.Length)
                            {
                                return false;
                            }

                            Unpack14(source.Slice(inPos, 14), s);
                            inPos += 14;
                        }

                        if (channel.Linear)
                        {
                            ushort[] log = s_logTable.Value;
                            for (int i = 0; i < 16; i++)
                            {
                                s[i] = log[s[i]];
                            }
                        }

                        int n = Math.Min(4, nx - x);
                        for (int r = 0; r < 4 && y + r < ny; r++)
                        {
                            s.Slice(r * 4, n).CopyTo(plane.Slice(((y + r) * nx) + x, n));
                        }
                    }
                }

                at += bytes;
            }

            // Back from one block per channel to one line after another.
            int[] starts = new int[channels.Length];
            int start = 0;
            for (int c = 0; c < channels.Length; c++)
            {
                starts[c] = start;
                start += ExrBlock.Sampled(block.X0, block.X1, channels[c].XSampling) * ExrBlock.Sampled(block.Y0, block.Y1, channels[c].YSampling) * channels[c].Bytes;
            }

            int outPos = 0;
            for (int y = block.Y0; y <= block.Y1; y++)
            {
                for (int c = 0; c < channels.Length; c++)
                {
                    ExrChannelInfo channel = channels[c];
                    if (y % channel.YSampling != 0)
                    {
                        continue;
                    }

                    int lineBytes = ExrBlock.Sampled(block.X0, block.X1, channel.XSampling) * channel.Bytes;
                    int row = (y - block.Y0) / channel.YSampling;
                    scratch.Slice(starts[c] + (row * lineBytes), lineBytes).CopyTo(destination[outPos..]);
                    outPos += lineBytes;
                }
            }

            if (!BitConverter.IsLittleEndian)
            {
                // The half planes were built in native order; only they need swapping, and the copy above kept them native.
                throw new PlatformNotSupportedException("B44 decoding assumes a little-endian machine.");
            }

            return outPos == destination.Length;
        }
        finally
        {
            ArrayPool<ushort>.Shared.Return(planarBuffer);
        }
    }

    /// <summary>A 14-byte block: s[0] as stored, then 6-bit differences down columns and across the first row.</summary>
    /// <summary>
    /// The usual case, every channel half at full resolution: each 4 × 4 block goes straight to its four lines of the output,
    /// four halves at a time.
    /// </summary>
    private static bool DecompressHalves(in ExrBlock block, ReadOnlySpan<byte> source, Span<byte> destination)
    {
        ExrChannelInfo[] channels = block.Part.Channels;
        int nx = block.X1 - block.X0 + 1, ny = block.Y1 - block.Y0 + 1;
        int lineBytes = nx * 2 * channels.Length;
        if ((long)lineBytes * ny != destination.Length)
        {
            return false;
        }

        ushort[]? log = null;
        Span<ushort> s = stackalloc ushort[16];
        ref byte output = ref MemoryMarshal.GetReference(destination);
        int inPos = 0;
        for (int c = 0; c < channels.Length; c++)
        {
            bool linear = channels[c].Linear;
            if (linear)
            {
                log ??= s_logTable.Value;
            }

            int channelOffset = c * nx * 2;
            for (int y = 0; y < ny; y += 4)
            {
                int rows = Math.Min(4, ny - y);
                for (int x = 0; x < nx; x += 4)
                {
                    if (inPos + 3 > source.Length)
                    {
                        return false;
                    }

                    if (source[inPos + 2] >= (13 << 2))
                    {
                        Unpack3(source.Slice(inPos, 3), s);
                        inPos += 3;
                    }
                    else
                    {
                        if (inPos + 14 > source.Length)
                        {
                            return false;
                        }

                        Unpack14(source.Slice(inPos, 14), s);
                        inPos += 14;
                    }

                    if (linear)
                    {
                        for (int i = 0; i < 16; i++)
                        {
                            s[i] = log![s[i]];
                        }
                    }

                    int n = Math.Min(4, nx - x);
                    for (int r = 0; r < rows; r++)
                    {
                        ref byte at = ref Unsafe.Add(ref output, ((y + r) * lineBytes) + channelOffset + (x * 2));
                        if (n == 4)
                        {
                            Unsafe.WriteUnaligned(ref at, Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<ushort, byte>(ref s[r * 4])));
                        }
                        else
                        {
                            MemoryMarshal.AsBytes(s.Slice(r * 4, n)).CopyTo(MemoryMarshal.CreateSpan(ref at, n * 2));
                        }
                    }
                }
            }
        }

        return true;
    }

    private static void Unpack14(ReadOnlySpan<byte> bytes, Span<ushort> s)
    {
        ref byte b = ref MemoryMarshal.GetReference(bytes);
        ref ushort o = ref MemoryMarshal.GetReference(s);
        int b2 = Unsafe.Add(ref b, 2), b3 = Unsafe.Add(ref b, 3), b4 = Unsafe.Add(ref b, 4), b5 = Unsafe.Add(ref b, 5), b6 = Unsafe.Add(ref b, 6);
        int b7 = Unsafe.Add(ref b, 7), b8 = Unsafe.Add(ref b, 8), b9 = Unsafe.Add(ref b, 9), b10 = Unsafe.Add(ref b, 10), b11 = Unsafe.Add(ref b, 11);
        int b12 = Unsafe.Add(ref b, 12), b13 = Unsafe.Add(ref b, 13);
        int shift = b2 >> 2;
        uint bias = (uint)(0x20 << shift);
        uint s0 = (uint)((b << 8) | Unsafe.Add(ref b, 1));
        uint s4 = Next(s0, ((b2 << 4) | (b3 >> 4)) & 0x3F);
        uint s8 = Next(s4, ((b3 << 2) | (b4 >> 6)) & 0x3F);
        uint s12 = Next(s8, b4 & 0x3F);
        uint s1 = Next(s0, b5 >> 2);
        uint s5 = Next(s4, ((b5 << 4) | (b6 >> 4)) & 0x3F);
        uint s9 = Next(s8, ((b6 << 2) | (b7 >> 6)) & 0x3F);
        uint s13 = Next(s12, b7 & 0x3F);
        uint s2 = Next(s1, b8 >> 2);
        uint s6 = Next(s5, ((b8 << 4) | (b9 >> 4)) & 0x3F);
        uint s10 = Next(s9, ((b9 << 2) | (b10 >> 6)) & 0x3F);
        uint s14 = Next(s13, b10 & 0x3F);
        uint s3 = Next(s2, b11 >> 2);
        uint s7 = Next(s6, ((b11 << 4) | (b12 >> 4)) & 0x3F);
        uint s11 = Next(s10, ((b12 << 2) | (b13 >> 6)) & 0x3F);
        uint s15 = Next(s14, b13 & 0x3F);
        Unsafe.Add(ref o, 0) = Sign(s0);
        Unsafe.Add(ref o, 1) = Sign(s1);
        Unsafe.Add(ref o, 2) = Sign(s2);
        Unsafe.Add(ref o, 3) = Sign(s3);
        Unsafe.Add(ref o, 4) = Sign(s4);
        Unsafe.Add(ref o, 5) = Sign(s5);
        Unsafe.Add(ref o, 6) = Sign(s6);
        Unsafe.Add(ref o, 7) = Sign(s7);
        Unsafe.Add(ref o, 8) = Sign(s8);
        Unsafe.Add(ref o, 9) = Sign(s9);
        Unsafe.Add(ref o, 10) = Sign(s10);
        Unsafe.Add(ref o, 11) = Sign(s11);
        Unsafe.Add(ref o, 12) = Sign(s12);
        Unsafe.Add(ref o, 13) = Sign(s13);
        Unsafe.Add(ref o, 14) = Sign(s14);
        Unsafe.Add(ref o, 15) = Sign(s15);

        uint Next(uint from, int d) => (ushort)(from + ((uint)d << shift) - bias);

        // From the ordered-magnitude form back to a half: set top bit means positive.
        static ushort Sign(uint v) => (v & 0x8000) != 0 ? (ushort)(v & 0x7FFF) : (ushort)~v;
    }

    private static void Unpack3(ReadOnlySpan<byte> b, Span<ushort> s)
    {
        ushort v = (ushort)((b[0] << 8) | b[1]);
        v = (v & 0x8000) != 0 ? (ushort)(v & 0x7FFF) : (ushort)~v;
        s.Fill(v);
    }

    private static ushort[] BuildLogTable()
    {
        ushort[] table = new ushort[65536];
        for (int i = 0; i < 65536; i++)
        {
            if ((i & 0x7C00) == 0x7C00 || i > 0x8000)
            {
                continue;
            }

            float f = (float)BitConverter.UInt16BitsToHalf((ushort)i);
            f = 8 * (float)Math.Log(f);
            table[i] = BitConverter.HalfToUInt16Bits((Half)f);
        }

        return table;
    }
}
