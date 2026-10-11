using System.Buffers;

using Egami.Metadata;
using Egami.Operations;
using Egami.Pixels;

namespace Egami;

/// <summary>Channel split and join.</summary>
public abstract partial class Raster
{
    /// <summary>
    /// Returns a new gray raster holding one channel of every frame: <see cref="Gray8"/> from 8-bit pixel types, <see cref="Gray16"/>
    /// from 16-bit ones, <see cref="GrayF32"/> from half and float ones. A gray raster's red, green and blue are its gray value; a
    /// raster without alpha gives an all-white alpha. Resolution and EXIF are kept; the ICC profile is dropped, as one channel of a
    /// color image is not a gray image in any profile.
    /// </summary>
    /// <param name="channel">The channel. <see cref="ColorChannel.Luminance"/> is 0.2126 R + 0.7152 G + 0.0722 B of the stored values.</param>
    /// <returns>The new raster. The caller must dispose it.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channel"/> is not defined.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Channels.cs" region="extract-combine"/>
    /// </example>
    public Raster ExtractChannel(ColorChannel channel)
    {
        return Layout.ChannelType switch
        {
            PixelChannelType.Float16 or PixelChannelType.Float32 => ExtractChannel<GrayF32>(channel),
            PixelChannelType.Unorm16 => ExtractChannel<Gray16>(channel),
            PixelChannelType.Other when MaxBits(Layout) > 8 => ExtractChannel<Gray16>(channel),
            _ => ExtractChannel<Gray8>(channel),
        };
    }

