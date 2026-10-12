using Egami;
using Egami.Colors;
using Egami.Formats;
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
// encode must write what Save writes. The jbig2 and jbig2codec targets (Phase 2.3.4) load JBIG2 files every way, standalone and
// embedded with globals, and feed each of JBIG2's decoding procedures (regions, dictionaries, Huffman tables) raw bytes. The cube
// target (Phase 2.5.3) reads .cube LUT files, which must load or fail with an EgamiException; a table that loads must write back
// and read again to the same values and run as a filter. The jpeg2000 and jpeg2000codec targets (Phase 2.3.4) load JP2, JPX and
// codestreams every way (color and raw components, quality layers, rows, regions, reductions, YUV planes) and feed tier 1 raw
// code-block bytes in every code-block style. The svgpath target (Phase 2.6.2) reads SVG path data strictly, with TryParseSvg and
// leniently, which must agree; writes it back every way, which must read again to the same path; and runs small paths through
// measuring, dashing, stroke outlines and the boolean engine.
// The ico target (Phase 2.3.3) loads ICO and CUR files every way (each entry, the
// best one, MinimumSize, rows) and writes the entries back, which must read again to the same pixels; icoentry wraps its input
// as the one entry of an icon, so mutations reach the BMP and PNG entry decoders and the AND mask directly.
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
    "webp" => LoadWebp,
    "webpcodec" => DecodeWebpCodec,
    "jbig2" => LoadJbig2,
    "jbig2codec" => DecodeJbig2Codec,
    "cube" => LoadCube,
    "jpeg2000" => LoadJpeg2000,
    "jpeg2000codec" => DecodeJpeg2000Codec,
    "font" => LoadFont,
    "cff" => LoadCff,
    "subset" => SubsetFont,
    "svgpath" => ParseSvgPath,
    "ico" => LoadIco,
    "icoentry" => LoadIcoEntry,
    _ => throw new ArgumentException($"Unknown fuzz target '{name}'. Targets: png, jpeg, jpeg-depth, jpeg-write, exif, icc, iptc, psd, xmp, rawprofile, gif, apng, lzw, bmp, tga, qoi, pnm, pfm, detect, stream, tiff, tiffcodec, webp, webpcodec, jbig2, jbig2codec, cube, jpeg2000, jpeg2000codec, font, cff, subset, svgpath, ico, icoentry."),
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
        2 => Egami.Formats.Tiff.CcittFax.Decode(input, output, width, rows, 2, 0),
        3 => Egami.Formats.Tiff.CcittFax.Decode(input, output, width, rows, 3, 0),
        4 => Egami.Formats.Tiff.CcittFax.Decode(input, output, width, rows, 3, 1),
        5 => Egami.Formats.Tiff.CcittFax.Decode(input, output, width, rows, 4, 0),
        6 => Egami.Formats.Tiff.SgiLog.DecodeStrip(input, output, Math.Min(width, 256), 8, data[1] % 2 == 0, data[2] % 2 == 0),
        7 => Predict(input, output, width),
        _ => Egami.Formats.Tiff.CcittFax.Decode(input, output, width, rows, 32771, 0),
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
    }
    catch (EgamiException)
    {
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
                raster.Save(Stream.Null, Egami.Formats.Webp.WebpFormat.Info, new Egami.Formats.Webp.WebpWriterOptions { Lossless = false, Effort = (data.Length / 7) % 7, Quality = data.Length % 101 });
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
    JpegWriterOptions Options(bool progressive) => new()
    {
        Quality = 1 + (data[7] % 100),
        Subsampling = (ChromaSubsampling)(flags % 3),
        OptimizeHuffman = (flags & 4) != 0,
        Progressive = progressive,
        Precision = (flags & 16) != 0 ? 12 : 8,
        ColorType = (JpegColorType)((flags >> 5) % 4),
        RestartInterval = (more & 8) != 0 ? more >> 4 : 0,
        Background = (more & 4) != 0 ? new Rgba8(data[0], data[1], data[2], 255) : null,
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

    bool progressive = (flags & 8) != 0;
    byte[] file = Save(raster, Options(progressive));
    using Raster back = Raster.Load(file, context);
    if (back.Width != width || back.Height != height || back.Metadata.Warnings.Count > 0)
    {
        throw new InvalidOperationException($"The JPEG read back as {back.Width} × {back.Height} with {back.Metadata.Warnings.Count} warnings.");
    }

    if (progressive)
    {
        using Raster baseline = Raster.Load(Save(raster, Options(false)), context);
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
        using (RasterEncoder<Rgb16> encoder = RasterEncoder.Create<Rgb16>(streamed, JpegFormat.Info, width, height, Options(progressive), context: context))
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
