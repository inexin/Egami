using Egami.Formats;
using Egami.Formats.Heic;
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

    [Fact]
    public void HdrAndItems()
    {
        using SampleFiles files = new();
        EgamiContext heic = new EgamiContext().WithHeic();

        #region hdr
        // An iPhone photo's gain map (Apple's HDRGainMap, or ISO 21496-1 on newer iPhones) comes with the SDR image; ask for the HDR
        // rendition for a display with 2 stops of headroom.
        using Raster sdr = Raster.Load("portrait.heic", heic);
        bool hasGainMap = sdr.Metadata.GainMap is not null;
        using Raster hdr = Raster.Load("portrait.heic", heic, new ReaderOptions { Hdr = new HdrDecode { Headroom = 2 } });
        #endregion

        #region items
        // The other images a photo holds: thumbnails, gain maps, depth maps and mattes (portrait, skin, sky).
        HeicDetails details = (HeicDetails)sdr.Metadata.FormatDetails!;
        HeicItem sky = details.Items.First(i => i.AuxiliaryType == "urn:com:apple:photo:2020:aux:semanticskymatte");
        using Raster matte = HeicImage.ReadItem("portrait.heic", sky.Id, context: heic);
        matte.Apply(ops => ops.AutoOrient());
        matte.Save("sky-matte.png");
        #endregion

        Assert.True(hasGainMap);
        Assert.IsType<Raster<Egami.Pixels.RgbaF16>>(hdr);
        Assert.True(File.Exists("sky-matte.png"));
    }

    [Fact]
    public void SequencesAndStreams()
    {
        using SampleFiles files = new();
        EgamiContext heic = new EgamiContext().WithHeic();

        #region sequence
        // A burst or animated HEIC file: every frame, inter-coded ones too, in output order.
        using Raster burst = Raster.Load("burst.heic", heic);
        int frames = burst.FrameCount;
        #endregion

        #region stream
        // An HEVC stream from any source: Annex B here, or SetConfiguration with an MP4 track's hvcC and its samples.
        using HevcStreamDecoder decoder = new(context: heic);
        decoder.Send(File.ReadAllBytes("clip.h265"));
        decoder.Flush();                                    // the end of the stream: the last pictures come out
        int shown = 0;
        while (decoder.NextFrame is HevcFrameInfo next)
        {
            using YuvRaster<byte> frame = YuvRaster.Create(next.Width, next.Height, next.Chroma, YuvColor.Bt709Limited);
            decoder.TryReceiveFrame(frame, out HevcFrameInfo info);     // info.Poc, info.Kind, info.Color
            shown++;
        }
        #endregion

        Assert.Equal(4, frames);
        Assert.Equal(12, shown);
    }
}
