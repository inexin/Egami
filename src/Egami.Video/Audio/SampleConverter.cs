using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Video.Audio;

/// <summary>
/// Converts samples between formats one channel at a time, through a chunk of 64-bit integers (integer to integer, exact) or
/// floats (anything with a float side). Integer values are scaled by their valid bits: a b-bit sample v is v / 2^(b-1) as a
/// float, and a float x becomes round(x * 2^(b-1)), clamped, as FFmpeg's swresample does it.
/// </summary>
internal static class SampleConverter
{
    internal const int Chunk = 1024;

    public static void Convert(AudioFrame source, AudioFrame destination, bool dither)
    {
        int channels = source.ChannelCount;
        if (destination.ChannelCount != channels)
        {
            throw new ArgumentException("The frames have different channel counts.", nameof(destination));
        }

        int count = source.SampleCount;
        if (count == 0)
        {
            return;
        }

        if (source.Format == destination.Format && source.ValidBits == destination.ValidBits)
        {
            for (int p = 0; p < source.PlaneCount; p++)
            {
                source.GetBytes(p).CopyTo(destination.GetBytes(p));
            }

            return;
        }

        SampleFormat from = source.Format.ToInterleaved(), to = destination.Format.ToInterleaved();
        if (from == to && source.ValidBits == destination.ValidBits)
        {
            Reorder(source, destination);
            return;
        }

        bool integers = !source.Format.IsFloat() && !destination.Format.IsFloat();
        int srcBits = source.ValidBits, dstBits = destination.ValidBits;
        bool narrowing = !destination.Format.IsFloat() && (source.Format.IsFloat() || srcBits > dstBits);
        Dither? noise = dither && narrowing ? new Dither() : null;
        if (integers && noise is null)
        {
            long[] longs = ArrayPool<long>.Shared.Rent(Chunk);
            try
            {
                for (int c = 0; c < channels; c++)
                {
                    for (int i = 0; i < count; i += Chunk)
                    {
                        int n = Math.Min(Chunk, count - i);
                        Span<long> chunk = longs.AsSpan(0, n);
                        ReadIntegers(source, c, i, chunk);
                        Shift(chunk, dstBits - srcBits);
                        WriteIntegers(destination, c, i, chunk);
                    }
                }
            }
            finally
            {
                ArrayPool<long>.Shared.Return(longs);
            }

            return;
        }

        double[] doubles = ArrayPool<double>.Shared.Rent(Chunk);
        try
        {
            for (int c = 0; c < channels; c++)
            {
                for (int i = 0; i < count; i += Chunk)
                {
                    int n = Math.Min(Chunk, count - i);
                    Span<double> chunk = doubles.AsSpan(0, n);
                    ReadDoubles(source, c, i, chunk);
                    WriteDoubles(destination, c, i, chunk, noise);
                }
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(doubles);
        }
    }

    /// <summary>Reads samples of one channel as floats: the fast path of the resampler and mixer.</summary>
    public static void ReadFloats(AudioFrame frame, int channel, int start, Span<float> destination)
    {
        if (frame.Format == SampleFormat.F32Planar)
        {
            frame.AsSpan<float>(channel).Slice(start, destination.Length).CopyTo(destination);
            return;
        }

        int stride = frame.Format.IsPlanar() ? 1 : frame.ChannelCount;
        int plane = frame.Format.IsPlanar() ? channel : 0;
        int offset = frame.Format.IsPlanar() ? start : (start * stride) + channel;
        switch (frame.Format.ToInterleaved())
        {
            case SampleFormat.F32:
                Gather(frame.AsSpan<float>(plane), offset, stride, destination);
                return;
            case SampleFormat.S16 when stride == 1:
                ToFloats(frame.AsSpan<short>(plane).Slice(offset, destination.Length), destination, Scale(frame.ValidBits));
                return;
            case SampleFormat.S32 when stride == 1:
                ToFloats(frame.AsSpan<int>(plane).Slice(offset, destination.Length), destination, Scale(frame.ValidBits));
                return;
        }

        double[] doubles = ArrayPool<double>.Shared.Rent(destination.Length);
        try
        {
            Span<double> chunk = doubles.AsSpan(0, destination.Length);
            ReadDoubles(frame, channel, start, chunk);
            for (int i = 0; i < chunk.Length; i++)
            {
                destination[i] = (float)chunk[i];
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(doubles);
        }
    }

    /// <summary>Writes floats into one channel of a frame, converting and clamping.</summary>
    public static void WriteFloats(AudioFrame frame, int channel, int start, ReadOnlySpan<float> source, Dither? dither = null)
    {
        if (frame.Format == SampleFormat.F32Planar)
        {
            source.CopyTo(frame.GetSpan<float>(channel).Slice(start, source.Length));
            return;
        }

        int stride = frame.Format.IsPlanar() ? 1 : frame.ChannelCount;
        int plane = frame.Format.IsPlanar() ? channel : 0;
        int offset = frame.Format.IsPlanar() ? start : (start * stride) + channel;
        if (frame.Format.ToInterleaved() == SampleFormat.F32)
        {
            Span<float> dst = frame.GetSpan<float>(plane);
            for (int i = 0; i < source.Length; i++)
            {
                dst[offset + (i * stride)] = source[i];
            }

            return;
        }

        double[] doubles = ArrayPool<double>.Shared.Rent(source.Length);
        try
        {
            Span<double> chunk = doubles.AsSpan(0, source.Length);
            for (int i = 0; i < chunk.Length; i++)
            {
                chunk[i] = source[i];
            }

            WriteDoubles(frame, channel, start, chunk, dither);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(doubles);
        }
    }

    private static float Scale(int validBits) => 1f / (1L << (validBits - 1));

    private static void Gather(ReadOnlySpan<float> source, int offset, int stride, Span<float> destination)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = source[offset + (i * stride)];
        }
    }

