using Egami.Formats;
using Egami.Formats.Extended;
using Egami.Pixels;

namespace Egami.Fuzz;

/// <summary>
/// The Egami.Formats.Extended fuzz targets (pcx, sgi, sun, xbm, xpm, wbmp and farbfeld): each loads its format every way, Probe,
/// Load leniently and strictly, as Rgba8 from a stream that cannot seek and row by row through RasterDecoder, which must agree
/// with Load; a file that loads is saved back in its format and must load again at its size. Only EgamiExceptions may escape.
/// </summary>
internal static class ExtendedFuzzTarget
{
    public static void Load(byte[] data, EgamiContext context, string name)
    {
        context = context.WithExtendedFormats();
        FormatInfo format = context.Formats.Single(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        context = context with { Formats = [format] };
        foreach (RecoveryMode recovery in (RecoveryMode[])[RecoveryMode.Lenient, RecoveryMode.Strict])
        {
            ReaderOptions options = new() { Recovery = recovery };
            try
            {
                RasterInfo info = Raster.Probe(data, format, context, options);
                using Raster raster = Raster.Load(data, format, context, options);
                if (raster.Layout != info.Layout || raster.Width != info.Width || raster.Height != info.Height)
                {
                    throw new InvalidOperationException($"{name}: Probe says {info.Width}×{info.Height} {info.Layout.ChannelOrder}, Load gives {raster.Width}×{raster.Height} {raster.Layout.ChannelOrder}.");
                }

                using Raster<Rgba8> converted = Raster.Load<Rgba8>(new ForwardOnlyStream(data), context, options);
                using RasterDecoder decoder = RasterDecoder.Open(new ForwardOnlyStream(data), context, options);
                using Raster<Rgba8> band = Raster.Create<Rgba8>(converted.Width, Math.Min(converted.Height, 16), context);
                int y = 0;
                while (decoder.ReadRows(band) is int rows and > 0)
                {
                    for (int i = 0; i < rows; i++)
                    {
                        if (!band.Row(i).SequenceEqual(converted.Row(y + i)))
                        {
                            throw new InvalidOperationException($"{name}: row {y + i} streams differently from Load.");
                        }
                    }

                    y += rows;
                }

                if (recovery == RecoveryMode.Lenient)
                {
                    using MemoryStream output = new();
                    raster.Save(output, format);
                    using Raster back = Raster.Load(output.ToArray(), format, context);
                    if (back.Width != raster.Width || back.Height != raster.Height)
                    {
                        throw new InvalidOperationException($"{name}: a saved file loads at another size.");
                    }
                }
            }
            catch (EgamiException)
            {
            }
            catch (NotSupportedException) when (recovery == RecoveryMode.Lenient)
            {
                // A writer refuses sizes its format cannot hold, such as PCX over 65,534 pixels wide.
            }
        }
    }
}
