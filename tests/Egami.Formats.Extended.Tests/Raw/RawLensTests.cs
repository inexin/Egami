using Egami.Formats.Extended.Raw;
using Egami.Pixels;

namespace Egami.Formats.Extended.Tests.Raw;

/// <summary>RAW Build Plan task 10: lens profiles read at run time from hand-made Lensfun and Adobe LCP files.</summary>
public sealed class RawLensTests : IDisposable
{
    private const string Lensfun = """
        <lensdatabase version="1">
          <lens>
            <maker>Egami</maker>
            <model>Egami Test 24-70mm f/2.8</model>
            <mount>Test</mount>
            <cropfactor>1</cropfactor>
            <calibration>
              <distortion model="poly3" focal="24" k1="0.04" />
              <distortion model="poly3" focal="70" k1="0" />
              <tca model="linear" focal="24" kr="1.002" kb="0.998" />
              <vignetting model="pa" focal="24" aperture="2.8" distance="1000" k1="-0.5" k2="0" k3="0" />
              <vignetting model="pa" focal="24" aperture="8" distance="1000" k1="-0.1" k2="0" k3="0" />
            </calibration>
          </lens>
        </lensdatabase>
        """;

    private const string Lcp = """
        <x:xmpmeta xmlns:x="adobe:ns:meta/">
         <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
          <rdf:Description xmlns:photoshop="http://ns.adobe.com/photoshop/1.0/" xmlns:stCamera="http://ns.adobe.com/photoshop/1.0/camera-profile">
           <photoshop:CameraProfiles>
            <rdf:Seq>
             <rdf:li stCamera:Make="Egami" stCamera:Model="Test Camera" stCamera:LensPrettyName="Egami Prime 35mm" stCamera:FocalLength="35"
                     stCamera:FocusDistance="10" stCamera:ApertureValue="4" stCamera:SensorFormatFactor="1">
              <stCamera:PerspectiveModel stCamera:FocalLengthX="0.95" stCamera:FocalLengthY="0.95" stCamera:ImageXCenter="0.5" stCamera:ImageYCenter="0.375"
                                         stCamera:RadialDistortParam1="0" stCamera:RadialDistortParam2="0">
               <stCamera:VignetteModel stCamera:VignetteModelParam1="-0.6" />
              </stCamera:PerspectiveModel>
             </rdf:li>
            </rdf:Seq>
           </photoshop:CameraProfiles>
          </rdf:Description>
         </rdf:RDF>
        </x:xmpmeta>
        """;