    private static void ToFloats(ReadOnlySpan<short> source, Span<float> destination, float scale)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated && source.Length >= Vector<short>.Count)
        {
            Vector<float> s = new(scale);
            ReadOnlySpan<Vector<short>> vectors = MemoryMarshal.Cast<short, Vector<short>>(source);
            Span<Vector<float>> outputs = MemoryMarshal.Cast<float, Vector<float>>(destination);
            for (int v = 0; v < vectors.Length; v++)
            {
                Vector.Widen(vectors[v], out Vector<int> low, out Vector<int> high);
                outputs[2 * v] = Vector.ConvertToSingle(low) * s;
                outputs[(2 * v) + 1] = Vector.ConvertToSingle(high) * s;
            }

            i = vectors.Length * Vector<short>.Count;
        }

        for (; i < source.Length; i++)
        {
            destination[i] = source[i] * scale;
        }
    }

    private static void ToFloats(ReadOnlySpan<int> source, Span<float> destination, float scale)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated && source.Length >= Vector<int>.Count)
        {
            Vector<float> s = new(scale);
            ReadOnlySpan<Vector<int>> vectors = MemoryMarshal.Cast<int, Vector<int>>(source);
            Span<Vector<float>> outputs = MemoryMarshal.Cast<float, Vector<float>>(destination);
            for (int v = 0; v < vectors.Length; v++)
            {
                outputs[v] = Vector.ConvertToSingle(vectors[v]) * s;
            }

            i = vectors.Length * Vector<int>.Count;
        }

        for (; i < source.Length; i++)
        {
            destination[i] = source[i] * scale;
        }
    }

    private static void Reorder(AudioFrame source, AudioFrame destination)
    {
        int bytes = source.Format.BytesPerSample();
        int channels = source.ChannelCount;
        int count = source.SampleCount;
        for (int c = 0; c < channels; c++)
        {
            ReadOnlySpan<byte> src = source.GetBytes(source.Format.IsPlanar() ? c : 0);
            Span<byte> dst = destination.GetBytes(destination.Format.IsPlanar() ? c : 0);
            int srcStride = source.Format.IsPlanar() ? bytes : bytes * channels;
            int dstStride = destination.Format.IsPlanar() ? bytes : bytes * channels;
            int srcOffset = source.Format.IsPlanar() ? 0 : c * bytes;
            int dstOffset = destination.Format.IsPlanar() ? 0 : c * bytes;
            switch (bytes)
            {
                case 1:
                    for (int i = 0; i < count; i++)
                    {
                        dst[dstOffset + (i * dstStride)] = src[srcOffset + (i * srcStride)];
                    }

                    break;
                case 2:
                    Copy<short>(src, srcOffset, srcStride, dst, dstOffset, dstStride, count);
                    break;
                case 4:
                    Copy<int>(src, srcOffset, srcStride, dst, dstOffset, dstStride, count);
                    break;
                default:
                    Copy<long>(src, srcOffset, srcStride, dst, dstOffset, dstStride, count);
                    break;
            }
        }
    }

    private static void Copy<T>(ReadOnlySpan<byte> src, int srcOffset, int srcStride, Span<byte> dst, int dstOffset, int dstStride, int count)
        where T : unmanaged
    {
        ref byte s = ref MemoryMarshal.GetReference(src);
        ref byte d = ref MemoryMarshal.GetReference(dst);
        for (int i = 0; i < count; i++)
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, dstOffset + (i * dstStride)), Unsafe.ReadUnaligned<T>(ref Unsafe.Add(ref s, srcOffset + (i * srcStride))));
        }
    }

    private static void Shift(Span<long> values, int shift)
    {
        if (shift > 0)
        {
            for (int i = 0; i < values.Length; i++)
            {
                values[i] <<= shift;
            }
        }
        else if (shift < 0)
        {
            // Round to nearest; the clamp in WriteIntegers catches the top value rounding up.
            int s = -shift;
            long half = 1L << (s - 1);
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = (values[i] + half) >> s;
            }
        }
    }

    private static (int Plane, int Offset, int Stride) Position(AudioFrame frame, int channel, int start) => frame.Format.IsPlanar()
        ? (channel, start, 1)
        : (0, (start * frame.ChannelCount) + channel, frame.ChannelCount);

    private static void ReadIntegers(AudioFrame frame, int channel, int start, Span<long> destination)
    {
        (int plane, int offset, int stride) = Position(frame, channel, start);
        switch (frame.Format.ToInterleaved())
        {
            case SampleFormat.U8:
                {
                    ReadOnlySpan<byte> s = frame.AsSpan<byte>(plane);
                    for (int i = 0; i < destination.Length; i++)
                    {
                        destination[i] = s[offset + (i * stride)] - 128;
                    }

                    break;
                }

            case SampleFormat.S16:
                {
                    ReadOnlySpan<short> s = frame.AsSpan<short>(plane);
                    for (int i = 0; i < destination.Length; i++)
                    {
                        destination[i] = s[offset + (i * stride)];
                    }

                    break;
                }

            case SampleFormat.S32:
                {
                    ReadOnlySpan<int> s = frame.AsSpan<int>(plane);
                    for (int i = 0; i < destination.Length; i++)
                    {
                        destination[i] = s[offset + (i * stride)];
                    }

                    break;
                }

            default:
                {
                    ReadOnlySpan<long> s = frame.AsSpan<long>(plane);
                    for (int i = 0; i < destination.Length; i++)
                    {
                        destination[i] = s[offset + (i * stride)];
                    }

                    break;
                }
        }
    }

    private static void WriteIntegers(AudioFrame frame, int channel, int start, ReadOnlySpan<long> source)
    {
        (int plane, int offset, int stride) = Position(frame, channel, start);
        int bits = frame.ValidBits;
        long max = bits == 64 ? long.MaxValue : (1L << (bits - 1)) - 1;
        long min = bits == 64 ? long.MinValue : -(1L << (bits - 1));
        switch (frame.Format.ToInterleaved())
        {
            case SampleFormat.U8:
                {
                    Span<byte> d = frame.GetSpan<byte>(plane);
                    for (int i = 0; i < source.Length; i++)
                    {
                        d[offset + (i * stride)] = (byte)(Math.Clamp(source[i], min, max) + 128);
                    }

                    break;
                }

            case SampleFormat.S16:
                {
                    Span<short> d = frame.GetSpan<short>(plane);
                    for (int i = 0; i < source.Length; i++)
                    {
                        d[offset + (i * stride)] = (short)Math.Clamp(source[i], min, max);
                    }

                    break;
                }

            case SampleFormat.S32:
                {
                    Span<int> d = frame.GetSpan<int>(plane);
                    for (int i = 0; i < source.Length; i++)
                    {
                        d[offset + (i * stride)] = (int)Math.Clamp(source[i], min, max);
                    }

                    break;
                }

            default:
                {
                    Span<long> d = frame.GetSpan<long>(plane);
                    for (int i = 0; i < source.Length; i++)
                    {
                        d[offset + (i * stride)] = Math.Clamp(source[i], min, max);
                    }

                    break;
                }
        }
    }

    private static void ReadDoubles(AudioFrame frame, int channel, int start, Span<double> destination)
    {
        (int plane, int offset, int stride) = Position(frame, channel, start);
        switch (frame.Format.ToInterleaved())
        {
            case SampleFormat.F32:
                {
                    ReadOnlySpan<float> s = frame.AsSpan<float>(plane);
                    for (int i = 0; i < destination.Length; i++)
                    {
                        destination[i] = s[offset + (i * stride)];
                    }

                    return;
                }

            case SampleFormat.F64:
                {
                    ReadOnlySpan<double> s = frame.AsSpan<double>(plane);
                    for (int i = 0; i < destination.Length; i++)
                    {
                        destination[i] = s[offset + (i * stride)];
                    }

                    return;
                }
        }

        long[] longs = ArrayPool<long>.Shared.Rent(destination.Length);
        try
        {
            Span<long> chunk = longs.AsSpan(0, destination.Length);
            ReadIntegers(frame, channel, start, chunk);
            double scale = Math.ScaleB(1.0, 1 - frame.ValidBits);
            for (int i = 0; i < chunk.Length; i++)
            {
                destination[i] = chunk[i] * scale;
            }
        }
        finally
        {
            ArrayPool<long>.Shared.Return(longs);
        }
    }

    private static void WriteDoubles(AudioFrame frame, int channel, int start, ReadOnlySpan<double> source, Dither? dither)
    {
        (int plane, int offset, int stride) = Position(frame, channel, start);
        switch (frame.Format.ToInterleaved())
        {
            case SampleFormat.F32:
                {
                    Span<float> d = frame.GetSpan<float>(plane);
                    for (int i = 0; i < source.Length; i++)
                    {
                        d[offset + (i * stride)] = (float)source[i];
                    }

                    return;
                }

            case SampleFormat.F64:
                {
                    Span<double> d = frame.GetSpan<double>(plane);
                    for (int i = 0; i < source.Length; i++)
                    {
                        d[offset + (i * stride)] = source[i];
                    }

                    return;
                }
        }

        long[] longs = ArrayPool<long>.Shared.Rent(source.Length);
        try
        {
            Span<long> chunk = longs.AsSpan(0, source.Length);
            double scale = Math.ScaleB(1.0, frame.ValidBits - 1);
            for (int i = 0; i < chunk.Length; i++)
            {
                double v = source[i] * scale;
                if (dither is not null)
                {
                    v += dither.Next();
                }

                // Saturating: NaN becomes 0, and values past the range clamp in WriteIntegers.
                chunk[i] = double.IsNaN(v) ? 0 : (long)Math.Clamp(Math.Round(v, MidpointRounding.ToEven), -9.2e18, 9.2e18);
            }

            WriteIntegers(frame, channel, start, chunk);
        }
        finally
        {
            ArrayPool<long>.Shared.Return(longs);
        }
    }

    /// <summary>Triangular dither of one step peak: the sum of two uniform values, from a fixed seed so output repeats.</summary>
    internal sealed class Dither
    {
        private ulong _state = 0x9E3779B97F4A7C15;

        public double Next() => Uniform() + Uniform() - 1.0;

        private double Uniform()
        {
            // xorshift64*
            _state ^= _state >> 12;
            _state ^= _state << 25;
            _state ^= _state >> 27;
            return ((_state * 0x2545F4914F6CDD1D) >> 11) * (1.0 / (1UL << 53));
        }
    }
}
