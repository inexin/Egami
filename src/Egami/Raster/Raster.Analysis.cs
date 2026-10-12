using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

using Egami.Analysis;
using Egami.Operations;
using Egami.Pixels;

namespace Egami;

/// <summary>Measuring images: comparison, statistics, histograms, colors and a pixel signature. Nothing here changes a raster.</summary>
public abstract partial class Raster
{
    /// <summary>
    /// Compares two images of the same size and returns every cheap metric at once, per channel and overall: mean absolute and
    /// mean squared error, RMSE, PSNR, the peak error and how many pixels differ, all on the 0 to 1 scale, with SSIM on request.
    /// The images may have different pixel types: an Rgb8 and an Rgba8 copy of one picture compare as identical. Color profiles
    /// are not reconciled, so convert both to one with <see cref="ConvertColor{TTo}"/> first when they differ.
    /// </summary>
    /// <param name="a">The first image.</param>
    /// <param name="b">The second image, the same size.</param>
    /// <param name="options">The alpha treatment, fuzz, SSIM, region and frame, or <see langword="null"/> for <see cref="CompareOptions.Default"/>.</param>
    /// <returns>The differences.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="a"/> or <paramref name="b"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The images differ in size.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The frame is missing from an image, or the region leaves the image.</exception>
    /// <exception cref="ObjectDisposedException">An image has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Analysis.cs" region="compare"/>
    /// </example>
    public static CompareResult Compare(Raster a, Raster b, CompareOptions? options = null)
    {
        return Comparer.Compare(a, b, options ?? CompareOptions.Default, CancellationToken.None);
    }

    /// <summary>
    /// Returns a picture of where two images of the same size differ: <see cref="DifferenceStyle.Highlight"/> marks differing
    /// pixels in red over a faded copy of <paramref name="a"/>, as ImageMagick's compare does, and
    /// <see cref="DifferenceStyle.Absolute"/> shows |a − b| per channel. The result has <paramref name="a"/>'s pixel type and size.
    /// </summary>
    /// <param name="a">The first image.</param>
    /// <param name="b">The second image, the same size.</param>
    /// <param name="style">What to draw. Defaults to <see cref="DifferenceStyle.Highlight"/>.</param>
    /// <param name="options">The colors, fuzz, factor and frame, or <see langword="null"/> for <see cref="DifferenceOptions.Default"/>.</param>
    /// <returns>The picture, one frame. The caller must dispose it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="a"/> or <paramref name="b"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The images differ in size.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="style"/> is not defined, or the frame is missing.</exception>
    /// <exception cref="ObjectDisposedException">An image has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Analysis.cs" region="difference"/>
    /// </example>
    public static Raster Difference(Raster a, Raster b, DifferenceStyle style = DifferenceStyle.Highlight, DifferenceOptions? options = null)
    {
        if (!Enum.IsDefined(style))
        {
            throw new ArgumentOutOfRangeException(nameof(style), style, "The difference style is not defined.");
        }

        options ??= DifferenceOptions.Default;
        Comparer.Region(a, b, null, options.Frame);
        return a.Accept(new DifferenceVisitor(b, style, options));
    }

    /// <summary>
    /// Returns the statistics of each channel of a frame and of its luma: minimum, maximum, mean, standard deviation, skewness,
    /// kurtosis and entropy on the 0 to 1 scale, as ImageMagick's identify -verbose reports them. Channels are straight, not
    /// premultiplied.
    /// </summary>
    /// <param name="frame">The frame, 0 (the default) for the first.</param>
    /// <returns>The statistics.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frame"/> is not a frame of the image.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Analysis.cs" region="statistics"/>
    /// </example>
    public RasterStatistics Statistics(int frame = 0)
    {
        CheckFrame(frame);
        return StatisticsMeter.Measure(this, frame, CancellationToken.None);
    }

