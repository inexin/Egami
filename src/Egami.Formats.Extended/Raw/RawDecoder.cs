using System.Buffers;
using System.IO.Compression;
using System.Runtime.InteropServices;

using Egami.Formats.Jpeg;
using Egami.Formats.Jxl;
using Egami.Pixels;

namespace Egami.Formats.Extended.Raw;

/// <summary>
/// Decodes the strips or tiles of TIFF-structured sensor data into <see cref="RawPixels"/>, from TIFF 6.0 and the DNG
/// specification: uncompressed samples of 1 to 32 bits and 16, 24 or 32-bit floats; lossless JPEG (compression 7); Deflate
/// (8) with the integer and floating-point predictors; lossy JPEG (34892, and 7 holding a baseline JPEG); and JPEG XL (52546).
/// Units decode in parallel; each one writes only its own pixels, so the result does not depend on the thread count.
/// </summary>
internal static class RawDecoder
{
    public const int CompressionNone = 1;
    public const int CompressionJpeg = 7;
    public const int CompressionDeflate = 8;
    public const int CompressionDeflateOld = 32946;
    public const int CompressionLossyJpeg = 34892;
    public const int CompressionJpegXl = 52546;

    /// <summary>Decodes every unit. A unit that is missing or damaged stays zero and is reported through <paramref name="damaged"/>.</summary>
    public static RawPixels Decode(RawSensor sensor, RawTiff tiff, EgamiInput input, Action<int, string> damaged)
    {
        if (sensor.Codec != RawCodec.Tiff)
        {
            if (sensor.Width <= 0 || sensor.Height <= 0 || (long)sensor.Width * sensor.Height > 1L << 30)
            {
                throw new InvalidDataException("The raw image has no size, or one larger than a camera's.");
            }

            RawPixels maker = new(input, sensor.Width, sensor.Height, sensor.Samples, false);
            try
            {
                ParallelOptions options = new() { MaxDegreeOfParallelism = Math.Max(1, input.Context.MaxParallelism), CancellationToken = input.CancellationToken };
                MakerDecoders.Decode(sensor, tiff, maker, options, damaged);
                return maker;
            }
            catch
            {
                maker.Dispose();
                throw;
            }
        }

        Check(sensor);
        RawPixels pixels = new(input, sensor.Width, sensor.Height, sensor.Samples, sensor.Float);
        try
        {
            int units = sensor.UnitsAcross * sensor.UnitsDown;
            if (sensor.Offsets.Length < units || sensor.Counts.Length < units)
            {
                damaged(-1, $"The image has {units} strips or tiles but lists {Math.Min(sensor.Offsets.Length, sensor.Counts.Length)}; the rest read as black.");
                units = Math.Min(units, Math.Min(sensor.Offsets.Length, sensor.Counts.Length));
            }

            ParallelOptions parallel = new()
            {
                MaxDegreeOfParallelism = Math.Max(1, input.Context.MaxParallelism),
                CancellationToken = input.CancellationToken,
            };
            if (units == 1 || parallel.MaxDegreeOfParallelism == 1)
            {
                for (int u = 0; u < units; u++)
                {
                    Unit(sensor, tiff, input, pixels, u, damaged);
                }
            }
            else
            {
                // Damage is reported after the units have run, in unit order, so a strict read throws the first as it is.
                List<(int Unit, string Message)> reports = [];
                try
                {
                    RawParallel.For(0, units, parallel, u => Unit(sensor, tiff, input, pixels, u, (i, m) =>
                    {
                        lock (reports)
                        {
                            reports.Add((i, m));
                        }
                    }));
                }
                catch (AggregateException e) when (e.InnerExceptions.Count > 0)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
                }

                foreach ((int unit, string message) in reports.OrderBy(r => r.Unit))
                {
                    damaged(unit, message);
                }
            }

            return pixels;
        }
        catch
        {
            pixels.Dispose();
            throw;
        }
    }

    private static void Check(RawSensor sensor)
    {
        if (sensor.Width <= 0 || sensor.Height <= 0)
        {
            throw new InvalidDataException("The raw image has no size.");
        }

        if (sensor.Float ? sensor.Bits is not (16 or 24 or 32) : sensor.Bits is < 1 or > 32)
        {
            throw new NotSupportedException($"Egami does not read {sensor.Bits}-bit {(sensor.Float ? "floating-point" : "integer")} raw samples.");
        }

        if (sensor.Compression is not (CompressionNone or CompressionJpeg or CompressionDeflate or CompressionDeflateOld or CompressionLossyJpeg or CompressionJpegXl))
        {
            throw new NotSupportedException($"Egami does not read raw data with TIFF compression {sensor.Compression}.");
        }

        if (sensor.Compression is CompressionLossyJpeg or CompressionJpegXl && sensor.Samples is not (1 or 3))
        {
            throw new NotSupportedException($"Egami reads lossy raw data with 1 or 3 samples, not {sensor.Samples}.");
        }

        // A strip or tile is decoded whole into one buffer; keep it to a size a real file has.
        long unitSamples = (long)sensor.UnitWidth * sensor.UnitHeight * sensor.Samples;
        if (unitSamples > 1 << 28 || sensor.UnitWidth > Math.Max(sensor.Width, 1 << 16) || sensor.UnitHeight > Math.Max(sensor.Height, 1 << 16))
        {
            throw new InvalidDataException($"The strips or tiles are {sensor.UnitWidth} × {sensor.UnitHeight}, larger than a raw image needs.");
        }

        if (sensor.Ifd.Int(RawTags.PlanarConfiguration, 1) == 2 && sensor.Samples > 1)
        {
            throw new NotSupportedException("Egami does not read raw data stored as separate planes (PlanarConfiguration 2).");
        }
    }

    private static void Unit(RawSensor sensor, RawTiff tiff, EgamiInput input, RawPixels pixels, int u, Action<int, string> damaged)
    {
        int x0 = u % sensor.UnitsAcross * sensor.UnitWidth;
        int y0 = u / sensor.UnitsAcross * sensor.UnitHeight;
        int rows = Math.Min(sensor.UnitHeight, sensor.Height - y0);
        int cols = Math.Min(sensor.UnitWidth, sensor.Width - x0);
        long offset = tiff.Origin + sensor.Offsets[u];
        long count = sensor.Counts[u];
        if (sensor.Offsets[u] <= 0 || count <= 0 || offset >= tiff.Length)
        {
            damaged(u, $"Strip or tile {u} is missing; it reads as black.");
            return;
        }

        if (offset + count > tiff.Length)
        {
            damaged(u, $"Strip or tile {u} runs past the end of the file; what is missing reads as black.");
            count = tiff.Length - offset;
        }

        ReadOnlySpan<byte> data = tiff.Data.AsSpan((int)offset, (int)count);
        try
        {
            switch (sensor.Compression)
            {
                case CompressionNone:
                    Unpacked(sensor, data, pixels, x0, y0, cols, rows, sensor.UnitWidth, damaged, u);
                    break;
                case CompressionDeflate:
                case CompressionDeflateOld:
                    Deflated(sensor, tiff.Data.AsMemory((int)offset, (int)count), pixels, x0, y0, cols, rows, input, damaged, u);
                    break;
                case CompressionJpeg when IsLossless(data):
                    Lossless(sensor, tiff.Data, (int)offset, (int)count, pixels, x0, y0, cols, rows, damaged, u);
                    break;
                case CompressionJpeg:
                case CompressionLossyJpeg:
                    Decoded(sensor, tiff.Data.AsMemory((int)offset, (int)count), JpegFormat.Info, pixels, x0, y0, cols, rows, input);
                    break;
                default:
                    Decoded(sensor, tiff.Data.AsMemory((int)offset, (int)count), JxlFormat.Info, pixels, x0, y0, cols, rows, input);
                    break;
            }
        }
        catch (Exception e) when (e is InvalidDataException or EgamiException)
        {
            damaged(u, $"Strip or tile {u} is damaged ({e.Message}); what did not decode reads as black.");
        }
    }

    /// <summary>Gets whether JPEG data is lossless: its first frame header is SOF3.</summary>
    private static bool IsLossless(ReadOnlySpan<byte> data)
    {
        for (int p = 2; p + 4 <= data.Length;)
        {
            if (data[p] != 0xFF)
            {
                return false;
            }

            int marker = data[p + 1];
            if (marker is 0xC3 or 0xC7)
            {
                return true;
            }

            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                return false;
            }

            p += 2 + ((data[p + 2] << 8) | data[p + 3]);
        }

        return false;
    }

    /// <summary>Bytes one row of a unit takes when uncompressed: samples packed most significant bit first, padded to a byte.</summary>
    private static int RowBytes(RawSensor sensor, int width) => (int)(((long)width * sensor.Samples * sensor.Bits + 7) / 8);

    private static void Unpacked(RawSensor sensor, ReadOnlySpan<byte> data, RawPixels pixels, int x0, int y0, int cols, int rows, int unitWidth, Action<int, string> damaged, int u)
    {
        int rowBytes = RowBytes(sensor, unitWidth);
        int samples = cols * sensor.Samples;
        for (int y = 0; y < rows; y++)
        {
            long at = (long)y * rowBytes;
            if (at + RowBytes(sensor, cols) > data.Length)
            {
                damaged(u, $"Strip or tile {u} ends after {y} of its {rows} rows; the rest reads as black.");
                return;
            }

            ReadOnlySpan<byte> row = data[(int)at..];
            if (sensor.Float)
            {
                Floats(row, sensor.Bits, sensor.BigEndian, pixels.F32Row(y0 + y).Slice(x0 * sensor.Samples, samples));
            }
            else
            {
                Ints(row, sensor.Bits, sensor.BigEndian, pixels.U16Row(y0 + y).Slice(x0 * sensor.Samples, samples));
            }
        }
    }

    /// <summary>Unpacks integer samples; samples wider than 16 bits keep their top 16.</summary>
    public static void Ints(ReadOnlySpan<byte> row, int bits, bool bigEndian, Span<ushort> output)
    {
        switch (bits)
        {
            case 8:
                for (int i = 0; i < output.Length; i++)
                {
                    output[i] = row[i];
                }

                return;
            case 16:
                ReadOnlySpan<ushort> words = MemoryMarshal.Cast<byte, ushort>(row[..(output.Length * 2)]);
                if (bigEndian == BitConverter.IsLittleEndian)
                {
                    System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(words, output);
                }
                else
                {
                    words.CopyTo(output);
                }

                return;
            case 32:
                for (int i = 0; i < output.Length; i++)
                {
                    uint v = bigEndian
                        ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(row[(i * 4)..])
                        : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(row[(i * 4)..]);
                    output[i] = (ushort)(v >> 16);
                }

                return;
        }

        // Other depths are packed most significant bit first (TIFF FillOrder 1), whatever the byte order.
        ulong buffer = 0;
        int count = 0;
        int p = 0;
        int drop = Math.Max(0, bits - 16);
        uint mask = (uint)((1UL << bits) - 1);
        for (int i = 0; i < output.Length; i++)
        {
            while (count < bits)
            {
                buffer = (buffer << 8) | row[p++];
                count += 8;
            }

            count -= bits;
            output[i] = (ushort)((((uint)(buffer >> count)) & mask) >> drop);
        }
    }

    /// <summary>Unpacks 16, 24 or 32-bit floating-point samples.</summary>
    public static void Floats(ReadOnlySpan<byte> row, int bits, bool bigEndian, Span<float> output)
    {
        int size = bits / 8;
        for (int i = 0; i < output.Length; i++)
        {
            ReadOnlySpan<byte> v = row.Slice(i * size, size);
            output[i] = size switch
            {
                2 => (float)BitConverter.UInt16BitsToHalf(bigEndian ? (ushort)((v[0] << 8) | v[1]) : (ushort)((v[1] << 8) | v[0])),
                3 => Float24(bigEndian ? (v[0] << 16) | (v[1] << 8) | v[2] : (v[2] << 16) | (v[1] << 8) | v[0]),
                _ => BitConverter.Int32BitsToSingle(bigEndian
                    ? System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(v)
                    : System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(v)),
            };
        }
    }

    /// <summary>Converts DNG's 24-bit float (1 sign, 7 exponent bits biased by 63, 16 mantissa bits) to a float.</summary>
    public static float Float24(int bits)
    {
        int sign = (bits >> 23) & 1;
        int exponent = (bits >> 16) & 0x7F;
        int mantissa = bits & 0xFFFF;
        if (exponent == 0 && mantissa == 0)
        {
            return sign == 0 ? 0f : -0f;
        }

        if (exponent == 0x7F)
        {
            return mantissa != 0 ? float.NaN : sign == 0 ? float.PositiveInfinity : float.NegativeInfinity;
        }

        float value = exponent == 0
            ? mantissa / 65536f * MathF.Pow(2, -62)
            : (1 + (mantissa / 65536f)) * MathF.Pow(2, exponent - 63);
        return sign == 0 ? value : -value;
    }

    private static void Deflated(RawSensor sensor, ReadOnlyMemory<byte> data, RawPixels pixels, int x0, int y0, int cols, int rows, EgamiInput input, Action<int, string> damaged, int u)
    {
        int unitWidth = sensor.UnitWidth;
        int rowBytes = RowBytes(sensor, unitWidth);
        long expected = (long)rowBytes * (sensor.Tiled ? sensor.UnitHeight : rows);
        input.CheckMemory(expected);
        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)expected);
        try
        {
            int got = Inflate(data, buffer.AsSpan(0, (int)expected));
            int whole = got / rowBytes;
            if (whole < rows)
            {
                damaged(u, $"Strip or tile {u} inflates to {whole} of its {rows} rows; the rest reads as black.");
            }

            int bytesPerSample = sensor.Bits / 8;
            int predictor = sensor.Predictor;
            bool floatPredictor = predictor is 3 or 34894 or 34895;
            if ((floatPredictor && !sensor.Float) || (predictor is 2 or 34892 or 34893 && sensor.Bits is not (8 or 16 or 32)))
            {
                throw new InvalidDataException($"Predictor {predictor} does not apply to {sensor.Bits}-bit {(sensor.Float ? "floating-point" : "integer")} samples.");
            }

            int factor = predictor is 34892 or 34894 ? 2 : predictor is 34893 or 34895 ? 4 : 1;
            for (int y = 0; y < Math.Min(rows, whole); y++)
            {
                Span<byte> row = buffer.AsSpan(y * rowBytes, rowBytes);
                if (floatPredictor)
                {
                    FloatPredictor(row, unitWidth * sensor.Samples, bytesPerSample, sensor.Samples * factor);
                    Floats(row, sensor.Bits, true, pixels.F32Row(y0 + y).Slice(x0 * sensor.Samples, cols * sensor.Samples));
                    continue;
                }

                if (predictor is 2 or 34892 or 34893)
                {
                    IntPredictor(row, unitWidth * sensor.Samples, bytesPerSample, sensor.Samples * factor, sensor.BigEndian);
                }

                if (sensor.Float)
                {
                    Floats(row, sensor.Bits, sensor.BigEndian, pixels.F32Row(y0 + y).Slice(x0 * sensor.Samples, cols * sensor.Samples));
                }
                else
                {
                    Ints(row, sensor.Bits, sensor.BigEndian, pixels.U16Row(y0 + y).Slice(x0 * sensor.Samples, cols * sensor.Samples));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int Inflate(ReadOnlyMemory<byte> data, Span<byte> output)
    {
        if (!System.Runtime.InteropServices.MemoryMarshal.TryGetArray(data, out ArraySegment<byte> segment))
        {
            segment = data.ToArray();
        }

        using MemoryStream source = new(segment.Array!, segment.Offset, segment.Count, writable: false);
        using ZLibStream zlib = new(source, CompressionMode.Decompress);
        int total = 0;
        try
        {
            while (total < output.Length)
            {
                int n = zlib.Read(output[total..]);
                if (n == 0)
                {
                    break;
                }

                total += n;
            }
        }
        catch (IOException)
        {
            // Keep what inflated before the damage (InvalidDataException and zlib's own errors are both IOExceptions).
        }

        return total;
    }

    /// <summary>Undoes horizontal differencing (predictor 2 and DNG's X2 and X4) on 8, 16 or 32-bit integers.</summary>
    private static void IntPredictor(Span<byte> row, int samples, int bytesPerSample, int stride, bool bigEndian)
    {
        switch (bytesPerSample)
        {
            case 1:
                for (int i = stride; i < samples; i++)
                {
                    row[i] += row[i - stride];
                }

                break;
            case 2:
                for (int i = stride; i < samples; i++)
                {
                    Span<byte> v = row[(i * 2)..];
                    ReadOnlySpan<byte> p = row[((i - stride) * 2)..];
                    ushort value = (ushort)(bigEndian
                        ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(v) + System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(p)
                        : System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(v) + System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(p));
                    if (bigEndian)
                    {
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(v, value);
                    }
                    else
                    {
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(v, value);
                    }
                }

                break;
            case 4:
                for (int i = stride; i < samples; i++)
                {
                    Span<byte> v = row[(i * 4)..];
                    ReadOnlySpan<byte> p = row[((i - stride) * 4)..];
                    uint value = bigEndian
                        ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(v) + System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(p)
                        : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(v) + System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p);
                    if (bigEndian)
                    {
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(v, value);
                    }
                    else
                    {
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(v, value);
                    }
                }

                break;
        }
    }

    /// <summary>
    /// Undoes the floating-point predictor of Adobe's TIFF Technical Note 3 (and DNG's X2 and X4): bytes differenced across the
    /// row, the bytes of each significance stored together, most significant first. Leaves big-endian floats in the row.
    /// </summary>
    private static void FloatPredictor(Span<byte> row, int samples, int bytesPerSample, int stride)
    {
        int length = samples * bytesPerSample;
        for (int i = stride; i < length; i++)
        {
            row[i] += row[i - stride];
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            Span<byte> copy = rented.AsSpan(0, length);
            row[..length].CopyTo(copy);
            for (int i = 0; i < samples; i++)
            {
                for (int k = 0; k < bytesPerSample; k++)
                {
                    row[(i * bytesPerSample) + k] = copy[(k * samples) + i];
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void Lossless(RawSensor sensor, byte[] file, int offset, int count, RawPixels pixels, int x0, int y0, int cols, int rows, Action<int, string> damaged, int u)
    {
        LosslessJpeg jpeg = LosslessJpeg.OpenReused(file, offset, count);
        long samples = jpeg.SampleCount;
        long unitSamples = (long)sensor.UnitWidth * (sensor.Tiled ? sensor.UnitHeight : rows) * sensor.Samples;
        if (samples > unitSamples * 2 + 65536)
        {
            throw new InvalidDataException("The lossless JPEG is larger than its strip or tile.");
        }

        ushort[] buffer = ArrayPool<ushort>.Shared.Rent((int)Math.Max(samples, unitSamples));
        try
        {
            Span<ushort> decoded = buffer.AsSpan(0, (int)Math.Max(samples, unitSamples));
            decoded.Clear();
            if (!jpeg.Decode(decoded))
            {
                damaged(u, $"The lossless JPEG of strip or tile {u} ends early; what is missing reads as black.");
            }

            // The JPEG's samples fill the unit row by row, whatever its own width and component count.
            int stride = sensor.UnitWidth * sensor.Samples;
            int shift = Math.Max(0, jpeg.Precision - 16);
            for (int y = 0; y < rows; y++)
            {
                Span<ushort> source = decoded.Slice(y * stride, cols * sensor.Samples);
                Span<ushort> target = pixels.U16Row(y0 + y).Slice(x0 * sensor.Samples, cols * sensor.Samples);
                if (shift == 0)
                {
                    source.CopyTo(target);
                }
                else
                {
                    for (int i = 0; i < source.Length; i++)
                    {
                        target[i] = (ushort)(source[i] >> shift);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<ushort>.Shared.Return(buffer);
        }
    }

    /// <summary>Decodes a lossy JPEG or JPEG XL unit through Egami's own decoders.</summary>
    private static void Decoded(RawSensor sensor, ReadOnlyMemory<byte> data, FormatInfo format, RawPixels pixels, int x0, int y0, int cols, int rows, EgamiInput input)
    {
        EgamiContext context = input.Context with { Formats = [format] };
        ReaderOptions options = new() { ReadMetadata = false };
        int samples = sensor.Samples;
        if (samples is not (1 or 3))
        {
            throw new NotSupportedException($"Egami reads lossy raw data with 1 or 3 samples, not {samples}.");
        }

        if (sensor.Float)
        {
            using Raster<RgbF32> raster = Raster.Load<RgbF32>(data, format, context, options);
            Copy(raster, pixels, x0, y0, cols, rows, samples, (p, c) => c == 0 ? p.R : c == 1 ? p.G : p.B, (r, x) => r.F32Row(x));
            return;
        }

        int max = (1 << Math.Min(sensor.Bits, 16)) - 1;
        using Raster<Rgb16> wide = Raster.Load<Rgb16>(data, format, context, options);
        for (int y = 0; y < Math.Min(rows, wide.Height); y++)
        {
            Span<Rgb16> source = wide.Row(y);
            Span<ushort> target = pixels.U16Row(y0 + y).Slice(x0 * samples, cols * samples);
            int n = Math.Min(cols, source.Length);
            for (int x = 0; x < n; x++)
            {
                Rgb16 p = source[x];
                if (samples == 1)
                {
                    target[x] = Scale(p.R, max);
                }
                else
                {
                    target[x * 3] = Scale(p.R, max);
                    target[(x * 3) + 1] = Scale(p.G, max);
                    target[(x * 3) + 2] = Scale(p.B, max);
                }
            }
        }
    }

    private static ushort Scale(ushort value, int max) => max == 65535 ? value : (ushort)(((value * (long)max) + 32767) / 65535);

    private static void Copy(Raster<RgbF32> raster, RawPixels pixels, int x0, int y0, int cols, int rows, int samples, Func<RgbF32, int, float> channel, Func<RawPixels, int, Span<float>> row)
    {
        for (int y = 0; y < Math.Min(rows, raster.Height); y++)
        {
            Span<RgbF32> source = raster.Row(y);
            Span<float> target = row(pixels, y0 + y).Slice(x0 * samples, cols * samples);
            int n = Math.Min(cols, source.Length);
            for (int x = 0; x < n; x++)
            {
                for (int c = 0; c < samples; c++)
                {
                    target[(x * samples) + c] = channel(source[x], c);
                }
            }
        }
    }
}
