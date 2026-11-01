using Egami;
using Egami.Colors;
using Egami.Formats;
using Egami.Formats.Ani;
using Egami.Formats.Jpeg;
using Egami.Metadata;
using Egami.Pixels;

using SharpFuzz;

using DrawingPath = Egami.Drawing.Path;

// Instrumented Egami code must not run before Fuzzer.OutOfProcess.Run sets up AFL++'s shared coverage map, so everything
// that touches Egami, including EgamiContext, is created inside the fuzzed delegate.
//
// Fuzz targets, one per reader. Each must load any input or fail with an EgamiException: any other exception is a crash,
// and AFL++ reports hangs. Limits are lowered so a valid but huge image cannot exhaust the fuzzer's memory. The exif target
// parses raw TIFF blocks, which must never throw at all; the icc target parses ICC profiles the same way, and building a color
// profile from one may fail only with an EgamiException. The iptc, psd, xmp and rawprofile targets (Phase 2.1.3) parse IPTC
// datasets, Photoshop resource blocks, XMP packets and PNG "Raw profile type" text, which must never throw, and check that
// what Egami writes after an edit reads back undamaged. The tiff and tiffcodec targets (Phase 2.3.2) load TIFF files every way
// and feed TIFF's decompressors and predictors raw bytes. The webp and webpcodec targets (Phase 2.3.1) load WebP files every way
// (stills and animations, planes, from a stream that cannot seek) and write them back, and feed the VP8 and VP8L decoders raw
// bit streams and the encoders arbitrary pixels, whose lossless round trip must be exact. The jpeg-write target (Phase 2.4.3)
// runs the JPEG writer: the input picks a pixel type, size, pixels and writer options (CMYK, YCCK, 12-bit, progressive among
// them); the file must read back at its size, a progressive file must decode to the baseline file's pixels, and a streamed
// encode must write what Save writes. Since the arithmetic and lossless writing Build Plan it also writes arithmetic-coded files,
// which must decode to the Huffman file's pixels, and lossless ones (any predictor, point transform and precision), whose samples
// must come back exactly when nothing asks for a loss, and equal between SOF3 and SOF11 always. The jbig2 and jbig2codec targets (Phase 2.3.4) load JBIG2 files every way, standalone and
// embedded with globals, and feed each of JBIG2's decoding procedures (regions, dictionaries, Huffman tables) raw bytes. The cube
// target (Phase 2.5.3) reads .cube LUT files, which must load or fail with an EgamiException; a table that loads must write back
// and read again to the same values and run as a filter. The jpeg2000 and jpeg2000codec targets (Phase 2.3.4) load JP2, JPX and
// codestreams every way (color and raw components, quality layers, rows, regions, reductions, YUV planes) and feed tier 1 raw
// code-block bytes in every code-block style. The svgpath target (Phase 2.6.2) reads SVG path data strictly, with TryParseSvg and
// leniently, which must agree; writes it back every way, which must read again to the same path; and runs small paths through
// measuring, dashing, stroke outlines and the boolean engine. The jpeg-transform target (Phase 2.4.4) runs lossless transforms
// with options picked from the input's hash: a result must load at the size the transform reports, a flip done twice with its
// edges kept must give back the source's pixels, and the result must not depend on the core count. The ccitt target (Phase
// 2.3.6) decodes raw CCITT data with PDF's parameters picked by its first bytes, through the filter call, Load and the row
// reader, which must agree. The zstd target (TIFF ZSTD task 1) decodes Zstandard frames into a large output and into one of
// exactly the size written, which must agree; zstd-write (task 2) encodes its bytes at the level its first byte picks, which must
// decode back exactly. The xz target (TIFF LZMA task 5) decodes xz streams as zstd does Zstandard frames, and xz-write (task 6)
// encodes its bytes at the preset and delta distance its first byte picks, which must decode back exactly.
// The ico target (Phase 2.3.3) loads ICO and CUR files every way (each entry, the
// best one, MinimumSize, rows) and writes the entries back, which must read again to the same pixels; icoentry wraps its input
// as the one entry of an icon, so mutations reach the BMP and PNG entry decoders and the AND mask directly. The psdfile target
// (Phase 2.3.3) loads PSD and PSB files every way (whole, as other pixel types, from a stream that cannot seek, rows, regions,
// reductions), which must agree; psddata puts its input behind a small header its first bytes choose (mode, depth, compression,
// PSD or PSB), so mutations reach the PackBits row table, the Deflate planes and the prediction directly. The psdlayers target
// (Phase 2.3.10) reads layered files' layers, from memory and from a stream that cannot seek, which must agree, and flattens them
// with Flatten and with a flattening Load. The egdr target
// (Phase 2.6.4) loads recorded pictures: a picture that loads must save and load again to the same bytes, and replays on a
// small raster canvas, so mutations reach lattices, atlases, meshes, patches, patterns and nested pictures.
// The vp8video target (Phase 2.3.5) feeds the VP8 stream decoder a stream of frames (inter frames, hidden frames, every version)
// on one thread or four, with and without concealment: each frame must decode, conceal or fail with an EgamiException.
// The shaping target (Phase 2.8.1) shapes texts in many scripts with a font that loads, with features and directions its bytes pick,
// and takes its GSUB closure; the bidi target reads its bytes as UTF-16 text and resolves, reorders, segments and shapes it.
// The radiance, exr and gainmap targets (Phase 2.9.1) load Radiance pictures (which must write back and write again to the same
// bytes), OpenEXR files every way (each part, layer, window and channel, written back as OpenEXR), and JPEGs with an MPF index
// and a gain map (every MP image, the HDR rendition at two headrooms, a crop and turn, and a JPEG save that must keep the map).
// The jxl, jxlbox, jxlentropy, jxl-write and jxl-jpeg targets (Phase 2.9.3, JxlFuzzTarget.cs) load JPEG XL files every way,
// reach the box reader, the entropy decoder, MA trees, Modular streams and the ICC decoder directly, and run the lossless encoder
// and JPEG recompression, whose round trips must be exact; jxl-lossy (Phase 2.9.7) runs the lossy encoder on every option, and
// jxl-frames (JPEG XL extras) the frame writer: animations, layers and extra channels, which must read back as written; and
// jxl-partial decodes a file as its bytes arrive, with previews of each cut, and checks the rows against a whole-file load.
// The avif, avifbox and av1 targets (Phase 2.9.2, AvifFuzzTarget.cs) load AVIF files every way (stills, grids, overlays, gain
// maps, sequences, YUV planes, items), reach the HEIF box reader directly, and feed raw OBU streams to the AV1 decoder; the
// av1stream target (Phase 2.9.5) feeds temporal units of inter-coded streams to the public AV1 stream decoder.
// The heic, heicbox and hevc targets (Phase 2.9.4, HeicFuzzTarget.cs) load HEIC files with Egami.Heic every way (stills, grids,
// gain maps, sequences, YUV planes, items), reach the HEIF box reader directly, and feed raw HEVC streams to the decoder on one
// thread and four, which must agree.
// The pcx, sgi, sun, xbm, xpm, wbmp, farbfeld, dds and raw targets (Egami.Formats.Extended, ExtendedFuzzTarget.cs) load each format
// every way, Probe, Load, Load from a stream that cannot seek and RasterDecoder, which must agree, and save what loads back; dds
// also loads every surface and mip level with LoadAll, and the smallest mip with MinimumSize; raw loads every output (mosaic,
// half size, preview) and develops with every demosaic, highlight, white balance and noise path.
// The isobox target (Phase 2.9.8, IsoBoxFuzzTarget.cs) walks the public ISO BMFF box walker over arbitrary bytes as a tree.
// The avif-write target (Phase 2.9.6) runs the AV1 encoder on arbitrary planes and options, which must decode exactly to its
// reconstruction (and its source when lossless), and the AVIF writer with alpha and grids, whose lossless files load back exactly.
// The inktiff target (Phase 2.6.5) loads separated TIFFs as plates, which must save and load again to the same plates; the
// icc target also builds the ink set of an n-colour profile and converts through it both ways.
// The css target (Phase 2.10, CssFuzzTarget.cs) parses its bytes as a style sheet and a declaration list, matches, cascades and
// computes them over a small tree and runs every value parser; Egami.Css never throws on input. The svg target (SvgFuzzTarget.cs)
// loads an SVG document under small limits, draws it, saves and loads it again, and loads it strictly.
// The hocr and alto targets (Egami 2.12, OcrFuzzTarget.cs) read OCR results, which must write back in both formats and read
// again to the same words.
// The cli-size, cli-template, cli-workflow, cli-run and cli-mcp targets (Roadmap 2.0 and 2.0.1, CliFuzzTarget.cs) read the egami
// tool's size syntax, output templates, JSON workflow files, run arguments and egami mcp tool calls, which may only fail with a
// usage error.
//
//   dotnet run --project tests/Egami.Fuzz -- png            run one input from stdin (used by AFL++ through SharpFuzz)
//   dotnet run --project tests/Egami.Fuzz -- png file.png   run one file, to reproduce a crash
//   dotnet run --project tests/Egami.Fuzz -c Release -- mutate jpeg 3600 seeds-dir findings-dir
//       mutation fuzzing without AFL++ (any OS): random edits of the seeds for the given seconds; crashes and inputs that take
//       over 10 seconds are saved to the findings directory. Not coverage-guided, so a complement to AFL++, not a replacement.
//   dotnet run --project tests/Egami.Fuzz -c Release -- replay png findings-dir
//       runs every file in a directory and counts the failures by message, to triage a run's findings
if (args is ["replay", string replayTarget, string directory])
{
    Action<Stream> replay = Target(replayTarget);
    Dictionary<string, int> failures = [];
    int files = 0;
    foreach (string file in Directory.GetFiles(directory))
    {
        files++;
        try
        {
            using FileStream input = File.OpenRead(file);
            replay(input);
        }
        catch (Exception e)
        {
            string key = $"{e.GetType().Name}: {System.Text.RegularExpressions.Regex.Replace(e.Message, "[0-9A-F]{16}|[0-9]+", "#")}";
            key = key[..Math.Min(key.Length, 160)];
            failures[key] = failures.GetValueOrDefault(key) + 1;
            if (failures[key] == 1)
            {
                Console.WriteLine($"{Path.GetFileName(file)}: {e.GetType().Name}: {e.Message}");
            }
        }
    }

    foreach ((string key, int count) in failures.OrderByDescending(f => f.Value))
    {
        Console.WriteLine($"{count,6}  {key}");
    }

    Console.WriteLine($"{files} files, {failures.Values.Sum()} failures.");
    return;
}

if (args is ["mutate", string mutateTarget, string seconds, string seeds, string findings])
{
    Mutator.Run(Target(mutateTarget), TimeSpan.FromSeconds(double.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture)), seeds, findings);
    return;
}

string target = args.Length > 0 ? args[0] : "png";
Action<Stream> run = Target(target);

if (args.Length > 1)
{
    using FileStream file = File.OpenRead(args[1]);
    run(file);
    Console.WriteLine("No crash.");
    return;
}

Fuzzer.OutOfProcess.Run(run);

static Action<Stream> Target(string name) => name switch
{
    "png" => LoadPng,
    "jpeg" => LoadJpeg,
    "jpeg-depth" => LoadJpegDepth,
    "jpeg-write" => WriteJpeg,
    "jpeg-transform" => TransformJpeg,
    "exif" => ParseExif,
    "icc" => ParseIcc,
    "gif" => LoadGif,
    "apng" => LoadApng,
    "lzw" => DecodeLzw,
    "bmp" => stream => LoadSimple(stream, Egami.Formats.Bmp.BmpFormat.Info),
    "tga" => stream => LoadSimple(stream, Egami.Formats.Tga.TgaFormat.Info),
    "qoi" => stream => LoadSimple(stream, Egami.Formats.Qoi.QoiFormat.Info),
    "pnm" => stream => LoadSimple(stream, Egami.Formats.Pnm.PnmFormat.Info),
    "pfm" => stream => LoadSimple(stream, Egami.Formats.Pnm.PnmFormat.Info),
    "iptc" => ParseIptc,
    "psd" => WalkPhotoshopResources,
    "xmp" => ParseXmp,
    "rawprofile" => DecodeRawProfile,
    "detect" => Detect,
    "stream" => DecodeStreaming,
    "tiff" => LoadTiff,
    "tiffcodec" => DecodeTiffCodec,
    "lossless-write" => WriteLosslessJpegSamples,
    "webp" => LoadWebp,
    "webpcodec" => DecodeWebpCodec,
    "vp8video" => DecodeVp8Video,
    "jbig2" => LoadJbig2,
    "jbig2codec" => DecodeJbig2Codec,
    "ccitt" => DecodeCcitt,
    "zstd" => DecodeZstd,
    "zstd-write" => EncodeZstd,
    "xz" => DecodeXz,
    "xz-write" => EncodeXz,
    "cube" => LoadCube,
    "jpeg2000" => LoadJpeg2000,
    "jpeg2000codec" => DecodeJpeg2000Codec,
    "font" => LoadFont,
    "hinting" => HintFont,
    "cff" => LoadCff,
    "fontdata" => FontData,
    "subset" => SubsetFont,
    "woff2" => LoadFont,
    "gvar" => LoadVariable,
    "cff2" => LoadVariable,
    "colr" => LoadColor,
    "instance" => InstanceFont,
    "svgpath" => ParseSvgPath,
    "ico" => LoadIco,
    "icoentry" => LoadIcoEntry,
    "ani" => LoadAni,
    "psdfile" => LoadPsd,
    "psddata" => LoadPsdData,
    "psdlayers" => ReadPsdLayers,
    "egdr" => LoadDrawing,
    "inktiff" => LoadInkTiff,
    "shaping" => ShapeFont,
    "bidi" => ResolveBidi,
    "radiance" => LoadRadiance,
    "exr" => LoadExr,
    "gainmap" => LoadGainMap,
    "jxl" => stream => Egami.Fuzz.JxlFuzzTarget.Load(ReadAll(stream), Context()),
    "jxlbox" => stream => Egami.Fuzz.JxlFuzzTarget.LoadBoxes(ReadAll(stream), Context()),
    "jxlentropy" => stream => Egami.Fuzz.JxlFuzzTarget.DecodeEntropy(ReadAll(stream), Context()),
    "jxl-write" => stream => Egami.Fuzz.JxlFuzzTarget.Write(ReadAll(stream), Context()),
    "avif-write" => stream => Egami.Fuzz.AvifFuzzTarget.Write(ReadAll(stream), Context()),
    "jxl-jpeg" => stream => Egami.Fuzz.JxlFuzzTarget.Recompress(ReadAll(stream), Context()),
    "jxl-lossy" => stream => Egami.Fuzz.JxlFuzzTarget.Lossy(ReadAll(stream), Context()),
    "jxl-frames" => stream => Egami.Fuzz.JxlFuzzTarget.Frames(ReadAll(stream), Context()),
    "jxl-partial" => stream => Egami.Fuzz.JxlFuzzTarget.Partial(ReadAll(stream), Context()),
    "avif" => stream => Egami.Fuzz.AvifFuzzTarget.Load(ReadAll(stream), Context()),
    "avifbox" => stream => Egami.Fuzz.AvifFuzzTarget.LoadBoxes(ReadAll(stream), Context()),
    "av1" => stream => Egami.Fuzz.AvifFuzzTarget.DecodeAv1(ReadAll(stream), Context()),
    "av1stream" => stream => Egami.Fuzz.AvifFuzzTarget.DecodeStream(ReadAll(stream), Context()),
    "pcx" or "sgi" or "sun" or "xbm" or "xpm" or "wbmp" or "farbfeld" or "dds" or "raw" => stream => Egami.Fuzz.ExtendedFuzzTarget.Load(ReadAll(stream), Context(), name),
    "heic" => stream => Egami.Fuzz.HeicFuzzTarget.Load(ReadAll(stream), Context()),
    "isobox" => stream => Egami.Fuzz.IsoBoxFuzzTarget.Walk(ReadAll(stream)),
    "heicbox" => stream => Egami.Fuzz.HeicFuzzTarget.LoadBoxes(ReadAll(stream), Context()),
    "hevc" => stream => Egami.Fuzz.HeicFuzzTarget.DecodeHevc(ReadAll(stream), Context()),
    "hevcstream" => stream => Egami.Fuzz.HeicFuzzTarget.DecodeHevcStream(ReadAll(stream), Context()),
    "css" => stream => Egami.Fuzz.CssFuzzTarget.Run(ReadAll(stream)),
    "hocr" => stream => Egami.Fuzz.OcrFuzzTarget.Hocr(ReadAll(stream)),
    "alto" => stream => Egami.Fuzz.OcrFuzzTarget.Alto(ReadAll(stream)),
    "cli-size" => stream => Egami.Fuzz.CliFuzzTarget.Size(ReadAll(stream)),
    "cli-template" => stream => Egami.Fuzz.CliFuzzTarget.Template(ReadAll(stream)),
    "cli-workflow" => stream => Egami.Fuzz.CliFuzzTarget.Workflow(ReadAll(stream)),
    "cli-run" => stream => Egami.Fuzz.CliFuzzTarget.Run(ReadAll(stream)),
    "cli-mcp" => stream => Egami.Fuzz.CliFuzzTarget.Mcp(ReadAll(stream)),
    "svg" => stream => Egami.Fuzz.SvgFuzzTarget.Run(ReadAll(stream)),
    "wmf" or "emf" => stream => Egami.Fuzz.MetafileFuzzTarget.Run(ReadAll(stream)),
    "emfplus" => stream => Egami.Fuzz.MetafileFuzzTarget.RunPlus(ReadAll(stream)),
    "sysdrawing" => stream => Egami.Fuzz.SystemDrawingFuzzTarget.Run(ReadAll(stream)),
    "barcode-qr" or "barcode-datamatrix" or "barcode-pdf417" or "barcode-aztec" or "barcode-code128" or "barcode-code39" or "barcode-itf" or "barcode-eanupc"
        => stream => Egami.Fuzz.BarcodeFuzzTarget.Encode(name["barcode-".Length..], ReadAll(stream)),
    "barcode-read" => stream => Egami.Fuzz.BarcodeFuzzTarget.Read(ReadAll(stream)),
    "paragraph" => stream => Egami.Fuzz.ParagraphFuzzTarget.Run(ReadAll(stream), ParagraphFaces(), DrawParagraph),
    _ => throw new ArgumentException($"Unknown fuzz target '{name}'. Targets: hinting, radiance, exr, gainmap, avif, avifbox, av1, av1stream, avif-write, heic, heicbox, hevc, hevcstream, isobox, jxl, jxlbox, jxlentropy, jxl-write, jxl-jpeg, jxl-lossy, jxl-frames, jxl-partial, png, jpeg, jpeg-depth, jpeg-write, jpeg-transform, exif, icc, iptc, psd, xmp, rawprofile, gif, apng, lzw, bmp, tga, qoi, pnm, pfm, detect, stream, tiff, tiffcodec, lossless-write, webp, webpcodec, vp8video, jbig2, jbig2codec, ccitt, cube, jpeg2000, jpeg2000codec, font, cff, subset, woff2, gvar, cff2, colr, instance, svgpath, ico, icoentry, psdfile, psddata, psdlayers, egdr, inktiff, shaping, bidi, css, hocr, alto, paragraph, svg, wmf, emf, emfplus, sysdrawing, barcode-qr, barcode-datamatrix, barcode-pdf417, barcode-aztec, barcode-code128, barcode-code39, barcode-itf, barcode-eanupc, barcode-read."),
};

