using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using Egami.Pixels;

namespace Egami;

/// <content>Copies to and from interleaved layouts.</content>
public abstract partial class YuvRaster
{
    /// <summary>
    /// Copies a frame in an interleaved layout into this raster's planes, with one SIMD pass. The raster must match the layout:
    /// 4:2:0 8-bit for NV12 and NV21, 4:2:0 10-bit for P010, 4:2:0 at 10, 12 or 16 bits for P016, 4:2:2 8-bit for YUYV and UYVY.
    /// </summary>
    /// <param name="format">The layout.</param>
    /// <param name="data">The frame: the luma plane for two-plane layouts, or the only plane for packed 4:2:2.</param>
    /// <param name="stride">The distance between rows of <paramref name="data"/>, in bytes. 0 means rows are packed.</param>
    /// <param name="chroma">
    /// The interleaved chroma plane of a two-plane layout, or empty when it follows the luma rows in <paramref name="data"/>
    /// (at <paramref name="stride"/> × <see cref="Height"/>). Ignored for YUYV and UYVY.
    /// </param>
    /// <param name="chromaStride">The distance between chroma rows, in bytes. 0 means the luma stride.</param>
    /// <exception cref="ArgumentException">The raster does not match the layout.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A stride is too small or a span too short.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/PlanarYuv.cs" region="packed"/>
    /// </example>
    public void CopyFromPacked(YuvPackedFormat format, ReadOnlySpan<byte> data, int stride = 0, ReadOnlySpan<byte> chroma = default, int chromaStride = 0)
    {
        ThrowIfDisposed();
        YuvPacked.Layout layout = YuvPacked.Check(this, format, data.Length, ref stride, chroma.Length, ref chromaStride, out bool chromaFollows);
        if (chromaFollows)
        {
            chroma = data[(stride * Height)..];
        }

        YuvPacked.CopyFrom(this, layout, data, stride, chroma, chromaStride);
    }

    /// <summary>
    /// Copies this raster's planes into a frame in an interleaved layout, with one SIMD pass. See
    /// <see cref="CopyFromPacked"/> for the layouts each raster matches.
    /// </summary>
    /// <param name="format">The layout.</param>
    /// <param name="data">The frame: the luma plane for two-plane layouts, or the only plane for packed 4:2:2.</param>
    /// <param name="stride">The distance between rows of <paramref name="data"/>, in bytes. 0 means rows are packed.</param>
    /// <param name="chroma">
    /// The interleaved chroma plane of a two-plane layout, or empty to write it after the luma rows in <paramref name="data"/>
    /// (at <paramref name="stride"/> × <see cref="Height"/>). Ignored for YUYV and UYVY.
    /// </param>
    /// <param name="chromaStride">The distance between chroma rows, in bytes. 0 means the luma stride.</param>
    /// <exception cref="ArgumentException">The raster does not match the layout.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A stride is too small or a span too short.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    public void CopyToPacked(YuvPackedFormat format, Span<byte> data, int stride = 0, Span<byte> chroma = default, int chromaStride = 0)
    {
        ThrowIfDisposed();
        YuvPacked.Layout layout = YuvPacked.Check(this, format, data.Length, ref stride, chroma.Length, ref chromaStride, out bool chromaFollows);
        if (chromaFollows)
        {
            chroma = data[(stride * Height)..];
        }

        YuvPacked.CopyTo(this, layout, data, stride, chroma, chromaStride);
    }
}

