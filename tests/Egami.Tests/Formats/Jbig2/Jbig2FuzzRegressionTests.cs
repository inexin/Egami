using Egami.Formats;
using Egami.Formats.Jbig2;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jbig2;

/// <summary>Inputs the JBIG2 fuzz targets found (tests/images/fuzz/jbig2): each must load every way or throw an <see cref="EgamiException"/>.</summary>
public class Jbig2FuzzRegressionTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "images", "fuzz", "jbig2");

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
                using Raster raster = Raster.Load(data, Jbig2Format.Info, options: options);
                using RasterList pages = Raster.LoadAll(data, Jbig2Format.Info, options: options);
                using RasterDecoder decoder = RasterDecoder.Open(data, Jbig2Format.Info, options: options);
                using Raster rows = decoder.ReadAll();
                using Raster<Gray8> cropped = Raster.Load<Gray8>(data, Jbig2Format.Info, options: options with { Region = new Rect(0, 0, 1, 1) });
            }
            catch (EgamiException)
            {
            }
        }
    }

    /// <summary>A height class of width 0 (found by the jbig2codec target) has a collective bitmap with no words to fill.</summary>
    [Fact]
    public void AnEmptyCollectiveBitmapIsEmpty()
    {
        Jbig2Bitmap empty = new(0, 3);
        Jbig2Generic.FromBytes(empty, [], 0);
        Assert.Equal(0, empty.Width);
    }
}
