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

    /// <summary>
    /// avif-write (Phase 2.9.6): the AV1 encoder on arbitrary planes, chosen by the first bytes (size, bit depth, chroma, quantizer,
    /// effort, screen content tools); Egami's decoder must give exactly the encoder's reconstruction, and the source when lossless.
    /// One input in four also goes through the AVIF writer with alpha and a small grid, and must load back exactly when lossless.
    /// </summary>
    public static void Write(byte[] data, EgamiContext context)
    {
        if (data.Length < 8)
        {
            return;
        }

        int width = 1 + (data[0] % 40), bitDepth = (data[1] % 3) switch { 0 => 8, 1 => 10, _ => 12 };
        ChromaFormat chroma = (data[2] % 4) switch { 0 => ChromaFormat.Yuv420, 1 => ChromaFormat.Yuv422, 2 => ChromaFormat.Yuv444, _ => ChromaFormat.Yuv400 };
        int qindex = data[3], effort = data[4] % 7;
        bool lossless = qindex == 0, screen = (data[5] & 1) != 0, copies = (data[5] & 2) != 0;
        int planes = chroma == ChromaFormat.Yuv400 ? 1 : 3;
        int height = Math.Clamp((data.Length - 8) / (width * planes * 2), 1, 40);
        int ssx = chroma is ChromaFormat.Yuv420 or ChromaFormat.Yuv422 ? 1 : 0, ssy = chroma == ChromaFormat.Yuv420 ? 1 : 0;
        int max = (1 << bitDepth) - 1, at = 8;
        ushort[][] samples = new ushort[planes][];
        for (int plane = 0; plane < planes; plane++)
        {
            int w = plane > 0 ? (width + ssx) >> ssx : width, h = plane > 0 ? (height + ssy) >> ssy : height;
            samples[plane] = new ushort[w * h];
            for (int i = 0; i < samples[plane].Length; i++, at += 2)
            {
                samples[plane][i] = (ushort)((at + 1 < data.Length ? data[at] | (data[at + 1] << 8) : at * 37) & max);
            }
        }

        Av1EncoderInput input = new() { Width = width, Height = height, BitDepth = bitDepth, Chroma = chroma, Planes = samples };
        Av1Encoder encoded = Av1Encoder.Encode(input, new Av1EncoderSettings
        {
            Lossless = lossless,
            QIndex = qindex,
            Effort = effort,
            ScreenContent = screen,
            IntraBlockCopy = screen && copies,
        });
        Av1Frame decoded = Av1Decoder.DecodeFirstFrame(encoded.Stream, "AV1");
        for (int plane = 0; plane < planes; plane++)
        {
            int w = plane > 0 ? (width + ssx) >> ssx : width, h = plane > 0 ? (height + ssy) >> ssy : height;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int got = decoded.Planes![plane][(y * decoded.Strides[plane]) + x];
                    int want = encoded.Reconstruction.Planes![plane][(y * encoded.Reconstruction.Strides[plane]) + x];
                    if (got != want || (lossless && got != samples[plane][(y * w) + x]))
                    {
                        throw new InvalidOperationException($"The {(lossless ? "lossless" : "lossy")} AV1 round trip differs in plane {plane} at {x}, {y}: decoded {got}, reconstructed {want}, source {samples[plane][(y * w) + x]} ({width} x {height}, {bitDepth} bits, {chroma}, q {qindex}, e {effort}, screen {screen}, copies {copies}).");
                    }
                }
            }
        }

        if (data[6] % 4 == 0)
        {
            WriteAvif(data, width, height, effort, context);
        }
    }

    private static void WriteAvif(byte[] data, int width, int height, int effort, EgamiContext context)
    {
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height, context);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = System.Runtime.InteropServices.MemoryMarshal.AsBytes(raster.Row(y));
            for (int i = 0; i < row.Length; i++)
            {
                row[i] = data[(8 + (y * row.Length) + i) % data.Length];
            }
        }

        AvifWriterOptions options = new() { Lossless = true, Effort = effort, GridTileSize = data[7] % 2 == 0 ? new Size(64, 64) : null };
        using MemoryStream file = new();
        raster.Save(file, AvifFormat.Info, options);
        using Raster<Rgba8> again = Raster.Load<Rgba8>(file.ToArray(), context);
        for (int y = 0; y < height; y++)
        {
            if (!raster.Row(y).SequenceEqual(again.Row(y)))
            {
                throw new InvalidOperationException($"The lossless AVIF round trip differs in row {y}.");
            }
        }
    }

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