    /// <summary>Counts how many pixels of a frame fall into each of <paramref name="bins"/> equal ranges of one channel's values.</summary>
    /// <param name="channel">The channel, or <see cref="ColorChannel.Luminance"/> for the Rec. 709 luma.</param>
    /// <param name="bins">The number of bins, 2 to 65,536. Defaults to 256, one per 8-bit value.</param>
    /// <param name="frame">The frame, 0 (the default) for the first.</param>
    /// <returns>The histogram.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range or not defined.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Analysis.cs" region="histogram"/>
    /// </example>
    public Analysis.Histogram Histogram(ColorChannel channel, int bins = 256, int frame = 0)
    {
        if (!Enum.IsDefined(channel))
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "The channel is not defined.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(bins, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bins, 65536);
        CheckFrame(frame);
        int width = Width;
        long[] counts = new long[bins];
        object gate = new();
        RowRunner.Run(Height, (long)width * 32, Context.MaxParallelism, (start, end) =>
        {
            long[] local = new long[bins];
            RgbaD[] buffer = ArrayPool<RgbaD>.Shared.Rent(width);
            using RowReader reader = new(this, frame, width);
            try
            {
                for (int y = start; y < end; y++)
                {
                    Span<RgbaD> row = buffer.AsSpan(0, width);
                    reader.Read(y, 0, row);
                    foreach (RgbaD p in row)
                    {
                        double v = channel switch
                        {
                            ColorChannel.Red => p.R,
                            ColorChannel.Green => p.G,
                            ColorChannel.Blue => p.B,
                            ColorChannel.Alpha => p.A,
                            _ => (0.2126 * p.R) + (0.7152 * p.G) + (0.0722 * p.B),
                        };
                        local[(int)Math.Clamp(v * bins, 0, bins - 1)]++;
                    }
                }
            }
            finally
            {
                ArrayPool<RgbaD>.Shared.Return(buffer);
            }

            lock (gate)
            {
                for (int i = 0; i < bins; i++)
                {
                    counts[i] += local[i];
                }
            }
        }, CancellationToken.None);
        return new Analysis.Histogram(counts);
    }

    /// <summary>
    /// Counts the distinct stored colors of a frame, exactly, as ImageMagick's %k does. Each distinct stored value counts, so two
    /// float pixels that differ in the last bit are two colors. Types of up to 3 bytes use a 2 MB table; wider ones keep a set of
    /// the colors seen, which counts against <see cref="SafetyLimits.MaxTotalBytes"/> at its worst case (every pixel distinct).
    /// </summary>
    /// <param name="frame">The frame, 0 (the default) for the first.</param>
    /// <returns>The number of distinct colors.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frame"/> is not a frame of the image.</exception>
    /// <exception cref="LimitExceededException">The set could break <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Analysis.cs" region="colors"/>
    /// </example>
    public long CountColors(int frame = 0)
    {
        CheckFrame(frame);
        return Accept(new CountColorsVisitor(frame));
    }

    /// <summary>Returns the distinct colors of a frame, as 16-bit colors in ascending order, when there are at most <paramref name="limit"/>.</summary>
    /// <param name="limit">The most colors wanted; the scan stops once there are more.</param>
    /// <param name="frame">The frame, 0 (the default) for the first.</param>
    /// <returns>The colors, or <see langword="null"/> when there are more than <paramref name="limit"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is negative, or <paramref name="frame"/> is not a frame of the image.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Analysis.cs" region="colors"/>
    /// </example>
    public IReadOnlyList<Rgba16>? GetColors(int limit, int frame = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        CheckFrame(frame);
        return ColorScans.GetColors(this, frame, limit);
    }

