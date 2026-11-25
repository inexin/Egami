using Egami.Formats;
using Egami.Formats.Eps;
using Egami.Metafiles;
using Egami.Pixels;

namespace Egami.Fuzz;

/// <summary>
/// EPS: the container (<see cref="EpsFile"/>), then Probe and Load in both recovery modes with Egami.Metafiles registered, so
/// TIFF, metafile, EPSI and Photoshop images are all reached. Only an <see cref="EgamiException"/> may come out.
/// </summary>
internal static class EpsFuzzTarget
{
    public static void Run(Stream stream, EgamiContext context)
    {
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        byte[] data = copy.ToArray();
        try
        {
            EpsFile file = EpsFile.Load(data, context.Limits);
            _ = file.Preview;
            _ = file.BoundingBox;
        }
        catch (EgamiException)
        {
        }

        EgamiContext eps = context.WithFormat(MetafileFormat.Info) with { Formats = [EpsFormat.Info, MetafileFormat.Info] };
        foreach (RecoveryMode recovery in new[] { RecoveryMode.Lenient, RecoveryMode.Strict })
        {
            ReaderOptions options = new() { Recovery = recovery, Format = EpsFormat.Info };
            try
            {
                _ = Raster.Probe(data, eps, options);
                using Raster raster = Raster.Load(data, eps, options);
                using Raster<Rgba8> rgba = Raster.Load<Rgba8>(data, eps, options);
            }
            catch (EgamiException)
            {
            }
        }
    }
}