static EgamiContext Context()
{
    return EgamiContext.Default with
    {
        MaxParallelism = 1,
        Limits = SafetyLimits.Default with { MaxPixels = 4_000_000, MaxTotalBytes = 256L * 1024 * 1024, MaxMetadataBytes = 1024 * 1024 },
        Diagnostics = CheckDiagnostic,
        DiagnosticLevel = DiagnosticLevel.Debug,
    };
}

// Every event a fuzzed file causes must be well formed: a handler sees only what the diagnostics design promises.
static void CheckDiagnostic(EgamiDiagnostic d)
{
    int dot = d.Id.IndexOf('.', StringComparison.Ordinal);
    if (dot < 1 || d.Source != d.Id[..dot] || string.IsNullOrWhiteSpace(d.Message) || d.Offset < 0 || d.Frame < 0 || !Enum.IsDefined(d.Level))
    {
        throw new InvalidOperationException($"A malformed diagnostic: {d}.");
    }
}

static void LoadPng(Stream stream)
{
    EgamiContext context = Context();
    byte[] data;
    using (MemoryStream copy = new())
    {
        stream.CopyTo(copy);
        data = copy.ToArray();
    }

    foreach (RecoveryMode recovery in new[] { RecoveryMode.Lenient, RecoveryMode.Strict })
    {
        try
        {
            using Raster raster = Raster.Load(data, context, new ReaderOptions { Recovery = recovery });
            using Raster<Rgba8> converted = raster.As<Rgba8>();
            Raster.Probe(data, context);
        }
        catch (EgamiException)
        {
        }
    }
}

// Phase 2.4.2: arithmetic, 12-bit and lossless files, the restart-interval split and the scan pipeline. Besides loading every
// way the jpeg target does, the pixels on one core must equal those on four, whatever the damage, and the raw entry point for
// TIFF and DNG must read the same stream with its tables split off.
static void LoadJpegDepth(Stream stream)
{
    EgamiContext context = Context();
    byte[] data = ReadAll(stream);

    // A region inside the image, since one that misses it is the caller's mistake and throws ArgumentOutOfRangeException.
    Rect region = new(0, 0, 1, 1);
    try
    {
        RasterInfo info = Raster.Probe(data, context);
        region = new Rect(info.Width / 4, info.Height / 3, Math.Max(1, info.Width / 2), Math.Max(1, info.Height / 2));
    }
    catch (EgamiException)
    {
    }

    foreach (ReaderOptions options in new ReaderOptions[]
    {
        new() { Recovery = RecoveryMode.Lenient },
        new() { Recovery = RecoveryMode.Strict },
        new JpegReaderOptions { FastUpsampling = true },
        new() { MinimumSize = new Size(1, 1) },
        new() { Region = region },
    })
    {
        try
        {
            Raster.Probe(data, context, options);
            using Raster raster = Raster.Load(data, context, options);
            using Raster<Rgba16> converted = raster.As<Rgba16>();
        }
        catch (EgamiException)
        {
        }
    }

    byte[]? serial = DecodeAt(data, 1);
    byte[]? parallel = DecodeAt(data, 4);
    if ((serial is null) != (parallel is null) || (serial is not null && !serial.AsSpan().SequenceEqual(parallel)))
    {
        throw new InvalidOperationException("One core and four cores decode differently.");
    }

    try
    {
        _ = JpegEmbedded.DecodeLosslessSamples(default, data, context, out _);
    }
    catch (EgamiException)
    {
    }
}

// Decodes through the parallel paths even for small files, or returns null if the file is refused.
static byte[]? DecodeAt(byte[] data, int cores)
{
    try
    {
        using MemoryStream stream = new(data);
        EgamiInput input = new(stream, Context() with { MaxParallelism = cores }, CancellationToken.None);
        JpegHeader header = JpegSegmentReader.ReadHeader(input, ReaderOptions.Default);
        using Raster<Rgba16> raster = JpegReader.Decode<Rgba16>(input, header, ReaderOptions.Default, forcePipeline: cores > 1);
        byte[] pixels = new byte[raster.Width * raster.Height * 8];
        for (int y = 0; y < raster.Height; y++)
        {
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(raster.Row(y)).CopyTo(pixels.AsSpan(y * raster.Width * 8));
        }

        return pixels;
    }
    catch (EgamiException)
    {
        return null;
    }
}

static void LoadJpeg(Stream stream)
{
    EgamiContext context = Context();
    byte[] data = ReadAll(stream);
    foreach (ReaderOptions options in new ReaderOptions[]
    {
        new() { Recovery = RecoveryMode.Lenient },
        new() { Recovery = RecoveryMode.Strict },
        new JpegReaderOptions { FastUpsampling = true },
        new() { MinimumSize = new Size(1, 1) },
        new() { MinimumSize = new Size(64, 64) },
    })
    {
        try
        {
            Raster.Probe(data, context, options);
            using Raster raster = Raster.Load(data, context, options);
            using Raster<Rgba8> converted = raster.As<Rgba8>();

            // Writing back exercises the EXIF, ICC and XMP writers on whatever was read, and the encoder on the pixels.
            converted.Apply(ops => ops.AutoOrient());
            converted.Save(Stream.Null, JpegFormat.Info);
        }
        catch (EgamiException)
        {
        }

        // Phase 2.1.4: the same file decoded into planes, converted, cropped, scaled and encoded back from them.
        try
        {
            YuvRasterInfo info = YuvRaster.Probe(data, context, options);
            if (info.CanLoad)
            {
                using YuvRaster planes = YuvRaster.Load(data, context, options);
                using Raster rgb = planes.ToRaster();
                planes.Save(Stream.Null, JpegFormat.Info);
                using YuvRaster crop = planes.Crop(new Rect(0, 0, Math.Max(1, planes.Width / 2), Math.Max(1, planes.Height / 2)));
                using YuvRaster small = planes.Resize(Math.Max(1, planes.Width / 3), Math.Max(1, planes.Height / 3));
            }
        }
        catch (EgamiException)
        {
        }
        catch (NotSupportedException)
        {
            // A CMYK, YCCK or RGB file has no planes.
        }
    }
}

static void LoadGif(Stream stream)
{
    EgamiContext context = Context() with { Limits = Context().Limits with { MaxFrames = 200 } };
    byte[] data = ReadAll(stream);
    foreach (ReaderOptions options in new ReaderOptions[]
    {
        new() { Recovery = RecoveryMode.Lenient },
        new() { Recovery = RecoveryMode.Strict },
        new() { Frames = ..1 },
        new() { Frames = ^2.. },
    })
    {
        try
        {
            Raster.Probe(data, context, options);
            using Raster<Rgba8> raster = Raster.Load<Rgba8>(data, context, options);

            // Writing back exercises palettes, frame optimization and the LZW encoder on whatever was read.
            if ((long)raster.Width * raster.Height * raster.FrameCount <= 1_000_000)
            {
                raster.Save(Stream.Null, Egami.Formats.Gif.GifFormat.Info);
                raster.Save(Stream.Null, Egami.Formats.Png.PngFormat.Info);
            }
        }
        catch (EgamiException)
        {
        }
    }
}

static void LoadApng(Stream stream)
{
    EgamiContext context = Context() with { Limits = Context().Limits with { MaxFrames = 200 } };
    byte[] data = ReadAll(stream);
    foreach (ReaderOptions options in new ReaderOptions[] { new() { Recovery = RecoveryMode.Lenient }, new() { Recovery = RecoveryMode.Strict }, new() { Frames = 1..2 } })
    {
        try
        {
            Raster.Probe(data, context, options);
            using Raster raster = Raster.Load(data, context, options);
            if ((long)raster.Width * raster.Height * raster.FrameCount <= 1_000_000)
            {
                raster.Save(Stream.Null, Egami.Formats.Png.PngFormat.Info);
            }
        }
        catch (EgamiException)
        {
        }
    }
}

// One of the 1.5 readers alone (so mutations are not lost to detection), from memory and from a stream that cannot seek (the
// TGA footer is then read after the pixels), then written back in its own format.
static void LoadSimple(Stream stream, FormatInfo format)
{
    EgamiContext context = Context() with { Formats = [format] };
    byte[] data = ReadAll(stream);
    foreach (RecoveryMode recovery in new[] { RecoveryMode.Lenient, RecoveryMode.Strict })
    {
        ReaderOptions options = new() { Recovery = recovery };
        try
        {
            Raster.Probe(data, context, options);
            using Raster raster = Raster.Load(data, context, options);
            using Raster<Rgba8> converted = Raster.Load<Rgba8>(new ForwardOnlyStream(data), context, options);
            raster.Save(Stream.Null, format);
        }
        catch (EgamiException)
        {
        }
    }
}

// Every format registered: detection, the TGA header check and the BMP hand-off to PNG and JPEG, fuzzed together.
static void Detect(Stream stream)
{
    EgamiContext context = Context();
    byte[] data = ReadAll(stream);
    try
    {
        Raster.Probe(data, context);
        using Raster raster = Raster.Load(data, context);
    }
    catch (EgamiException)
    {
    }
}

// Phase 2.2: every reader through RasterDecoder, whole and incremental. The input's own hash picks, reproducibly, a region, a
// reduction, a band height and the sizes the bytes arrive in (1 to 64 bytes, sometimes up to 64 KB), with previews drawn along
// the way. Strict decodes must agree: the incremental decode fails exactly when the whole one does, and otherwise hands out the
// same rows (with a region, only when both succeed); a difference is thrown as a crash.
static void DecodeStreaming(Stream stream)
{
    EgamiContext context = Context();
    byte[] data = ReadAll(stream);
    Random random = new(Hash(data));
    RasterInfo info;
    try
    {
        info = Raster.Probe(data, context);
    }
    catch (EgamiException)
    {
        return;
    }

    ReaderOptions options = new() { Recovery = random.Next(2) == 0 ? RecoveryMode.Lenient : RecoveryMode.Strict };
    bool sized = info.Width is > 0 and < 1 << 24 && info.Height is > 0 and < 1 << 24;
    if (sized && random.Next(3) == 0)
    {
        int x = random.Next(info.Width);
        int y = random.Next(info.Height);
        options = options with { Region = new Rect(x, y, random.Next(1, info.Width - x + 1), random.Next(1, info.Height - y + 1)) };
    }

    if (sized && random.Next(3) == 0)
    {
        options = options with { MinimumSize = new Size(random.Next(1, info.Width + 1), random.Next(1, info.Height + 1)) };
    }

    int bandHeight = random.Next(1, 33);
    ulong? whole = null;
    string? wholeError = null;
    string? incrementalError = null;
    try
    {
        using RasterDecoder decoder = RasterDecoder.Open(data, context, options);
        using Raster<Rgba8> band = Raster.Create<Rgba8>(decoder.Info.Width, bandHeight, context);
        ulong hash = 14695981039346656037;
        do
        {
            int rows;
            while ((rows = decoder.ReadRows(band)) > 0)
            {
                hash = Mix(hash, band, rows);
            }
        }
        while (decoder.NextFrame());

        whole = hash;
    }
    catch (EgamiException e)
    {
        wholeError = e.Message;
    }

    ulong? incremental = null;
    try
    {
        using RasterDecoder decoder = RasterDecoder.OpenIncremental(context, options);
        Raster<Rgba8>? band = null;
        Raster<Rgba8>? preview = null;
        ulong hash = 14695981039346656037;
        try
        {
            int at = 0;
            bool ended = false;
            while (true)
            {
                if (at < data.Length)
                {
                    int size = random.Next(8) == 0 ? random.Next(1, 65537) : random.Next(1, 65);
                    size = Math.Min(size, data.Length - at);
                    decoder.Append(data.AsSpan(at, size));
                    at += size;
                }
                else
                {
                    decoder.EndOfInput();
                    ended = true;
                }

                if (decoder.HeaderRead)
                {
                    band ??= Raster.Create<Rgba8>(decoder.Info.Width, bandHeight, context);
                    while (true)
                    {
                        int rows = decoder.ReadRows(band);
                        hash = Mix(hash, band, rows);
                        if (decoder.Status == DecodeStatus.FrameEnded && decoder.NextFrame())
                        {
                            continue;
                        }

                        if (rows == 0 || decoder.Status != DecodeStatus.RowsAvailable)
                        {
                            break;
                        }
                    }

                    if (random.Next(16) == 0 && decoder.Status == DecodeStatus.NeedsInput)
                    {
                        preview ??= Raster.Create<Rgba8>(decoder.Info.Width, decoder.Info.Height, context);
                        decoder.RenderPreview(preview);
                    }
                }

                if (ended)
                {
                    break;
                }
            }

            incremental = hash;
        }
        finally
        {
            band?.Dispose();
            preview?.Dispose();
        }
    }
    catch (EgamiException e)
    {
        incrementalError = e.Message;
    }

    // A region decode stops at the region's last row, and on a seekable input skips straight to the rows it needs, so damage
    // outside the region may be seen by one decode and not the other; only pixels both decodes give must agree.
    bool comparable = options.Region is null || (whole is not null && incremental is not null);
    if (options.Recovery == RecoveryMode.Strict && comparable && whole != incremental)
    {
        throw new InvalidOperationException($"{info.Format.Name}: the incremental decode gave {incremental?.ToString("X16", System.Globalization.CultureInfo.InvariantCulture) ?? incrementalError}, the whole decode {whole?.ToString("X16", System.Globalization.CultureInfo.InvariantCulture) ?? wholeError}, with {options}.");
    }
}

static int Hash(byte[] data)
{
    uint hash = 2166136261;
    foreach (byte b in data)
    {
        hash = (hash ^ b) * 16777619;
    }

    return (int)hash;
}

static ulong Mix(ulong hash, Raster<Rgba8> band, int rows)
{
    for (int y = 0; y < rows; y++)
    {
        foreach (byte b in System.Runtime.InteropServices.MemoryMarshal.AsBytes(band.Row(y)))
        {
            hash = (hash ^ b) * 1099511628211;
        }
    }

    return hash;
}

// The LZW decoder alone: the first byte is the minimum code size, the rest the data. Bad codes are reported, never thrown.
// Phase 2.3.2: TIFF files probed, loaded leniently and strictly, every page and SubIFD, from a stream that cannot seek, reduced
// (through a pyramid when there is one) and cropped.
static void LoadTiff(Stream stream)
{
    EgamiContext context = Context() with { Formats = [Egami.Formats.Tiff.TiffFormat.Info], Limits = Context().Limits with { MaxFrames = 64 } };
    byte[] data = ReadAll(stream);
    foreach (RecoveryMode recovery in new[] { RecoveryMode.Lenient, RecoveryMode.Strict })
    {
        Egami.Formats.Tiff.TiffReaderOptions options = new() { Recovery = recovery, IncludeReducedPages = true };
        try
        {
            RasterInfo info = Raster.Probe(data, context, options);
            using Raster raster = Raster.Load(data, context, options);
            using Raster<Rgba8> converted = Raster.Load<Rgba8>(new ForwardOnlyStream(data), context, options);
            using RasterList pages = Raster.LoadAll(data, context, options);
            if (pages.Count != info.ImageCount)
            {
                throw new InvalidOperationException($"LoadAll gave {pages.Count} images where Probe said {info.ImageCount}.");
            }

            using Raster<Rgba16> reduced = Raster.Load<Rgba16>(data, context, options with { MinimumSize = new Size(Math.Max(1, info.Width / 3), Math.Max(1, info.Height / 3)) });
            using Raster<Gray8> cropped = Raster.Load<Gray8>(data, context, options with { Region = new Rect(info.Width / 4, info.Height / 4, Math.Max(1, info.Width / 2), Math.Max(1, info.Height / 2)) });
        }
        catch (EgamiException)
        {
        }
        catch (NotSupportedException)
        {
            // Compressions and layouts Egami does not read say so.
        }
    }
}

