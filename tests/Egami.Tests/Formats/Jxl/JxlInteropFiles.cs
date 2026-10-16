using Egami.Colors;
using Egami.Formats.Jxl;
using Egami.Formats.Pnm;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jxl;

/// <summary>
/// Writes Egami's JPEG XL files in every layout the writer takes, each next to the samples it holds as PAM (raw floats for float), for
/// tools/check-jxl-encoder.py --writer to decode with djxl and compare, and to check ICC, EXIF, XMP and orientation with
/// jxlinfo. Runs only when EGAMI_INTEROP_DIR is set.
/// </summary>
public class JxlInteropFiles
{
    [Fact]
    public void WriteFiles()
    {
        string? root = Environment.GetEnvironmentVariable("EGAMI_INTEROP_DIR");
        Assert.SkipUnless(root is not null, "EGAMI_INTEROP_DIR is not set.");
        string directory = Path.Combine(root!, "jxl");
        Directory.CreateDirectory(directory);
        using Raster<Rgba8> photo = Raster.Load<Rgba8>(Path.Combine(SimpleCorpus.Images, "ops", "duck.png"));
        using Raster<Rgba8> rgba = photo.Derive(ops => ops.Fit(301, 217), TestContext.Current.CancellationToken) is Raster<Rgba8> r ? WithAlpha(r) : throw new InvalidOperationException();
        using Raster<Rgb8> rgb = rgba.As<Rgb8>();
        using Raster<Gray8> gray = rgba.As<Gray8>();
        using Raster<GrayAlpha8> grayAlpha = rgba.As<GrayAlpha8>();
        using Raster<Rgb16> rgb16 = rgba.As<Rgb16>();
        using Raster<Rgba16> rgba16 = rgba.As<Rgba16>();
        using Raster<Gray16> gray16 = rgba.As<Gray16>();
        using Raster<RgbF32> rgbF = rgba.As<RgbF32>();
        foreach (int effort in (int[])[1, 2, 3, 5, 7])
        {
            JxlWriterOptions options = new() { Effort = effort };
            Write(directory, $"rgb8-e{effort}", rgb, options);
            Write(directory, $"rgba8-e{effort}", rgba, options);
            Write(directory, $"gray8-e{effort}", gray, options);
            Write(directory, $"grayalpha8-e{effort}", grayAlpha, options);
            Write(directory, $"rgb16-e{effort}", rgb16, options);
            Write(directory, $"rgba16-e{effort}", rgba16, options);
            Write(directory, $"gray16-e{effort}", gray16, options);
            Write(directory, $"rgbf32-e{effort}", rgbF, options);
        }

        // Metadata: an ICC profile, EXIF with an orientation, XMP.
        using Raster<Rgb8> tagged = rgba.As<Rgb8>();
        tagged.Metadata.IccProfile = ColorProfile.DisplayP3.ToIcc();
        tagged.Metadata.ColorSpace = ColorSpaceHint.EmbeddedIcc;
        Write(directory, "icc-p3", tagged, JxlWriterOptions.Default);
        using Raster<Rgb8> exif = rgba.As<Rgb8>();
        exif.Metadata.Exif = new ExifData { Orientation = ImageOrientation.Rotate90 };
        exif.Metadata.Xmp = "<x:xmpmeta xmlns:x='adobe:ns:meta/'/>"u8.ToArray();
        Write(directory, "exif-xmp-orientation", exif, JxlWriterOptions.Default);
        using Raster<Rgb8> linear = rgba.As<Rgb8>();
        linear.Metadata.ColorSpace = ColorSpaceHint.Linear;
        Write(directory, "linear", linear, JxlWriterOptions.Default);
    }

    private static void Write<T>(string directory, string name, Raster<T> raster, JxlWriterOptions options)
        where T : unmanaged, IPixelType<T>
    {
        raster.Save(Path.Combine(directory, name + ".jxl"), options);
        if (T.Layout.ChannelType != PixelChannelType.Float32)
        {
            using FileStream pam = File.Create(Path.Combine(directory, name + ".pam"));
            raster.Save(pam, PnmFormat.Info, new PnmWriterOptions { Kind = PnmKind.Pam });
            return;
        }

        // Float samples as they are: width and height, then RGB rows top down, little-endian.
        using FileStream raw = File.Create(Path.Combine(directory, name + ".f32"));
        using BinaryWriter w = new(raw);
        w.Write(raster.Width);
        w.Write(raster.Height);
        using Raster<RgbF32> f = raster.As<RgbF32>();
        for (int y = 0; y < f.Height; y++)
        {
            foreach (RgbF32 p in f.Row(y))
            {
                w.Write(p.R);
                w.Write(p.G);
                w.Write(p.B);
            }
        }
    }

    private static Raster<Rgba8> WithAlpha(Raster<Rgba8> r)
    {
        for (int y = 0; y < r.Height; y++)
        {
            Span<Rgba8> row = r.Row(y);
            for (int x = 0; x < r.Width; x++)
            {
                row[x].A = (byte)((x * 3) + y);
            }
        }

        return r;
    }
}