    /// <summary>The same lens in the layout Adobe's own files use: each entry's values in an rdf:Description under rdf:li.</summary>
    private const string AdobeLayoutLcp = """
        <x:xmpmeta xmlns:x="adobe:ns:meta/">
         <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
          <rdf:Description xmlns:photoshop="http://ns.adobe.com/photoshop/1.0/" xmlns:stCamera="http://ns.adobe.com/photoshop/1.0/camera-profile">
           <photoshop:CameraProfiles>
            <rdf:Seq>
             <rdf:li>
              <rdf:Description stCamera:Make="Egami" stCamera:Model="Test Camera" stCamera:LensPrettyName="Egami Prime 35mm" stCamera:FocalLength="35"
                     stCamera:FocusDistance="10" stCamera:ApertureValue="4" stCamera:SensorFormatFactor="1">
               <stCamera:PerspectiveModel stCamera:FocalLengthX="0.95" stCamera:FocalLengthY="0.95" stCamera:ImageXCenter="0.5" stCamera:ImageYCenter="0.375"
                                          stCamera:RadialDistortParam1="0" stCamera:RadialDistortParam2="0">
                <stCamera:VignetteModel stCamera:VignetteModelParam1="-0.6" />
               </stCamera:PerspectiveModel>
              </rdf:Description>
             </rdf:li>
            </rdf:Seq>
           </photoshop:CameraProfiles>
          </rdf:Description>
         </rdf:RDF>
        </x:xmpmeta>
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "egami-lens-" + Guid.NewGuid().ToString("N"));

    public RawLensTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void ParsesLensfunAndInterpolates()
    {
        RawLensProfile profile = Assert.Single(RawLensProfiles.Lensfun(System.Xml.Linq.XDocument.Parse(Lensfun)));
        Assert.Equal("Egami Test 24-70mm f/2.8", profile.Lens);
        Assert.Equal(2, profile.Distortion.Count);
        Assert.Equal(0.02, RawLens.Interpolate(profile.Distortion, 47, 0, 0)!.Parameters[0], 6);
        Assert.Equal(-0.3, RawLens.Interpolate(profile.Vignetting, 24, 5.4, 1000)!.Parameters[0], 6);
        Assert.Equal(0.04, RawLens.Interpolate(profile.Distortion, 10, 0, 0)!.Parameters[0], 6);
    }

    [Fact]
    public void LensfunVignettingBrightensTheCorners()
    {
        File.WriteAllText(Path.Combine(_folder, "lenses.xml"), Lensfun);
        byte[] file = Shot("Egami Test 24-70mm f/2.8", 24, 2.8);
        RawReaderOptions options = new() { LensProfiles = [_folder], ColorNoiseReduction = 0 };
        using Raster<Rgb16> corrected = Raster.Load<Rgb16>(file, TestImages.Context, options);
        using Raster<Rgb16> plain = Raster.Load<Rgb16>(file, TestImages.Context, options with { LensCorrections = false });
        Assert.True(corrected.Row(1)[1].G > plain.Row(1)[1].G + 500, "The corner is brightened.");
        Assert.InRange(corrected.Row(16)[24].G - plain.Row(16)[24].G, -60, 300);
        RawDetails details = Assert.IsType<RawDetails>(corrected.Metadata.FormatDetails);
        Assert.Contains("Lensfun: Egami Egami Test 24-70mm f/2.8", details.LensProfile);
        Assert.Contains("vignetting", details.LensProfile);
        Assert.Null(Assert.IsType<RawDetails>(plain.Metadata.FormatDetails).LensProfile);
    }

    [Fact]
    public void LensfunDistortionMovesPixels()
    {
        File.WriteAllText(Path.Combine(_folder, "lenses.xml"), Lensfun);

        // A horizontal ramp: the correction at 24 mm samples the source elsewhere, so a pixel off centre changes.
        byte[] file = Shot("Egami Test 24-70mm f/2.8", 24, 8, (x, _) => 4000 + (x * 800));
        RawReaderOptions options = new() { LensProfiles = [_folder], ColorNoiseReduction = 0 };
        using Raster<Rgb16> corrected = Raster.Load<Rgb16>(file, TestImages.Context, options);
        using Raster<Rgb16> plain = Raster.Load<Rgb16>(file, TestImages.Context, options with { LensCorrections = false });
        Assert.NotEqual(plain.Row(16)[40].G, corrected.Row(16)[40].G);
        Assert.Contains("distortion", Assert.IsType<RawDetails>(corrected.Metadata.FormatDetails).LensProfile);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdobeLcpVignetting(bool adobeLayout)
    {
        // Review finding: in Adobe's own layout the models sat one rdf:Description deeper and were skipped.
        File.WriteAllText(Path.Combine(_folder, "prime.lcp"), adobeLayout ? AdobeLayoutLcp : Lcp);
        byte[] file = Shot("Egami Prime 35mm", 35, 4);
        RawReaderOptions options = new() { LensProfiles = [_folder], ColorNoiseReduction = 0 };
        using Raster<Rgb16> corrected = Raster.Load<Rgb16>(file, TestImages.Context, options);
        using Raster<Rgb16> plain = Raster.Load<Rgb16>(file, TestImages.Context, options with { LensCorrections = false });
        Assert.True(corrected.Row(1)[1].G > plain.Row(1)[1].G + 500);
        Assert.StartsWith("Adobe: Egami Prime 35mm", Assert.IsType<RawDetails>(corrected.Metadata.FormatDetails).LensProfile);
    }

    [Fact]
    public void UnknownLensesAndFilesWithTheirOwnCorrectionsAreLeftAlone()
    {
        File.WriteAllText(Path.Combine(_folder, "lenses.xml"), Lensfun);
        RawReaderOptions options = new() { LensProfiles = [_folder], ColorNoiseReduction = 0 };
        using Raster unknown = Raster.Load(Shot("Some Other Lens 50mm", 50, 2), TestImages.Context, options);
        Assert.Null(Assert.IsType<RawDetails>(unknown.Metadata.FormatDetails).LensProfile);

        // A DNG whose opcode list 3 corrects the lens keeps its own correction.
        byte[] own = new OpcodeList().Add(3, p => p.F64(0, 0, 0, 0, 0, 0.5, 0.5)).Build();
        using Raster dng = Raster.Load(Shot("Egami Test 24-70mm f/2.8", 24, 2.8, opcodes3: own), TestImages.Context, options);
        Assert.Null(Assert.IsType<RawDetails>(dng.Metadata.FormatDetails).LensProfile);

        // A folder that is not there is a warning, not an error.
        using Raster missing = Raster.Load(Shot("Egami Test 24-70mm f/2.8", 24, 2.8), TestImages.Context, new RawReaderOptions { LensProfiles = [Path.Combine(_folder, "nope")] });
        Assert.Contains(missing.Metadata.Warnings, w => w.Id == "Raw.LensProfilesMissing");
    }

    private static byte[] Shot(string lens, double focal, double fnumber, Func<int, int, int>? value = null, byte[]? opcodes3 = null)
    {
        DngFile dng = new DngFile { Width = 48, Height = 32, Opcodes3 = opcodes3 }.Fill((x, y, _) => value?.Invoke(x, y) ?? 20000);
        dng.Exif.Add(TiffTag.Rational(37386, focal));
        dng.Exif.Add(TiffTag.Rational(33437, fnumber));
        dng.Exif.Add(TiffTag.Ascii(42036, lens));
        return dng.Build();
    }
}

/// <summary>RAW Build Plan task 10: automatic lateral chromatic aberration correction for files without lens data.</summary>
public class RawAutoCaTests
{
    [Fact]
    public void FindsAndUndoesARadialScaleOfRedAndBlue()
    {
        // Fine rings around the centre; red is drawn 0.2 % larger and blue 0.15 % smaller than green.
        const int W = 400, H = 300;
        float[] rgb = new float[W * H * 3];
        static float Scene(double x, double y)
        {
            double r = Math.Sqrt((x * x) + (y * y));
            return (float)(0.45 + (0.35 * Math.Sin(r / 2.5)));
        }

        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                double dx = x + 0.5 - (W / 2.0), dy = y + 0.5 - (H / 2.0);
                rgb[((y * W) + x) * 3] = Scene(dx / 1.002, dy / 1.002);
                rgb[(((y * W) + x) * 3) + 1] = Scene(dx, dy);
                rgb[(((y * W) + x) * 3) + 2] = Scene(dx / 0.9985, dy / 0.9985);
            }
        }

        (double red, double blue)? found = RawAutoCa.Apply(rgb, W, H, new ParallelOptions());
        Assert.NotNull(found);
        Assert.Equal(1.002, found.Value.red, 0.0002);
        Assert.Equal(0.9985, found.Value.blue, 0.0002);

        // A plane without CA is left alone.
        float[] gray = new float[W * H * 3];
        for (int i = 0; i < W * H; i++)
        {
            double x = (i % W) + 0.5 - (W / 2.0), y = (i / W) + 0.5 - (H / 2.0);
            gray[i * 3] = gray[(i * 3) + 1] = gray[(i * 3) + 2] = Scene(x, y);
        }

        Assert.Null(RawAutoCa.Apply(gray, W, H, new ParallelOptions()));
    }
}