// Phase 2.3.6: raw CCITT data with PDF's parameters. Six bytes pick them: K, the five flags, Columns, Rows and
// DamagedRowsBeforeError; the rest is the data. The filter call, Load (lenient and strict) and the row reader may fail only
// with an EgamiException, and must agree on every pixel and on the row count.
static void DecodeCcitt(Stream stream)
{
    byte[] data = ReadAll(stream);
    if (data.Length < 6)
    {
        return;
    }

    int flags = data[1];
    Egami.Formats.Ccitt.CcittFaxParameters p = new()
    {
        K = (data[0] % 6) switch { 0 => -1, 1 => 0, int k => k - 1 },
        EndOfLine = (flags & 1) != 0,
        EncodedByteAlign = (flags & 2) != 0,
        EndOfBlock = (flags & 4) == 0,
        BlackIs1 = (flags & 8) != 0,
        Columns = 1 + (((data[2] << 8) | data[3]) % 2048),
        Rows = (flags & 16) != 0 ? 1 + data[4] : 0,
        DamagedRowsBeforeError = data[5] % 4,
    };
    ReadOnlyMemory<byte> input = data.AsMemory(6);
    EgamiContext context = Context();
    byte[] packed;
    int rows;
    try
    {
        packed = Egami.Formats.Ccitt.CcittFax.Decode(input.Span, p, out rows, context);
    }
    catch (EgamiException)
    {
        packed = [];
        rows = -1;
    }

    int rowBytes = (p.Columns + 7) / 8;
    if (rows >= 0 && packed.Length != rows * rowBytes)
    {
        throw new InvalidOperationException($"The filter gave {packed.Length} bytes for {rows} rows of {rowBytes}.");
    }

    foreach (RecoveryMode recovery in new[] { RecoveryMode.Lenient, RecoveryMode.Strict })
    {
        Egami.Formats.Ccitt.CcittReaderOptions options = new() { Parameters = p, Recovery = recovery };
        try
        {
            RasterInfo info = Raster.Probe(input, Egami.Formats.Ccitt.CcittFormat.Info, context, options);
            using Raster<Gray8> raster = Raster.Load<Gray8>(input, Egami.Formats.Ccitt.CcittFormat.Info, context, options);
            using RasterDecoder decoder = RasterDecoder.Open(input, Egami.Formats.Ccitt.CcittFormat.Info, context, options);
            using Raster<Gray8> streamed = (Raster<Gray8>)decoder.ReadAll();
            if (raster.Height != rows || info.Height != rows || streamed.Height != rows || raster.Width != p.Columns)
            {
                throw new InvalidOperationException($"Load gave {raster.Width}x{raster.Height}, Probe {info.Height} rows, streaming {streamed.Height}, the filter {rows}.");
            }

            for (int y = 0; y < rows; y++)
            {
                Span<Gray8> row = raster.Row(y);
                Span<Gray8> other = streamed.Row(y);
                for (int x = 0; x < p.Columns; x++)
                {
                    bool black = ((packed[(y * rowBytes) + (x >> 3)] >> (7 - (x & 7))) & 1) == (p.BlackIs1 ? 1 : 0);
                    if (black != (row[x].L == 0) || row[x].L != other[x].L)
                    {
                        throw new InvalidOperationException($"Pixel ({x}, {y}) differs between the filter, Load and streaming.");
                    }
                }
            }
        }
        catch (EgamiException)
        {
            if (recovery == RecoveryMode.Lenient && rows >= 0)
            {
                throw new InvalidOperationException("Load failed where the filter decoded.");
            }
        }
    }
}

// Phase 2.3.2: TIFF's decompressors and predictors fed directly: the first byte picks the codec and the next two the row width.
// No decoder may throw or write past its output.
static void DecodeTiffCodec(Stream stream)
{
    byte[] data = ReadAll(stream);
    if (data.Length < 3)
    {
        return;
    }

    int width = 1 + (((data[1] << 8) | data[2]) % 2048);
    ReadOnlySpan<byte> input = data.AsSpan(3);
    byte[] output = new byte[64 * 1024];
    int rows = Math.Max(1, output.Length / ((width + 7) / 8) / 2);
    int written = (data[0] % 9) switch
    {
        0 => Egami.Formats.Tiff.TiffLzw.Decode(input, output),
        1 => Egami.Formats.PackBits.Decode(input, output, out _),
        2 => Egami.Formats.Ccitt.CcittDecoder.DecodeTiff(input, output, width, rows, 2, 0),
        3 => Egami.Formats.Ccitt.CcittDecoder.DecodeTiff(input, output, width, rows, 3, 0),
        4 => Egami.Formats.Ccitt.CcittDecoder.DecodeTiff(input, output, width, rows, 3, 1),
        5 => Egami.Formats.Ccitt.CcittDecoder.DecodeTiff(input, output, width, rows, 4, 0),
        6 => Egami.Formats.Tiff.SgiLog.DecodeStrip(input, output, Math.Min(width, 256), 8, data[1] % 2 == 0, data[2] % 2 == 0),
        7 => Predict(input, output, width),
        _ => Egami.Formats.Ccitt.CcittDecoder.DecodeTiff(input, output, width, rows, 32771, 0),
    };
    if (written < 0 || written > output.Length)
    {
        throw new InvalidOperationException($"The decoder wrote {written} bytes into {output.Length}.");
    }

    static int Predict(ReadOnlySpan<byte> input, byte[] output, int width)
    {
        int n = Math.Min(input.Length, output.Length) & ~7;
        input[..n].CopyTo(output);
        int samples = 1 + (width % 4);
        Egami.Formats.Tiff.TiffDecoder.UndoHorizontal(output.AsSpan(0, n), samples, 8 << (width % 4));
        Egami.Formats.Tiff.TiffDecoder.UndoFloatPredictor(output.AsSpan(0, n), samples, 4);
        return n;
    }
}

static void DecodeZstd(Stream stream)
{
    // Into a large output, then into exactly what that wrote, which must give the same bytes; only EgamiExceptions may escape.
    byte[] data = ReadAll(stream);
    byte[] output = new byte[512 * 1024];
    int written;
    try
    {
        written = Egami.Compression.Zstd.ZstdDecoder.Decode(data, output);
    }
    catch (EgamiException)
    {
        return;
    }

    if (written < 0 || written > output.Length)
    {
        throw new InvalidOperationException($"The Zstandard decoder reports {written} bytes written.");
    }

    byte[] exact = new byte[written];
    if (Egami.Compression.Zstd.ZstdDecoder.Decode(data, exact) != written || !exact.AsSpan().SequenceEqual(output.AsSpan(0, written)))
    {
        throw new InvalidOperationException("The Zstandard decoder writes differently into an output of exactly the right size.");
    }
}

static void DecodeXz(Stream stream)
{
    // Into a large output, then into exactly what that wrote, which must give the same bytes; only EgamiExceptions may escape.
    byte[] data = ReadAll(stream);
    byte[] output = new byte[512 * 1024];
    int written;
    try
    {
        written = Egami.Compression.Lzma.XzDecoder.Decode(data, output);
    }
    catch (EgamiException)
    {
        return;
    }

    if (written < 0 || written > output.Length)
    {
        throw new InvalidOperationException($"The xz decoder reports {written} bytes written.");
    }

    byte[] exact = new byte[written];
    if (Egami.Compression.Lzma.XzDecoder.Decode(data, exact) != written || !exact.AsSpan().SequenceEqual(output.AsSpan(0, written)))
    {
        throw new InvalidOperationException("The xz decoder writes differently into an output of exactly the right size.");
    }
}

static void EncodeXz(Stream stream)
{
    // The first byte picks the preset (low four bits, to 9) and the delta distance (high four bits), the rest is the data: the
    // stream must decode back to it exactly.
    byte[] data = ReadAll(stream);
    if (data.Length == 0)
    {
        return;
    }

    int preset = (data[0] & 15) % 10;
    int delta = data[0] >> 4;
    ReadOnlySpan<byte> input = data.AsSpan(1);
    byte[] packed = new byte[Egami.Compression.Lzma.XzEncoder.MaxEncodedLength(input.Length)];
    int size = Egami.Compression.Lzma.XzEncoder.Encode(input, packed, preset, delta);
    byte[] back = new byte[input.Length];
    if (Egami.Compression.Lzma.XzDecoder.Decode(packed.AsSpan(0, size), back) != input.Length || !back.AsSpan().SequenceEqual(input))
    {
        throw new InvalidOperationException($"An xz stream written at preset {preset}, delta {delta} does not decode to its input.");
    }
}

static void EncodeZstd(Stream stream)
{
    // The first byte picks the level, the rest is the data: the frame must decode back to it exactly.
    byte[] data = ReadAll(stream);
    if (data.Length == 0)
    {
        return;
    }

    int level = 1 + (data[0] % Egami.Compression.Zstd.ZstdEncoder.MaxLevel);
    ReadOnlySpan<byte> input = data.AsSpan(1);
    byte[] packed = new byte[Egami.Compression.Zstd.ZstdEncoder.MaxEncodedLength(input.Length)];
    int size = Egami.Compression.Zstd.ZstdEncoder.Encode(input, packed, level);
    byte[] back = new byte[input.Length];
    if (Egami.Compression.Zstd.ZstdDecoder.Decode(packed.AsSpan(0, size), back) != input.Length || !back.AsSpan().SequenceEqual(input))
    {
        throw new InvalidOperationException($"A Zstandard frame written at level {level} does not decode to its input.");
    }
}

static void DecodeLzw(Stream stream)
{
    byte[] data = ReadAll(stream);
    if (data.Length == 0)
    {
        return;
    }

    using Egami.Formats.Gif.LzwDecoder decoder = new();
    byte[] output = new byte[1 << 16];
    decoder.Decode(1 + (data[0] % 11), data.AsSpan(1), output, out int written);
    if (written > output.Length)
    {
        throw new InvalidOperationException("The decoder wrote past its output.");
    }
}

static void ParseIcc(Stream stream)
{
    byte[] data = ReadAll(stream);
    IccData icc = IccData.Parse(data, SafetyLimits.Default with { MaxMetadataBytes = 1024 * 1024 });
    _ = (icc.Version, icc.ProfileClass, icc.Model, icc.Created, icc.Description, icc.Copyright, icc.CodePoints, icc.ToString());
    foreach (IccTag tag in icc.Tags)
    {
        _ = icc.TryGetTag(tag.Signature, out _);
    }

    try
    {
        ColorProfile profile = ColorProfile.FromIcc(icc);
        _ = (profile.Primaries, profile.White, profile.Transfer?.ToLinear(0.5), profile.IsEquivalentTo(ColorProfile.Srgb));
        if (profile.Model is ColorModel.NColor or ColorModel.Cmyk)
        {
            InkSet inks = InkSet.FromProfile(profile);
            float[] tints = new float[profile.ChannelCount * 2], rgb = new float[6];
            Array.Fill(tints, 0.5f);
            tints[0] = 1;
            ColorTransform.Create(profile, ColorProfile.Srgb).Apply(tints, rgb);
            ColorTransform.Create(ColorProfile.Srgb, profile, new ColorTransformOptions { BlackPointCompensation = true }).Apply(rgb, tints);
            _ = (inks.Count, profile.ColorantNames);
        }
    }
    catch (EgamiException)
    {
    }
}

static void LoadInkTiff(Stream stream)
{
    EgamiContext context = Context();
    byte[] data = ReadAll(stream);
    InkRaster plates;
    try
    {
        plates = InkRaster.Load(new MemoryStream(data), null, context);
    }
    catch (EgamiException)
    {
        return;
    }

    using (plates)
    {
        using MemoryStream saved = new();
        plates.Save(saved, new InkTiffOptions { IncludeAlpha = true });
        saved.Position = 0;
        using InkRaster again = InkRaster.Load(saved, null, context);
        for (int p = 0; p < plates.Plates.Count; p++)
        {
            for (int y = 0; y < plates.Height; y++)
            {
                bool same = plates.Depth == InkDepth.Unorm8 ? plates.Plates[p].Row8(y).SequenceEqual(again.Plates[p].Row8(y)) : plates.Plates[p].Row16(y).SequenceEqual(again.Plates[p].Row16(y));
                if (!same || plates.Plates[p].Name != again.Plates[p].Name)
                {
                    throw new InvalidOperationException($"Plate {p} row {y} changed in a save and load.");
                }
            }
        }

        _ = plates.MeasureCoverage();
        using Raster<Rgba8> proof = plates.Proof<Rgba8>();
    }
}

static void ParseExif(Stream stream)
{
    byte[] data = ReadAll(stream);
    ExifData exif = ExifData.Parse(data);
    _ = (exif.Orientation, exif.DateTaken, exif.CameraMake, exif.CameraModel, exif.Location, exif.Thumbnail.Length);
    foreach (ExifEntry entry in exif.Entries)
    {
        if (entry.Type == ExifType.Ascii)
        {
            _ = entry.AsString();
        }
        else if (entry.Count > 0)
        {
            _ = entry.AsDouble(entry.Count - 1);
        }
    }

    // An edit makes the block lay itself out again, which must read back the same.
    exif.Set(ExifKey.Artist, "fuzz");
    ExifData again = ExifData.Parse(exif.ToBytes().Span);
    if (again.IsDamaged || again.Entries.Count != exif.Entries.Count)
    {
        throw new InvalidOperationException("A rebuilt EXIF block did not read back the same.");
    }
}

static void ParseIptc(Stream stream)
{
    byte[] data = ReadAll(stream);
    IptcData iptc = IptcData.Parse(data, SafetyLimits.Default with { MaxMetadataBytes = 1024 * 1024 });
    _ = (iptc.Keywords, iptc.Bylines, iptc.Caption, iptc.Headline, iptc.Copyright, iptc.Credit, iptc.DateCreated, iptc.IsUtf8);
    foreach (IptcEntry entry in iptc.Entries)
    {
        _ = entry.AsString();
    }

    if (!iptc.IsDamaged && !iptc.ToBytes().Span.SequenceEqual(data))
    {
        throw new InvalidOperationException("An undamaged IPTC block did not write back byte for byte.");
    }

    // An edit rebuilds the block, which must read back the same.
    iptc.Add(IptcKey.Keywords, "fuzz ø");
    IptcData again = IptcData.Parse(iptc.ToBytes().Span);
    if (again.IsDamaged || again.Entries.Count < iptc.Entries.Count || !again.Keywords.Contains("fuzz ø"))
    {
        throw new InvalidOperationException("A rebuilt IPTC block did not read back the same.");
    }
}

static void WalkPhotoshopResources(Stream stream)
{
    byte[] data = ReadAll(stream);
    ReadOnlyMemory<byte> iptc = PhotoshopResources.FindIptc(data, null);
    byte[] replaced = PhotoshopResources.WithIptc(data, "\u001C\u0002\u0005\u0000\u0001x"u8);
    List<PhotoshopResources.Resource> resources = [];
    if (PhotoshopResources.TryWalk(data, resources))
    {
        // A block Egami can walk keeps every other resource and gets the new IPTC.
        if (PhotoshopResources.FindIptc(replaced, null).Length != 6)
        {
            throw new InvalidOperationException("A rewritten Photoshop resource block lost its IPTC.");
        }
    }

    _ = (iptc.Length, PhotoshopResources.Create(iptc.Span).Length);
}

static void ParseXmp(Stream stream)
{
    byte[] data = ReadAll(stream);
    XmpData xmp = XmpData.Parse(data, SafetyLimits.Default with { MaxMetadataBytes = 1024 * 1024 });
    _ = (xmp.Title, xmp.Description, xmp.Rights, xmp.Creators, xmp.Keywords, xmp.CreateDate, xmp.ModifyDate, xmp.Rating, xmp.Orientation, xmp.Headline);
    if (xmp.IsDamaged)
    {
        return;
    }

    // What Egami writes after an edit must parse, with the same properties.
    xmp.Label = "fuzz";
    XmpData again = XmpData.Parse(xmp.ToBytes().Span);
    if (again.IsDamaged || again.Count != xmp.Count || again.Label != "fuzz")
    {
        throw new InvalidOperationException("An edited XMP packet did not read back: " + string.Join(" ", again.Warnings));
    }
}

static void DecodeRawProfile(Stream stream)
{
    byte[] data = ReadAll(stream);
    string text = System.Text.Encoding.Latin1.GetString(data);
    if (RawProfiles.Decode(text, 1024 * 1024, out _) is byte[] bytes)
    {
        _ = RawProfiles.Iim(bytes);
        _ = RawProfiles.WithoutExifHeader(bytes).Length;
        if (RawProfiles.Decode(RawProfiles.Encode("exif", bytes), 1024 * 1024, out _) is not byte[] again || !again.AsSpan().SequenceEqual(bytes))
        {
            throw new InvalidOperationException("A raw profile did not encode and decode back.");
        }
    }
}

static void LoadCube(Stream stream)
{
    EgamiContext context = Context() with { Limits = Context().Limits with { MaxTotalBytes = 64L * 1024 * 1024 } };
    Egami.Operations.Lut3D lut;
    try
    {
        lut = Egami.Operations.CubeFile.Read(stream, context);
    }
    catch (EgamiException)
    {
        return;
    }

    // What loads must write back and read again to the same values.
    using MemoryStream written = new();
    Egami.Operations.CubeFile.Write(written, lut, "fuzz");
    written.Position = 0;
    Egami.Operations.Lut3D again = Egami.Operations.CubeFile.Read(written, context);
    if (again.Size != lut.Size || again.ShaperSize != lut.ShaperSize || again.DomainMin != lut.DomainMin || again.DomainMax != lut.DomainMax)
    {
        throw new InvalidOperationException("A written .cube file read back with a different shape.");
    }

    for (int b = 0; b < lut.Size; b += Math.Max(1, lut.Size / 5))
    {
        for (int g = 0; g < lut.Size; g += Math.Max(1, lut.Size / 5))
        {
            for (int r = 0; r < lut.Size; r += Math.Max(1, lut.Size / 5))
            {
                if (!lut[r, g, b].Equals(again[r, g, b]))
                {
                    throw new InvalidOperationException($"Grid point ({r}, {g}, {b}) changed in a round trip.");
                }
            }
        }
    }

    // And run as a filter on a small image, every way it can be read.
    using Raster<Rgba8> image = Raster.Create<Rgba8>(17, 9, context);
    for (int y = 0; y < 9; y++)
    {
        Span<Rgba8> row = image.Row(y);
        for (int x = 0; x < 17; x++)
        {
            row[x] = new Rgba8((byte)(x * 15), (byte)(y * 28), (byte)(x * y), 255);
        }
    }

    foreach (Egami.Operations.LutInterpolation mode in Enum.GetValues<Egami.Operations.LutInterpolation>())
    {
        image.Apply(ops => ops.ApplyFilter(Egami.Operations.ColorFilter.FromLut(lut, mode)));
    }
}

