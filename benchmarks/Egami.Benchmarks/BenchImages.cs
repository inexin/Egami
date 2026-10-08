using Egami.Formats.Png;
using Egami.Pixels;
using Egami.Tests.Formats.Png;

namespace Egami.Benchmarks;

/// <summary>
/// The benchmark images, generated in memory from fixed formulas so every run and machine uses the same bytes.
/// Sizes and kinds follow the PNG design's benchmark scenarios.
/// </summary>
internal static class BenchImages
{
    private static readonly Dictionary<string, byte[]> Cache = [];

    /// <summary>Gets the decode scenarios, by name.</summary>
    internal static string[] DecodeScenarios { get; } = ["Photo-RGBA8", "Screenshot-RGB8", "RGB16", "Palette8", "Gray1", "Adam7-RGBA8"];

    internal static byte[] Png(string scenario)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(scenario, out byte[]? file))
            {
                file = Create(scenario);
                Cache[scenario] = file;
            }

            return file;
        }
    }

    /// <summary>A photo-like image: smooth gradients with fine noise, which compresses about as poorly as a real photo.</summary>
    internal static Raster<Rgba8> Photo(int width, int height)
    {
        Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height);
        raster.ForEachRow((row, y) =>
        {
            uint state = (uint)y * 2654435761u;
            for (int x = 0; x < row.Length; x++)
            {
                state = (state * 1664525) + 1013904223;
                int noise = (int)(state >> 28) - 8;
                double fx = x / (double)width;
                double fy = y / (double)height;
                row[x] = new Rgba8(
                    Clamp(128 + (100 * Math.Sin(fx * 7.1) * Math.Cos(fy * 3.3)) + noise),
                    Clamp(128 + (90 * Math.Sin((fx + fy) * 5.7)) + noise),
                    Clamp(110 + (80 * Math.Cos(fy * 9.2)) + noise),
                    255);
            }
        });
        return raster;
    }

    /// <summary>A screenshot-like image: flat panels, borders and dense "text" strokes.</summary>
    internal static Raster<Rgb8> Screenshot(int width, int height)
    {
        Raster<Rgb8> raster = Raster.Create<Rgb8>(width, height);
        Rgb8[] panels = [new(245, 245, 245), new(30, 30, 30), new(0, 120, 215), new(255, 255, 255), new(230, 230, 230)];
        raster.ForEachRow((row, y) =>
        {
            for (int x = 0; x < row.Length; x++)
            {
                Rgb8 panel = panels[((x / 480) + (y / 270)) % panels.Length];
                bool border = x % 480 < 2 || y % 270 < 2;
                bool text = y % 24 is >= 8 and < 18 && x % 480 is > 20 and < 400 && ((x * 7) + (y * 3)) % 11 < 4;
                row[x] = border ? new Rgb8(200, 200, 200) : text ? new Rgb8(20, 20, 20) : panel;
            }
        });
        return raster;
    }

    private static byte[] Create(string scenario)
    {
        switch (scenario)
        {
            case "Photo-RGBA8":
                {
                    using Raster<Rgba8> photo = Photo(4000, 3000);
                    return Encode(photo, new PngWriterOptions());
                }

            case "Screenshot-RGB8":
                {
                    using Raster<Rgb8> screenshot = Screenshot(1920, 1080);
                    return Encode(screenshot, new PngWriterOptions());
                }

            case "RGB16":
                {
                    using Raster<Rgba8> photo = Photo(2000, 1500);
                    using Raster<Rgba16> wide = photo.As<Rgba16>();
                    wide.ForEachRow((row, y) =>
                    {
                        for (int x = 0; x < row.Length; x++)
                        {
                            row[x].R ^= (ushort)((x + y) % 251);
                        }
                    });
                    return Encode(wide, new PngWriterOptions { ColorType = PngColorType.Rgb, BitDepth = 16 });
                }

            case "Palette8":
                {
                    using Raster<Rgb8> screenshot = Screenshot(1920, 1080);
                    return Encode(screenshot, new PngWriterOptions { ColorType = PngColorType.Palette });
                }

            case "Gray1":
                {
                    using Raster<Gray8> gray = Raster.Create<Gray8>(4000, 3000);
                    gray.ForEachRow((row, y) =>
                    {
                        for (int x = 0; x < row.Length; x++)
                        {
                            row[x] = new Gray8(((x / 3) + (y / 5) + (x * y % 7)) % 2 == 0 ? (byte)0 : (byte)255);
                        }
                    });
                    return Encode(gray, new PngWriterOptions { ColorType = PngColorType.Gray, BitDepth = 1 });
                }

            case "Adam7-RGBA8":
                {
                    // Egami writes only non-interlaced PNG, so the interlaced file comes from the test suite's independent builder.
                    using Raster<Rgba8> photo = Photo(2000, 1500);
                    PngBuilder builder = new(2000, 1500, PngColorType.Rgba, 8) { Interlaced = true, IdatChunks = 40, Filter = _ => 4 };
                    for (int y = 0; y < 1500; y++)
                    {
                        Span<Rgba8> row = photo.Row(y);
                        for (int x = 0; x < 2000; x++)
                        {
                            builder.SetSample(x, y, 0, row[x].R);
                            builder.SetSample(x, y, 1, row[x].G);
                            builder.SetSample(x, y, 2, row[x].B);
                            builder.SetSample(x, y, 3, row[x].A);
                        }
                    }

                    return builder.Build();
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown scenario.");
        }
    }

    private static byte[] Encode(Raster raster, PngWriterOptions options)
    {
        using MemoryStream stream = new();
        raster.Save(stream, PngFormat.Info, options);
        return stream.ToArray();
    }

    private static byte Clamp(double value)
    {
        return (byte)Math.Clamp(value, 0, 255);
    }
}