/// <summary>The copy kernels between planes and the interleaved layouts. SIMD kernels give exactly the scalar path's samples.</summary>
internal static class YuvPacked
{
    /// <summary>What a layout is made of: two planes or one, bytes per sample, which chroma comes first, where luma sits in a pair.</summary>
    internal readonly record struct Layout(bool TwoPlanes, int SampleBytes, bool VFirst, bool LumaFirst, int Shift);

#pragma warning disable CA2208 // Check reports the names of the public methods' parameters it checks.
    internal static Layout Check(YuvRaster raster, YuvPackedFormat format, int dataLength, ref int stride, int chromaLength, ref int chromaStride, out bool chromaFollows)
    {
        (ChromaFormat chromaFormat, bool depthOk, Layout layout) = format switch
        {
            YuvPackedFormat.Nv12 => (ChromaFormat.Yuv420, raster.BitDepth == 8, new Layout(true, 1, false, true, 0)),
            YuvPackedFormat.Nv21 => (ChromaFormat.Yuv420, raster.BitDepth == 8, new Layout(true, 1, true, true, 0)),
            YuvPackedFormat.P010 => (ChromaFormat.Yuv420, raster.BitDepth == 10, new Layout(true, 2, false, true, 6)),
            YuvPackedFormat.P016 => (ChromaFormat.Yuv420, raster.BitDepth > 8, new Layout(true, 2, false, true, 16 - raster.BitDepth)),
            YuvPackedFormat.Yuyv => (ChromaFormat.Yuv422, raster.BitDepth == 8, new Layout(false, 1, false, true, 0)),
            YuvPackedFormat.Uyvy => (ChromaFormat.Yuv422, raster.BitDepth == 8, new Layout(false, 1, false, false, 0)),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Not a YuvPackedFormat."),
        };

        if (raster.ChromaFormat != chromaFormat || !depthOk)
        {
            throw new ArgumentException($"{format} needs a {chromaFormat} raster of matching depth; this one is {raster.ChromaFormat} at {raster.BitDepth} bits.", nameof(format));
        }

        int pairs = (raster.Width + 1) / 2;
        long rowBytes = layout.TwoPlanes ? (long)raster.Width * layout.SampleBytes : 4L * pairs;
        if (stride == 0)
        {
            stride = (int)rowBytes;
        }

        if (stride < rowBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(stride), stride, $"The stride must be at least {rowBytes} bytes.");
        }

        chromaFollows = false;
        long needed = ((long)(raster.Height - 1) * stride) + rowBytes;
        if (layout.TwoPlanes)
        {
            int chromaRows = (raster.Height + 1) / 2;
            long chromaRowBytes = 2L * pairs * layout.SampleBytes;
            if (chromaStride == 0)
            {
                chromaStride = stride;
            }

            if (chromaStride < chromaRowBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(chromaStride), chromaStride, $"The chroma stride must be at least {chromaRowBytes} bytes.");
            }

            long chromaNeeded = ((long)(chromaRows - 1) * chromaStride) + chromaRowBytes;
            if (chromaLength == 0)
            {
                chromaFollows = true;
                needed = ((long)stride * raster.Height) + chromaNeeded;
            }
            else if (chromaLength < chromaNeeded)
            {
                throw new ArgumentOutOfRangeException("chroma", chromaLength, $"The chroma plane holds {chromaLength} bytes but needs {chromaNeeded}.");
            }
        }

        if (dataLength < needed)
        {
            throw new ArgumentOutOfRangeException("data", dataLength, $"The data holds {dataLength} bytes but needs {needed}.");
        }

        return layout;
    }