static void LoadWebp(Stream stream)
{
    EgamiContext context = Context() with { Formats = [Egami.Formats.Webp.WebpFormat.Info], Limits = Context().Limits with { MaxFrames = 100 } };
    byte[] data = ReadAll(stream);
    // One reading of each kind per input, picked by its length, keeps an execution well under AFL's time limit.
    ReaderOptions[] all =
    [
        new() { Recovery = RecoveryMode.Lenient },
        new() { Recovery = RecoveryMode.Strict },
        new Egami.Formats.Webp.WebpReaderOptions { UseBackgroundColor = true, FastUpsampling = true },
        new() { Frames = 1..3 },
        new() { Frames = ^1.. },
    ];
    foreach (ReaderOptions options in new[] { all[0], all[1 + (data.Length % 4)] })
    {
        try
        {
            RasterInfo info = Raster.Probe(data, context, options);
            using Raster raster = Raster.Load(data, context, options);
            using Raster<Rgba8> converted = Raster.Load<Rgba8>(new ForwardOnlyStream(data), context, options);
            if (info.FrameCount == 1)
            {
                try
                {
                    using YuvRaster planes = YuvRaster.Load(data, context, options);
                }
                catch (NotSupportedException)
                {
                    // A lossless file has no planes.
                }
            }

            // Writing back exercises both encoders and the animation writer on whatever was read.
            if ((long)raster.Width * raster.Height * raster.FrameCount <= 40_000)
            {
                using MemoryStream lossless = new();
                raster.Save(lossless, Egami.Formats.Webp.WebpFormat.Info, new Egami.Formats.Webp.WebpWriterOptions { Lossless = true, Effort = data.Length % 3 });
                if (raster.Width <= 16383 && raster.Height <= 16383)
                {
                    // Lossy WebP is one pixel narrower than lossless at most.
                    raster.Save(Stream.Null, Egami.Formats.Webp.WebpFormat.Info, new Egami.Formats.Webp.WebpWriterOptions { Lossless = false, Effort = (data.Length / 7) % 7, Quality = data.Length % 101 });
                }

                using Raster<Rgba8> again = Raster.Load<Rgba8>(lossless.ToArray(), context);
                using Raster<Rgba8> expected = raster.As<Rgba8>();
                for (int f = 0; f < expected.FrameCount; f++)
                {
                    for (int y = 0; y < expected.Height; y++)
                    {
                        if (!expected.Frames[f].Row(y).SequenceEqual(again.Frames[f].Row(y)))
                        {
                            throw new InvalidOperationException($"The lossless round trip differs in frame {f}, row {y}.");
                        }
                    }
                }
            }
        }
        catch (EgamiException)
        {
        }
    }
}

// The first byte picks the codec: a raw VP8 frame, a raw VP8L stream, or the encoders on the rest as pixels.
// A VP8 video stream (Phase 2.3.5): a flags byte (bit 0 conceals errors, bit 1 decodes on four threads), then frames, each a
// 16-bit little-endian length and its bytes. Every frame must decode, conceal or throw an EgamiException; shown frames are copied out.
static void DecodeVp8Video(Stream stream)
{
    byte[] data = ReadAll(stream);
    if (data.Length < 1)
    {
        return;
    }

    EgamiContext context = Context() with { MaxParallelism = (data[0] & 2) != 0 ? 4 : 1 };
    using Egami.Formats.Webp.Vp8StreamDecoder decoder = new(context) { ConcealErrors = (data[0] & 1) != 0 };
    YuvRaster<byte>? frame = null;
    try
    {
        for (int at = 1; at + 2 <= data.Length;)
        {
            int length = Math.Min(data[at] | (data[at + 1] << 8), data.Length - at - 2);
            ReadOnlySpan<byte> packet = data.AsSpan(at + 2, length);
            at += 2 + length;
            Egami.Formats.Webp.Vp8FrameInfo info;
            try
            {
                info = decoder.Decode(packet);
            }
            catch (EgamiException)
            {
                continue;
            }

            if (info.IsShown)
            {
                if (frame is null || frame.Width != info.Width || frame.Height != info.Height)
                {
                    frame?.Dispose();
                    frame = YuvRaster.Create(info.Width, info.Height, ChromaFormat.Yuv420, YuvColor.Bt601Limited, context: context);
                }

                decoder.CopyFrameTo(frame);
            }
        }
    }
    finally
    {
        frame?.Dispose();
    }
}

static void DecodeWebpCodec(Stream stream)
{
    byte[] data = ReadAll(stream);
    if (data.Length < 4)
    {
        return;
    }

    EgamiContext context = Context();
    if (data[0] % 3 == 0 && (data[1] & 1) != 0)
    {
        // An inter frame: the VP8 decoder refuses it (NotSupportedException), as WebP holds key frames only.
        return;
    }

    try
    {
        switch (data[0] % 3)
        {
            case 0:
                {
                    using Egami.Formats.Webp.Vp8Decoder decoder = Egami.Formats.Webp.Vp8Decoder.Create(data, 1, data.Length - 1);
                    if ((long)decoder.Width * decoder.Height > 1_000_000)
                    {
                        return;
                    }

                    while (decoder.DecodeRow((d, from, to, chromaFrom, chromaTo) =>
                    {
                        for (int y = from; y < to; y++)
                        {
                            _ = d.RowY(y);
                        }
                    }))
                    {
                    }

                    break;
                }

            case 1:
                {
                    EgamiInput input = new(new MemoryStream(data), context, CancellationToken.None);
                    using Egami.Formats.Webp.Vp8lBitReader reader = new(data, 1, data.Length - 1);
                    using Egami.Formats.Webp.Vp8lDecoder decoder = new(input, reader, data[1] % 2 == 0);
                    if ((long)decoder.Width * decoder.Height > 1_000_000)
                    {
                        return;
                    }

                    uint[] row = new uint[decoder.Width];
                    for (int y = 0; y < decoder.Height; y++)
                    {
                        decoder.DecodeRow(row);
                    }

                    break;
                }

            default:
                {
                    int width = 1 + (data[1] % 64);
                    int height = Math.Max(1, (data.Length - 4) / 4 / width);
                    using Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height, context);
                    for (int y = 0; y < height; y++)
                    {
                        Span<byte> row = System.Runtime.InteropServices.MemoryMarshal.AsBytes(raster.Row(y));
                        int at = 4 + (y * width * 4);
                        data.AsSpan(Math.Min(at, data.Length), Math.Min(row.Length, Math.Max(0, data.Length - at))).CopyTo(row);
                    }

                    int effort = data[2] % 7;
                    bool exact = data[3] % 2 == 0;
                    using MemoryStream lossless = new();
                    raster.Save(lossless, Egami.Formats.Webp.WebpFormat.Info, new Egami.Formats.Webp.WebpWriterOptions { Lossless = true, Effort = effort, NearLossless = exact ? 100 : data[3] % 101 });
                    using MemoryStream lossy = new();
                    raster.Save(lossy, Egami.Formats.Webp.WebpFormat.Info, new Egami.Formats.Webp.WebpWriterOptions { Lossless = false, Effort = effort, Quality = data[3] % 101, AlphaQuality = data[2] % 101 });
                    using Raster<Rgba8> again = Raster.Load<Rgba8>(lossless.ToArray(), context);
                    using Raster<Rgba8> decoded = Raster.Load<Rgba8>(lossy.ToArray(), context);
                    for (int y = 0; y < height && exact; y++)
                    {
                        if (!raster.Row(y).SequenceEqual(again.Row(y)))
                        {
                            throw new InvalidOperationException($"The lossless round trip differs in row {y}.");
                        }
                    }

                    break;
                }
        }
    }
    catch (EgamiException)
    {
    }
}

// Lossless transforms (Phase 2.4.4): the input is a JPEG, and its hash picks the operation, edges, crop, gray conversion, output
// coding and metadata copying. Only EgamiExceptions and the documented argument errors may escape; a result must load at the size
// reported, flipping twice with kept edges must restore the pixels of a plain copy, and four cores must write what one writes.
static void TransformJpeg(Stream stream)
{
    byte[] data = ReadAll(stream);
    uint hash = 2166136261;
    foreach (byte value in data.AsSpan(0, Math.Min(data.Length, 4096)))
    {
        hash = (hash ^ value) * 16777619;
    }

    JpegTransformOperation[] operations = Enum.GetValues<JpegTransformOperation>();
    JpegTransformOptions options = new()
    {
        Operation = operations[hash % (uint)operations.Length],
        Edges = (JpegTransformEdges)((hash >> 4) % 3),
        Grayscale = ((hash >> 6) & 7) == 0,
        Progressive = ((hash >> 9) & 3) == 0,
        OptimizeHuffman = ((hash >> 11) & 3) != 0,
        CopyMetadata = ((hash >> 13) & 1) == 0,
    };

    if (((hash >> 14) & 3) == 0)
    {
        try
        {
            RasterInfo info = Raster.Probe(data, Context());
            int w = info.Width, h = info.Height;
            (w, h) = options.Operation is JpegTransformOperation.Rotate90 or JpegTransformOperation.Rotate270 or JpegTransformOperation.Transpose or JpegTransformOperation.Transverse ? (h, w) : (w, h);
            int x = (int)((hash >> 16) % (uint)w), y = (int)((hash >> 20) % (uint)h);
            options = new JpegTransformOptions
            {
                Operation = options.Operation,
                Edges = options.Edges,
                Grayscale = options.Grayscale,
                Progressive = options.Progressive,
                Crop = new Rect(x, y, Math.Max(1, (w - x) / 2), Math.Max(1, (h - y) / 2)),
            };
        }
        catch (EgamiException)
        {
        }
    }

    byte[]? result = TransformOnce(data, options, 1, out JpegTransformResult? reported);
    if (result is null)
    {
        return;
    }

    if (!TransformOnce(data, options, 4, out _)!.AsSpan().SequenceEqual(result))
    {
        throw new InvalidOperationException("One core and four cores transform differently.");
    }

    using (Raster loaded = Raster.Load(result, Context()))
    {
        if (loaded.Width != reported!.Width || loaded.Height != reported.Height)
        {
            throw new InvalidOperationException($"The result is {loaded.Width}×{loaded.Height}, but the transform said {reported.Width}×{reported.Height}.");
        }
    }

    JpegTransformOptions keepFlip = new() { Operation = JpegTransformOperation.FlipHorizontal, Edges = JpegTransformEdges.Keep, CopyMetadata = false };
    byte[] copy = TransformOnce(data, new JpegTransformOptions { CopyMetadata = false }, 1, out _)!;
    byte[] twice = TransformOnce(TransformOnce(data, keepFlip, 1, out _)!, keepFlip, 1, out _)!;
    using Raster a = Raster.Load(copy, Context());
    using Raster b = Raster.Load(twice, Context());
    using Raster<Rgba16> pa = a.As<Rgba16>();
    using Raster<Rgba16> pb = b.As<Rgba16>();
    for (int row = 0; row < pa.Height; row++)
    {
        if (!pa.Row(row).SequenceEqual(pb.Row(row)))
        {
            throw new InvalidOperationException("Flipping twice with kept edges changed the pixels.");
        }
    }
}

// Transforms at a core count, or returns null when the file or options are refused as documented.
static byte[]? TransformOnce(byte[] data, JpegTransformOptions options, int cores, out JpegTransformResult? result)
{
    try
    {
        using MemoryStream input = new(data);
        using MemoryStream output = new();
        result = JpegTransform.Apply(input, output, options, Context() with { MaxParallelism = cores });
        return output.ToArray();
    }
    catch (Exception e) when (e is EgamiException or NotSupportedException or InvalidOperationException { Source: "Egami" } or ArgumentOutOfRangeException { ParamName: "options" })
    {
        result = null;
        return null;
    }
}

static void WriteJpeg(Stream stream)
{
    byte[] data = ReadAll(stream);
    if (data.Length < 8)
    {
        return;
    }

    EgamiContext context = Context() with { MaxParallelism = 1 + (data[6] % 4) };
    int width = 1 + ((data[0] | ((data[1] & 1) << 8)) % 300);
    int height = 1 + ((data[2] | ((data[3] & 1) << 8)) % 300);
    byte flags = data[5];
    byte more = data[6];

    // The high bits of the size bytes: arithmetic coding, lossless, the predictor (0 automatic), the point transform and the
    // lossless precision (0 to 3 the pixel type's).
    bool arithmetic = (data[3] & 2) != 0;
    bool lossless = (data[3] & 4) != 0;
    int predictor = (data[3] >> 3) & 7;
    int pt = data[3] >> 6;
    int precisionCode = data[1] >> 1;
    int? llPrecision = precisionCode < 4 ? null : 2 + (precisionCode % 15);
    JpegLosslessOptions? Lossless(int bits) => lossless
        ? new JpegLosslessOptions { Predictor = predictor == 0 ? null : predictor, PointTransform = Math.Min(pt, (llPrecision ?? bits) - 1), Precision = llPrecision }
        : null;
    JpegWriterOptions Options(bool progressive, bool arith, int bits) => new()
    {
        Quality = 1 + (data[7] % 100),
        Subsampling = (ChromaSubsampling)(flags % 3),
        OptimizeHuffman = (flags & 4) != 0,
        Progressive = progressive && !lossless,
        Precision = (flags & 16) != 0 ? 12 : 8,
        ColorType = lossless && (flags >> 5) % 4 == 3 ? JpegColorType.Auto : (JpegColorType)((flags >> 5) % 4),
        RestartInterval = (more & 8) != 0 ? more >> 4 : 0,
        Background = (more & 4) != 0 ? new Rgba8(data[0], data[1], data[2], 255) : null,
        Arithmetic = arith,
        Lossless = Lossless(bits),
    };

    using Raster raster = (data[4] % 7) switch
    {
        0 => Fill<Rgb8>(width, height, data, context),
        1 => Fill<Gray8>(width, height, data, context),
        2 => Fill<Cmyk8>(width, height, data, context),
        3 => Fill<Rgb16>(width, height, data, context),
        4 => Fill<Gray16>(width, height, data, context),
        5 => Fill<Cmyk16>(width, height, data, context),
        _ => Fill<Rgba8>(width, height, data, context),
    };

    int sourceBits = raster.Layout.ChannelType == PixelChannelType.Unorm8 ? 8 : 16;
    bool progressive = (flags & 8) != 0 && !lossless;
    byte[] file = Save(raster, Options(progressive, arithmetic, sourceBits));
    using Raster back = Raster.Load(file, context);
    if (back.Width != width || back.Height != height || back.Metadata.Warnings.Count > 0)
    {
        throw new InvalidOperationException($"The JPEG read back as {back.Width} × {back.Height} with {back.Metadata.Warnings.Count} warnings.");
    }

    if (arithmetic)
    {
        // Only the entropy coding differs, so the Huffman file must give the same pixels.
        using Raster huffman = Raster.Load(Save(raster, Options(progressive, false, sourceBits)), context);
        Same(huffman, back, "the arithmetic file", "the Huffman one");
    }

    if (lossless && llPrecision is null && pt == 0 && (more & 4) == 0 && (flags >> 5) % 4 is 0 or 3)
    {
        // Nothing asked for a loss: the samples come back exactly, alpha aside. 8-bit CMYK loads as RGB unless asked for inks.
        if (raster.Layout.ChannelOrder == PixelChannelOrder.Cmyk)
        {
            using Raster<Cmyk16> written = raster.As<Cmyk16>();
            using Raster<Cmyk16> read = Raster.Load<Cmyk16>(file, context);
            Same(written, read, "the lossless file", "the pixels written");
        }
        else
        {
            using Raster<Rgb16> written = raster.As<Rgb16>();
            using Raster<Rgb16> read = back.As<Rgb16>();
            Same(written, read, "the lossless file", "the pixels written");
        }
    }

    if (progressive)
    {
        using Raster baseline = Raster.Load(Save(raster, Options(false, arithmetic, sourceBits)), context);
        using Raster<Rgba16> a = baseline.As<Rgba16>();
        using Raster<Rgba16> b = back.As<Rgba16>();
        for (int y = 0; y < height; y++)
        {
            if (!a.Row(y).SequenceEqual(b.Row(y)))
            {
                throw new InvalidOperationException($"Progressive row {y} decodes differently from baseline.");
            }
        }
    }

    if ((more & 2) != 0 && raster is Raster<Rgb16> rgb)
    {
        using MemoryStream streamed = new();
        using (RasterEncoder<Rgb16> encoder = RasterEncoder.Create<Rgb16>(streamed, JpegFormat.Info, width, height, Options(progressive, arithmetic, sourceBits), context: context))
        {
            encoder.WriteRows(rgb);
            encoder.Complete();
        }

        if (!streamed.ToArray().AsSpan().SequenceEqual(file))
        {
            throw new InvalidOperationException("A streamed encode wrote a different file from Save.");
        }
    }

    static byte[] Save(Raster raster, JpegWriterOptions options)
    {
        using MemoryStream output = new();
        raster.Save(output, JpegFormat.Info, options);
        return output.ToArray();
    }

    static void Same(Raster expected, Raster actual, string what, string than)
    {
        using Raster<Rgba16> a = expected.As<Rgba16>();
        using Raster<Rgba16> b = actual.As<Rgba16>();
        for (int y = 0; y < a.Height; y++)
        {
            if (!a.Row(y).SequenceEqual(b.Row(y)))
            {
                throw new InvalidOperationException($"Row {y} of {what} decodes differently from {than}.");
            }
        }
    }

    static Raster<T> Fill<T>(int width, int height, byte[] data, EgamiContext context)
        where T : unmanaged, IPixelType<T>
    {
        Raster<T> raster = Raster.Create<T>(width, height, context);
        byte[] pixels = new byte[width * height * T.Layout.BytesPerPixel];
        ReadOnlySpan<byte> source = data.AsSpan(8);
        for (int i = 0; i < pixels.Length && !source.IsEmpty; i++)
        {
            pixels[i] = source[i % source.Length];
        }

        raster.CopyPixelsFrom(pixels);
        return raster;
    }
}

