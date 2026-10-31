using System.Buffers;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;

using Egami.Compression.Zstd;

namespace Egami.Formats.Tiff;

/// <summary>
/// Decodes one strip or tile of a page into its raw samples: decompressed, bits put back in order (FillOrder 2), multi-byte
/// samples in little-endian order whatever the file's, and the predictor undone. JPEG and WebP units go through their own
/// readers instead (<see cref="TiffRows{TNatural}"/>).
/// </summary>
internal sealed class TiffDecoder : IDisposable
{
    private readonly TiffPage _page;
    private byte[]? _input;

    public TiffDecoder(TiffPage page)
    {
        _page = page;
    }

    /// <summary>
    /// Decodes unit <paramref name="unit"/>, <paramref name="rows"/> rows high, into <paramref name="output"/>; returns the bytes it
    /// filled. Whatever it did not fill is zeroed.
    /// </summary>
    public int Decode(long unit, int rows, Span<byte> output)
    {
        TiffPage page = _page;
        (long offset, long count) = page.Unit(unit);
        int expected = page.Subsampled ? output.Length : (int)Math.Min(output.Length, page.UnitRowBytes * rows);
        if (count <= 0 || unit >= page.UnitCount)
        {
            output.Clear();
            return 0;
        }

        if (count > int.MaxValue)
        {
            throw page.File.Input.Corrupt(offset, $"A strip or tile of {count} bytes is beyond what Egami reads.");
        }

        ReadOnlySpan<byte> packed = Fetch(offset, (int)count, out byte[]? reversed);
        int written;
        Span<byte> target = output[..expected];
        try
        {
            written = page.Compression switch
            {
                TiffCodes.None => Copy(packed, target),
                TiffCodes.Lzw => TiffLzw.Decode(packed, target),
                TiffCodes.Deflate or TiffCodes.AdobeDeflate => Inflate(packed, target),
                TiffCodes.Zstd => ZstdDecoder.DecodePartial(packed, target),
                TiffCodes.PackBits => PackBits.Decode(packed, target, out _),
                TiffCodes.ThunderScan => ThunderScan(packed, target, page.UnitWidth, rows),
                TiffCodes.CcittRle or TiffCodes.CcittGroup3 or TiffCodes.CcittGroup4 or TiffCodes.CcittRleWord
                    => Ccitt.CcittDecoder.DecodeTiff(packed, target, page.UnitWidth, rows, page.Compression, page.FaxOptions),
                TiffCodes.SgiLog or TiffCodes.SgiLog24
                    => SgiLog.DecodeStrip(packed, target, page.UnitWidth, rows, page.Photometric == 32844, page.Compression == TiffCodes.SgiLog24),
                _ => throw new NotSupportedException($"TIFF: compression {page.Compression} is not read here."),
            };
        }
        finally
        {
            if (reversed is not null)
            {
                ArrayPool<byte>.Shared.Return(reversed);
            }
        }

        output[written..].Clear();
        if (page.Subsampled || page.Compression is TiffCodes.SgiLog or TiffCodes.SgiLog24)
        {
            return written;
        }

        int rowBytes = (int)page.UnitRowBytes;
        int whole = rowBytes == 0 ? 0 : Math.Min(rows, (written + rowBytes - 1) / rowBytes);
        Span<byte> decoded = output[..(whole * rowBytes)];
        if (page.Predictor == 3)
        {
            // The floating-point predictor's output is in the host's order already.
            for (int r = 0; r < whole; r++)
            {
                UndoFloatPredictor(decoded.Slice(r * rowBytes, rowBytes), page.UnitSamples, page.Bits / 8);
            }

            return written;
        }

        if (page.File.BigEndian && page.Bits is 16 or 24 or 32 or 64)
        {
            if (page.Bits == 24)
            {
                for (int i = 0; i + 3 <= decoded.Length; i += 3)
                {
                    (decoded[i], decoded[i + 2]) = (decoded[i + 2], decoded[i]);
                }
            }
            else
            {
                TiffFile.Swap(decoded, page.Bits / 8);
            }
        }

        if (page.Predictor == 2)
        {
            for (int r = 0; r < whole; r++)
            {
                UndoHorizontal(decoded.Slice(r * rowBytes, rowBytes), page.UnitSamples, page.Bits);
            }
        }

        return written;
    }