    /// <summary>
    /// Returns a new raster of <typeparamref name="TPixel"/> holding one channel of every frame as its gray value, as
    /// <see cref="ExtractChannel(ColorChannel)"/> does, converted to the type asked for.
    /// </summary>
    /// <typeparam name="TPixel">The pixel type of the result, usually a gray type.</typeparam>
    /// <param name="channel">The channel.</param>
    /// <returns>The new raster. The caller must dispose it.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channel"/> is not defined.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Channels.cs" region="extract-combine"/>
    /// </example>
    public Raster<TPixel> ExtractChannel<TPixel>(ColorChannel channel)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (!Enum.IsDefined(channel))
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "The channel is not defined.");
        }

        RasterMetadata metadata = Metadata.Copy();
        DropProfile(metadata);
        return Build<TPixel>(Width, Height, FrameCount, Context, metadata, FrameMetadataAt, writesRgba: false, (frame, y, rgba, gray) =>
        {
            ReadFrameRow(frame, y, 0, rgba);
            for (int x = 0; x < gray.Length; x++)
            {
                RgbaF32 p = rgba[x];
                gray[x] = new GrayF32(channel switch
                {
                    ColorChannel.Red => p.R,
                    ColorChannel.Green => p.G,
                    ColorChannel.Blue => p.B,
                    ColorChannel.Alpha => p.A,
                    _ => (0.2126f * p.R) + (0.7152f * p.G) + (0.0722f * p.B),
                });
            }
        });
    }

    /// <summary>
    /// Builds a raster from three or four rasters of the same size, reading each one's gray value (the luminance of a color raster,
    /// the alpha of an <see cref="Alpha8"/> raster) as red, green, blue and alpha. Without <paramref name="alpha"/> the result is
    /// opaque. Frame counts must match, or be 1, which is then used for every frame. The result takes <paramref name="red"/>'s
    /// metadata and frame metadata, with the sRGB profile.
    /// </summary>
    /// <typeparam name="TPixel">The pixel type of the result.</typeparam>
    /// <param name="red">The red channel.</param>
    /// <param name="green">The green channel.</param>
    /// <param name="blue">The blue channel.</param>
    /// <param name="alpha">The alpha channel, or <see langword="null"/> for opaque.</param>
    /// <returns>The new raster. The caller must dispose it.</returns>
    /// <exception cref="ArgumentNullException">A color channel is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The rasters differ in size, or in frame count other than 1.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Channels.cs" region="extract-combine"/>
    /// </example>
    public static Raster<TPixel> CombineChannels<TPixel>(Raster red, Raster green, Raster blue, Raster? alpha = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ArgumentNullException.ThrowIfNull(red);
        ArgumentNullException.ThrowIfNull(green);
        ArgumentNullException.ThrowIfNull(blue);
        Raster[] inputs = alpha is null ? [red, green, blue] : [red, green, blue, alpha];
        int frames = inputs.Max(r => r.FrameCount);
        foreach (Raster r in inputs)
        {
            if (r.Width != red.Width || r.Height != red.Height)
            {
                throw new ArgumentException($"Every channel must be {red.Width}×{red.Height}; one is {r.Width}×{r.Height}.", nameof(red));
            }

            if (r.FrameCount != frames && r.FrameCount != 1)
            {
                throw new ArgumentException($"Every channel must have {frames} frames or 1; one has {r.FrameCount}.", nameof(red));
            }
        }

        RasterMetadata metadata = red.Metadata.Copy();
        DropProfile(metadata);
        return Build<TPixel>(red.Width, red.Height, frames, red.Context, metadata, f => red.FrameMetadataAt(Math.Min(f, red.FrameCount - 1)), writesRgba: true, (frame, y, rgba, row) =>
        {
            float[] gray = ArrayPool<float>.Shared.Rent(4 * row.Length);
            try
            {
                for (int c = 0; c < inputs.Length; c++)
                {
                    Raster input = inputs[c];
                    input.ReadFrameRow(Math.Min(frame, input.FrameCount - 1), y, 0, rgba);
                    PixelChannelOrder order = input.Layout.ChannelOrder;
                    for (int x = 0; x < row.Length; x++)
                    {
                        RgbaF32 p = rgba[x];
                        gray[(x * 4) + c] = order switch
                        {
                            PixelChannelOrder.Gray or PixelChannelOrder.GrayAlpha => p.R,
                            PixelChannelOrder.Alpha => p.A,
                            _ => (0.2126f * p.R) + (0.7152f * p.G) + (0.0722f * p.B),
                        };
                    }
                }

                for (int x = 0; x < row.Length; x++)
                {
                    rgba[x] = new RgbaF32(gray[x * 4], gray[(x * 4) + 1], gray[(x * 4) + 2], inputs.Length == 4 ? gray[(x * 4) + 3] : 1f);
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(gray);
            }
        });
    }

    private static int MaxBits(PixelLayoutInfo layout)
    {
        int max = 0;
        foreach (int bits in layout.ChannelBits)
        {
            max = Math.Max(max, bits);
        }

        return max;
    }

    private static void DropProfile(RasterMetadata metadata)
    {
        metadata.IccProfile = default;
        metadata.Gamma = null;
        if (metadata.ColorSpace is ColorSpaceHint.EmbeddedIcc or ColorSpaceHint.Gamma)
        {
            metadata.ColorSpace = ColorSpaceHint.Unknown;
        }
    }

    /// <summary>
    /// Allocates a raster and fills each row through <paramref name="fill"/>, which gets a scratch RgbaF32 row and a GrayF32 row; with
    /// <paramref name="writesRgba"/> the RgbaF32 row is the result (CombineChannels), otherwise the gray row is (ExtractChannel).
    /// </summary>
    private static Raster<TPixel> Build<TPixel>(
        int width, int height, int frameCount, EgamiContext context, RasterMetadata metadata, Func<int, FrameMetadata> frameMetadata,
        bool writesRgba, RowFill fill)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        List<Frame<TPixel>> frames = new(frameCount);
        RgbaF32[] rgba = ArrayPool<RgbaF32>.Shared.Rent(width);
        GrayF32[] gray = ArrayPool<GrayF32>.Shared.Rent(width);
        try
        {
            for (int f = 0; f < frameCount; f++)
            {
                PixelPlane<TPixel> plane = PixelPlane.Allocate<TPixel>(width, height, context.Memory);
                frames.Add(new Frame<TPixel>(plane, frameMetadata(f).Copy()));
                for (int y = 0; y < height; y++)
                {
                    Span<RgbaF32> rgbaRow = rgba.AsSpan(0, width);
                    Span<GrayF32> grayRow = gray.AsSpan(0, width);
                    grayRow.Clear();
                    rgbaRow.Clear();
                    fill(f, y, rgbaRow, grayRow);
                    if (writesRgba)
                    {
                        PixelConvert.Convert<RgbaF32, TPixel>(rgbaRow, plane.Row(y));
                    }
                    else
                    {
                        PixelConvert.Convert<GrayF32, TPixel>(grayRow, plane.Row(y));
                    }
                }
            }
        }
        catch
        {
            foreach (Frame<TPixel> frame in frames)
            {
                frame.Plane.Dispose();
            }

            throw;
        }
        finally
        {
            ArrayPool<RgbaF32>.Shared.Return(rgba);
            ArrayPool<GrayF32>.Shared.Return(gray);
        }

        return new Raster<TPixel>(frames, metadata, context);
    }

    private delegate void RowFill(int frame, int y, Span<RgbaF32> rgba, Span<GrayF32> gray);
}
