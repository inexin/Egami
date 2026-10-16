using Egami.Formats;
using Egami.Formats.Av1;
using Egami.Formats.Avif;
using Egami.Pixels;

namespace Egami.Fuzz;

/// <summary>
/// The AVIF fuzz targets (Phase 2.9.2). avif loads files every way: Probe, Load leniently and strictly, as Rgba8 from a stream
/// that cannot seek, a region, the HDR rendition of a gain map, the YUV planes, without film grain, and every image item on its
/// own. avifbox puts its input after an ftyp box, so mutations reach the HEIF box reader (meta, iinf, iloc, iref, iprp, grpl and
/// the track boxes) directly. av1 feeds a raw OBU stream to the AV1 decoder, with and without its in-loop filters, its film grain
/// applied, on one thread or four. Only EgamiExceptions may escape.
/// </summary>
internal static class AvifFuzzTarget
{
    private static readonly byte[] Ftyp = [0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'a', (byte)'v', (byte)'i', (byte)'f', 0, 0, 0, 0,
        (byte)'a', (byte)'v', (byte)'i', (byte)'f', (byte)'m', (byte)'i', (byte)'f', (byte)'1'];

    public static void Load(byte[] data, EgamiContext context)
    {
        context = context with { Formats = [AvifFormat.Info], Limits = context.Limits with { MaxFrames = 32 } };
        ReaderOptions[] all =
        [
            new() { Recovery = RecoveryMode.Lenient },
            new() { Recovery = RecoveryMode.Strict },
            new AvifReaderOptions { FilmGrain = false },
            new() { Region = new Rect(1, 1, 7, 5) },
            new() { Hdr = new HdrDecode { Headroom = 4 } },
        ];

        // One extra reading per input, picked by its length, keeps an execution within AFL's time limit.
        foreach (ReaderOptions options in new[] { all[0], all[1 + (data.Length % 4)] })
        {
            try
            {
                RasterInfo info = Raster.Probe(data, context, options);
                using Raster raster = Raster.Load(data, context, options);
                using Raster<Rgba8> converted = Raster.Load<Rgba8>(new ForwardOnlyStream(data), context, options);
                if (converted.Width != raster.Width || converted.Height != raster.Height)
                {
                    throw new InvalidOperationException("An AVIF file loads at two sizes.");
                }

                if (options == all[0])
                {
                    Extras(data, context, raster);
                }
            }
            catch (EgamiException)
            {
            }
            catch (ArgumentOutOfRangeException e) when (e.ParamName == "region" && options.Region is not null)
            {
                // A region that misses the image is the caller's mistake.
            }
        }
    }

    public static void LoadBoxes(byte[] data, EgamiContext context) => Load([.. Ftyp, .. data], context);

    public static void DecodeAv1(byte[] data, EgamiContext context)
    {
        bool filter = data.Length % 2 == 0;
        int parallelism = data.Length % 3 == 0 ? 4 : 1;
        try
        {
            Av1Frame frame = Av1Decoder.DecodeFirstFrame(data, "AV1", filter: filter, parallelism: parallelism);
            if (filter)
            {
                Av1FilmGrainSynthesis.Apply(frame);
            }
        }
        catch (EgamiException)
        {
        }
    }

    private static void Extras(byte[] data, EgamiContext context, Raster raster)
    {
        try
        {
            using YuvRaster planes = YuvRaster.Load(data, context);
        }
        catch (Exception e) when (e is EgamiException or NotSupportedException)
        {
        }

        if (raster.Metadata.FormatDetails is not AvifDetails details)
        {
            return;
        }

        foreach (AvifItem item in details.Items.Where(i => i.IsImage).Take(4))
        {
            try
            {
                using Raster image = AvifImage.ReadItem(new MemoryStream(data), item.Id, context: context);
            }
            catch (EgamiException)
            {
            }
        }
    }
}