// DNG writing PR A: the lossless JPEG writer's sample entry point and lossless JPEG in TIFF. The input picks a precision, a
// component count, a size, a predictor, a restart interval and arithmetic coding, and the samples; the stream must decode to
// exactly those samples, a CFA mosaic coded as two components too, and a TIFF of the same samples at quality 100, in strips or
// tiles, must load them back exactly.
static void WriteLosslessJpegSamples(Stream stream)
{
    byte[] data = ReadAll(stream);
    if (data.Length < 6)
    {
        return;
    }

    EgamiContext context = Context() with { MaxParallelism = 1 + (data[5] % 4) };
    int precision = 2 + (data[0] % 15);
    int components = 1 + ((data[1] >> 6) & 3);
    int width = 1 + (data[1] % 64);
    int height = 1 + (data[2] % 64);
    int? predictor = data[3] % 8 == 0 ? null : data[3] % 8;
    int restart = (data[3] & 8) != 0 ? data[3] >> 4 : 0;
    bool arithmetic = (data[4] & 1) != 0;
    ReadOnlySpan<byte> source = data.AsSpan(6);
    ushort[] samples = new ushort[width * height * components];
    for (int i = 0; i < samples.Length; i++)
    {
        int at = (2 * i) % Math.Max(1, source.Length - 1);
        samples[i] = source.Length < 2 ? (ushort)0 : (ushort)(((source[at] << 8) | source[at + 1]) & ((1 << precision) - 1));
    }

    using (MemoryStream coded = new())
    {
        JpegLosslessWriter.Write(coded, samples, components, width, precision, predictor, restart, arithmetic);
        Exact(coded.ToArray(), samples, "the sample stream");
    }

    if (components == 1 && width % 2 == 0)
    {
        using MemoryStream cfa = new();
        JpegLosslessWriter.WriteCfa(cfa, samples, width, precision);
        Exact(cfa.ToArray(), samples, "the CFA stream");
    }

    // The TIFF side: 8 or 16-bit gray, RGB or CMYK at quality 100.
    bool wide = (data[4] & 2) != 0;
    int tile = (data[4] & 4) != 0 ? 16 : 0;
    Egami.Formats.Tiff.TiffWriterOptions options = new() { Compression = Egami.Formats.Tiff.TiffCompression.Jpeg, Quality = 100, TileSize = tile, ParallelCompression = (data[4] & 8) != 0 };
    switch ((data[4] >> 4) % 3, wide)
    {
        case (0, false): TiffRoundTrip<Gray8>(); break;
        case (0, true): TiffRoundTrip<Gray16>(); break;
        case (1, false): TiffRoundTrip<Rgb8>(); break;
        case (1, true): TiffRoundTrip<Rgb16>(); break;
        case (_, false): TiffRoundTrip<Cmyk8>(); break;
        default: TiffRoundTrip<Cmyk16>(); break;
    }

    void TiffRoundTrip<T>()
        where T : unmanaged, IPixelType<T>
    {
        using Raster<T> raster = Raster.Create<T>(width, height, context);
        byte[] written = new byte[width * height * T.Layout.BytesPerPixel];
        for (int i = 0; i < written.Length; i++)
        {
            written[i] = data[6 + (i % (data.Length - 6))];
        }

        raster.CopyPixelsFrom(written);
        using MemoryStream file = new();
        raster.Save(file, Egami.Formats.Tiff.TiffFormat.Info, options);
        using Raster<T> back = Raster.Load<T>(file.ToArray(), context, new ReaderOptions { Recovery = RecoveryMode.Strict });
        byte[] read = new byte[written.Length];
        back.CopyPixelsTo(read);
        if (back.Width != width || back.Height != height || back.Metadata.Warnings.Count > 0 || !written.AsSpan().SequenceEqual(read))
        {
            throw new InvalidOperationException($"The lossless JPEG TIFF of {typeof(T).Name} did not load back the pixels written.");
        }
    }

    void Exact(byte[] coded, ushort[] expected, string what)
    {
        ushort[] decoded = JpegEmbedded.DecodeLosslessSamples(default, coded, context, out _);
        if (!decoded.AsSpan().SequenceEqual(expected))
        {
            throw new InvalidOperationException($"{what} did not decode to the samples written.");
        }
    }
}

// Phase 2.3.4: JBIG2 files every way: standalone and, when the first byte is odd, embedded with the format named, the bytes
// split at the second byte's place into a globals stream and a page stream. Probe's page count must match LoadAll's.
static void LoadJbig2(Stream stream)
{
    EgamiContext context = Context() with { Limits = Context().Limits with { MaxFrames = 64 } };
    byte[] data = ReadAll(stream);
    foreach (RecoveryMode recovery in new[] { RecoveryMode.Lenient, RecoveryMode.Strict })
    {
        try
        {
            Egami.Formats.Jbig2.Jbig2ReaderOptions options = new() { Recovery = recovery };
            byte[] page = data;
            Egami.Formats.FormatInfo? named = null;
            if (data.Length > 2 && (data[0] & 1) != 0)
            {
                int split = Math.Min(data.Length - 1, 2 + (data[1] * 4));
                named = Egami.Formats.Jbig2.Jbig2Format.Info;
                page = data[split..];
                try
                {
                    options = options with { Globals = Egami.Formats.Jbig2.Jbig2Globals.Parse(data.AsSpan(2, split - 2), context) };
                }
                catch (EgamiException)
                {
                }
            }

            RasterInfo info = named is null ? Raster.Probe(page, context, options) : Raster.Probe(page, named, context, options);
            using Raster raster = named is null ? Raster.Load(page, context, options) : Raster.Load(page, named, context, options);
            using RasterList pages = named is null ? Raster.LoadAll(page, context, options) : Raster.LoadAll(page, named, context, options);
            if (pages.Count != info.ImageCount)
            {
                throw new InvalidOperationException($"LoadAll gave {pages.Count} images where Probe said {info.ImageCount}.");
            }

            using RasterDecoder decoder = named is null ? RasterDecoder.Open(page, context, options) : RasterDecoder.Open(page, named, context, options);
            using Raster rows = decoder.ReadAll();
            if (rows.Width != raster.Width || rows.Height != raster.Height)
            {
                throw new InvalidOperationException("Streaming gave another size than Load.");
            }

            Rect region = new(info.Width / 4, info.Height / 4, Math.Max(1, info.Width / 2), Math.Max(1, info.Height / 2));
            using Raster<Gray8> cropped = named is null
                ? Raster.Load<Gray8>(page, context, options with { Region = region, MinimumSize = new Size(Math.Max(1, info.Width / 5), 1) })
                : Raster.Load<Gray8>(page, named, context, options with { Region = region, MinimumSize = new Size(Math.Max(1, info.Width / 5), 1) });
        }
        catch (EgamiException)
        {
        }
        catch (NotSupportedException)
        {
            // Extensions marked necessary that Egami does not read say so.
        }
    }
}

// Phase 2.3.4: JPEG 2000 every way: Probe, Load in color and as raw components, strict and lenient, the first quality layer,
// rows through the decoder (which must give Load's size), a region at a reduced size, and YUV planes where there are some.
static void LoadJpeg2000(Stream stream)
{
    EgamiContext context = Context();
    byte[] data = ReadAll(stream);
    foreach (RecoveryMode recovery in new[] { RecoveryMode.Lenient, RecoveryMode.Strict })
    {
        try
        {
            Egami.Formats.Jpeg2000.Jpeg2000ReaderOptions options = new() { Recovery = recovery };
            RasterInfo info = Raster.Probe(data, context, options);
            using Raster raster = Raster.Load(data, context, options);
            using Raster raw = Raster.Load(data, context, options with { Components = Egami.Formats.Jpeg2000.Jpeg2000Components.Raw, MaxQualityLayers = 1, FastUpsampling = true });
            using RasterDecoder decoder = RasterDecoder.Open(data, context, options);
            using Raster rows = decoder.ReadAll();
            if (rows.Width != raster.Width || rows.Height != raster.Height || info.Width != raster.Width || info.Height != raster.Height)
            {
                throw new InvalidOperationException("Probe, Load and streaming disagree on the size.");
            }

            Rect region = new(info.Width / 4, info.Height / 4, Math.Max(1, info.Width / 2), Math.Max(1, info.Height / 2));
            using Raster<Rgba8> cropped = Raster.Load<Rgba8>(data, context, options with { Region = region, MinimumSize = new Size(Math.Max(1, info.Width / 5), 1) });
            try
            {
                using YuvRaster planes = YuvRaster.Load(data, context, options);
            }
            catch (NotSupportedException)
            {
                // Not sYCC or gray at a depth YUV planes hold.
            }
        }
        catch (EgamiException)
        {
        }
        catch (NotSupportedException)
        {
            // Part 2 and High-Throughput coding tools say so.
        }
    }
}

// Phase 2.3.3: ICO and CUR, as icons and as cursors. Every entry LoadAll finds must write back exactly.
static void LoadIco(Stream stream)
{
    byte[] data = ReadAll(stream);
    IcoEvery(data);
    if (data.Length > 4 && data[2] is 1 or 2)
    {
        data[2] ^= 3;
        IcoEvery(data);
    }
}

static void IcoEvery(byte[] data)
{
    EgamiContext context = Context();
    foreach (RecoveryMode recovery in new[] { RecoveryMode.Lenient, RecoveryMode.Strict })
    {
        ReaderOptions options = new() { Recovery = recovery };
        try
        {
            RasterInfo info = Raster.Probe(data, context, options);
            using Raster best = Raster.Load(data, context, options);
            using RasterList entries = Raster.LoadAll<Rgba8>(data, context, options);
            if (info.Width != best.Width || info.Height != best.Height || info.ImageCount != entries.Count)
            {
                throw new InvalidOperationException("Probe, Load and LoadAll disagree.");
            }

            using Raster<Rgba8> reduced = Raster.Load<Rgba8>(data, context, options with { MinimumSize = new Size(Math.Max(1, info.Width / 3), 1) });
            using (RasterDecoder decoder = RasterDecoder.Open(data, context, options))
            using (Raster rows = decoder.ReadAll())
            {
            }

            if (entries.Select(e => (e.Width, e.Height)).Distinct().Count() != entries.Count || entries.Any(e => e.Width > 256 || e.Height > 256))
            {
                // Two entries of a size cannot both be written, and entries over 256 are fitted to 256.
                continue;
            }

            using MemoryStream written = new();
            Raster.SaveAll(entries, written, data[2] == 2 ? Egami.Formats.Ico.CurFormat.Info : Egami.Formats.Ico.IcoFormat.Info);
            using RasterList again = Raster.LoadAll<Rgba8>(written.ToArray(), context);
            foreach (Raster<Rgba8> entry in entries.Cast<Raster<Rgba8>>())
            {
                Raster<Rgba8> back = (Raster<Rgba8>)again.First(r => r.Width == entry.Width && r.Height == entry.Height);
                for (int y = 0; y < entry.Height; y++)
                {
                    if (!entry.Row(y).SequenceEqual(back.Row(y)))
                    {
                        throw new InvalidOperationException($"A {entry.Width}x{entry.Height} entry did not write back exactly.");
                    }
                }
            }
        }
        catch (EgamiException)
        {
        }
    }
}

// Phase 2.3.3: the input as the data of an icon's one entry, behind a directory the target writes.
static void LoadIcoEntry(Stream stream)
{
    byte[] entry = ReadAll(stream);
    byte[] data = new byte[22 + entry.Length];
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), 1);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), 1);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), 1);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), 32);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(14), (uint)entry.Length);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(18), 22);
    entry.CopyTo(data, 22);
    IcoEvery(data);
}

// Phase 2.3.9: ANI animated cursors, leniently and strictly: Probe, Load, LoadAll, a smaller size, a frame range and the row
// decoder. Probe's size and image count must match what loads.
static void LoadAni(Stream stream)
{
    byte[] data = ReadAll(stream);
    EgamiContext context = Context();
    foreach (RecoveryMode recovery in new[] { RecoveryMode.Lenient, RecoveryMode.Strict })
    {
        ReaderOptions options = new() { Recovery = recovery };
        try
        {
            RasterInfo info = Raster.Probe(data, context, options);
            using Raster<Rgba8> animation = Raster.Load<Rgba8>(data, context, options);
            if (info.Width != animation.Width || info.Height != animation.Height || animation.FrameCount > info.FrameCount)
            {
                throw new InvalidOperationException("Probe and Load disagree.");
            }

            using RasterList sizes = Raster.LoadAll<Rgba8>(data, context, options);
            if (sizes.Count != info.ImageCount)
            {
                throw new InvalidOperationException("Probe and LoadAll disagree.");
            }

            using Raster<Rgba8> small = Raster.Load<Rgba8>(data, context, options with { MinimumSize = new Size(Math.Max(1, info.Width / 2), 1) });
            using Raster<RgbaF32> tail = Raster.Load<RgbaF32>(data, context, options with { Frames = 1.. });
            using RasterDecoder decoder = RasterDecoder.Open(data, context, options);
            using Raster rows = decoder.ReadAll();

            // Whatever loads saves, and the saved cursor loads back with the same steps.
            using MemoryStream saved = new();
            animation.Save(saved, AniFormat.Info);
            using Raster<Rgba8> again = Raster.Load<Rgba8>(saved.ToArray(), context, new ReaderOptions { Recovery = RecoveryMode.Strict });
            bool fits = animation.Width <= 256 && animation.Height <= 256;
            if (again.FrameCount != animation.FrameCount || (fits && (again.Width != animation.Width || again.Height != animation.Height)))
            {
                throw new InvalidOperationException("A saved cursor loads back differently.");
            }
        }
        catch (EgamiException)
        {
        }
    }
}

// Phase 2.3.3: PSD and PSB loaded every way; a seekable and a forward-only load must give the same pixels.
static void LoadPsd(Stream stream)
{
    EgamiContext context = Context();
    byte[] data = ReadAll(stream);
    foreach (RecoveryMode recovery in new[] { RecoveryMode.Lenient, RecoveryMode.Strict })
    {
        ReaderOptions options = new() { Recovery = recovery, Format = null };
        try
        {
            RasterInfo info = Raster.Probe(data, Egami.Formats.Psd.PsdFormat.Info, context, options);
            using Raster raster = Raster.Load(data, Egami.Formats.Psd.PsdFormat.Info, context, options);
            using Raster<Rgba16> wide = Raster.Load<Rgba16>(data, Egami.Formats.Psd.PsdFormat.Info, context, options);
            using Raster<Rgba16> forward = Raster.Load<Rgba16>(new ForwardOnlyStream(data), Egami.Formats.Psd.PsdFormat.Info, context, options);
            if (info.Width != raster.Width || info.Height != raster.Height || info.Layout != raster.Layout)
            {
                throw new InvalidOperationException("Probe and Load disagree.");
            }

            for (int y = 0; y < wide.Height; y++)
            {
                if (!wide.Row(y).SequenceEqual(forward.Row(y)))
                {
                    throw new InvalidOperationException($"Row {y} differs between a seekable and a forward-only load.");
                }
            }

            Rect region = new(info.Width / 3, info.Height / 4, Math.Max(1, info.Width / 2), Math.Max(1, info.Height / 2));
            using Raster<Rgba8> cropped = Raster.Load<Rgba8>(data, Egami.Formats.Psd.PsdFormat.Info, context, options with { Region = region, MinimumSize = new Size(Math.Max(1, info.Width / 7), 1) });
        }
        catch (EgamiException)
        {
        }
    }
}

// Phase 2.3.10: a PSD's layers read with ReadLayers; a seekable and a forward-only read must give the same layers.
static void ReadPsdLayers(Stream stream)
{
    EgamiContext context = Context();
    byte[] data = ReadAll(stream);
    foreach (RecoveryMode recovery in new[] { RecoveryMode.Lenient, RecoveryMode.Strict })
    {
        ReaderOptions options = new() { Recovery = recovery };
        try
        {
            using Egami.Formats.Psd.PsdDocument document = Egami.Formats.Psd.PsdImage.ReadLayers(new MemoryStream(data), options, context);
            using Egami.Formats.Psd.PsdDocument forward = Egami.Formats.Psd.PsdImage.ReadLayers<RgbaF32>(new ForwardOnlyStream(data), options, context);
            if (document.ColorMode is Egami.Formats.Psd.PsdColorMode.Rgb or Egami.Formats.Psd.PsdColorMode.Grayscale or Egami.Formats.Psd.PsdColorMode.Cmyk)
            {
                using Raster flat = document.Flatten();
                using Raster loaded = Raster.Load(data, Egami.Formats.Psd.PsdFormat.Info, context, new Egami.Formats.Psd.PsdReaderOptions { Recovery = recovery, Composite = Egami.Formats.Psd.PsdComposite.Flatten });
                if (flat.Width != loaded.Width || flat.Height != loaded.Height)
                {
                    throw new InvalidOperationException("Flatten and a flattening Load disagree.");
                }
            }
            if (document.Layers.Count != forward.Layers.Count)
            {
                throw new InvalidOperationException("A seekable and a forward-only read give different layers.");
            }

            for (int i = 0; i < document.Layers.Count; i++)
            {
                if (document.Layers[i].Pixels is Raster pixels)
                {
                    using Raster<RgbaF32> mine = pixels.As<RgbaF32>();
                    Raster<RgbaF32> theirs = (Raster<RgbaF32>)forward.Layers[i].Pixels!;
                    for (int y = 0; y < mine.Height; y++)
                    {
                        if (!mine.Row(y).SequenceEqual(theirs.Row(y)))
                        {
                            throw new InvalidOperationException($"Layer {i} row {y} differs between a seekable and a forward-only read.");
                        }
                    }
                }
            }
        }
        catch (EgamiException)
        {
        }
    }
}

// Phase 2.3.3: the input as a PSD's flattened image data behind a header its first four bytes choose.
static void LoadPsdData(Stream stream)
{
    byte[] input = ReadAll(stream);
    if (input.Length < 4)
    {
        return;
    }

    (int mode, int depth, int colors) = (input[0] % 6) switch
    {
        0 => (1, 8, 1),
        1 => (1, 16, 1),
        2 => (3, 8, 3),
        3 => (3, 16, 3),
        4 => (3, 32, 3),
        _ => (4, 8, 4),
    };
    int channels = colors + (input[1] & 1);
    int version = 1 + ((input[1] >> 1) & 1);
    int width = 1 + (input[2] % 40);
    int height = 1 + (input[3] % 40);
    using MemoryStream file = new();
    file.Write("8BPS"u8);
    Span<byte> b = stackalloc byte[22];
    b.Clear();
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)version);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(b[8..], (ushort)channels);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b[10..], (uint)height);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b[14..], (uint)width);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(b[18..], (ushort)depth);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(b[20..], (ushort)mode);
    file.Write(b);
    file.Write(new byte[8]);
    if (channels > colors)
    {
        // A layer section with a count of -1 and no records: its sign makes the extra channel the transparency.
        byte[] section = version == 1
            ? [0, 0, 0, 10, 0, 0, 0, 2, 0xFF, 0xFF, 0, 0, 0, 0]
            : [0, 0, 0, 0, 0, 0, 0, 14, 0, 0, 0, 0, 0, 0, 0, 2, 0xFF, 0xFF, 0, 0, 0, 0];
        file.Write(section);
    }
    else
    {
        file.Write(new byte[version == 1 ? 4 : 8]);
    }

    file.Write(input.AsSpan(4));
    LoadPsd(new MemoryStream(file.ToArray()));
}

