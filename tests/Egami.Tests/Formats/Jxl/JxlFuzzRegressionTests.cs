using Egami.Formats;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jxl;

/// <summary>
/// Inputs the png and stream fuzz targets found in JPEG XL codestreams (tests/images/fuzz/jxl): each must load every way or
/// throw an <see cref="EgamiException"/>. A lossless frame with a channel smaller than the frame, adaptive LF smoothing on
/// subsampled chroma and a varblock that leaves the subsampled chroma plane indexed past their arrays; a cropped frame far larger
/// than the image took seconds to upsample.
/// </summary>
public class JxlFuzzRegressionTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "images", "fuzz", "jxl");

    public static TheoryData<string> Files() => [.. Directory.GetFiles(Folder).Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Files))]
    public void LoadsEveryWayOrFailsCleanly(string file)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Folder, file));
        foreach (RecoveryMode recovery in (RecoveryMode[])[RecoveryMode.Lenient, RecoveryMode.Strict])
        {
            ReaderOptions options = new() { Recovery = recovery };
            try
            {
                RasterInfo info = Raster.Probe(data, options: options);
                using Raster raster = Raster.Load(data, options: options);
                using Raster<Rgba8> rgba = Raster.Load<Rgba8>(data, options: options);
                using Raster<Rgba16> wide = Raster.Load<Rgba16>(data, options: options);
                using RasterDecoder decoder = RasterDecoder.Open(data, options: options);
                using Raster rows = decoder.ReadAll();
            }
            catch (EgamiException)
            {
            }
        }
    }

    [Fact]
    public void CroppedFrameIsHeldToMaxPixels()
    {
        // The image is 71 × 40; its one frame is cropped to 2797 × 3156, more than the limit allows.
        byte[] data = File.ReadAllBytes(Path.Combine(Folder, "large-cropped-frame.jxl"));
        EgamiContext context = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxPixels = 4_000_000 } };
        LimitExceededException e = Assert.Throws<LimitExceededException>(() => Raster.Load(data, context));
        Assert.Equal(nameof(SafetyLimits.MaxPixels), e.LimitName);
    }
}
