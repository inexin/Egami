using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Jxl;

/// <content>
/// Layers (JxlImage.WriteLayers): each layer is a frame with its own crop, blend mode, duration and name, blended onto the
/// canvas the layers before it made, which each but the last saves in reference slot 1.
/// </content>
internal sealed partial class JxlWriter
{
    /// <summary>Writes the layers as a codestream of one frame each, in the pixel format of the first (with alpha when any has it).</summary>
    internal static void WriteLayers(Stream output, int width, int height, IReadOnlyList<JxlLayer> layers, JxlWriterOptions options, EgamiContext context)
    {
        PixelLayoutInfo first = layers[0].Raster.Layout;
        bool alpha = layers.Any(l => l.Raster.Layout.HasAlpha);
        bool gray = first.ChannelOrder is PixelChannelOrder.Gray or PixelChannelOrder.GrayAlpha;
        switch (first.ChannelType)
        {
            case PixelChannelType.Unorm8 when gray:
                if (alpha)
                {
                    WriteLayers<GrayAlpha8>(output, width, height, layers, options, context, alpha);
                }
                else
                {
                    WriteLayers<Gray8>(output, width, height, layers, options, context, alpha);
                }

                break;
            case PixelChannelType.Float32 or PixelChannelType.Float16 when gray && !alpha:
                WriteLayers<GrayF32>(output, width, height, layers, options, context, alpha);
                break;
            case PixelChannelType.Float32 or PixelChannelType.Float16 when gray:
                WriteLayers<RgbaF32>(output, width, height, layers, options, context, alpha);
                break;
            case PixelChannelType.Unorm8:
                WriteLayers<Rgba8>(output, width, height, layers, options, context, alpha);
                break;
            case PixelChannelType.Float32:
                WriteLayers<RgbaF32>(output, width, height, layers, options, context, alpha);
                break;
            case PixelChannelType.Float16:
                WriteLayers<RgbaF16>(output, width, height, layers, options, context, alpha);
                break;
            default:
                if (gray && alpha)
                {
                    WriteLayers<GrayAlpha16>(output, width, height, layers, options, context, alpha);
                }
                else if (gray)
                {
                    WriteLayers<Gray16>(output, width, height, layers, options, context, alpha);
                }
                else
                {
                    WriteLayers<Rgba16>(output, width, height, layers, options, context, alpha);
                }

                break;
        }
    }

    private static void WriteLayers<TPixel>(Stream output, int width, int height, IReadOnlyList<JxlLayer> layers, JxlWriterOptions options, EgamiContext context, bool alpha)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        RasterMetadata metadata = layers[0].Raster.Metadata;
        bool animated = layers.Any(l => l.Duration > TimeSpan.Zero);
        JxlAnimation? animation = animated ? new JxlAnimation(TicksPerSecond, 1, 0, false) : null;
        FrameCoder<TPixel> coder = new(metadata, options, context.MaxParallelism, width, height, alpha, options.Lossless ?? options.Distance is null, animation);
        using MemoryStream codestream = new();
        for (int i = 0; i < layers.Count; i++)
        {
            JxlLayer layer = layers[i];
            using Raster<TPixel> raster = layer.Raster.As<TPixel>();
            int w = raster.Width, h = raster.Height;
            TPixel[] pixels = new TPixel[w * h];
            for (int y = 0; y < h; y++)
            {
                raster.Row(y).CopyTo(pixels.AsSpan(y * w, w));
            }

            bool last = i == layers.Count - 1;
            JxlFrameInfo info = new()
            {
                Crop = layer.X != 0 || layer.Y != 0 || w != width || h != height,
                X0 = layer.X,
                Y0 = layer.Y,
                Blending = new JxlBlending((JxlBlendMode)layer.Blend, 0, false, CanvasSlot),
                Duration = animated ? (uint)Math.Round(layer.Duration.TotalMilliseconds) : 0,
                IsLast = last,
                SaveAsReference = last ? 0 : CanvasSlot,
                Name = layer.Name,
            };
            codestream.Write(coder.Code(pixels, w, 0, 0, w, h, info));
        }

        bool strip = options.StripMetadata;
        byte[]? exif = strip || metadata.Exif is null ? null : metadata.Exif.ToBytes().ToArray();
        byte[]? xmp = strip || metadata.Xmp.IsEmpty ? null : metadata.Xmp.ToArray();
        WriteFile(output, codestream.ToArray(), exif, xmp);
    }
}
