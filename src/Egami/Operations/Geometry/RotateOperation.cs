using System.Buffers;
using System.Numerics;

using Egami.Operations.Resampling;
using Egami.Pixels;

namespace Egami.Operations.Geometry;

/// <summary>
/// Rotates by any angle onto a canvas the size of the rotated bounding box. Each output pixel maps back to a source point and is
/// sampled with the filter's 2D kernel (the product of the 1D kernel in x and y, unstretched, since rotation keeps scale).
/// Sampling is premultiplied, and taps that fall outside the source read the background, so the image edges are antialiased.
/// </summary>
internal sealed class RotateOperation(double degrees, ResampleFilter filter, Rgba8 background) : IRasterOperation, INeedsNewPlane, IUpdatesMetadata
{
    public void UpdateMetadata(Metadata.RasterMetadata metadata, Size input, Size output)
    {
        ExifUpkeep.Upright(metadata);
        ExifUpkeep.Dimensions(metadata, output);
        ExifUpkeep.DropThumbnail(metadata);
        ExifUpkeep.Subject(metadata, SubjectMap.Rotate(input, degrees, output));
    }

    private readonly double _cos = Math.Cos(degrees * Math.PI / 180);
    private readonly double _sin = Math.Sin(degrees * Math.PI / 180);

    public bool NeedsNewPlane(Size input)
    {
        return true;
    }

    public Size OutputSize(Size input)
    {
        // The bounding box is rounded to the nearest pixel, so a tiny angle keeps the size instead of growing a pixel.
        double width = (input.Width * Math.Abs(_cos)) + (input.Height * Math.Abs(_sin));
        double height = (input.Width * Math.Abs(_sin)) + (input.Height * Math.Abs(_cos));
        return new Size(Math.Max(1, (int)Math.Round(width, MidpointRounding.AwayFromZero)), Math.Max(1, (int)Math.Round(height, MidpointRounding.AwayFromZero)));
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        // The footprint sampler's plain kernel is Rotate's own: the filter's 2D kernel, unstretched, premultiplied, with taps
        // outside the source reading the background.
        SamplerSettings settings = new(filter, EdgeMode.Transparent, background, curve: null);
        PixelPlane<TPixel> source = input.Source;
        PixelPlane<TPixel> destination = input.Destination;
        RowConverter<TPixel> converter = new(curve: null);
        bool nearest = settings.Taps == 1;
        TPixel fill = TPixel.FromRgba8(background);

        // Output pixel centers, relative to the output center, rotate back onto source positions relative to the source center.
        // Pixel i covers i..i+1, so its center is at i + 0.5; subtracting 0.5 gives positions where pixel i's center is i.
        double ocx = destination.Width / 2.0;
        double ocy = destination.Height / 2.0;
        double scx = (source.Width / 2.0) - 0.5;
        double scy = (source.Height / 2.0) - 0.5;
        input.ForEachBand((start, end) =>
        {
            float[] rowBuffer = ArrayPool<float>.Shared.Rent(destination.Width * 4);
            try
            {
                FootprintSampler<TPixel> sampler = new(source, settings);
                Span<float> row = rowBuffer.AsSpan(0, destination.Width * 4);
                for (int y = start; y < end; y++)
                {
                    // Positions advance by (cos, −sin) per output pixel along the row.
                    double dy = y + 0.5 - ocy;
                    double dx0 = 0.5 - ocx;
                    double u0 = scx + (_cos * dx0) + (_sin * dy);
                    double v0 = scy - (_sin * dx0) + (_cos * dy);
                    if (nearest)
                    {
                        // Point sampling copies the chosen pixel exactly.
                        sampler.PointRow(u0, v0, _cos, -_sin, destination.Row(y), fill);
                        continue;
                    }

                    sampler.KernelRow(u0, v0, _cos, -_sin, row);
                    converter.Store(row, destination.Row(y), opaque: false);
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(rowBuffer);
            }
        });
    }
}