    /// <summary>
    /// Returns whether every pixel is fully opaque. It stops at the first pixel that is not, and answers at once for pixel types
    /// without alpha.
    /// </summary>
    /// <param name="frame">The frame, or -1 (the default) for every frame.</param>
    /// <returns><see langword="true"/> when no pixel has alpha below 1.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frame"/> is not -1 or a frame of the image.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Analysis.cs" region="colors"/>
    /// </example>
    public bool IsOpaque(int frame = -1)
    {
        CheckFrameOrAll(frame);
        if (!Layout.HasAlpha)
        {
            return true;
        }

        return !AnyFrame(frame, p => p.A < 1);
    }

    /// <summary>
    /// Returns whether every pixel is gray: its red, green and blue lie within <paramref name="tolerance"/> of each other, as
    /// ImageMagick's Grayscale type with -fuzz. It stops at the first pixel that is not, and answers at once for gray pixel types.
    /// </summary>
    /// <param name="tolerance">How far apart the channels may be, from 0 (equal) to 1.</param>
    /// <param name="frame">The frame, or -1 (the default) for every frame.</param>
    /// <returns><see langword="true"/> when every pixel is gray.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Analysis.cs" region="colors"/>
    /// </example>
    public bool IsGray(float tolerance = 0, int frame = -1)
    {
        if (!(tolerance >= 0 && tolerance <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance), tolerance, "The tolerance must be from 0 to 1.");
        }

        CheckFrameOrAll(frame);
        if (Layout.ChannelOrder is PixelChannelOrder.Gray or PixelChannelOrder.GrayAlpha or PixelChannelOrder.Alpha)
        {
            return true;
        }

