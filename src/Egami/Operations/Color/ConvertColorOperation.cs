using Egami.Colors;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Operations.Color;

/// <summary>
/// Converts pixels in place from the raster's encoding to another of the same model. Recorded unbound; the pipeline binds it to
/// the encoding the raster has at that point in the chain (<see cref="Bind"/>), and records the new encoding in the metadata.
/// </summary>
internal sealed class ConvertColorOperation(ColorProfile target, ColorTransformOptions options, ColorProfile? source = null) : IRasterOperation, IUpdatesColor
{
    public ColorProfile Target => target;

    public ColorProfile? Source => source;

    public ConvertColorOperation Bind(ColorProfile from) => new(target, options, from);

    public Size OutputSize(Size input) => input;

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (ColorTransform.ModelOf<TPixel>() is not ColorModel model)
        {
            return;   // an alpha mask has no color
        }

        ColorProfile from = ForPixels(source ?? ColorProfile.Srgb, model);
        ColorProfile to = ForPixels(target, model);
        if (to.Model != model)
        {
            throw new InvalidOperationException(
                $"ConvertColor converts in place, so it keeps the pixel model ({model}); '{target.Name}' is {target.Model}. Use Raster.ConvertColor<TTo> to change the pixel type.");
        }

        ColorTransform transform = ColorTransform.Create(from, to, options, input.Context);
        if (transform.IsIdentity && input.IsInPlace)
        {
            return;
        }

        PixelPlane<TPixel> src = input.Source, dst = input.Destination;
        input.ForEachBand((start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                transform.Apply<TPixel, TPixel>(src.Row(y), dst.Row(y));
            }
        });
    }

    public void UpdateMetadata(RasterMetadata metadata) => metadata.SetColorProfile(target);

    /// <summary>
    /// Returns the encoding as pixels of <paramref name="model"/> read it: gray pixels take an RGB encoding's curve and white.
    /// Other mismatches keep the encoding, and the pixels are read in its model numerically.
    /// </summary>
    internal static ColorProfile ForPixels(ColorProfile profile, ColorModel model)
    {
        if (model == ColorModel.Gray && profile.Model == ColorModel.Rgb && profile.Transfer is TransferFunction curve)
        {
            if (profile.IsEquivalentTo(ColorProfile.Srgb))
            {
                return ColorProfile.GraySrgb;
            }

            return profile.IsEquivalentTo(ColorProfile.LinearSrgb) ? ColorProfile.GrayLinear : ColorProfile.CreateGray(curve, profile.White, profile.Name);
        }

        return profile;
    }
}

/// <summary>A step that changes what the pixels' color values mean, and so the metadata that says it.</summary>
internal interface IUpdatesColor
{
    void UpdateMetadata(RasterMetadata metadata);
}
