using Egami.Formats;
using Egami.Formats.Jpeg2000;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jpeg2000;

/// <summary>Inputs the JPEG 2000 fuzz targets found (tests/images/fuzz/jpeg2000): each must load every way or throw an <see cref="EgamiException"/>.</summary>
public class Jpeg2000FuzzRegressionTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "images", "fuzz", "jpeg2000");

    public static TheoryData<string> Files() => [.. Directory.GetFiles(Folder).Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Files))]
    public void LoadsEveryWayOrFailsCleanly(string file)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Folder, file));
        foreach (RecoveryMode recovery in (RecoveryMode[])[RecoveryMode.Lenient, RecoveryMode.Strict])
        {
            Jpeg2000ReaderOptions options = new() { Recovery = recovery };
            try
            {
                RasterInfo info = Raster.Probe(data, options: options);
                using Raster raster = Raster.Load(data, options: options);
                using RasterDecoder decoder = RasterDecoder.Open(data, options: options);
                using Raster rows = decoder.ReadAll();
                using Raster<Rgba8> small = Raster.Load<Rgba8>(data, options: options with { Region = new Rect(0, 0, info.Width, info.Height), MinimumSize = new Size(1, 1) });
            }
            catch (EgamiException)
            {
            }
        }
    }
}
