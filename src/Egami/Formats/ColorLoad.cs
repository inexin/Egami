using Egami.Colors;
using Egami.Pixels;

namespace Egami.Formats;

/// <summary>
/// <see cref="ReaderOptions.ConvertColorTo"/>: a raster loaded as stored, converted, and the stored one released. Converting
/// after the load gives exactly what load-then-convert gives, which is what the option promises.
/// </summary>
internal static class ColorLoad
{
    /// <summary>Converts to <typeparamref name="TPixel"/>.</summary>
    public static Raster<TPixel> Typed<TPixel>(Raster loaded, ColorProfile target)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using (loaded)
        {
            return loaded.ConvertColor<TPixel>(target);
        }
    }

    /// <summary>
    /// Converts in place when the model stays the same; otherwise to the target model's type at the stored depth, keeping alpha
    /// where the target model has a type with it.
    /// </summary>
    public static Raster Natural(Raster loaded, ColorProfile target)
    {
        try
        {
            Raster result = loaded.Accept(new Visitor(target));
            if (!ReferenceEquals(result, loaded))
            {
                loaded.Dispose();
            }

            return result;
        }
        catch
        {
            loaded.Dispose();
            throw;
        }
    }

    private sealed class Visitor(ColorProfile target) : IRasterVisitor<Raster>
    {
        public Raster Visit<TPixel>(Raster<TPixel> raster)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            if (ColorTransform.ModelOf<TPixel>() is not ColorModel model)
            {
                return raster;
            }

            if (model == target.Model)
            {
                raster.Apply(ops => ops.ConvertColor(target));
                return raster;
            }

            PixelLayoutInfo layout = TPixel.Layout;
            int depth = layout.ChannelType switch
            {
                PixelChannelType.Unorm8 => 8,
                PixelChannelType.Unorm16 => 16,
                _ => 32,
            };
            bool alpha = layout.HasAlpha;
            return (target.Model, depth, alpha) switch
            {
                (ColorModel.Rgb, 8, false) => raster.ConvertColor<Rgb8>(target),
                (ColorModel.Rgb, 8, true) => raster.ConvertColor<Rgba8>(target),
                (ColorModel.Rgb, 16, false) => raster.ConvertColor<Rgb16>(target),
                (ColorModel.Rgb, 16, true) => raster.ConvertColor<Rgba16>(target),
                (ColorModel.Rgb, _, false) => raster.ConvertColor<RgbF32>(target),
                (ColorModel.Rgb, _, true) => raster.ConvertColor<RgbaF32>(target),
                (ColorModel.Gray, 8, false) => raster.ConvertColor<Gray8>(target),
                (ColorModel.Gray, 8, true) => raster.ConvertColor<GrayAlpha8>(target),
                (ColorModel.Gray, 16, false) => raster.ConvertColor<Gray16>(target),
                (ColorModel.Gray, 16, true) => raster.ConvertColor<GrayAlpha16>(target),
                (ColorModel.Gray, _, _) => raster.ConvertColor<GrayF32>(target),
                (_, 8, _) => raster.ConvertColor<Cmyk8>(target),
                _ => raster.ConvertColor<Cmyk16>(target),
            };
        }
    }
}
