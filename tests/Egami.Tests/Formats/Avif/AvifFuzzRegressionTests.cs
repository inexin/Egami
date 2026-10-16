using Egami.Formats;
using Egami.Formats.Av1;
using Egami.Pixels;
using Egami.Tests.Streaming;

namespace Egami.Tests.Formats.Avif;

/// <summary>
/// Inputs the AVIF fuzz targets found (tests/images/fuzz/avif and av1): each must load every way or throw an
/// <see cref="EgamiException"/>.
/// </summary>
public class AvifFuzzRegressionTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "images", "fuzz");

    public static TheoryData<string> AvifFiles() => [.. Directory.GetFiles(Path.Combine(Folder, "avif")).Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal)];

    public static TheoryData<string> Av1Files() => [.. Directory.GetFiles(Path.Combine(Folder, "av1")).Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(AvifFiles))]
    public void AvifInputsLoadOrFailCleanly(string file)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Folder, "avif", file));
        foreach (RecoveryMode recovery in (RecoveryMode[])[RecoveryMode.Lenient, RecoveryMode.Strict])
        {
            ReaderOptions options = new() { Recovery = recovery };
            try
            {
                Raster.Probe(data, options: options);
                using Raster raster = Raster.Load(data, options: options);
                using Raster<Rgba8> converted = Raster.Load<Rgba8>(new SimpleStreamingTests.NonSeekableStream(data), options: options);
            }
            catch (EgamiException)
            {
            }
        }
    }

    /// <summary>The raw AV1 inputs decode, on one thread and on four, or throw a <see cref="CorruptDataException"/> either way.</summary>
    [Theory]
    [MemberData(nameof(Av1Files))]
    public void Av1InputsDecodeOrFailCleanly(string file)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Folder, "av1", file));
        foreach (int parallelism in (int[])[1, 4])
        {
            try
            {
                Av1Frame frame = Av1Decoder.DecodeFirstFrame(data, "AV1", parallelism: parallelism, cancellationToken: TestContext.Current.CancellationToken);
                Av1FilmGrainSynthesis.Apply(frame);
            }
            catch (EgamiException)
            {
            }
        }
    }
}
