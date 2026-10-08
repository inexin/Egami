using Egami;
using Egami.Formats;
using Egami.Pixels;

using SharpFuzz;

// Instrumented Egami code must not run before Fuzzer.OutOfProcess.Run sets up AFL++'s shared coverage map, so everything
// that touches Egami, including EgamiContext, is created inside the fuzzed delegate.
//
// Fuzz targets, one per reader. Each must load any input or fail with an EgamiException: any other exception is a crash,
// and AFL++ reports hangs. Limits are lowered so a valid but huge image cannot exhaust the fuzzer's memory.
//
//   dotnet run --project tests/Egami.Fuzz -- png            run one input from stdin (used by AFL++ through SharpFuzz)
//   dotnet run --project tests/Egami.Fuzz -- png file.png   run one file, to reproduce a crash
string target = args.Length > 0 ? args[0] : "png";
Action<Stream> run = target switch
{
    "png" => LoadPng,
    _ => throw new ArgumentException($"Unknown fuzz target '{target}'. Targets: png."),
};

if (args.Length > 1)
{
    using FileStream file = File.OpenRead(args[1]);
    run(file);
    Console.WriteLine("No crash.");
    return;
}

Fuzzer.OutOfProcess.Run(run);

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
