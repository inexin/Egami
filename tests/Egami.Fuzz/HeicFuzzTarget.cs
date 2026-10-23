using Egami.Formats;
using Egami.Formats.Heic;
using Egami.Heic;
using Egami.Heic.Hevc;
using Egami.Nal;
using Egami.Pixels;

namespace Egami.Fuzz;

/// <summary>
/// The HEIC fuzz targets (Phase 2.9.4, task 14). heic loads files with Egami.Heic every way: Probe, Load leniently and strictly,
/// as Rgba8 from a stream that cannot seek, a region, the HDR rendition of a gain map, the YUV planes, and every image item on its
/// own. heicbox puts its input after an ftyp box with the heic brand, so mutations reach the HEIF box reader directly. hevc feeds
/// an Annex B stream of NAL units to the HEVC decoder on one thread and on four, which must give the same picture. Only
/// EgamiExceptions may escape.
/// </summary>
internal static class HeicFuzzTarget
{
    private static readonly byte[] Ftyp = [0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'h', (byte)'e', (byte)'i', (byte)'c', 0, 0, 0, 0,
        (byte)'h', (byte)'e', (byte)'i', (byte)'c', (byte)'m', (byte)'i', (byte)'f', (byte)'1'];

    public static void Load(byte[] data, EgamiContext context)
    {
        context = context.WithHeic() with { Formats = [HeicFormat.Info], Limits = context.Limits with { MaxFrames = 32 } };
        ReaderOptions[] all =
        [
            new() { Recovery = RecoveryMode.Lenient },
            new() { Recovery = RecoveryMode.Strict },
            new() { Region = new Rect(1, 1, 7, 5) },
            new() { Hdr = new HdrDecode { Headroom = 4 } },
        ];

        // One extra reading per input, picked by its length, keeps an execution within AFL's time limit.
        foreach (ReaderOptions options in new[] { all[0], all[1 + (data.Length % 3)] })
        {
            try
            {
                RasterInfo info = Raster.Probe(data, context, options);
                using Raster raster = Raster.Load(data, context, options);
                using Raster<Rgba8> converted = Raster.Load<Rgba8>(new ForwardOnlyStream(data), context, options);
                if (converted.Width != raster.Width || converted.Height != raster.Height)
                {
                    throw new InvalidOperationException("A HEIC file loads at two sizes.");
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

    /// <summary>Decodes an Annex B stream's first picture on one thread and on four; both must agree.</summary>
    public static void DecodeHevc(byte[] data, EgamiContext context)
    {
        List<HevcNal> nals = [];
        try
        {
            foreach (ReadOnlyMemory<byte> unit in NalFraming.SplitAnnexB(data))
            {
                nals.Add(HevcNal.Parse(unit.Span));
            }

            SafetyLimits limits = context.Limits with { MaxPixels = 1 << 20 };
            HevcPicture one = HevcDecoder.Decode(nals, limits, out _, 1);
            HevcPicture four = HevcDecoder.Decode(nals, limits, out _, 4);
            for (int c = 0; c < one.Planes.Length; c++)
            {
                if (!one.Planes[c].AsSpan().SequenceEqual(four.Planes[c]))
                {
                    throw new InvalidOperationException($"Plane {c} differs on one thread and on four.");
                }
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

        if (raster.Metadata.FormatDetails is not HeicDetails details)
        {
            return;
        }

        foreach (HeicItem item in details.Items.Where(i => i.IsImage).Take(4))
        {
            try
            {
                using Raster image = HeicImage.ReadItem(new MemoryStream(data), item.Id, context: context);
            }
            catch (EgamiException)
            {
            }
        }
    }
}
