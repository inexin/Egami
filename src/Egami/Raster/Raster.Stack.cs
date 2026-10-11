using System.Buffers;
using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Operations;
using Egami.Pixels;

namespace Egami;

/// <summary>Stacking several same-size rasters into one.</summary>
public abstract partial class Raster
{
    /// <summary>
    /// Returns a new raster where each channel of each pixel is the <paramref name="mode"/> (mean, median, min or max) of that channel
    /// across the first frames of <paramref name="images"/>, as ImageMagick's -evaluate-sequence does: noise stacking, averaging
    /// exposures, background plates. Channels are combined as straight values, so a mean of semi-transparent images averages their
    /// colors and their alphas separately. The result has the first raster's pixel type and metadata. It works a few rows at a time,
    /// holding one row per input, so stacking 50 large photos needs 50 rows of memory, not 50 photos.
    /// </summary>
    /// <param name="images">At least two rasters of the same size, of any pixel types.</param>
    /// <param name="mode">How each channel is combined.</param>
    /// <returns>The new raster. The caller must dispose it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="images"/> or one of them is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">There are fewer than two rasters, or they differ in size.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not defined.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Arithmetic.cs" region="stack"/>
    /// </example>
    public static Raster Stack(IReadOnlyList<Raster> images, StackMode mode)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count < 2)
        {
            throw new ArgumentException("Stacking needs at least two rasters.", nameof(images));
        }

        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "The stack mode is not defined.");
        }

        for (int i = 0; i < images.Count; i++)
        {
            Raster image = images[i] ?? throw new ArgumentNullException(nameof(images), $"Raster {i} is null.");
            if (image.Width != images[0].Width || image.Height != images[0].Height)
            {
                throw new ArgumentException($"Every raster must be {images[0].Width}×{images[0].Height}; raster {i} is {image.Width}×{image.Height}.", nameof(images));
            }
        }

        return images[0].Accept(new StackVisitor(images, mode));
    }

    private sealed class StackVisitor(IReadOnlyList<Raster> images, StackMode mode) : IRasterVisitor<Raster>
    {
        public Raster Visit<TPixel>(Raster<TPixel> first)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            int width = first.Width, height = first.Height, count = images.Count;
            EgamiContext context = first.Context;
            PixelPlane<TPixel> plane = PixelPlane.Allocate<TPixel>(width, height, context.Memory);

            // Integer pixels combine as 16-bit samples, rounded as ImageMagick's Q16 build rounds, so results match -evaluate-sequence.
            bool samples16 = TPixel.Layout.ChannelType is PixelChannelType.Unorm8 or PixelChannelType.Unorm16 or PixelChannelType.Other;
            try
            {
                long rowBytes = (long)width * (samples16 ? 8 : 16) * count;
                Operations.RowRunner.Run(height, rowBytes, context.MaxParallelism, (start, end) =>
                {
                    // One row of every input, from the memory source, so it counts as pixel memory.
                    using IMemoryOwner<byte> block = context.Memory.Allocate((int)rowBytes, ClearMode.None);
                    Span<double> values = count <= 64 ? stackalloc double[count] : new double[count];
                    if (samples16)
                    {
                        Span<ushort> rows = MemoryMarshal.Cast<byte, ushort>(block.Memory.Span);
                        Rgba16[] output = ArrayPool<Rgba16>.Shared.Rent(width);
                        try
                        {
                            Span<ushort> result = MemoryMarshal.Cast<Rgba16, ushort>(output.AsSpan(0, width));
                            for (int y = start; y < end; y++)
                            {
                                for (int i = 0; i < count; i++)
                                {
                                    images[i].ReadRow(y, 0, MemoryMarshal.Cast<ushort, Rgba16>(rows.Slice(i * width * 4, width * 4)));
                                }

                                for (int s = 0; s < width * 4; s++)
                                {
                                    for (int i = 0; i < count; i++)
                                    {
                                        values[i] = rows[(i * width * 4) + s];
                                    }

                                    double v = Combine(values);
                                    result[s] = v <= 0 ? (ushort)0 : v >= 65535 ? (ushort)65535 : (ushort)(v + 0.5);
                                }

                                PixelConvert.Convert<Rgba16, TPixel>(output.AsSpan(0, width), plane.Row(y));
                            }
                        }
                        finally
                        {
                            ArrayPool<Rgba16>.Shared.Return(output);
                        }

                        return;
                    }

                    Span<float> floats = MemoryMarshal.Cast<byte, float>(block.Memory.Span);
                    RgbaF32[] row = ArrayPool<RgbaF32>.Shared.Rent(width);
                    try
                    {
                        Span<float> result = MemoryMarshal.Cast<RgbaF32, float>(row.AsSpan(0, width));
                        for (int y = start; y < end; y++)
                        {
                            for (int i = 0; i < count; i++)
                            {
                                images[i].ReadRow(y, 0, MemoryMarshal.Cast<float, RgbaF32>(floats.Slice(i * width * 4, width * 4)));
                            }

                            for (int s = 0; s < width * 4; s++)
                            {
                                for (int i = 0; i < count; i++)
                                {
                                    values[i] = floats[(i * width * 4) + s];
                                }

                                result[s] = (float)Combine(values);
                            }

                            PixelConvert.Convert<RgbaF32, TPixel>(row.AsSpan(0, width), plane.Row(y));
                        }
                    }
                    finally
                    {
                        ArrayPool<RgbaF32>.Shared.Return(row);
                    }
                }, CancellationToken.None);
            }
            catch
            {
                plane.Dispose();
                throw;
            }

            RasterMetadata metadata = first.Metadata.Copy();
            return new Raster<TPixel>([new Frame<TPixel>(plane, first.FrameMetadataAt(0).Copy())], metadata, context);
        }

        private double Combine(Span<double> values)
        {
            switch (mode)
            {
                case StackMode.Mean:
                    {
                        double sum = 0;
                        foreach (double v in values)
                        {
                            sum += v;
                        }

                        return sum / values.Length;
                    }

                case StackMode.Min:
                    {
                        double min = values[0];
                        foreach (double v in values)
                        {
                            min = Math.Min(min, v);
                        }

                        return min;
                    }

                case StackMode.Max:
                    {
                        double max = values[0];
                        foreach (double v in values)
                        {
                            max = Math.Max(max, v);
                        }

                        return max;
                    }

                default:
                    // The median of each channel: the middle value, the upper of the two middle ones for an even count. Small counts
                    // sort by insertion.
                    values.Sort();
                    return values[values.Length / 2];
            }
        }
    }
}
