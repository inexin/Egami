using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Formats.Exr;

/// <summary>
/// PIZ: the 16-bit words of a block, mapped through a table of the values that occur, transformed by a 2D Haar wavelet per
/// channel, then Huffman coded. Translated from OpenEXR's internal_piz.c and internal_huf.c (BSD-3-Clause; see
/// THIRD-PARTY-NOTICES).
/// </summary>
internal static class ExrPiz
{
    private const int UShortRange = 1 << 16;
    private const int BitmapSize = UShortRange >> 3;

    public static bool Decompress(in ExrBlock block, ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.Length < 4)
        {
            return false;
        }

        int minNonZero = BinaryPrimitives.ReadUInt16LittleEndian(source);
        int maxNonZero = BinaryPrimitives.ReadUInt16LittleEndian(source[2..]);
        int pos = 4;
        if (maxNonZero >= BitmapSize)
        {
            return false;
        }

        ushort[] lut = ArrayPool<ushort>.Shared.Rent(UShortRange);
        byte[] bitmap = ArrayPool<byte>.Shared.Rent(BitmapSize);
        ushort[] words = ArrayPool<ushort>.Shared.Rent((destination.Length / 2) + 1);
        try
        {
            Array.Clear(bitmap, 0, BitmapSize);
            if (minNonZero <= maxNonZero)
            {
                int count = maxNonZero - minNonZero + 1;
                if (pos + count > source.Length)
                {
                    return false;
                }

                source.Slice(pos, count).CopyTo(bitmap.AsSpan(minNonZero));
                pos += count;
            }

            int maxValue = ReverseLut(bitmap, lut);
            if (pos + 4 > source.Length)
            {
                return false;
            }

            uint huffmanBytes = BinaryPrimitives.ReadUInt32LittleEndian(source[pos..]);
            pos += 4;
            if (huffmanBytes > source.Length - pos)
            {
                return false;
            }

            Span<ushort> data = words.AsSpan(0, destination.Length / 2);
            if (!ExrHuffman.Decode(source.Slice(pos, (int)huffmanBytes), data))
            {
                return false;
            }

            // The wavelet runs on each channel's block of lines, each 16-bit word of a sample on its own.
            ExrChannelInfo[] channels = block.Part.Channels;
            Span<int> starts = channels.Length <= 64 ? stackalloc int[channels.Length] : new int[channels.Length];
            int offset = 0;
            for (int c = 0; c < channels.Length; c++)
            {
                ExrChannelInfo channel = channels[c];
                int nx = ExrBlock.Sampled(block.X0, block.X1, channel.XSampling);
                int ny = ExrBlock.Sampled(block.Y0, block.Y1, channel.YSampling);
                int wcount = channel.Bytes / 2;
                starts[c] = offset;
                for (int j = 0; j < wcount; j++)
                {
                    Wav2DDecode(data[(offset + j)..], nx, wcount, ny, wcount * nx, maxValue);
                }

                offset += nx * ny * wcount;
            }

            ref ushort table = ref MemoryMarshal.GetArrayDataReference(lut);
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = Unsafe.Add(ref table, data[i]);
            }

