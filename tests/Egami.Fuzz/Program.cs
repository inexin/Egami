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
// profile from one may fail only with an EgamiException.
//
//   dotnet run --project tests/Egami.Fuzz -- png            run one input from stdin (used by AFL++ through SharpFuzz)
//   dotnet run --project tests/Egami.Fuzz -- png file.png   run one file, to reproduce a crash
//   dotnet run --project tests/Egami.Fuzz -c Release -- mutate jpeg 3600 seeds-dir findings-dir
//       mutation fuzzing without AFL++ (any OS): random edits of the seeds for the given seconds; crashes and inputs that take
//       over 10 seconds are saved to the findings directory. Not coverage-guided, so a complement to AFL++, not a replacement.
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
    "detect" => Detect,
    _ => throw new ArgumentException($"Unknown fuzz target '{name}'. Targets: png, jpeg, exif, icc, gif, apng, lzw, bmp, tga, qoi, pnm, pfm, detect."),
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