        return !AnyFrame(frame, p => Math.Max(p.R, Math.Max(p.G, p.B)) - Math.Min(p.R, Math.Min(p.G, p.B)) > tolerance);
    }

    /// <summary>
    /// Returns a SHA-256 fingerprint of the pixels, as 64 lowercase hex digits: of the size, the frame count, and each frame's
    /// delay and pixels as 16-bit RGBA. Two images get the same signature exactly when they show the same pixels, whatever file
    /// or pixel type they came from: an 8-bit image keeps its signature when converted to 16 bits. Metadata other than the
    /// delays does not count.
    /// </summary>
    /// <returns>The signature.</returns>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Analysis.cs" region="signature"/>
    /// </example>
    public string Signature()
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(header, Width);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], Height);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], FrameCount);
        hash.AppendData(header);
        Rgba16[] row = ArrayPool<Rgba16>.Shared.Rent(Width);
        byte[] bytes = ArrayPool<byte>.Shared.Rent(Width * 8);
        Span<byte> delay = stackalloc byte[8];
        try
        {
            for (int f = 0; f < FrameCount; f++)
            {
                BinaryPrimitives.WriteInt64LittleEndian(delay, FrameMetadataAt(f).Delay.Ticks);
                hash.AppendData(delay);
                for (int y = 0; y < Height; y++)
                {
                    Span<Rgba16> pixels = row.AsSpan(0, Width);
                    ReadFrameRow(f, y, 0, pixels);
                    for (int x = 0; x < pixels.Length; x++)
                    {
                        Span<byte> at = bytes.AsSpan(x * 8, 8);
                        BinaryPrimitives.WriteUInt16LittleEndian(at, pixels[x].R);
                        BinaryPrimitives.WriteUInt16LittleEndian(at[2..], pixels[x].G);
                        BinaryPrimitives.WriteUInt16LittleEndian(at[4..], pixels[x].B);
                        BinaryPrimitives.WriteUInt16LittleEndian(at[6..], pixels[x].A);
                    }

                    hash.AppendData(bytes, 0, Width * 8);
                }
            }
        }
        finally
        {
            ArrayPool<Rgba16>.Shared.Return(row);
            ArrayPool<byte>.Shared.Return(bytes);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private void CheckFrame(int frame)
    {
        if (frame < 0 || frame >= FrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(frame), frame, $"The image has {FrameCount} frames.");
        }
    }

    private void CheckFrameOrAll(int frame)
    {
        if (frame < -1 || frame >= FrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(frame), frame, $"The frame must be -1 (every frame) or below {FrameCount}.");
        }
    }

    private bool AnyFrame(int frame, Func<RgbaF32, bool> test)
    {
        for (int f = frame < 0 ? 0 : frame; f < (frame < 0 ? FrameCount : frame + 1); f++)
        {
            if (ColorScans.Any(this, f, test, CancellationToken.None))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class CountColorsVisitor(int frame) : IRasterVisitor<long>
    {
        public long Visit<TPixel>(Raster<TPixel> raster)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            raster.ThrowIfDisposed();
            return ColorScans.CountColors(raster.Frames[frame].Plane, raster.Context, CancellationToken.None);
        }
    }

    private sealed class DifferenceVisitor(Raster b, DifferenceStyle style, DifferenceOptions options) : IRasterVisitor<Raster>
    {
        public Raster Visit<TPixel>(Raster<TPixel> a)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            int width = a.Width;
            Raster<TPixel> result = Create<TPixel>(width, a.Height, a.Context);
            try
            {
                bool premultiply = options.Alpha == AlphaCompare.Premultiplied;
                double fuzz = options.Fuzz;
                double keep = options.LowlightOpacity;
                RgbaF32 high = RgbaF32.FromRgba8(options.HighlightColor);
                RgbaF32 low = RgbaF32.FromRgba8(options.LowlightColor);
                RowRunner.Run(a.Height, (long)width * 48, a.Context.MaxParallelism, (start, end) =>
                {
                    RgbaD[] pa = ArrayPool<RgbaD>.Shared.Rent(width);
                    RgbaD[] pb = ArrayPool<RgbaD>.Shared.Rent(width);
                    RgbaF32[] output = ArrayPool<RgbaF32>.Shared.Rent(width);
                    using RowReader readA = new(a, options.Frame, width);
                    using RowReader readB = new(b, options.Frame, width);
                    try
                    {
                        for (int y = start; y < end; y++)
                        {
                            readA.Read(y, 0, pa.AsSpan(0, width));
                            readB.Read(y, 0, pb.AsSpan(0, width));
                            for (int x = 0; x < width; x++)
                            {
                                RgbaD p = pa[x], q = pb[x];
                                double sa = premultiply ? p.A : 1, sb = premultiply ? q.A : 1;
                                double dr = Math.Abs((p.R * sa) - (q.R * sb));
                                double dg = Math.Abs((p.G * sa) - (q.G * sb));
                                double db = Math.Abs((p.B * sa) - (q.B * sb));
                                if (style == DifferenceStyle.Absolute)
                                {
                                    double k = options.Amplify;
                                    output[x] = new RgbaF32((float)Math.Min(1, dr * k), (float)Math.Min(1, dg * k), (float)Math.Min(1, db * k), 1);
                                    continue;
                                }

                                bool differs = dr > fuzz || dg > fuzz || db > fuzz || Math.Abs(p.A - q.A) > fuzz;
                                RgbaF32 color = differs ? high : low;
                                double mix = 1 - keep;
                                // The faded copy is the first image flattened onto white, as ImageMagick shows it.
                                double white = 1 - p.A;
                                output[x] = new RgbaF32(
                                    (float)((color.R * mix) + (((p.R * p.A) + white) * keep)),
                                    (float)((color.G * mix) + (((p.G * p.A) + white) * keep)),
                                    (float)((color.B * mix) + (((p.B * p.A) + white) * keep)),
                                    1);
                            }

                            PixelConvert.Convert<RgbaF32, TPixel>(output.AsSpan(0, width), result.Row(y));
                        }
                    }
                    finally
                    {
                        ArrayPool<RgbaD>.Shared.Return(pa);
                        ArrayPool<RgbaD>.Shared.Return(pb);
                        ArrayPool<RgbaF32>.Shared.Return(output);
                    }
                }, CancellationToken.None);
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }
    }
}
