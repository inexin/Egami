using System.Numerics;

using Egami.Colors;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Operations.Color;

/// <summary>
/// Tone maps linear HDR pixels (reference white 1.0) into 0..1 on max(R, G, B). Recorded unbound; the pipeline binds it to the
/// raster's <see cref="HdrMetadata"/> for the source peak when none was given.
/// </summary>
internal sealed class ToneMapOperation(ToneMapping method, float? sourcePeakNits, HdrMetadata? hdr = null) : IRasterOperation, IUpdatesColor
{
    private const double ReferenceWhite = 203;

    public ToneMapOperation Bind(HdrMetadata? metadata) => new(method, sourcePeakNits, metadata);

    public Size OutputSize(Size input) => input;

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (ColorTransform.ModelOf<TPixel>() is not ColorModel model || model == ColorModel.Cmyk)
        {
            if (!input.IsInPlace)
            {
                input.ForEachBand((start, end) =>
                {
                    for (int y = start; y < end; y++)
                    {
                        input.Source.Row(y).CopyTo(input.Destination.Row(y));
                    }
                });
            }

            return;
        }

        double peak = new ColorTransformOptions { SourcePeakNits = sourcePeakNits }.PeakFor(hdr) / ReferenceWhite;
        ToneTable table = new(method, peak);
        bool gray = model == ColorModel.Gray;
        PixelPlane<TPixel> src = input.Source, dst = input.Destination;
        input.ForEachBand((start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                Span<TPixel> from = src.Row(y), to = dst.Row(y);
                for (int x = 0; x < from.Length; x++)
                {
                    Vector4 v = from[x].ToScaled();
                    float max = gray ? v.X : MathF.Max(v.X, MathF.Max(v.Y, v.Z));
                    float ratio = table.Ratio(max);
                    to[x] = TPixel.FromScaled(new Vector4(v.X * ratio, v.Y * ratio, v.Z * ratio, v.W));
                }
            }
        });
    }

    public void UpdateMetadata(RasterMetadata metadata) => metadata.Hdr = null;
}