#pragma warning restore CA2208

    internal static void CopyFrom(YuvRaster raster, Layout layout, ReadOnlySpan<byte> data, int stride, ReadOnlySpan<byte> chroma, int chromaStride)
    {
        if (raster is YuvRaster<byte> bytes)
        {
            if (layout.TwoPlanes)
            {
                for (int y = 0; y < raster.Height; y++)
                {
                    data.Slice(y * stride, raster.Width).CopyTo(bytes.Y.Row(y));
                }

                for (int j = 0; j < bytes.U!.Height; j++)
                {
                    Deinterleave(chroma.Slice(j * chromaStride, 2 * bytes.U.Width), layout.VFirst ? bytes.V!.Row(j) : bytes.U.Row(j), layout.VFirst ? bytes.U.Row(j) : bytes.V!.Row(j));
                }

                return;
            }

            for (int y = 0; y < raster.Height; y++)
            {
                Unpack422(data.Slice(y * stride, 4 * bytes.U!.Width), layout.LumaFirst, bytes.Y.Row(y), bytes.U.Row(y), bytes.V!.Row(y));
            }

            return;
        }

        YuvRaster<ushort> words = (YuvRaster<ushort>)raster;
        for (int y = 0; y < raster.Height; y++)
        {
            ShiftDown(MemoryMarshal.Cast<byte, ushort>(data.Slice(y * stride, 2 * raster.Width)), words.Y.Row(y), layout.Shift);
        }

        for (int j = 0; j < words.U!.Height; j++)
        {
            DeinterleaveWords(MemoryMarshal.Cast<byte, ushort>(chroma.Slice(j * chromaStride, 4 * words.U.Width)), words.U.Row(j), words.V!.Row(j), layout.Shift);
        }
    }

    internal static void CopyTo(YuvRaster raster, Layout layout, Span<byte> data, int stride, Span<byte> chroma, int chromaStride)
    {
        if (raster is YuvRaster<byte> bytes)
        {
            if (layout.TwoPlanes)
            {
                for (int y = 0; y < raster.Height; y++)
                {
                    bytes.Y.Row(y).CopyTo(data.Slice(y * stride, raster.Width));
                }

                for (int j = 0; j < bytes.U!.Height; j++)
                {
                    Interleave(layout.VFirst ? bytes.V!.Row(j) : bytes.U.Row(j), layout.VFirst ? bytes.U.Row(j) : bytes.V!.Row(j), chroma.Slice(j * chromaStride, 2 * bytes.U.Width));
                }

                return;
            }

            for (int y = 0; y < raster.Height; y++)
            {
                Pack422(bytes.Y.Row(y), bytes.U!.Row(y), bytes.V!.Row(y), layout.LumaFirst, data.Slice(y * stride, 4 * bytes.U.Width));
            }

            return;
        }

        YuvRaster<ushort> words = (YuvRaster<ushort>)raster;
        int max = raster.MaxSample;
        for (int y = 0; y < raster.Height; y++)
        {
            ShiftUp(words.Y.Row(y), MemoryMarshal.Cast<byte, ushort>(data.Slice(y * stride, 2 * raster.Width)), layout.Shift, max);
        }

        for (int j = 0; j < words.U!.Height; j++)
        {
            InterleaveWords(words.U.Row(j), words.V!.Row(j), MemoryMarshal.Cast<byte, ushort>(chroma.Slice(j * chromaStride, 4 * words.U.Width)), layout.Shift, max);
        }
    }

    /// <summary>Splits byte pairs into two planes: the first byte of each pair to <paramref name="a"/>.</summary>
    internal static void Deinterleave(ReadOnlySpan<byte> pairs, Span<byte> a, Span<byte> b)
    {
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            ref byte rp = ref MemoryMarshal.GetReference(pairs);
            Vector128<ushort> low = Vector128.Create((ushort)0xFF);
            for (; i <= a.Length - 16; i += 16)
            {
                Vector128<ushort> p0 = Vector128.LoadUnsafe(ref rp, (nuint)(2 * i)).AsUInt16(), p1 = Vector128.LoadUnsafe(ref rp, (nuint)((2 * i) + 16)).AsUInt16();
                Vector128.Narrow(p0 & low, p1 & low).StoreUnsafe(ref MemoryMarshal.GetReference(a), (nuint)i);
                Vector128.Narrow(p0 >>> 8, p1 >>> 8).StoreUnsafe(ref MemoryMarshal.GetReference(b), (nuint)i);
            }
        }

        for (; i < a.Length; i++)
        {
            a[i] = pairs[2 * i];
            b[i] = pairs[(2 * i) + 1];
        }
    }

    /// <summary>Interleaves two planes into byte pairs, <paramref name="a"/> first.</summary>
    internal static void Interleave(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> pairs)
    {
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            ref byte rp = ref MemoryMarshal.GetReference(pairs);
            for (; i <= a.Length - 16; i += 16)
            {
                (Vector128<ushort> a0, Vector128<ushort> a1) = Vector128.Widen(Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(a), (nuint)i));
                (Vector128<ushort> b0, Vector128<ushort> b1) = Vector128.Widen(Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(b), (nuint)i));
                (a0 | (b0 << 8)).AsByte().StoreUnsafe(ref rp, (nuint)(2 * i));
                (a1 | (b1 << 8)).AsByte().StoreUnsafe(ref rp, (nuint)((2 * i) + 16));
            }
        }

        for (; i < a.Length; i++)
        {
            pairs[2 * i] = a[i];
            pairs[(2 * i) + 1] = b[i];
        }
    }

    /// <summary>
    /// Unpacks one row of YUYV (or UYVY when not <paramref name="lumaFirst"/>). An odd width drops the second luma of the last
    /// pair.
    /// </summary>
    internal static void Unpack422(ReadOnlySpan<byte> packed, bool lumaFirst, Span<byte> y, Span<byte> u, Span<byte> v)
    {
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            ref byte rp = ref MemoryMarshal.GetReference(packed);
            Vector128<uint> low = Vector128.Create(0xFFu);
            int lumaShift = lumaFirst ? 0 : 8, chromaShift = lumaFirst ? 8 : 0;

            // Eight pairs (32 bytes) per step; the step writes 16 luma samples, so stay off the last pair of an odd width.
            for (; i <= u.Length - 8 && (2 * i) + 16 <= y.Length; i += 8)
            {
                Vector128<uint> p0 = Vector128.LoadUnsafe(ref rp, (nuint)(4 * i)).AsUInt32(), p1 = Vector128.LoadUnsafe(ref rp, (nuint)((4 * i) + 16)).AsUInt32();

                // Per 32-bit pair: luma at bytes 0 and 2 (YUYV) or 1 and 3 (UYVY), U and V at the others.
                Vector128<ushort> l0 = (p0 >>> lumaShift).AsUInt16() & Vector128.Create((ushort)0xFF), l1 = (p1 >>> lumaShift).AsUInt16() & Vector128.Create((ushort)0xFF);
                Vector128.Narrow(l0, l1).StoreUnsafe(ref MemoryMarshal.GetReference(y), (nuint)(2 * i));
                Vector128<uint> c0 = p0 >>> chromaShift, c1 = p1 >>> chromaShift;
                Vector128<byte> us = Vector128.Narrow(Vector128.Narrow(c0 & low, c1 & low), Vector128<ushort>.Zero);
                Vector128<byte> vs = Vector128.Narrow(Vector128.Narrow((c0 >>> 16) & low, (c1 >>> 16) & low), Vector128<ushort>.Zero);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(u), i), us.AsUInt64().ToScalar());
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(v), i), vs.AsUInt64().ToScalar());
            }
        }

        int l = lumaFirst ? 0 : 1, c = lumaFirst ? 1 : 0;
        for (; i < u.Length; i++)
        {
            y[2 * i] = packed[(4 * i) + l];
            if ((2 * i) + 1 < y.Length)
            {
                y[(2 * i) + 1] = packed[(4 * i) + l + 2];
            }

            u[i] = packed[(4 * i) + c];
            v[i] = packed[(4 * i) + c + 2];
        }
    }

    /// <summary>Packs one row as YUYV (or UYVY). An odd width repeats the last luma sample in the padding.</summary>
    internal static void Pack422(ReadOnlySpan<byte> y, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v, bool lumaFirst, Span<byte> packed)
    {
        int i = 0;
        if (Vector128.IsHardwareAccelerated && YuvSimd.IsSupported)
        {
            ref byte rp = ref MemoryMarshal.GetReference(packed);
            for (; i <= u.Length - 8 && (2 * i) + 16 <= y.Length; i += 8)
            {
                Vector128<byte> luma = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(y), (nuint)(2 * i));
                Vector128<byte> cu = Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(u), i))).AsByte();
                Vector128<byte> cv = Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(v), i))).AsByte();
                Vector128<byte> chroma = YuvSimd.ZipLow(cu, cv);
                Vector128<byte> first = lumaFirst ? luma : chroma, second = lumaFirst ? chroma : luma;
                YuvSimd.ZipLow(first, second).StoreUnsafe(ref rp, (nuint)(4 * i));
                YuvSimd.ZipHigh(first, second).StoreUnsafe(ref rp, (nuint)((4 * i) + 16));
            }
        }

        int l = lumaFirst ? 0 : 1, c = lumaFirst ? 1 : 0;
        for (; i < u.Length; i++)
        {
            packed[(4 * i) + l] = y[2 * i];
            packed[(4 * i) + l + 2] = y[Math.Min((2 * i) + 1, y.Length - 1)];
            packed[(4 * i) + c] = u[i];
            packed[(4 * i) + c + 2] = v[i];
        }
    }

    /// <summary>MSB-aligned words to samples.</summary>
    internal static void ShiftDown(ReadOnlySpan<ushort> words, Span<ushort> samples, int shift)
    {
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            for (; i <= samples.Length - 8; i += 8)
            {
                (Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(words), (nuint)i) >>> shift).StoreUnsafe(ref MemoryMarshal.GetReference(samples), (nuint)i);
            }
        }

        for (; i < samples.Length; i++)
        {
            samples[i] = (ushort)(words[i] >> shift);
        }
    }

    /// <summary>Samples, clamped to the depth, to MSB-aligned words.</summary>
    internal static void ShiftUp(ReadOnlySpan<ushort> samples, Span<ushort> words, int shift, int max)
    {
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            Vector128<ushort> limit = Vector128.Create((ushort)max);
            for (; i <= samples.Length - 8; i += 8)
            {
                (Vector128.Min(Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(samples), (nuint)i), limit) << shift).StoreUnsafe(ref MemoryMarshal.GetReference(words), (nuint)i);
            }
        }

        for (; i < samples.Length; i++)
        {
            words[i] = (ushort)(Math.Min(samples[i], max) << shift);
        }
    }

    /// <summary>MSB-aligned U, V word pairs to two planes of samples.</summary>
    internal static void DeinterleaveWords(ReadOnlySpan<ushort> pairs, Span<ushort> u, Span<ushort> v, int shift)
    {
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            ref uint rp = ref Unsafe.As<ushort, uint>(ref MemoryMarshal.GetReference(pairs));
            Vector128<uint> low = Vector128.Create(0xFFFFu);
            for (; i <= u.Length - 8; i += 8)
            {
                Vector128<uint> p0 = Vector128.LoadUnsafe(ref rp, (nuint)i), p1 = Vector128.LoadUnsafe(ref rp, (nuint)(i + 4));
                (Vector128.Narrow(p0 & low, p1 & low) >>> shift).StoreUnsafe(ref MemoryMarshal.GetReference(u), (nuint)i);
                (Vector128.Narrow(p0 >>> 16, p1 >>> 16) >>> shift).StoreUnsafe(ref MemoryMarshal.GetReference(v), (nuint)i);
            }
        }

        for (; i < u.Length; i++)
        {
            u[i] = (ushort)(pairs[2 * i] >> shift);
            v[i] = (ushort)(pairs[(2 * i) + 1] >> shift);
        }
    }

    /// <summary>Two planes of samples, clamped to the depth, to MSB-aligned U, V word pairs.</summary>
    internal static void InterleaveWords(ReadOnlySpan<ushort> u, ReadOnlySpan<ushort> v, Span<ushort> pairs, int shift, int max)
    {
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            ref uint rp = ref Unsafe.As<ushort, uint>(ref MemoryMarshal.GetReference(pairs));
            Vector128<ushort> limit = Vector128.Create((ushort)max);
            for (; i <= u.Length - 8; i += 8)
            {
                (Vector128<uint> u0, Vector128<uint> u1) = Vector128.Widen(Vector128.Min(Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(u), (nuint)i), limit) << shift);
                (Vector128<uint> v0, Vector128<uint> v1) = Vector128.Widen(Vector128.Min(Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(v), (nuint)i), limit) << shift);
                (u0 | (v0 << 16)).StoreUnsafe(ref rp, (nuint)i);
                (u1 | (v1 << 16)).StoreUnsafe(ref rp, (nuint)(i + 4));
            }
        }

        for (; i < u.Length; i++)
        {
            pairs[2 * i] = (ushort)(Math.Min(u[i], max) << shift);
            pairs[(2 * i) + 1] = (ushort)(Math.Min(v[i], max) << shift);
        }
    }
}