    /// <summary>Returns a unit's compressed bytes, from the file's memory when it is there; FillOrder 2 bits are reversed into a copy.</summary>
    private ReadOnlySpan<byte> Fetch(long offset, int count, out byte[]? reversed)
    {
        reversed = null;
        TiffSource source = _page.File.Source;
        ReadOnlySpan<byte> packed;
        if (source.InMemory)
        {
            packed = source.Slice(offset, count, default);
        }
        else
        {
            if (_input is null || _input.Length < count)
            {
                if (_input is not null)
                {
                    ArrayPool<byte>.Shared.Return(_input);
                }

                _input = ArrayPool<byte>.Shared.Rent(count);
            }

            packed = _input.AsSpan(0, source.Read(offset, _input.AsSpan(0, count)));
        }

        if (!_page.Reversed || _page.Compression is TiffCodes.Jpeg or TiffCodes.OldJpeg)
        {
            // FillOrder 2 reverses every codec's bytes, as libtiff does, except JPEG's, whose streams are always in order.
            return packed;
        }

        reversed = ArrayPool<byte>.Shared.Rent(packed.Length);
        Span<byte> copy = reversed.AsSpan(0, packed.Length);
        for (int i = 0; i < packed.Length; i++)
        {
            copy[i] = Reverse[packed[i]];
        }

        return copy;
    }

    private static readonly byte[] Reverse = [.. Enumerable.Range(0, 256).Select(b => (byte)(((ulong)b * 0x0202020202UL & 0x010884422010UL) % 1023))];

    private static int Copy(ReadOnlySpan<byte> from, Span<byte> to)
    {
        int n = Math.Min(from.Length, to.Length);
        from[..n].CopyTo(to);
        return n;
    }

    /// <summary>Inflates zlib data, as Adobe's TIFF note and code 8 have it; damaged data keeps what came before the damage.</summary>
    internal static unsafe int Inflate(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (input.IsEmpty)
        {
            // An empty strip inflates to nothing (and pins to a null pointer, which a stream refuses).
            return 0;
        }

        fixed (byte* pointer = input)
        {
            using UnmanagedMemoryStream raw = new(pointer, input.Length);
            using ZLibStream zlib = new(raw, CompressionMode.Decompress);
            int written = 0;
            try
            {
                while (written < output.Length)
                {
                    int n = zlib.Read(output[written..]);
                    if (n == 0)
                    {
                        break;
                    }

                    written += n;
                }
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                // A damaged stream, or a header asking for a preset dictionary: what was inflated stays.
            }

            return written;
        }
    }

    /// <summary>
    /// ThunderScan's 4-bit compression (Thunderware's Macintosh scanner software), row by row, each from pixel 0. A byte's top two
    /// bits say what it is: 00 the last pixel again, as many times as its low six bits; 01 three 2-bit deltas; 10 two 3-bit
    /// deltas; 11 a pixel in its low four bits. Moved from Lars's ClassicMac (TiffFile.cs, MIT).
    /// </summary>
    private static int ThunderScan(ReadOnlySpan<byte> input, Span<byte> output, int width, int height)
    {
        ReadOnlySpan<int> twoBit = [0, 1, 0, -1];
        ReadOnlySpan<int> threeBit = [0, 1, 2, 3, 0, -3, -2, -1];
        int rowBytes = (width + 1) / 2;
        int at = 0;
        int rows = 0;
        for (; rows < height && at < input.Length; rows++)
        {
            Span<byte> row = output.Slice(rows * rowBytes, rowBytes);
            row.Clear();
            int last = 0;
            int count = 0;
            while (count < width && at < input.Length)
            {
                int n = input[at++];
                switch (n & 0xC0)
                {
                    case 0x00:
                        // A run is written only when it ends inside the row, as libtiff does; one that would pass the end
                        // writes nothing and ends the row.
                        int run = n & 0x3F;
                        if ((count & 1) != 0 && run > 0)
                        {
                            Set(row, last, ref last, ref count, width);
                            run--;
                        }

                        if (count + run < width)
                        {
                            for (int k = 0; k < run; k++)
                            {
                                Set(row, last, ref last, ref count, width);
                            }
                        }
                        else
                        {
                            count += run;
                        }

                        break;
                    case 0x40:
                        for (int shift = 4; shift >= 0; shift -= 2)
                        {
                            int delta = (n >> shift) & 3;
                            if (delta != 2)
                            {
                                Set(row, last + twoBit[delta], ref last, ref count, width);
                            }
                        }

                        break;
                    case 0x80:
                        for (int shift = 3; shift >= 0; shift -= 3)
                        {
                            int delta = (n >> shift) & 7;
                            if (delta != 4)
                            {
                                Set(row, last + threeBit[delta], ref last, ref count, width);
                            }
                        }

                        break;
                    default:
                        Set(row, n, ref last, ref count, width);
                        break;
                }
            }

            if (count < width)
            {
                break;
            }
        }

        return rows * rowBytes;

        static void Set(Span<byte> row, int value, ref int last, ref int count, int width)
        {
            last = value & 0xF;
            if (count < width)
            {
                row[count >> 1] |= (byte)((count & 1) == 0 ? last << 4 : last);
            }

            count++;
        }
    }