            // From one block per channel back to one line after another.
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(data);
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
                    bytes.Slice((starts[c] * 2) + (row * lineBytes), lineBytes).CopyTo(destination[outPos..]);
                    outPos += lineBytes;
                }
            }

            if (!BitConverter.IsLittleEndian)
            {
                Span<ushort> swap = MemoryMarshal.Cast<byte, ushort>(destination);
                BinaryPrimitives.ReverseEndianness(swap, swap);
            }

            return outPos == destination.Length;
        }
        finally
        {
            ArrayPool<ushort>.Shared.Return(lut);
            ArrayPool<byte>.Shared.Return(bitmap);
            ArrayPool<ushort>.Shared.Return(words);
        }
    }

    /// <summary>
    /// Compresses a block (OpenEXR's line-interleaved layout) as PIZ into <paramref name="destination"/>; returns the bytes
    /// written, or -1 when the result would not be smaller than the block.
    /// </summary>
    public static int Compress(in ExrBlock block, ReadOnlySpan<byte> source, Span<byte> destination)
    {
        ExrChannelInfo[] channels = block.Part.Channels;
        int count16 = source.Length / 2;
        ushort[] words = ArrayPool<ushort>.Shared.Rent(count16 + 1);
        ushort[] lut = ArrayPool<ushort>.Shared.Rent(UShortRange);
        byte[] bitmap = ArrayPool<byte>.Shared.Rent(BitmapSize);
        try
        {
            // One block per channel, its lines in order.
            Span<ushort> data = words.AsSpan(0, count16);
            Span<int> starts = channels.Length <= 64 ? stackalloc int[channels.Length] : new int[channels.Length];
            int offset = 0;
            for (int c = 0; c < channels.Length; c++)
            {
                starts[c] = offset;
                offset += ExrBlock.Sampled(block.X0, block.X1, channels[c].XSampling) * ExrBlock.Sampled(block.Y0, block.Y1, channels[c].YSampling) * (channels[c].Bytes / 2);
            }

            Span<byte> planar = MemoryMarshal.AsBytes(data);
            int inPos = 0;
            for (int y = block.Y0; y <= block.Y1; y++)
            {
                for (int c = 0; c < channels.Length; c++)
                {
                    if (y % channels[c].YSampling != 0)
                    {
                        continue;
                    }

                    int lineBytes = ExrBlock.Sampled(block.X0, block.X1, channels[c].XSampling) * channels[c].Bytes;
                    int row = (y - block.Y0) / channels[c].YSampling;
                    source.Slice(inPos, lineBytes).CopyTo(planar[((starts[c] * 2) + (row * lineBytes))..]);
                    inPos += lineBytes;
                }
            }

            // The values that occur, as a bitmap; zero is always assumed.
            Array.Clear(bitmap, 0, BitmapSize);
            foreach (ushort v in data)
            {
                bitmap[v >> 3] |= (byte)(1 << (v & 7));
            }

            bitmap[0] &= 0xFE;
            int minNonZero = BitmapSize - 1, maxNonZero = 0;
            for (int i = 0; i < BitmapSize; i++)
            {
                if (bitmap[i] != 0)
                {
                    minNonZero = Math.Min(minNonZero, i);
                    maxNonZero = Math.Max(maxNonZero, i);
                }
            }

            int k = 0;
            for (int i = 0; i < UShortRange; i++)
            {
                lut[i] = i == 0 || (bitmap[i >> 3] & (1 << (i & 7))) != 0 ? (ushort)k++ : (ushort)0;
            }

            int maxValue = k - 1;
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = lut[data[i]];
            }

            if (destination.Length < 8 + BitmapSize)
            {
                return -1;
            }

            BinaryPrimitives.WriteUInt16LittleEndian(destination, (ushort)minNonZero);
            BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], (ushort)maxNonZero);
            int pos = 4;
            if (minNonZero <= maxNonZero)
            {
                bitmap.AsSpan(minNonZero, maxNonZero - minNonZero + 1).CopyTo(destination[pos..]);
                pos += maxNonZero - minNonZero + 1;
            }

            offset = 0;
            foreach (ExrChannelInfo channel in channels)
            {
                int nx = ExrBlock.Sampled(block.X0, block.X1, channel.XSampling);
                int ny = ExrBlock.Sampled(block.Y0, block.Y1, channel.YSampling);
                int wcount = channel.Bytes / 2;
                for (int j = 0; j < wcount; j++)
                {
                    Wav2DEncode(data[(offset + j)..], nx, wcount, ny, wcount * nx, maxValue);
                }

                offset += nx * ny * wcount;
            }

            int lengthAt = pos;
            pos += 4;
            int huffman = ExrHuffman.Encode(data, destination[pos..]);
            if (huffman < 0 || pos + huffman >= source.Length)
            {
                return -1;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(destination[lengthAt..], (uint)huffman);
            return pos + huffman;
        }
        finally
        {
            ArrayPool<ushort>.Shared.Return(words);
            ArrayPool<ushort>.Shared.Return(lut);
            ArrayPool<byte>.Shared.Return(bitmap);
        }
    }

    /// <summary>The forward 2D Haar wavelet in place: 14-bit lifting when every value is below 2^14, else 16-bit modular.</summary>
    private static void Wav2DEncode(Span<ushort> data, int nx, int ox, int ny, int oy, int mx)
    {
        bool w14 = mx < (1 << 14);
        int n = Math.Min(nx, ny);
        int p = 1, p2 = 2;
        while (p2 <= n)
        {
            long oy1 = (long)oy * p, oy2 = (long)oy * p2;
            int ox1 = ox * p, ox2 = ox * p2;
            long ey = (long)oy * (ny - p2);
            long py = 0;
            for (; py <= ey; py += oy2)
            {
                long px = py;
                long ex = py + ((long)ox * (nx - p2));
                for (; px <= ex; px += ox2)
                {
                    int i0 = (int)px, p01 = (int)(px + ox1), p10 = (int)(px + oy1), p11 = p10 + ox1;
                    ushort i00, i01, i10, i11;
                    if (w14)
                    {
                        Enc14(data[i0], data[p01], out i00, out i01);
                        Enc14(data[p10], data[p11], out i10, out i11);
                        Enc14(i00, i10, out data[i0], out data[p10]);
                        Enc14(i01, i11, out data[p01], out data[p11]);
                    }
                    else
                    {
                        Enc16(data[i0], data[p01], out i00, out i01);
                        Enc16(data[p10], data[p11], out i10, out i11);
                        Enc16(i00, i10, out data[i0], out data[p10]);
                        Enc16(i01, i11, out data[p01], out data[p11]);
                    }
                }

                if ((nx & p) != 0)
                {
                    int i0 = (int)px, p10 = (int)(px + oy1);
                    if (w14)
                    {
                        Enc14(data[i0], data[p10], out data[i0], out data[p10]);
                    }
                    else
                    {
                        Enc16(data[i0], data[p10], out data[i0], out data[p10]);
                    }
                }
            }

            if ((ny & p) != 0)
            {
                long px = py;
                long ex = py + ((long)ox * (nx - p2));
                for (; px <= ex; px += ox2)
                {
                    int i0 = (int)px, p01 = (int)(px + ox1);
                    if (w14)
                    {
                        Enc14(data[i0], data[p01], out data[i0], out data[p01]);
                    }
                    else
                    {
                        Enc16(data[i0], data[p01], out data[i0], out data[p01]);
                    }
                }
            }

            p = p2;
            p2 <<= 1;
        }
    }

    private static void Enc14(ushort a, ushort b, out ushort l, out ushort h)
    {
        short a1 = (short)a, b1 = (short)b;
        l = (ushort)(short)((a1 + b1) >> 1);
        h = (ushort)(short)(a1 - b1);
    }

    private static void Enc16(ushort a, ushort b, out ushort l, out ushort h)
    {
        const int AOffset = 1 << 15, MOffset = 1 << 15, ModMask = (1 << 16) - 1;
        int ao = (a + AOffset) & ModMask;
        int m = (ao + b) >> 1;
        int d = ao - b;
        if (d < 0)
        {
            m = (m + MOffset) & ModMask;
        }

        d &= ModMask;
        l = (ushort)m;
        h = (ushort)d;
    }

    /// <summary>Builds the table from stored index to value: 0, then every value the bitmap marks. Returns the largest index.</summary>
    private static int ReverseLut(byte[] bitmap, ushort[] lut)
    {
        int k = 0;
        lut[k++] = 0;
        for (int b = 0; b < BitmapSize; b++)
        {
            int bits = bitmap[b];
            if (b == 0)
            {
                bits &= 0xFE;
            }

            while (bits != 0)
            {
                int bit = System.Numerics.BitOperations.TrailingZeroCount(bits);
                lut[k++] = (ushort)((b << 3) | bit);
                bits &= bits - 1;
            }
        }

        int n = k - 1;
        Array.Clear(lut, k, UShortRange - k);
        return n;
    }

    /// <summary>Undoes the 2D Haar wavelet in place: 14-bit lifting when every value is below 2^14, else 16-bit modular.</summary>
    private static void Wav2DDecode(Span<ushort> data, int nx, int ox, int ny, int oy, int mx)
    {
        bool w14 = mx < (1 << 14);
        int n = Math.Min(nx, ny);
        int p = 1;
        while (p <= n)
        {
            p <<= 1;
        }

        p >>= 1;
        int p2 = p;
        p >>= 1;
        ref ushort d = ref MemoryMarshal.GetReference(data);
        while (p >= 1)
        {
            long oy1 = (long)oy * p;
            long oy2 = (long)oy * p2;
            int ox1 = ox * p;
            int ox2 = ox * p2;
            long ey = (long)oy * (ny - p2);
            long py = 0;
            for (; py <= ey; py += oy2)
            {
                long px = py;
                long ex = py + ((long)ox * (nx - p2));
                for (; px <= ex; px += ox2)
                {
                    long p01 = px + ox1;
                    long p10 = px + oy1;
                    long p11 = p10 + ox1;
                    if (w14)
                    {
                        Dec14x4(ref Unsafe.Add(ref d, (nint)px), ref Unsafe.Add(ref d, (nint)p01), ref Unsafe.Add(ref d, (nint)p10), ref Unsafe.Add(ref d, (nint)p11));
                    }
                    else
                    {
                        Dec16(Unsafe.Add(ref d, (nint)px), Unsafe.Add(ref d, (nint)p10), out ushort i00, out ushort i10);
                        Dec16(Unsafe.Add(ref d, (nint)p01), Unsafe.Add(ref d, (nint)p11), out ushort i01, out ushort i11);
                        Dec16(i00, i01, out Unsafe.Add(ref d, (nint)px), out Unsafe.Add(ref d, (nint)p01));
                        Dec16(i10, i11, out Unsafe.Add(ref d, (nint)p10), out Unsafe.Add(ref d, (nint)p11));
                    }
                }

                if ((nx & p) != 0)
                {
                    long p10 = px + oy1;
                    ushort i00;
                    if (w14)
                    {
                        Dec14(Unsafe.Add(ref d, (nint)px), Unsafe.Add(ref d, (nint)p10), out i00, out Unsafe.Add(ref d, (nint)p10));
                    }
                    else
                    {
                        Dec16(Unsafe.Add(ref d, (nint)px), Unsafe.Add(ref d, (nint)p10), out i00, out Unsafe.Add(ref d, (nint)p10));
                    }

                    Unsafe.Add(ref d, (nint)px) = i00;
                }
            }

            if ((ny & p) != 0)
            {
                long px = py;
                long ex = py + ((long)ox * (nx - p2));
                for (; px <= ex; px += ox2)
                {
                    long p01 = px + ox1;
                    ushort i00;
                    if (w14)
                    {
                        Dec14(Unsafe.Add(ref d, (nint)px), Unsafe.Add(ref d, (nint)p01), out i00, out Unsafe.Add(ref d, (nint)p01));
                    }
                    else
                    {
                        Dec16(Unsafe.Add(ref d, (nint)px), Unsafe.Add(ref d, (nint)p01), out i00, out Unsafe.Add(ref d, (nint)p01));
                    }

                    Unsafe.Add(ref d, (nint)px) = i00;
                }
            }

            p2 = p;
            p >>= 1;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Dec14x4(ref ushort px, ref ushort p01, ref ushort p10, ref ushort p11)
    {
        int a = (short)px, b = (short)p10, c = (short)p01, d = (short)p11;
        int i00 = a + (b & 1) + (b >> 1);
        int i10 = i00 - b;
        int i01 = c + (d & 1) + (d >> 1);
        int i11 = i01 - d;
        a = i00 + (i01 & 1) + (i01 >> 1);
        b = a - i01;
        c = i10 + (i11 & 1) + (i11 >> 1);
        d = c - i11;
        px = (ushort)a;
        p01 = (ushort)b;
        p10 = (ushort)c;
        p11 = (ushort)d;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Dec14(ushort l, ushort h, out ushort a, out ushort b)
    {
        int hi = (short)h;
        int li = (short)l;
        int ai = li + (hi & 1) + (hi >> 1);
        a = (ushort)(short)ai;
        b = (ushort)(short)(ai - hi);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Dec16(ushort l, ushort h, out ushort a, out ushort b)
    {
        const int AOffset = 1 << 15;
        const int ModMask = (1 << 16) - 1;
        int m = l;
        int d = h;
        int bb = (m - (d >> 1)) & ModMask;
        int aa = (d + bb - AOffset) & ModMask;
        b = (ushort)bb;
        a = (ushort)aa;
    }
}
