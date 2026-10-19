using Egami.Formats;
using Egami.Heic;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Heic
{
    [Fact]
    public void ProbeWithoutThePackage()
    {
        using SampleFiles files = new();

        #region probe
        // Core Egami detects HEIC and reads its container: size, natural pixel type and orientation.
        RasterInfo info = Raster.Probe("photo.heic");
        string name = info.Format.Name;                          // "HEIC"

        // Loading needs the Egami.Heic package: the exception says so.
        try
        {
            using Raster image = Raster.Load("photo.heic");
        }
        catch (UnsupportedFormatException e)
        {
            Console.WriteLine(e.Message);
        }
        #endregion

        Assert.Equal("HEIC", name);
        Assert.Equal((1024, 768), (info.Width, info.Height));
    }

    [Fact]
    public void Register()
    {
        using SampleFiles files = new();

        #region register
        // Opt in to HEVC once: register Egami.Heic in a context (at startup, EgamiContext.SetDefault(new EgamiContext().WithHeic())
        // makes it the default for every call).
        EgamiContext heic = new EgamiContext().WithHeic();
        RasterInfo info = Raster.Probe("photo.heic", heic);
        bool fromPackage = info.Format == HeicFormat.Info;
        #endregion

        Assert.True(fromPackage);
        Assert.Equal((1024, 768), (info.Width, info.Height));
    }

    [Fact]
    public void LoadAPhonePhoto()
    {
        using SampleFiles files = new();
        EgamiContext heic = new EgamiContext().WithHeic();

        #region load
        // An iPhone photo: a grid of HEVC tiles, decoded in parallel and converted to RGB as heif-dec converts them.
        using Raster photo = Raster.Load("photo.heic", heic);
        photo.Apply(ops => ops.AutoOrient());           // irot and imir come as the orientation
        photo.Save("photo.jpg");
        #endregion

        #region yuv
        // The HEVC planes themselves, without the conversion to RGB: luma, then chroma at the file's subsampling.
        using YuvRaster planes = YuvRaster.Load("photo.heic", heic);
        ChromaFormat chroma = planes.ChromaFormat;          // Yuv420 for phone photos
        int depth = planes.BitDepth;                        // 8, or 10 for HDR photos
        #endregion

        Assert.True(File.Exists("photo.jpg"));
        Assert.Equal(ChromaFormat.Yuv420, chroma);
        Assert.Equal(8, depth);
    }
}