    /// <summary>The horizontal predictor (TIFF 6.0 section 14): each sample but a row's first is the difference from the one a pixel before it.</summary>
    internal static void UndoHorizontal(Span<byte> row, int samples, int bits)
    {
        switch (bits)
        {
            case 8:
                Accumulate(row, samples);
                break;
            case 16:
                Accumulate(MemoryMarshal.Cast<byte, ushort>(row), samples);
                break;
            case 32:
                Accumulate(MemoryMarshal.Cast<byte, uint>(row), samples);
                break;
            default:
                Accumulate(MemoryMarshal.Cast<byte, ulong>(row), samples);
                break;
        }
    }

    private static void Accumulate<T>(Span<T> row, int samples)
        where T : unmanaged, IBinaryInteger<T>
    {
        switch (samples)
        {
            case 1:
                T last = row.Length > 0 ? row[0] : T.Zero;
                for (int i = 1; i < row.Length; i++)
                {
                    last += row[i];
                    row[i] = last;
                }

                break;
            case 3:
                if (row.Length < 3)
                {
                    break;
                }

                T r = row[0], g = row[1], b = row[2];
                for (int i = 3; i + 2 < row.Length; i += 3)
                {
                    r += row[i];
                    g += row[i + 1];
                    b += row[i + 2];
                    row[i] = r;
                    row[i + 1] = g;
                    row[i + 2] = b;
                }

                break;
            case 4:
                if (row.Length < 4)
                {
                    break;
                }

                T c0 = row[0], c1 = row[1], c2 = row[2], c3 = row[3];
                for (int i = 4; i + 3 < row.Length; i += 4)
                {
                    c0 += row[i];
                    c1 += row[i + 1];
                    c2 += row[i + 2];
                    c3 += row[i + 3];
                    row[i] = c0;
                    row[i + 1] = c1;
                    row[i + 2] = c2;
                    row[i + 3] = c3;
                }

                break;
            default:
                for (int i = samples; i < row.Length; i++)
                {
                    row[i] += row[i - samples];
                }

                break;
        }
    }

    /// <summary>
    /// The floating-point predictor (Adobe Photoshop TIFF Technical Note 3): each byte the difference from the one a pixel (a sample
    /// count) before it, then the row's samples gathered from byte planes, most significant first, into little-endian values.
    /// </summary>
    internal static void UndoFloatPredictor(Span<byte> row, int samples, int size)
    {
        for (int i = samples; i < row.Length; i++)
        {
            row[i] += row[i - samples];
        }

        int count = row.Length / size;
        byte[] rented = ArrayPool<byte>.Shared.Rent(row.Length);
        try
        {
            Span<byte> planes = rented.AsSpan(0, row.Length);
            row.CopyTo(planes);
            for (int c = 0; c < count; c++)
            {
                for (int k = 0; k < size; k++)
                {
                    row[(c * size) + k] = planes[((size - 1 - k) * count) + c];
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public void Dispose()
    {
        if (_input is not null)
        {
            ArrayPool<byte>.Shared.Return(_input);
            _input = null;
        }
    }
}