// Phase 2.3.4: JPEG 2000's tier 1 fed directly. The first eight bytes pick the code-block's size, its band's orientation, its
// style, its bit-planes and its segments' passes; the rest is its coded data. Nothing may throw but InvalidDataException.
static void DecodeJpeg2000Codec(Stream stream)
{
    byte[] data = ReadAll(stream);
    if (data.Length < 8)
    {
        return;
    }

    int width = 1 + (data[0] % 64);
    int height = 1 + (data[1] % 64);
    int style = data[3] & 0x3F;
    int planes = 1 + (data[4] % 30);
    byte[] coded = data[8..];
    Egami.Formats.Jpeg2000.J2kCodeBlock block = new() { X0 = 0, Y0 = 0, X1 = width, Y1 = height, Included = true };
    int passes = Math.Min((3 * planes) - 2, 1 + data[5]);
    int segments = 0;
    int used = 0;
    for (int pass = 0; pass < passes; segments++)
    {
        int n = Math.Min(passes - pass, Egami.Formats.Jpeg2000.J2kTier2.MaxPasses(style, segments));
        int length = segments == 0 ? Math.Min(coded.Length, data[6] * 4) : Math.Min(coded.Length - used, data[7] + segments);
        length = Math.Max(0, Math.Min(length, coded.Length - used));
        block.Segments.Add(new Egami.Formats.Jpeg2000.J2kSegment { Passes = n, KeptPasses = n, Length = length });
        block.Chunks.Add(coded.AsMemory(used, length));
        block.Bytes += length;
        used += length;
        pass += n;
    }

    Egami.Formats.Jpeg2000.J2kTileComponent tc = new()
    {
        Style = new Egami.Formats.Jpeg2000.J2kCodingStyle { BlockStyle = style, Reversible = (data[2] & 4) != 0 },
        Quantization = new Egami.Formats.Jpeg2000.J2kQuantization(),
        RoiShift = (data[2] & 8) != 0 ? data[2] >> 4 : 0,
    };
    Egami.Formats.Jpeg2000.J2kBand band = new() { Orientation = data[2] & 3, MagnitudePlanes = planes, Step = 0.5f };
    int[] integers = new int[width * height];
    float[] floats = new float[width * height];
    try
    {
        new Egami.Formats.Jpeg2000.J2kTier1().Decode(block, band, tc, integers, floats, 0, width);
    }
    catch (InvalidDataException)
    {
    }
}

// Phase 2.3.4: JBIG2's procedures fed directly. The first byte picks one, the next eight its parameters, the rest is its data:
// generic regions (every template, TPGDON, the extended template, AT pixels), MMR, refinement, code table segments and
// their decoding, arithmetic and Huffman symbol dictionaries, text regions over fixed symbols, pattern dictionaries and
// halftone regions. Nothing may throw but InvalidDataException, EndOfStreamException or LimitExceededException.
static void DecodeJbig2Codec(Stream stream)
{
    byte[] data = ReadAll(stream);
    if (data.Length < 9)
    {
        return;
    }

    byte[] rest = data[9..];
    int width = 1 + (data[1] % 200);
    int height = 1 + (data[2] % 120);
    int template = data[3] & 3;
    Egami.Formats.Jbig2.Jbig2Budget budget = new(Context().Limits with { MaxTotalBytes = 64L * 1024 * 1024 });
    Egami.Formats.Jbig2.Jbig2ArithState arith = new()
    {
        Gb = new byte[Egami.Formats.Jbig2.Jbig2Generic.ContextCount(template)],
        Gr = new byte[Egami.Formats.Jbig2.Jbig2Refinement.ContextCount(data[4] & 1)],
    };
    arith.Mq.Start(rest, 0, rest.Length);
    int[] at = [(sbyte)data[5], (sbyte)data[6], (sbyte)data[7], (sbyte)data[8], -2, -2, 2, -1, 1, -2, 0, -1, -3, 0, -4, 0, 2, -2, 3, -1, -2, -2, -3, -1];
    Egami.Formats.Jbig2.Jbig2Bitmap[] symbols = [new(3, 5), new(7, 2), new(1, 1), new(12, 9)];
    symbols[3].Fill(1);
    try
    {
        switch (data[0] % 9)
        {
            case 0:
                Egami.Formats.Jbig2.Jbig2Generic.DecodeArithmetic(
                    new() { Width = width, Height = height, Template = template, TypicalPrediction = (data[4] & 2) != 0, ExtendedTemplate = (data[4] & 4) != 0, At = at },
                    arith.Mq,
                    new byte[Egami.Formats.Jbig2.Jbig2Generic.ContextCount(template)]);
                break;
            case 1:
                Egami.Formats.Jbig2.Jbig2Generic.DecodeMmr(width * 4, height, rest);
                break;
            case 2:
                Egami.Formats.Jbig2.Jbig2Bitmap reference = new(width, height);
                reference.Fill(data[4] & 1);
                Egami.Formats.Jbig2.Jbig2Refinement.Decode(
                    new() { Width = width + 3, Height = height, Template = data[4] & 1, Reference = reference, ReferenceDx = (sbyte)data[5] % 8, ReferenceDy = (sbyte)data[6] % 8, TypicalPrediction = (data[4] & 2) != 0, At = at[..4] },
                    arith.Mq,
                    arith.Gr);
                break;
            case 3:
                Egami.Formats.Jbig2.Jbig2HuffmanTable table = Egami.Formats.Jbig2.Jbig2HuffmanTable.Read(rest, 0, rest.Length);
                Egami.Formats.Jbig2.Jbig2BitReader bits = new(rest, Math.Min(rest.Length, 9), rest.Length);
                for (int i = 0; i < 64; i++)
                {
                    table.Decode(bits);
                }

                break;
            case 4:
            case 5:
                bool huffman = data[0] % 9 == 5;
                Egami.Formats.Jbig2.Jbig2Dictionaries.DecodeSymbols(
                    new()
                    {
                        Huffman = huffman,
                        RefAgg = (data[4] & 1) != 0,
                        Input = symbols,
                        NewSymbols = data[2] % 20,
                        Exported = data[3] % 24,
                        HuffDh = huffman ? Egami.Formats.Jbig2.Jbig2HuffmanTable.Standard(4) : null,
                        HuffDw = huffman ? Egami.Formats.Jbig2.Jbig2HuffmanTable.Standard(2) : null,
                        HuffBmSize = huffman ? Egami.Formats.Jbig2.Jbig2HuffmanTable.Standard(1) : null,
                        HuffAggInst = huffman ? Egami.Formats.Jbig2.Jbig2HuffmanTable.Standard(1) : null,
                        Template = template,
                        At = template == 0 ? at[..8] : at[..2],
                        RefinementTemplate = data[4] & 1,
                        RefinementAt = at[..4],
                    },
                    arith,
                    huffman ? new Egami.Formats.Jbig2.Jbig2BitReader(rest, 0, rest.Length) : null,
                    budget);
                break;
            case 6:
            case 7:
                bool huffmanText = data[0] % 9 == 7;
                Egami.Formats.Jbig2.Jbig2BitReader textBits = new(rest, 0, rest.Length);
                arith.Iaid = new Egami.Formats.Jbig2.Jbig2SymbolIdDecoder(2);
                Egami.Formats.Jbig2.Jbig2Text.Decode(
                    new()
                    {
                        Huffman = huffmanText,
                        Refine = (data[4] & 1) != 0,
                        Width = width,
                        Height = height,
                        Instances = data[3] % 40,
                        Strips = 1 << (data[4] >> 6),
                        Symbols = symbols,
                        SymbolCodes = huffmanText ? Egami.Formats.Jbig2.Jbig2Text.ReadSymbolCodes(textBits, symbols.Length) : null,
                        SymbolCodeLength = 2,
                        DefaultPixel = (data[4] >> 1) & 1,
                        Combine = (Egami.Formats.Jbig2.Jbig2Combine)(data[5] % 5),
                        Transposed = (data[4] & 4) != 0,
                        RefCorner = (data[4] >> 3) & 3,
                        DsOffset = ((sbyte)data[6]) % 16,
                        HuffFs = Egami.Formats.Jbig2.Jbig2HuffmanTable.Standard(6),
                        HuffDs = Egami.Formats.Jbig2.Jbig2HuffmanTable.Standard(8),
                        HuffDt = Egami.Formats.Jbig2.Jbig2HuffmanTable.Standard(11),
                        HuffRdw = Egami.Formats.Jbig2.Jbig2HuffmanTable.Standard(15),
                        HuffRdh = Egami.Formats.Jbig2.Jbig2HuffmanTable.Standard(15),
                        HuffRdx = Egami.Formats.Jbig2.Jbig2HuffmanTable.Standard(15),
                        HuffRdy = Egami.Formats.Jbig2.Jbig2HuffmanTable.Standard(15),
                        HuffRsize = Egami.Formats.Jbig2.Jbig2HuffmanTable.Standard(1),
                        RefinementTemplate = data[4] & 1,
                        RefinementAt = at[..4],
                    },
                    arith,
                    textBits,
                    budget);
                break;
            default:
                Egami.Formats.Jbig2.Jbig2Bitmap[] patterns = Egami.Formats.Jbig2.Jbig2Dictionaries.DecodePatterns(
                    (data[4] & 1) != 0, template, 1 + (data[5] % 16), 1 + (data[6] % 16), data[7] % 32, rest, 0, rest.Length / 2, arith, budget);
                Egami.Formats.Jbig2.Jbig2Halftone.Decode(
                    new()
                    {
                        Width = width,
                        Height = height,
                        Mmr = (data[4] & 1) != 0,
                        Template = template,
                        Patterns = patterns,
                        DefaultPixel = (data[4] >> 1) & 1,
                        Combine = (Egami.Formats.Jbig2.Jbig2Combine)(data[8] % 5),
                        EnableSkip = (data[4] & 4) != 0,
                        GridWidth = 1 + (data[1] % 40),
                        GridHeight = 1 + (data[2] % 40),
                        GridX = (sbyte)data[5] * 64,
                        GridY = (sbyte)data[6] * 64,
                        VectorX = data[7] * 16,
                        VectorY = data[8] * 4,
                    },
                    rest,
                    rest.Length / 2,
                    rest.Length,
                    arith,
                    budget);
                break;
        }
    }
    catch (InvalidDataException)
    {
    }
    catch (EndOfStreamException)
    {
    }
    catch (LimitExceededException)
    {
    }
}

static void ParseSvgPath(Stream stream)
{
    byte[] bytes = ReadAll(stream);
    string text = System.Text.Encoding.UTF8.GetString(bytes);

    // Strict, Try and lenient must agree: what Try accepts parses strictly to the same path, and what it refuses throws an
    // SvgPathException at a position inside the text, while lenient reading never throws.
    bool accepted = DrawingPath.TryParseSvg(text, out DrawingPath? tried);
    DrawingPath lenient = DrawingPath.ParseSvg(text, lenient: true);
    try
    {
        DrawingPath strict = DrawingPath.ParseSvg(text);
        if (!accepted || !Same(strict, tried!) || !Same(strict, lenient))
        {
            throw new InvalidOperationException("ParseSvg, TryParseSvg and lenient reading disagree on valid data.");
        }
    }
    catch (Egami.Drawing.SvgPathException e)
    {
        if (accepted || e.Position < 0 || e.Position > text.Length)
        {
            throw new InvalidOperationException($"A bad position or a refusal of accepted data: {e.Message}");
        }
    }

    // Whatever was read writes back and reads again to the same commands and points, within the written precision; relative
    // offsets between coordinates near the float range cannot be written, so those paths are written absolute only.
    float largest = 0;
    foreach (System.Numerics.Vector2 p in lenient.Points)
    {
        largest = MathF.Max(largest, MathF.Max(MathF.Abs(p.X), MathF.Abs(p.Y)));
    }

    foreach (Egami.Drawing.SvgPathFormat format in (Egami.Drawing.SvgPathFormat[])[new(), new() { Compact = true }, new() { Relative = true }, new() { Relative = true, Compact = true, Precision = 6 }])
    {
        if (format.Relative && largest > 1e30f)
        {
            continue;
        }

        DrawingPath back = DrawingPath.ParseSvg(lenient.ToSvg(format));
        if (!back.Verbs.SequenceEqual(lenient.Verbs) || back.Points.Length != lenient.Points.Length)
        {
            throw new InvalidOperationException("A written path read back with other commands.");
        }

        for (int i = 0; i < back.Points.Length; i++)
        {
            System.Numerics.Vector2 a = lenient.Points[i], b = back.Points[i];
            // A relative offset from a far point lands only as close as floats near that point allow.
            float slack = (0.6f * MathF.Pow(10, -format.Precision)) + (MathF.Max(1, format.Relative ? largest : MathF.Max(MathF.Abs(a.X), MathF.Abs(a.Y))) * 1e-6f * (format.Relative ? 4 : 1));
            if (MathF.Abs(a.X - b.X) > slack || MathF.Abs(a.Y - b.Y) > slack)
            {
                throw new InvalidOperationException($"Point {i} moved from {a} to {b} in a round trip as {format}.");
            }
        }
    }

    // Small paths go on through the geometry: measuring, dashing, stroking and the boolean engine must not fail.
    if (lenient.Verbs.Length <= 64 && largest < 1e6f)
    {
        Egami.Drawing.PathMeasure measure = new(lenient);
        foreach (Egami.Drawing.ContourMeasure contour in measure.Contours)
        {
            contour.GetSegment(contour.Length / 3, contour.Length * 2 / 3);
            contour.GetMatrix(contour.Length / 2);
        }

        lenient.Apply(Egami.Drawing.PathEffect.Dash(new Egami.Drawing.DashPattern([3, 2])));
        new Egami.Drawing.Stroke(2) { Join = Egami.Drawing.LineJoin.Round }.ToPath(lenient, simplify: true);
        lenient.Simplify();
        lenient.WithFillRule(Egami.Drawing.FillRule.EvenOdd).Simplify();
    }

    static bool Same(DrawingPath a, DrawingPath b) => a.Verbs.SequenceEqual(b.Verbs) && a.Points.SequenceEqual(b.Points);
}

static void LoadDrawing(Stream stream)
{
    EgamiContext context = Context();
    byte[] data = ReadAll(stream);
    try
    {
        Egami.Drawing.Picture picture = Egami.Drawing.Picture.Load(new MemoryStream(data), context);
        byte[] first = SaveDrawing(picture);
        byte[] second = SaveDrawing(Egami.Drawing.Picture.Load(new MemoryStream(first), context));
        if (!first.AsSpan().SequenceEqual(second))
        {
            throw new InvalidOperationException("A loaded picture does not save to the same bytes twice.");
        }

        using Raster<Rgba8> target = Raster.Create<Rgba8>(32, 32, context);
        using Egami.Drawing.RasterCanvas<Rgba8> canvas = new(target);
        float fit = 32 / MathF.Max(picture.Bounds.Width, picture.Bounds.Height);
        if (float.IsFinite(fit))
        {
            canvas.Scale(fit);
        }
        canvas.DrawPicture(picture);
    }
    catch (EgamiException)
    {
    }

    static byte[] SaveDrawing(Egami.Drawing.Picture picture)
    {
        using MemoryStream output = new();
        picture.Save(output);
        return output.ToArray();
    }
}

static byte[] ReadAll(Stream stream)
{
    using MemoryStream copy = new();
    stream.CopyTo(copy);
    return copy.ToArray();
}

// Fonts (Phase 2.7.1): every face of the data loaded leniently and strictly, every name, metric and table read, the first glyphs'
// outlines and extents, kerning among them, and text shaped and measured. Only EgamiExceptions may escape.
static void LoadFont(Stream stream)
{
    byte[] data = ReadAll(stream);
    _ = Egami.Fonts.FontLoader.Probe(data);
    foreach (bool strict in new[] { false, true })
    {
        var options = new Egami.Fonts.FontLoadOptions { Strict = strict, Context = Context() };
        IReadOnlyList<Egami.Fonts.Typeface> faces;
        try
        {
            faces = Egami.Fonts.FontLoader.LoadAll(data, options);
        }
        catch (EgamiException)
        {
            continue;
        }

        foreach (Egami.Fonts.Typeface face in faces)
        {
            try
            {
                ExerciseFace(face);
            }
            catch (EgamiException) when (strict)
            {
            }
        }
    }
}

// TrueType hinting (Phase 2.7.5): the first glyphs of every face hinted for each target at a few sizes, GDI's compatible widths,
// and text shaped and measured with grid-fitted metrics. The interpreter must stop broken programs itself: only EgamiExceptions
// may escape, and no glyph may hang.
static void HintFont(Stream stream)
{
    byte[] data = ReadAll(stream);
    IReadOnlyList<Egami.Fonts.Typeface> faces;
    try
    {
        faces = Egami.Fonts.FontLoader.LoadAll(data, new Egami.Fonts.FontLoadOptions { Context = Context() });
    }
    catch (EgamiException)
    {
        return;
    }

    foreach (Egami.Fonts.Typeface face in faces)
    {
        try
        {
            int glyphs = Math.Min(face.GlyphCount, 48);
            foreach (int ppem in new[] { 9, 16, 37 })
            {
                foreach (Egami.Fonts.TrueTypeTarget target in new[] { Egami.Fonts.TrueTypeTarget.GdiPlus, Egami.Fonts.TrueTypeTarget.Gdi, Egami.Fonts.TrueTypeTarget.FreeType })
                {
                    for (int g = 0; g < glyphs; g++)
                    {
                        _ = face.GetHintedGlyph((ushort)g, ppem, target);
                    }
                }

                for (int g = 0; g < glyphs; g++)
                {
                    _ = face.GetCompatibleAdvance((ushort)g, ppem);
                }

                var font = new Egami.Fonts.Font(face, ppem) { Hinting = Egami.Fonts.FontHinting.Full, Metrics = Egami.Fonts.FontMetrics.GridFitted };
                _ = font.MeasureText("Hamburgefonstiv 0123");
                _ = (font with { Metrics = Egami.Fonts.FontMetrics.Compatible }).MeasureText("Hamburgefonstiv 0123");
            }
        }
        catch (EgamiException)
        {
        }
    }
}

// Bare CFF streams, as PDF embeds them.
static void LoadCff(Stream stream)
{
    byte[] data = ReadAll(stream);
    Egami.Fonts.Typeface face;
    try
    {
        face = Egami.Fonts.Typeface.FromCff(data, new Egami.Fonts.CffOptions { LoadOptions = new Egami.Fonts.FontLoadOptions { Context = Context() } });
    }
    catch (EgamiException)
    {
        return;
    }

    ExerciseFace(face);
}

