using System.Runtime.InteropServices;

using Egami.Formats.Webp;
using Egami.Pixels;

namespace Egami.Tests.Formats.Webp;

/// <summary>
/// Writes WebP files from Egami's encoders, each with the RGBA it must decode to, for tools/check-webp-interop.py to decode with
/// libwebp. Runs only when EGAMI_INTEROP_DIR is set.
/// </summary>
public class WebpInteropFiles
{
    [Fact]
    public void WriteFiles()
    {
        string? directory = Environment.GetEnvironmentVariable("EGAMI_INTEROP_DIR");
        Assert.SkipUnless(directory is not null, "EGAMI_INTEROP_DIR is not set.");
        directory = Path.Combine(directory!, "webp");
        Directory.CreateDirectory(directory);
        foreach ((string name, Raster<Rgba8> raster) in WebpTestImages.All())
        {
            using (raster)
            {
                for (int effort = 0; effort <= 6; effort++)
                {
                    using MemoryStream file = new();
                    raster.Save(file, WebpFormat.Info, new WebpWriterOptions { Lossless = true, Effort = effort });
                    File.WriteAllBytes(Path.Combine(directory, $"{name}.e{effort}.webp"), file.ToArray());
                    File.WriteAllBytes(Path.Combine(directory, $"{name}.e{effort}.rgba"), WebpTestImages.Bytes(raster));
                }

                foreach (int partitions in new[] { 2, 4, 8 })
                {
                    // Egami's decoder against libwebp's on several token partitions, which libwebp 1.3.2 no longer writes.
                    Vp8Planes planes = WebpRgbToYuv.Convert(WebpTestImages.Argb(raster), raster.Width, raster.Height);
                    byte[] frame = new Vp8Encoder(planes, new Vp8EncoderOptions { Partitions = partitions }).Encode();
                    using MemoryStream file = new();
                    WebpContainer.WriteSimple(file, WebpContainer.Vp8, frame);
                    string stem = Path.Combine(directory, $"{name}.lossy.p{partitions}");
                    File.WriteAllBytes(stem + ".webp", file.ToArray());
                    using Raster<Rgba8> decoded = Raster.Load<Rgba8>(file.ToArray());
                    File.WriteAllBytes(stem + ".rgba", WebpTestImages.Bytes(decoded));
                    File.WriteAllBytes(stem + ".yuv", WebpTestImages.Planes(file.ToArray()));
                }

                foreach ((int quality, int effort) in new[] { (75, 0), (75, 1), (75, 2), (75, 3), (75, 4), (75, 5), (75, 6), (0, 4), (30, 4), (90, 6), (100, 4) })
                {
                    using MemoryStream file = new();
                    raster.Save(file, WebpFormat.Info, new WebpWriterOptions { Lossless = false, Quality = quality, Effort = effort });
                    string stem = Path.Combine(directory, $"{name}.lossy.q{quality}e{effort}");
                    File.WriteAllBytes(stem + ".webp", file.ToArray());
                    using Raster<Rgba8> decoded = Raster.Load<Rgba8>(file.ToArray());
                    File.WriteAllBytes(stem + ".rgba", WebpTestImages.Bytes(decoded));
                    File.WriteAllBytes(stem + ".yuv", WebpTestImages.Planes(file.ToArray()));
                }
            }
        }
    }
}

