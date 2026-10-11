using Egami;
using Egami.Colors;
using Egami.Formats;
using Egami.Formats.Jpeg;
using Egami.Metadata;
using Egami.Pixels;

using SharpFuzz;

// Instrumented Egami code must not run before Fuzzer.OutOfProcess.Run sets up AFL++'s shared coverage map, so everything
// that touches Egami, including EgamiContext, is created inside the fuzzed delegate.
//
// Fuzz targets, one per reader. Each must load any input or fail with an EgamiException: any other exception is a crash,
// and AFL++ reports hangs. Limits are lowered so a valid but huge image cannot exhaust the fuzzer's memory. The exif target
// parses raw TIFF blocks, which must never throw at all; the icc target parses ICC profiles the same way, and building a color
// profile from one may fail only with an EgamiException. The iptc, psd, xmp and rawprofile targets (Phase 2.1.3) parse IPTC
// datasets, Photoshop resource blocks, XMP packets and PNG "Raw profile type" text, which must never throw, and check that
// what Egami writes after an edit reads back undamaged.
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
    _ => throw new ArgumentException($"Unknown fuzz target '{name}'. Targets: png, jpeg, exif, icc, iptc, psd, xmp, rawprofile, gif, apng, lzw, bmp, tga, qoi, pnm, pfm, detect, stream."),
};

static EgamiContext Context()
{
    return EgamiContext.Default with
    {
        MaxParallelism = 1,
        Limits = SafetyLimits.Default with { MaxPixels = 4_000_000, MaxTotalBytes = 256L * 1024 * 1024, MaxMetadataBytes = 1024 * 1024 },
    };
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

static byte[] ReadAll(Stream stream)
{
    using MemoryStream copy = new();
    stream.CopyTo(copy);
    return copy.ToArray();
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