// Font data for PDF (Phase 2.7.3): glyph names both ways, CIDs, every cmap subtable and a CFF font's own encoding of a font or
// bare CFF stream that loads. Nothing may throw once it has loaded, and every lookup must agree with its reverse.
static void FontData(Stream stream)
{
    byte[] data = ReadAll(stream);
    Egami.Fonts.Typeface face;
    try
    {
        face = Egami.Fonts.FontLoader.Probe(data).Kind == Egami.Fonts.FontFileKind.Cff
            ? Egami.Fonts.Typeface.FromCff(data, new Egami.Fonts.CffOptions { LoadOptions = new Egami.Fonts.FontLoadOptions { Context = Context() } })
            : Egami.Fonts.Typeface.FromMemory(data, 0, new Egami.Fonts.FontLoadOptions { Context = Context() });
    }
    catch (EgamiException)
    {
        return;
    }

    ExerciseFontData(face);
}

static void ExerciseFontData(Egami.Fonts.Typeface face)
{
    int count = Math.Min(face.GlyphCount, 512);
    for (int g = 0; g < count; g++)
    {
        if (face.GetGlyphName((ushort)g) is string name && (!face.TryGetGlyphByName(name, out ushort back) || back > g))
        {
            throw new InvalidOperationException($"Glyph {g} is named {name}, which finds glyph {back}.");
        }

        int cid = face.GetCid((ushort)g);
        if (cid >= 0 && (!face.TryGetGlyphByCid(cid, out ushort byCid) || byCid > g))
        {
            throw new InvalidOperationException($"Glyph {g} has CID {cid}, which finds glyph {byCid}.");
        }
    }

    _ = face.CidSystemInfo;
    var maps = new List<Egami.Fonts.CharacterMap>(face.CharacterMaps);
    if (face.BuiltInEncoding is Egami.Fonts.CharacterMap own)
    {
        maps.Add(own);
    }

    foreach (Egami.Fonts.CharacterMap map in maps)
    {
        int seen = 0;
        foreach (Egami.Fonts.CharacterMapping mapping in map.GetMappings())
        {
            if (map.GetGlyph(mapping.Code) != mapping.Glyph || mapping.Glyph == 0)
            {
                throw new InvalidOperationException($"{map} lists {mapping} but maps the code to {map.GetGlyph(mapping.Code)}.");
            }

            if (++seen > 70000)
            {
                break;
            }
        }

        _ = face.FindCharacterMap(map.PlatformId, map.EncodingId);
    }
}

// Subsets of whatever loads: written, re-loaded, and every kept glyph's outline unchanged.
static void SubsetFont(Stream stream)
{
    byte[] data = ReadAll(stream);
    Egami.Fonts.Typeface face;
    try
    {
        face = Egami.Fonts.FontLoader.Probe(data).Kind == Egami.Fonts.FontFileKind.Cff
            ? Egami.Fonts.Typeface.FromCff(data)
            : Egami.Fonts.Typeface.FromMemory(data, 0, new Egami.Fonts.FontLoadOptions { Context = Context() });
    }
    catch (EgamiException)
    {
        return;
    }

    int hash = data.Length > 0 ? data[^1] : 0;
    ushort[] glyphs = [.. Enumerable.Range(0, Math.Min(face.GlyphCount, 40)).Where(g => ((g + hash) % 3) != 0).Select(g => (ushort)g)];
    var options = new Egami.Fonts.SubsetOptions { RetainGlyphIds = (hash & 1) != 0, KeepCmap = (hash & 2) != 0, FontNamePrefix = "FUZZED+" };
    Egami.Fonts.FontSubset subset;
    try
    {
        subset = Egami.Fonts.FontSubsetter.Subset(face, glyphs, options);
    }
    catch (EgamiException)
    {
        // A kept glyph that cannot be read.
        return;
    }

    Egami.Fonts.Typeface again = subset.ToTypeface();
    foreach ((ushort old, ushort now) in subset.GlyphMap)
    {
        var a = new RecordingSink();
        var b = new RecordingSink();
        bool readA = face.GetGlyphOutline(old, a), readB = again.GetGlyphOutline(now, b);
        if (readA && (!readB || !a.SameAs(b)))
        {
            throw new InvalidOperationException($"Glyph {old} changed in the subset.");
        }
    }
}

// Variable fonts (Phase 2.7.2): every face at its default, at each named instance and at positions the data picks, every
// outline, advance, extent and kerning pair read there.
static void LoadVariable(Stream stream)
{
    byte[] data = ReadAll(stream);
    IReadOnlyList<Egami.Fonts.Typeface> faces;
    try
    {
        faces = Egami.Fonts.FontLoader.LoadAll(data, new Egami.Fonts.FontLoadOptions { Context = Context() });
    }
    catch (EgamiException)
    {
        return;
    }

    foreach (Egami.Fonts.Typeface face in faces)
    {
        foreach (Egami.Fonts.Typeface at in Positions(face, data))
        {
            ExerciseFace(at);
        }
    }
}

static IEnumerable<Egami.Fonts.Typeface> Positions(Egami.Fonts.Typeface face, byte[] data)
{
    yield return face;
    IReadOnlyList<Egami.Fonts.VariationAxis> axes = face.VariationAxes;
    for (int i = 0; i < Math.Min(face.NamedInstances.Count, 3); i++)
    {
        yield return face.WithNamedInstance(i);
    }

    for (int k = 0; k < 3 && axes.Count > 0; k++)
    {
        var values = new Egami.Fonts.FontVariation[axes.Count];
        for (int a = 0; a < axes.Count; a++)
        {
            int b = data.Length == 0 ? 0 : data[((k * 31) + (a * 7)) % data.Length];
            float t = (b / 255f * 1.4f) - 0.2f;
            values[a] = new Egami.Fonts.FontVariation(axes[a].Tag, axes[a].Min + ((axes[a].Max - axes[a].Min) * t));
        }

        yield return face.WithVariations(values);
    }
}

// Color fonts (Phase 2.7.2): palettes, every glyph's color data and SVG document read, and the first color glyphs drawn on a
// small canvas, at default and other positions.
static void LoadColor(Stream stream)
{
    byte[] data = ReadAll(stream);
    IReadOnlyList<Egami.Fonts.Typeface> faces;
    try
    {
        faces = Egami.Fonts.FontLoader.LoadAll(data, new Egami.Fonts.FontLoadOptions { Context = Context() });
    }
    catch (EgamiException)
    {
        return;
    }

    foreach (Egami.Fonts.Typeface face in faces)
    {
        _ = face.ColorFormats;
        _ = face.Palettes.Count;
        foreach (Egami.Fonts.Typeface at in Positions(face, data).Take(2))
        {
            int drawn = 0;

            // Embedded black-and-white and gray strikes (EBLC and EBDT), decoded and drawn at each strike size up to 24 ppem.
            if (at.EmbeddedBitmaps is not null)
            {
                for (int ppem = 8; ppem <= 24; ppem++)
                {
                    for (int g = 0; g < Math.Min(at.GlyphCount, 64); g++)
                    {
                        _ = at.GetEmbeddedBitmap((ushort)g, ppem);
                    }

                    using Raster<Rgba8> strike = Raster.Create<Rgba8>(64, 32, Context());
                    using Egami.Drawing.RasterCanvas<Rgba8> strikeCanvas = new(strike);
                    strikeCanvas.DrawText("AaBb19", new System.Numerics.Vector2(2, 24), new Egami.Fonts.Font(at, ppem) { EmbeddedBitmaps = true, Metrics = Egami.Fonts.FontMetrics.GridFitted }, Rgba8.Black);
                }
            }

            for (int g = 0; g < Math.Min(at.GlyphCount, 300); g++)
            {
                _ = at.TryGetSvgDocument((ushort)g, out _, out _, out _);
                if (!at.TryGetColorGlyph((ushort)g, data.Length % 2 == 0 ? null : Egami.Fonts.FontPalette.Dark, out _, 20))
                {
                    continue;
                }

                if (drawn++ < 6)
                {
                    using Raster<Rgba8> target = Raster.Create<Rgba8>(24, 24, Context());
                    using Egami.Drawing.RasterCanvas<Rgba8> canvas = new(target);
                    var font = new Egami.Fonts.Font(at, 18);
                    canvas.DrawGlyphRun(Egami.Fonts.GlyphRun.Create(font, [(ushort)g], [new System.Numerics.Vector2(2, 20)]), default, Rgba8.Black);
                    canvas.Rotate(10);
                    canvas.DrawGlyphRun(Egami.Fonts.GlyphRun.Create(font, [(ushort)g], [new System.Numerics.Vector2(2, 20)]), default, new Rgba8(0, 0, 0, 100));
                }
            }
        }
    }
}

// Static instances (Phase 2.7.2): a variable face subset at positions the data picks, re-loaded and drawn.
static void InstanceFont(Stream stream)
{
    byte[] data = ReadAll(stream);
    Egami.Fonts.Typeface face;
    try
    {
        face = Egami.Fonts.Typeface.FromMemory(data, 0, new Egami.Fonts.FontLoadOptions { Context = Context() });
    }
    catch (EgamiException)
    {
        return;
    }

    foreach (Egami.Fonts.Typeface at in Positions(face, data).Skip(1).Take(2))
    {
        ushort[] glyphs = [.. Enumerable.Range(0, Math.Min(at.GlyphCount, 24)).Select(g => (ushort)g)];
        Egami.Fonts.FontSubset subset;
        try
        {
            subset = Egami.Fonts.FontSubsetter.Subset(at, glyphs, new Egami.Fonts.SubsetOptions { RetainGlyphIds = data.Length % 2 == 0 });
        }
        catch (EgamiException)
        {
            continue;
        }

        Egami.Fonts.Typeface again = subset.ToTypeface();
        if (again.IsVariable)
        {
            throw new InvalidOperationException("An instance is still variable.");
        }

        ExerciseFace(again);
    }
}

static void ExerciseFace(Egami.Fonts.Typeface face)
{
    _ = face.FamilyName + face.SubfamilyName + face.FullName + face.PostScriptName;
    _ = face.Style;
    _ = face.Extents;
    _ = face.HheaMetrics;
    _ = face.TypoMetrics;
    _ = face.WinMetrics;
    _ = face.Embedding;
    _ = face.IsFixedPitch;
    _ = face.GetNames().Count;
    var sink = new RecordingSink();
    int count = Math.Min(face.GlyphCount, 64);
    for (int g = 0; g < count; g++)
    {
        face.GetGlyphOutline((ushort)g, sink);
        _ = face.GetGlyphExtents((ushort)g);
        _ = face.GetAdvance((ushort)g);
        _ = face.GetLeftSideBearing((ushort)g);
        _ = face.GetKerning((ushort)g, (ushort)((g * 7) % count));
    }

    var font = new Egami.Fonts.Font(face, 16);
    Egami.Fonts.GlyphRun run = font.Shape("Fuzz AV\u00C5\r\nfi \U0001F600\uFE0F");
    _ = run.Bounds;
    _ = font.MeasureText("To Wa");
    ExerciseFontData(face);
}

// Shaping (Phase 2.8.1): a font that loads shapes a fixed set of texts in many scripts, run by run with features and directions
// the bytes pick and through TextShaper.Shape in every direction, and its GSUB closure is taken for each text's glyphs. Nothing
// may throw once the font has loaded, and every glyph's cluster must be inside its run.
static void ShapeFont(Stream stream)
{
    byte[] data = ReadAll(stream);
    Egami.Fonts.Typeface face;
    try
    {
        face = Egami.Fonts.Typeface.FromMemory(data, 0, new Egami.Fonts.FontLoadOptions { Context = Context() });
    }
    catch (EgamiException)
    {
        return;
    }

    int hash = data.Length > 0 ? data[^1] : 0;
    var font = new Egami.Fonts.Font(face, 16);
    Egami.Fonts.Shaping.FontFeature[][] featureSets =
    [
        [],
        [Egami.Fonts.Shaping.FontFeature.Ligatures(false), Egami.Fonts.Shaping.FontFeature.NoKerning],
        [Egami.Fonts.Shaping.FontFeature.SmallCaps, Egami.Fonts.Shaping.FontFeature.StylisticSet(1), new(new Egami.Fonts.FontTag("dlig"), 1, 2, 5), new(new Egami.Fonts.FontTag("aalt"), 3)],
        [new(new Egami.Fonts.FontTag("liga"), 0, 1, 3), Egami.Fonts.Shaping.FontFeature.TabularFigures, new(new Egami.Fonts.FontTag("vert"))],
    ];
    Egami.Fonts.Unicode.TextDirection[] directions =
    [
        Egami.Fonts.Unicode.TextDirection.Auto, Egami.Fonts.Unicode.TextDirection.LeftToRight,
        Egami.Fonts.Unicode.TextDirection.RightToLeft, Egami.Fonts.Unicode.TextDirection.TopToBottom,
    ];
    var buffer = new Egami.Fonts.Shaping.GlyphBuffer();
    Egami.Fonts.Shaping.OpenTypeLayout layout = face.Layout;
    IReadOnlyList<Egami.Fonts.FontTag> allFeatures = layout.Features;
    for (int i = 0; i < ShapingSamples.Texts.Length; i++)
    {
        (string text, Egami.Fonts.Unicode.Script? script, Egami.Fonts.Unicode.TextDirection natural) = ShapingSamples.Texts[i];
        int pick = hash + i;
        Egami.Fonts.Shaping.FontFeature[] features = featureSets[pick % featureSets.Length];
        Egami.Fonts.Unicode.TextDirection direction = (pick / 4 % 3) switch
        {
            0 => natural,
            1 => directions[1 + (pick % 3)],
            _ => Egami.Fonts.Unicode.TextDirection.TopToBottom,
        };
        var properties = new Egami.Fonts.Shaping.RunProperties
        {
            Script = script,
            Direction = direction,
            Features = features,
            ClusterLevel = (Egami.Fonts.Shaping.ClusterLevel)(pick % 3),
            Language = (pick & 8) != 0 ? "ur" : null,
        };
        Egami.Fonts.Shaping.TextShaper.ShapeRun(text, 0, text.Length, font, properties, buffer);
        CheckClusters(buffer, 0, text.Length);

        // A run in the middle of the text, with the characters around it as context.
        int start = Math.Min(text.Length, 1 + (pick % 3));
        Egami.Fonts.Shaping.TextShaper.ShapeRun(text, start, text.Length - start, font, properties, buffer);
        CheckClusters(buffer, start, text.Length);

        Egami.Fonts.Shaping.ShapedText shaped = Egami.Fonts.Shaping.TextShaper.Shape(text, font, new Egami.Fonts.Shaping.ShapingOptions
        {
            Direction = directions[pick % directions.Length],
            Features = features,
            ClusterLevel = properties.ClusterLevel,
        });
        CheckShaped(shaped, text.Length);

        ushort[] glyphs = [.. text.EnumerateRunes().Select(r => face.GetGlyph(r.Value))];
        _ = layout.GetClosure(glyphs).Count;
        _ = layout.GetClosure(glyphs, allFeatures).Count;
    }
}

static void CheckClusters(Egami.Fonts.Shaping.GlyphBuffer buffer, int start, int end)
{
    foreach (int cluster in buffer.Clusters)
    {
        if (cluster < start || cluster >= end)
        {
            throw new InvalidOperationException($"Cluster {cluster} is outside the run [{start}, {end}).");
        }
    }
}

static void CheckShaped(Egami.Fonts.Shaping.ShapedText shaped, int length)
{
    foreach (Egami.Fonts.Shaping.ShapedLine line in shaped.Lines)
    {
        if (line.Start < 0 || line.Start + line.Length > length)
        {
            throw new InvalidOperationException($"Line [{line.Start}, +{line.Length}) is outside the text of {length}.");
        }

        foreach (Egami.Fonts.Shaping.ShapedRun run in line.Runs)
        {
            if (run.Start < line.Start || run.Start + run.Length > line.Start + line.Length)
            {
                throw new InvalidOperationException($"Run [{run.Start}, +{run.Length}) is outside its line.");
            }

            CheckClusters(run.Glyphs, run.Start, run.Start + run.Length);
        }
    }
}

// Bidi and segmentation (Phase 2.8.1): the bytes as UTF-16 text (any units, unpaired surrogates included), resolved in every
// direction; the runs must cover the text, and lines the bytes cut must reorder to runs covering the line. Graphemes and words
// must tile the text, and shaping it with one font must keep every cluster inside its run. Nothing may throw.
static void ResolveBidi(Stream stream)
{
    byte[] data = ReadAll(stream);
    string text = new(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(data.AsSpan(0, Math.Min(data.Length, 8192) & ~1)));
    int seed = 17;
    foreach (byte b in data)
    {
        seed = (seed * 31) + b;
    }

    var random = new Random(seed);
    Egami.Fonts.Unicode.TextDirection[] directions =
    [
        Egami.Fonts.Unicode.TextDirection.Auto, Egami.Fonts.Unicode.TextDirection.LeftToRight,
        Egami.Fonts.Unicode.TextDirection.RightToLeft, Egami.Fonts.Unicode.TextDirection.TopToBottom,
    ];
    foreach (Egami.Fonts.Unicode.TextDirection direction in directions)
    {
        using Egami.Fonts.Unicode.BidiResult result = Egami.Fonts.Unicode.Bidi.Resolve(text, direction);
        if (result.Length != text.Length || result.Levels.Length != text.Length)
        {
            throw new InvalidOperationException($"Bidi resolved {result.Length} units of {text.Length}.");
        }

        int next = 0;
        foreach (Egami.Fonts.Unicode.BidiRun run in result.Runs)
        {
            if (run.Start != next || run.Length <= 0 || run.Level > 126)
            {
                throw new InvalidOperationException($"Bidi run {run} does not follow {next}.");
            }

            next = run.Start + run.Length;
        }

        if (next != text.Length)
        {
            throw new InvalidOperationException($"Bidi runs end at {next} of {text.Length}.");
        }

        CheckReorder(result, 0, text.Length);
        for (int start = 0; start < text.Length;)
        {
            int length = 1 + random.Next(Math.Min(text.Length - start, 80));
            CheckReorder(result, start, length);
            start += length;
        }

        CheckReorder(result, random.Next(text.Length + 1), 0);
    }

    CheckTiling(text, true, Graphemes(text));
    CheckTiling(text, false, Words(text));

    using Stream fontData = typeof(RecordingSink).Assembly.GetManifestResourceStream("NotoSansArabic-subset.ttf")!;
    var font = new Egami.Fonts.Font(Egami.Fonts.Typeface.FromMemory(ReadAll(fontData)), 16);
    CheckShaped(Egami.Fonts.Shaping.TextShaper.Shape(text, font, new Egami.Fonts.Shaping.ShapingOptions { Direction = directions[seed & 3] }), text.Length);
}