/// <summary>The pictures the WebP encoder tests use, and helpers to encode and compare them.</summary>
internal static class WebpTestImages
{
    /// <summary>Returns the test pictures: the ported ClassicMac cases, photos, alpha and odd sizes.</summary>
    internal static IEnumerable<(string Name, Raster<Rgba8> Raster)> All()
    {
        Random random = new(1);
        byte[] noise = new byte[97 * 61 * 4];
        random.NextBytes(noise);
        yield return ("noise", FromBytes(97, 61, noise));
        yield return ("gradient", Make(130, 70, (x, y) => new Rgba8((byte)(x * 2), (byte)(y * 3), (byte)(x + y), 255)));
        yield return ("half-clear", Make(64, 64, (x, y) => x < 32 ? new Rgba8(200, 10, 10, 255) : new Rgba8(0, 0, 0, 0)));
        yield return ("mixed-alpha", Make(50, 40, (x, y) => new Rgba8((byte)(x * 5), (byte)(255 - (y * 6)), (byte)(x ^ y), (byte)(x * y % 256))));
        yield return ("checker", Make(256, 192, (x, y) => ((x / 8) + (y / 8)) % 2 == 0 ? new Rgba8(255, 255, 255, 255) : new Rgba8(0, 0, 204, 255)));
        foreach ((int w, int h) in new[] { (1, 1), (1, 300), (300, 1), (17, 33) })
        {
            yield return ($"edge-{w}x{h}", Make(w, h, (x, y) => new Rgba8((byte)((x * 7) + y), (byte)(y * 11), (byte)(x * 13), (byte)(255 - x - y))));
        }

        foreach (string photo in new[] { "duck", "egret" })
        {
            using Raster<Rgba8> full = Raster.Load<Rgba8>(Path.Combine(AppContext.BaseDirectory, "images", "ops", photo + ".png"));
            yield return (photo, (Raster<Rgba8>)full.Derive(ops => ops.Resize(320, 240)));
        }

        yield return ("shapes", Raster.Load<Rgba8>(Path.Combine(AppContext.BaseDirectory, "images", "ops", "shapes.png")));
    }

    /// <summary>Returns one test picture by name.</summary>
    internal static Raster<Rgba8> Get(string name)
    {
        Raster<Rgba8>? found = null;
        foreach ((string n, Raster<Rgba8> raster) in All())
        {
            if (n == name && found is null)
            {
                found = raster;
            }
            else
            {
                raster.Dispose();
            }
        }

        return found ?? throw new ArgumentException($"No test picture \"{name}\".", nameof(name));
    }

    internal static uint[] Argb(Raster<Rgba8> raster)
    {
        uint[] argb = new uint[raster.Width * raster.Height];
        for (int y = 0; y < raster.Height; y++)
        {
            PixelConvert.Convert<Rgba8, Bgra8>(raster.Row(y), MemoryMarshal.Cast<uint, Bgra8>(argb.AsSpan(y * raster.Width, raster.Width)));
        }

        return argb;
    }

    /// <summary>Decodes a lossy file's planes as WebPDecodeYUV packs them: Y, then U and V.</summary>
    internal static byte[] Planes(byte[] webp)
    {
        using YuvRaster yuv = YuvRaster.Load(webp);
        YuvRaster<byte> planes = (YuvRaster<byte>)yuv;
        int width = planes.Width, height = planes.Height, cw = (width + 1) / 2, ch = (height + 1) / 2;
        byte[] packed = new byte[(width * height) + (2 * cw * ch)];
        for (int y = 0; y < height; y++)
        {
            planes.Y.Row(y).CopyTo(packed.AsSpan(y * width, width));
        }

        for (int y = 0; y < ch; y++)
        {
            planes.U!.Row(y).CopyTo(packed.AsSpan((width * height) + (y * cw), cw));
            planes.V!.Row(y).CopyTo(packed.AsSpan((width * height) + (cw * ch) + (y * cw), cw));
        }

        return packed;
    }

    internal static byte[] Bytes(Raster<Rgba8> raster)
    {
        byte[] bytes = new byte[raster.Width * raster.Height * 4];
        for (int y = 0; y < raster.Height; y++)
        {
            MemoryMarshal.AsBytes(raster.Row(y)).CopyTo(bytes.AsSpan(y * raster.Width * 4));
        }

        return bytes;
    }

    internal static Raster<Rgba8> Make(int width, int height, Func<int, int, Rgba8> at)
    {
        Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = at(x, y);
            }
        }

        return raster;
    }

    internal static Raster<Rgba8> FromBytes(int width, int height, byte[] rgba)
    {
        Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height);
        for (int y = 0; y < height; y++)
        {
            MemoryMarshal.Cast<byte, Rgba8>(rgba.AsSpan(y * width * 4, width * 4)).CopyTo(raster.Row(y));
        }

        return raster;
    }
}
