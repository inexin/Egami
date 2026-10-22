using Egami.Formats;
using Egami.Formats.Jpeg;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class PlanarYuv
{
    [Fact]
    public void Create()
    {
        #region create
        // A 1080p frame as video stores it: full-size luma, chroma at half width and height, BT.709 at limited range.
        using YuvRaster<byte> frame = YuvRaster.Create(1920, 1080, ChromaFormat.Yuv420, YuvColor.Bt709Limited);

        // 10-bit HDR planes hold samples 0 to 1023 in ushorts; an alpha plane is optional.
        using YuvRaster<ushort> hdr = YuvRaster.Create(3840, 2160, ChromaFormat.Yuv420, 10, YuvColor.Bt2020Limited, alpha: true);
        Console.WriteLine($"Chroma planes are {frame.U!.Width}×{frame.U.Height}");
        #endregion

        Assert.Equal(960, frame.U.Width);
        Assert.True(hdr.HasAlpha);
    }

    [Fact]
    public void Color()
    {
        #region color
        // How samples turn into RGB is always stated: a preset, or the codes a video container carries (ITU-T H.273).
        YuvColor jpeg = YuvColor.Jpeg;                                         // BT.601, full range, chroma centered
        YuvColor custom = new(YuvMatrix.Bt709, YuvRange.Full, ChromaSiting.Left);
        if (YuvColor.TryFromH273(matrixCoefficients: 9, fullRange: false, ChromaSiting.TopLeft, out YuvColor fromStream))
        {
            Console.WriteLine($"{fromStream.Matrix}, {fromStream.Range}; back to code {fromStream.ToH273()}");
        }

        // Or straight from the code points an AVIF or PNG cICP box carries.
        Egami.Colors.ColorCodePoints hdr10 = new(Primaries: 9, TransferCharacteristics: 16, MatrixCoefficients: 9, FullRange: false);
        hdr10.TryGetYuvColor(ChromaSiting.TopLeft, out YuvColor fromBox);
        #endregion

        Assert.Equal(YuvColor.Bt2020Limited, fromStream);
        Assert.Equal(fromStream, fromBox);
        Assert.NotEqual(jpeg, custom);
    }

    [Fact]
    public void Planes()
    {
        #region planes
        using YuvRaster<byte> yuv = YuvRaster.Create(640, 480, ChromaFormat.Yuv420, YuvColor.Jpeg);

        // Planes are read and written by row, like a raster's pixels.
        for (int y = 0; y < yuv.Y.Height; y++)
        {
            yuv.Y.Row(y).Fill((byte)(y / 2));
        }

        yuv.U!.Row(0)[0] = 128;

        // An 8-bit plane can be viewed as Gray8 pixels, without copying.
        PixelPlane<Gray8> luma = yuv.Y.AsGray8();
        #endregion

        Assert.Equal(100, luma.Row(200)[0].L);
    }

    [Fact]
    public void Wrap()
    {
        #region wrap
        // A decoder's 10-bit frame, rows padded to 64 samples: wrapped without a copy. Disposing the raster disposes the owner.
        int width = 50, height = 30, stride = 64, chromaStride = 32;
        ushort[] y = new ushort[stride * height], u = new ushort[chromaStride * 15], v = new ushort[chromaStride * 15];
        using YuvRaster<ushort> wrapped = YuvRaster.Wrap<ushort>(
            y, stride, u, chromaStride, v, chromaStride, width, height, ChromaFormat.Yuv420, bitDepth: 10, YuvColor.Bt2020Limited, owner: null);
        #endregion

        Assert.Equal(25, wrapped.U!.Width);
    }

    [Fact]
    public void Quarter()
    {
        #region quarter
        // An NTSC DV frame: 4:1:1, chroma a quarter of the luma width at full height, on the left column of each four.
        byte[] y = new byte[720 * 480], u = new byte[180 * 480], v = new byte[180 * 480];
        using YuvRaster<byte> dv = YuvRaster.Wrap<byte>(y, 0, u, 0, v, 0, 720, 480, ChromaFormat.Yuv411, bitDepth: 8, YuvColor.Bt601Limited);
        using Raster<Rgb8> rgb = dv.ToRaster<Rgb8>();
        #endregion

        Assert.Equal((180, 480), (dv.U!.Width, dv.U.Height));
        Assert.Equal(720, rgb.Width);
    }

    [Fact]
    public void Convert()
    {
        using SampleFiles files = new();
        #region convert
        using Raster photo = Raster.Load("duck.png");

        // RGB to planes and back. RGB comes out in the picture's own transfer (BT.709 video gives BT.709-encoded RGB).
        using YuvRaster<byte> yuv = YuvRaster.FromRaster(photo, ChromaFormat.Yuv420, YuvColor.Bt709Limited);
        using Raster rgba = yuv.ToRaster();                        // Rgba8 for 8-bit planes, Rgba16 for more
        using Raster<Rgb8> rgb = yuv.ToRaster<Rgb8>();             // or any pixel type
        #endregion

        Assert.Equal(photo.Width, rgb.Width);
    }

    [Fact]
    public void Reuse()
    {
        #region reuse
        // A player converts every frame into the same raster: after the first frame, nothing is allocated.
        using YuvRaster<byte> frame = YuvRaster.Create(1280, 720, ChromaFormat.Yuv420, YuvColor.Bt709Limited);
        using Raster<Bgra8> screen = Raster.Create<Bgra8>(1280, 720);
        for (int i = 0; i < 3; i++)
        {
            // ... the decoder writes frame's planes ...
            frame.CopyTo(screen);
        }

        // And the other way, for an encoder: CopyFrom fills existing planes.
        frame.CopyFrom(screen);
        #endregion
    }

    [Fact]
    public void Options()
    {
        using SampleFiles files = new();
        #region options
        using Raster photo = Raster.Load("duck.png");

        // The defaults filter chroma by its siting both ways; Nearest and Fast trade edge quality for speed.
        YuvConvertOptions fast = new() { Upsampling = ChromaUpsampling.Nearest, Downsampling = ChromaDownsampling.Fast };
        using YuvRaster<byte> yuv = YuvRaster.FromRaster(photo, ChromaFormat.Yuv420, YuvColor.Bt601Limited, options: fast);
        using Raster<Rgba8> back = yuv.ToRaster<Rgba8>(fast);
        #endregion

        Assert.Equal(photo.Height, back.Height);
    }

    [Fact]
    public void Packed()
    {
        #region packed
        // An NV12 frame from Media Foundation or MediaCodec: Y rows, then interleaved U, V rows, both at the same stride.
        int width = 640, height = 360, stride = 704;
        byte[] nv12 = new byte[stride * height * 3 / 2];
        using YuvRaster<byte> frame = YuvRaster.Create(width, height, ChromaFormat.Yuv420, YuvColor.Bt709Limited);
        frame.CopyFromPacked(YuvPackedFormat.Nv12, nv12, stride);

        // A webcam's YUYV, 4:2:2 in one plane, the other way.
        using YuvRaster<byte> webcam = YuvRaster.Create(width, height, ChromaFormat.Yuv422, YuvColor.Bt601Limited);
        byte[] yuyv = new byte[width * 2 * height];
        webcam.CopyToPacked(YuvPackedFormat.Yuyv, yuyv);
        #endregion
    }

    [Fact]
    public void Jpeg()
    {
        using SampleFiles files = new();
        #region jpeg
        // A JPEG's own planes, as decoded: no chroma upsampling and no color conversion.
        YuvRasterInfo info = YuvRaster.Probe("duck.jpg");
        Console.WriteLine($"{info.Info.Width}×{info.Info.Height}, planes: {info.ChromaFormat}");
        using YuvRaster planes = YuvRaster.Load("duck.jpg");

        // Saving JPEG-colored 8-bit planes skips color conversion and downsampling; other planes are converted first.
        planes.Save("copy.jpg", new JpegWriterOptions { Quality = 90 });
        #endregion

        Assert.Equal(ChromaFormat.Yuv420, planes.ChromaFormat);
    }

    [Fact]
    public void Operations()
    {
        #region operations
        using YuvRaster<byte> frame = YuvRaster.Create(1920, 1080, ChromaFormat.Yuv420, YuvColor.Bt709Limited);

        // Scale the planes before converting, as video does; crops start on a chroma sample (even x and y at 4:2:0).
        using YuvRaster<byte> small = frame.Resize(1280, 720, ResampleFilter.Bicubic);
        using YuvRaster<byte> center = small.Crop(new Rect(320, 180, 640, 360));

        // A player scales every frame into the same raster.
        using YuvRaster<byte> screen = YuvRaster.Create(960, 540, ChromaFormat.Yuv420, YuvColor.Bt709Limited);
        frame.ResizeTo(screen);

        // Everything else (rotate, color adjustments, drawing) runs on a Raster after conversion.
        using Raster<Rgba8> rgb = center.ToRaster<Rgba8>();
        rgb.Apply(ops => ops.Rotate(90));
        #endregion

        Assert.Equal(640, center.Width);
    }
}