// The paragraph target's fonts: the bidi target's Latin and Arabic subset, then the Chinese, Japanese and Korean layout subset.
static Egami.Fonts.Typeface[] ParagraphFaces()
{
    using Stream arabic = typeof(RecordingSink).Assembly.GetManifestResourceStream("NotoSansArabic-subset.ttf")!;
    using Stream cjk = typeof(RecordingSink).Assembly.GetManifestResourceStream("NotoSansCJK-layout-subset.otf")!;
    return [Egami.Fonts.Typeface.FromMemory(ReadAll(arabic)), Egami.Fonts.Typeface.FromMemory(ReadAll(cjk))];
}

// Draws the paragraph on a small canvas, culled to it, as Canvas.DrawTextLayout does for any backend.
static void DrawParagraph(Egami.Fonts.Layout.TextLayout layout)
{
    using Raster<Rgba8> image = Raster.Create<Rgba8>(64, 64);
    using Egami.Drawing.RasterCanvas<Rgba8> canvas = Egami.Drawing.DrawingExtensions.CreateCanvas(image);
    canvas.DrawTextLayout(layout, new System.Numerics.Vector2(-8, -8), placeholderPainter: (c, _, box) => c.FillRect(box, new Egami.Drawing.SolidPaint(Rgba8.Black)));
}

static void CheckReorder(Egami.Fonts.Unicode.BidiResult result, int start, int length)
{
    Egami.Fonts.Unicode.BidiRun[] runs = result.Reorder(start, length);
    var covered = new bool[length];
    foreach (Egami.Fonts.Unicode.BidiRun run in runs)
    {
        if (run.Length <= 0 || run.Start < start || run.Start + run.Length > start + length)
        {
            throw new InvalidOperationException($"Reordered run {run} is outside the line [{start}, +{length}).");
        }

        for (int i = run.Start; i < run.Start + run.Length; i++)
        {
            if (covered[i - start])
            {
                throw new InvalidOperationException($"Reordered runs of [{start}, +{length}) overlap at {i}.");
            }

            covered[i - start] = true;
        }
    }

    int gap = Array.IndexOf(covered, false);
    if (gap >= 0)
    {
        throw new InvalidOperationException($"Reordered runs of [{start}, +{length}) miss {start + gap}.");
    }
}

static List<Egami.Fonts.Unicode.TextSegment> Graphemes(string text)
{
    List<Egami.Fonts.Unicode.TextSegment> segments = [];
    foreach (Egami.Fonts.Unicode.TextSegment segment in Egami.Fonts.Unicode.TextSegmenter.Graphemes(text))
    {
        segments.Add(segment);
    }

    return segments;
}

static List<Egami.Fonts.Unicode.TextSegment> Words(string text)
{
    List<Egami.Fonts.Unicode.TextSegment> segments = [];
    foreach (Egami.Fonts.Unicode.TextSegment segment in Egami.Fonts.Unicode.TextSegmenter.Words(text))
    {
        segments.Add(segment);
    }

    return segments;
}

static void CheckTiling(string text, bool graphemes, List<Egami.Fonts.Unicode.TextSegment> segments)
{
    string kind = graphemes ? "Grapheme" : "Word";
    int next = 0;
    foreach (Egami.Fonts.Unicode.TextSegment segment in segments)
    {
        if (segment.Start != next || segment.Length <= 0)
        {
            throw new InvalidOperationException($"{kind} {segment} does not follow {next}.");
        }

        if (graphemes && !Egami.Fonts.Unicode.TextSegmenter.IsGraphemeBoundary(text, segment.Start))
        {
            throw new InvalidOperationException($"{kind} {segment} starts off a grapheme boundary.");
        }

        next = segment.End;
    }

    if (next != text.Length)
    {
        throw new InvalidOperationException($"{kind}s end at {next} of {text.Length}.");
    }
}

static void LoadRadiance(Stream stream)
{
    EgamiContext context = Context() with { Formats = [Egami.Formats.Radiance.RadianceFormat.Info] };
    byte[] data = ReadAll(stream);
    foreach (RecoveryMode recovery in new[] { RecoveryMode.Lenient, RecoveryMode.Strict })
    {
        ReaderOptions options = new() { Recovery = recovery };
        try
        {
            RasterInfo info = Raster.Probe(data, context, options);
            using Raster<RgbF32> raster = Raster.Load<RgbF32>(data, context, options);
            using Raster<RgbF32> forward = Raster.Load<RgbF32>(new ForwardOnlyStream(data), context, options);
            if (info.Width != raster.Width || info.Height != raster.Height)
            {
                throw new InvalidOperationException("Probe and Load disagree.");
            }

            // What the writer writes reads back to the same floats, so it writes again to the same bytes.
            using MemoryStream written = new();
            raster.Save(written, Egami.Formats.Radiance.RadianceFormat.Info);
            using Raster<RgbF32> again = Raster.Load<RgbF32>(written.ToArray(), context);
            using MemoryStream twice = new();
            again.Save(twice, Egami.Formats.Radiance.RadianceFormat.Info);
            if (!written.ToArray().AsSpan().SequenceEqual(twice.ToArray()))
            {
                throw new InvalidOperationException("A Radiance file Egami wrote does not write back to the same bytes.");
            }
        }
        catch (EgamiException)
        {
        }
    }
}

static void LoadExr(Stream stream)
{
    EgamiContext context = Context() with { Formats = [Egami.Formats.Exr.ExrFormat.Info] };
    byte[] data = ReadAll(stream);
    foreach (RecoveryMode recovery in new[] { RecoveryMode.Lenient, RecoveryMode.Strict })
    {
        try
        {
            Egami.Formats.Exr.ExrReaderOptions options = new() { Recovery = recovery };
            Raster.Probe(data, context, options);
            using Raster raster = Raster.Load(data, context, options);
            using Raster<RgbaF32> forward = Raster.Load<RgbaF32>(new ForwardOnlyStream(data), context, options);
            using RasterList parts = Raster.LoadAll(data, context, options);
            var details = (Egami.Formats.Exr.ExrDetails)raster.Metadata.FormatDetails!;
            for (int part = 0; part < Math.Min(details.Parts.Count, 4); part++)
            {
                Egami.Formats.Exr.ExrPart header = details.Parts[part];
                foreach (string? layer in header.Layers.Take(3).Prepend(null))
                {
                    try
                    {
                        using Raster display = Raster.Load(data, context, options with { Part = part, Layer = layer, Window = Egami.Formats.Exr.ExrWindow.Display });
                    }
                    catch (EgamiException)
                    {
                    }
                }

                foreach (Egami.Formats.Exr.ExrChannel channel in header.Channels.Take(3))
                {
                    try
                    {
                        using Raster<GrayF32> one = Egami.Formats.Exr.ExrImage.ReadChannel(new MemoryStream(data), channel.Name, options with { Part = part }, context);
                    }
                    catch (EgamiException)
                    {
                    }
                }
            }

            // What loads writes back, and reads again at the same size.
            foreach (Egami.Formats.Exr.ExrCompression compression in new[] { Egami.Formats.Exr.ExrCompression.Zip, Egami.Formats.Exr.ExrCompression.Piz })
            {
                using MemoryStream written = new();
                forward.Save(written, Egami.Formats.Exr.ExrFormat.Info, new Egami.Formats.Exr.ExrWriterOptions { Compression = compression });
                using Raster<RgbaF32> again = Raster.Load<RgbaF32>(written.ToArray(), context);
                if (again.Width != forward.Width || again.Height != forward.Height)
                {
                    throw new InvalidOperationException("An OpenEXR file Egami wrote reads back at another size.");
                }
            }
        }
        catch (EgamiException)
        {
        }
    }
}

static void LoadGainMap(Stream stream)
{
    EgamiContext context = Context();
    byte[] data = ReadAll(stream);
    foreach (RecoveryMode recovery in new[] { RecoveryMode.Lenient, RecoveryMode.Strict })
    {
        ReaderOptions options = new() { Recovery = recovery };
        try
        {
            RasterInfo info = Raster.Probe(data, context, options);
            using Raster raster = Raster.Load(data, context, options);
            using RasterList images = Raster.LoadAll(data, context, options);
            if (info.ImageCount != images.Count)
            {
                throw new InvalidOperationException("Probe and LoadAll count the MP images differently.");
            }

            using Raster<RgbaF16> full = Raster.Load<RgbaF16>(data, context, options with { Hdr = new HdrDecode() });
            using Raster<RgbaF16> some = Raster.Load<RgbaF16>(new ForwardOnlyStream(data), context, options with { Hdr = new HdrDecode { Headroom = 2 } });
            if (raster.Metadata.GainMap is not GainMap map)
            {
                continue;
            }

            using Raster<RgbaF16> applied = map.Apply(raster, 3);
            using Raster moved = raster.Derive(ops => ops.Crop(raster.Width / 5, raster.Height / 7, Math.Max(1, raster.Width / 2), Math.Max(1, raster.Height / 2)).Rotate(90));
            using MemoryStream written = new();
            moved.Save(written, JpegFormat.Info);
            using Raster again = Raster.Load(written.ToArray(), context);
            // A map that no longer has the image's shape (the file's sizes disagreed) is dropped with Save.GainMapDropped.
            if (moved.Metadata.GainMap is GainMap kept && JpegGainMapWriter.Fits(kept, moved.Width, moved.Height) && again.Metadata.GainMap is null)
            {
                throw new InvalidOperationException("A JPEG save lost the gain map.");
            }
        }
        catch (EgamiException)
        {
        }
    }
}

/// <summary>A simple mutation fuzzer: random edits of seed files, run in process, with a watchdog for hangs.</summary>
internal static class Mutator
{
    internal static void Run(Action<Stream> target, TimeSpan duration, string seeds, string findings)
    {
        byte[][] corpus = [.. Directory.GetFiles(seeds).Select(File.ReadAllBytes).Where(b => b.Length > 0)];
        if (corpus.Length == 0)
        {
            throw new ArgumentException($"No seed files in {seeds}.");
        }

        Directory.CreateDirectory(findings);
        Random random = new();
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        byte[] current = [];
        long started = 0;
        long cases = 0;
        int crashes = 0;

        // The watchdog saves an input that runs too long and stops the process, as AFL++ would report a hang.
        using Timer watchdog = new(_ =>
        {
            long at = Interlocked.Read(ref started);
            if (at != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(at) > TimeSpan.FromSeconds(10))
            {
                Save(findings, "hang", Volatile.Read(ref current));
                Console.WriteLine("Hang found; stopping.");
                Environment.Exit(2);
            }
        }, null, 1000, 1000);

        while (clock.Elapsed < duration)
        {
            byte[] input = Mutate(corpus[random.Next(corpus.Length)], corpus, random);
            Volatile.Write(ref current, input);
            Interlocked.Exchange(ref started, System.Diagnostics.Stopwatch.GetTimestamp());
            try
            {
                using MemoryStream stream = new(input, writable: false);
                target(stream);
            }
            catch (Exception e)
            {
                crashes++;
                string name = Save(findings, "crash", input);
                Console.WriteLine($"Crash {crashes}: {e.GetType().Name}: {e.Message} ({name})");
            }

            Interlocked.Exchange(ref started, 0);
            if (++cases % 10_000 == 0)
            {
                Console.WriteLine($"{cases:N0} inputs, {crashes} crashes, {(int)clock.Elapsed.TotalMinutes} min");
            }
        }

        Console.WriteLine($"Done: {cases:N0} inputs, {crashes} crashes.");
    }

    private static string Save(string findings, string kind, byte[] input)
    {
        string name = Path.Combine(findings, $"{kind}-{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input))[..16]}");
        File.WriteAllBytes(name, input);
        return name;
    }

    /// <summary>One to eight edits: bit flips, interesting bytes, insertions, deletions, truncation, duplicated or spliced ranges.</summary>
    private static byte[] Mutate(byte[] seed, byte[][] corpus, Random random)
    {
        List<byte> data = [.. seed];
        byte[] interesting = [0x00, 0x01, 0x7F, 0x80, 0xFF, 0xFE, 0xD8, 0xD9, 0xDA, 0xC0, 0xC2, 0xC4, 0xDB, 0xDD, 0xE1];
        for (int n = random.Next(1, 9); n > 0 && data.Count > 0; n--)
        {
            int at = random.Next(data.Count);
            switch (random.Next(8))
            {
                case 0:
                    data[at] ^= (byte)(1 << random.Next(8));
                    break;
                case 1:
                    data[at] = interesting[random.Next(interesting.Length)];
                    break;
                case 2:
                    data.Insert(at, (byte)random.Next(256));
                    break;
                case 3:
                    data.RemoveRange(at, Math.Min(random.Next(1, 64), data.Count - at));
                    break;
                case 4:
                    data.RemoveRange(at, data.Count - at);
                    break;
                case 5:
                    int length = Math.Min(random.Next(1, 256), data.Count - at);
                    data.InsertRange(random.Next(data.Count), data.GetRange(at, length));
                    break;
                case 6:
                    byte[] other = corpus[random.Next(corpus.Length)];
                    int from = random.Next(other.Length);
                    data.InsertRange(at, other.AsSpan(from, Math.Min(random.Next(1, 512), other.Length - from)).ToArray());
                    break;
                default:
                    // A 16-bit length or count, big- or little-endian, set to an edge value.
                    ushort value = (ushort)(random.Next(4) switch { 0 => 0, 1 => 0xFFFF, 2 => random.Next(16), _ => random.Next(65536) });
                    if (at + 1 < data.Count)
                    {
                        (data[at], data[at + 1]) = random.Next(2) == 0 ? ((byte)(value >> 8), (byte)value) : ((byte)value, (byte)(value >> 8));
                    }

                    break;
            }
        }

        return [.. data];
    }
}

/// <summary>A stream over bytes that says it cannot seek.</summary>
internal sealed class ForwardOnlyStream(byte[] data) : MemoryStream(data, writable: false)
{
    public override bool CanSeek => false;

    public override long Position { get => base.Position; set => throw new NotSupportedException(); }

    public override long Length => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
}

/// <summary>Records an outline to compare two of them.</summary>
internal sealed class RecordingSink : Egami.Fonts.IGlyphOutlineSink
{
    private readonly List<float> _values = [];

    public void MoveTo(float x, float y) => _values.AddRange([1, x, y]);

    public void LineTo(float x, float y) => _values.AddRange([2, x, y]);

    public void QuadTo(float cx, float cy, float x, float y) => _values.AddRange([3, cx, cy, x, y]);

    public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y) => _values.AddRange([4, c1x, c1y, c2x, c2y, x, y]);

    public void Close() => _values.Add(5);

    public bool SameAs(RecordingSink other)
    {
        if (_values.Count != other._values.Count)
        {
            return false;
        }

        for (int i = 0; i < _values.Count; i++)
        {
            if (MathF.Abs(_values[i] - other._values[i]) > 1e-3f && !(float.IsNaN(_values[i]) && float.IsNaN(other._values[i])))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>The texts the shaping fuzz target shapes: one per script, with ligatures, marks, joiners and clusters to exercise.</summary>
internal static class ShapingSamples
{
    public static readonly (string Text, Egami.Fonts.Unicode.Script? Script, Egami.Fonts.Unicode.TextDirection Direction)[] Texts =
    [
        ("office affluent fjord Ta AV x\u0301 e\u0308\u0323 \uFB01 1/2 \u00C5", Egami.Fonts.Unicode.Script.Latin, Egami.Fonts.Unicode.TextDirection.LeftToRight),
        ("\u0628\u0650\u0633\u0652\u0645\u0650 \u0627\u0644\u0644\u0651\u0647\u0650 \u0644\u0627 \u0640\u0643\u062A\u0627\u0628\u200D \u0661\u0662 \u06CC\u0670", Egami.Fonts.Unicode.Script.Arabic, Egami.Fonts.Unicode.TextDirection.RightToLeft),
        ("\u0915\u094D\u0937\u093F \u0930\u094D\u0915\u0947 \u0939\u093F\u0928\u094D\u0926\u0940 \u0926\u094D\u0930\u200D\u094D \u0915\u093C\u0902", Egami.Fonts.Unicode.Script.Devanagari, Egami.Fonts.Unicode.TextDirection.LeftToRight),
        ("\u1780\u1793\u17D2\u178F\u17D2\u179A\u17B6 \u1781\u17D2\u1798\u17C2\u179A \u17C9\u17D2 \u1784\u17D2\u1782\u17C4", Egami.Fonts.Unicode.Script.Khmer, Egami.Fonts.Unicode.TextDirection.LeftToRight),
        ("\u1019\u103C\u1014\u103A\u1019\u102C \u1000\u103B\u1031\u102C\u103A \u1004\u103A\u1039\u1000\u102D\u102F", Egami.Fonts.Unicode.Script.Myanmar, Egami.Fonts.Unicode.TextDirection.LeftToRight),
        ("\u0E20\u0E32\u0E29\u0E32\u0E44\u0E17\u0E22 \u0E19\u0E49\u0E33 \u0E01\u0E33 \u0E1B\u0E39\u0E48", Egami.Fonts.Unicode.Script.Thai, Egami.Fonts.Unicode.TextDirection.LeftToRight),
        ("\u0F56\u0F7C\u0F51\u0F0B\u0F66\u0F90\u0F51\u0F0B \u0F40\u0FB2\u0F74 \u0F00 \u0F66\u0F92\u0FB2\u0F7C", Egami.Fonts.Unicode.Script.Tibetan, Egami.Fonts.Unicode.TextDirection.LeftToRight),
        ("\uD55C\uAD6D\uC5B4 \u1100\u1161\u11A8 \u1112\u1173 \uAC00\u302E", Egami.Fonts.Unicode.Script.Hangul, Egami.Fonts.Unicode.TextDirection.LeftToRight),
        ("\uD83D\uDC69\u200D\uD83D\uDCBB \uD83C\uDDF3\uD83C\uDDF4 \u263A\uFE0F \uD83D\uDC4D\uD83C\uDFFD 1\uFE0F\u20E3", Egami.Fonts.Unicode.Script.Common, Egami.Fonts.Unicode.TextDirection.LeftToRight),
        ("abc \u05E9\u05DC\u05D5\u05DD 123 \u0627\u0644\u0639\u0631\u0628\u064A\u0629! (\u202B\u05D0 b\u202C)", null, Egami.Fonts.Unicode.TextDirection.Auto),
    ];
}
