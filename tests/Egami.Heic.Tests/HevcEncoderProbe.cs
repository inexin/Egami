using System.Diagnostics;
using System.Globalization;

using Egami.Heic.Hevc;
using Egami.Pixels;

namespace Egami.Heic.Tests;

/// <summary>
/// A rough rate-distortion and speed probe of the HEVC encoder for development: with EGAMI_HEVC_PROBE set to a folder of PNG
/// photos, each is converted to 8-bit 4:2:0 (BT.601, full range), written as a .yuv file beside the results for other encoders,
/// and encoded at a few QPs; bytes, PSNR and time go to probe.csv in EGAMI_HEVC_PROBE_OUT (default: the system's temp folder).
/// EGAMI_HEVC_PROBE_EFFORT, _QPS and _AQ change the settings.
/// </summary>
public class HevcEncoderProbe(ITestOutputHelper output)
{
    [Fact]
    public void RateDistortion()
    {
        if (Environment.GetEnvironmentVariable("EGAMI_HEVC_PROBE") is not string folder)
        {
            return;
        }

        string target = Environment.GetEnvironmentVariable("EGAMI_HEVC_PROBE_OUT") ?? Path.GetTempPath();
        int effort = int.Parse(Environment.GetEnvironmentVariable("EGAMI_HEVC_PROBE_EFFORT") ?? "4", CultureInfo.InvariantCulture);
        int[] qps = [.. (Environment.GetEnvironmentVariable("EGAMI_HEVC_PROBE_QPS") ?? "22,27,32,37").Split(',').Select(int.Parse)];
        bool aq = Environment.GetEnvironmentVariable("EGAMI_HEVC_PROBE_AQ") == "1";
        int threads = int.Parse(Environment.GetEnvironmentVariable("EGAMI_HEVC_PROBE_THREADS") ?? "1", CultureInfo.InvariantCulture);
        List<string> lines = ["file,width,height,qp,bytes,psnr_y,psnr_yuv,seconds"];
        foreach (string file in Directory.GetFiles(folder, "*.png").Order())
        {
            using Raster<Rgb8> raster = Raster.Load<Rgb8>(file);
            int width = raster.Width & ~1, height = raster.Height & ~1;
            ushort[][] planes = [new ushort[width * height], new ushort[width * height / 4], new ushort[width * height / 4]];
            for (int y = 0; y < height; y++)
            {
                Span<Rgb8> row = raster.Row(y);
                for (int x = 0; x < width; x++)
                {
                    Rgb8 p = row[x];
                    planes[0][(y * width) + x] = (ushort)Math.Clamp((int)Math.Round((0.299 * p.R) + (0.587 * p.G) + (0.114 * p.B)), 0, 255);
                }
            }

            for (int y = 0; y < height / 2; y++)
            {
                for (int x = 0; x < width / 2; x++)
                {
                    double cb = 0, cr = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        Rgb8 p = raster.Row((2 * y) + (k >> 1))[(2 * x) + (k & 1)];
                        cb += 128 - (0.168736 * p.R) - (0.331264 * p.G) + (0.5 * p.B);
                        cr += 128 + (0.5 * p.R) - (0.418688 * p.G) - (0.081312 * p.B);
                    }

                    planes[1][(y * width / 2) + x] = (ushort)Math.Clamp((int)Math.Round(cb / 4), 0, 255);
                    planes[2][(y * width / 2) + x] = (ushort)Math.Clamp((int)Math.Round(cr / 4), 0, 255);
                }
            }

            string name = Path.GetFileNameWithoutExtension(file);
            using (FileStream yuv = File.Create(Path.Combine(target, $"{name}_{width}x{height}.yuv")))
            {
                foreach (ushort[] plane in planes)
                {
                    yuv.Write([.. plane.Select(v => (byte)v)]);
                }
            }

            int[] strides = [width, width / 2, width / 2];
            foreach (int qp in qps)
            {
                HevcEncoderSettings settings = new() { Width = width, Height = height, Qp = qp, Effort = effort, AdaptiveQp = aq, Parallelism = threads };
                Stopwatch watch = Stopwatch.StartNew();
                HevcEncodedPicture encoded = HevcEncoder.Encode(settings, planes, strides, TestContext.Current.CancellationToken);
                double seconds = watch.Elapsed.TotalSeconds;
                int bytes = encoded.Slice.Length + encoded.Vps.Length + encoded.Sps.Length + encoded.Pps.Length;
                double[] sse = new double[3];
                HevcPicture picture = encoded.Reconstruction;
                for (int c = 0; c < 3; c++)
                {
                    int w = c == 0 ? width : width / 2, h = c == 0 ? height : height / 2;
                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            double d = planes[c][(y * w) + x] - picture.Planes[c][(y * picture.Widths[c]) + x];
                            sse[c] += d * d;
                        }
                    }
                }

                static double Psnr(double s, int samples) => 10 * Math.Log10(255.0 * 255 * samples / Math.Max(s, 1e-9));
                double psnrY = Psnr(sse[0], width * height);
                double psnrYuv = ((6 * psnrY) + Psnr(sse[1], width * height / 4) + Psnr(sse[2], width * height / 4)) / 8;
                string line = string.Create(CultureInfo.InvariantCulture, $"{name},{width},{height},{qp},{bytes},{psnrY:F3},{psnrYuv:F3},{seconds:F3}");
                lines.Add(line);
                output.WriteLine(line);
            }
        }

        File.WriteAllLines(Path.Combine(target, "probe.csv"), lines);
    }

    /// <summary>
    /// With EGAMI_HEIC_RATE set to a folder of PNG photos, writes each as HEIC at the QPs of EGAMI_HEVC_PROBE_QPS (or the qualities
    /// of EGAMI_HEIC_RATE_QUALITIES) into EGAMI_HEVC_PROBE_OUT as name-qpNN.heic, with the tuning EGAMI_HEIC_AQ (0 or 1),
    /// EGAMI_HEIC_AQ_STRENGTH and EGAMI_HEIC_CHROMA_OFFSET give, for scoring with other tools; the time each takes goes to
    /// times.csv.
    /// </summary>
    [Fact]
    public void RateFiles()
    {
        if (Environment.GetEnvironmentVariable("EGAMI_HEIC_RATE") is not string folder)
        {
            return;
        }

        string target = Environment.GetEnvironmentVariable("EGAMI_HEVC_PROBE_OUT") ?? Path.GetTempPath();
        int effort = int.Parse(Environment.GetEnvironmentVariable("EGAMI_HEVC_PROBE_EFFORT") ?? "4", CultureInfo.InvariantCulture);
        string? qualities = Environment.GetEnvironmentVariable("EGAMI_HEIC_RATE_QUALITIES");
        int[] points = [.. (qualities ?? Environment.GetEnvironmentVariable("EGAMI_HEVC_PROBE_QPS") ?? "22,27,32,37").Split(',').Select(int.Parse)];
        HeicWriter.HeicTuning saved = HeicWriter.Tuning;
        double strength = HevcAdaptiveQp.Strength;
        try
        {
            HeicWriter.Tuning = new HeicWriter.HeicTuning
            {
                AdaptiveQp = Environment.GetEnvironmentVariable("EGAMI_HEIC_AQ") != "0",
                ChromaQpOffset = int.Parse(Environment.GetEnvironmentVariable("EGAMI_HEIC_CHROMA_OFFSET") ?? "0", CultureInfo.InvariantCulture),
            };
            HevcAdaptiveQp.Strength = double.Parse(Environment.GetEnvironmentVariable("EGAMI_HEIC_AQ_STRENGTH") ?? "1", CultureInfo.InvariantCulture);
            if (Environment.GetEnvironmentVariable("EGAMI_HEVC_PROFILE") is string changes)
            {
                // Name=value pairs, as "MaxCuLog2=5;SmallCandidates=2", applied to the effort's profile by reflection.
                HevcEncoderProfile.Adjust = profile =>
                {
                    foreach (string change in changes.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        string[] parts = change.Split('=');
                        System.Reflection.PropertyInfo property = typeof(HevcEncoderProfile).GetProperty(parts[0], System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                        profile = profile with { };
                        property.SetValue(profile, Convert.ChangeType(parts[1], property.PropertyType, CultureInfo.InvariantCulture));
                    }

                    return profile;
                };
            }
            int threads = int.Parse(Environment.GetEnvironmentVariable("EGAMI_HEVC_PROBE_THREADS") ?? "0", CultureInfo.InvariantCulture);
            EgamiContext context = threads > 0 ? new EgamiContext { MaxParallelism = threads }.WithHeic() : new EgamiContext().WithHeic();
            List<string> times = ["file,point,bytes,seconds"];
            foreach (string file in Directory.GetFiles(folder, "*.png").Order())
            {
                string name = Path.GetFileNameWithoutExtension(file);
                using Raster raster = Raster.Load(file, context);
                using HeicWriter.HeicStill still = new(raster, raster.Metadata, new HeicWriterOptions { Effort = effort }, context);
                foreach (int point in points)
                {
                    string path = Path.Combine(target, qualities is null ? $"{name}-qp{point:D2}.heic" : $"{name}-q{point:D3}.heic");
                    TimeSpan cpu = Process.GetCurrentProcess().TotalProcessorTime;
                    using (FileStream stream = File.Create(path))
                    {
                        if (qualities is null)
                        {
                            still.Write(50, stream, null, point);
                        }
                        else
                        {
                            still.Write(point, stream, null);
                        }
                    }

                    double seconds = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalSeconds;
                    times.Add(string.Create(CultureInfo.InvariantCulture, $"{name},{point},{new FileInfo(path).Length},{seconds:F3}"));
                }
            }

            File.WriteAllLines(Path.Combine(target, "times.csv"), times);
        }
        finally
        {
            HeicWriter.Tuning = saved;
            HevcAdaptiveQp.Strength = strength;
            HevcEncoderProfile.Adjust = null;
        }
    }

    /// <summary>
    /// With EGAMI_HEIC_WRITE_PROBE set to a folder of PNG files, writes each as HEIC in several ways into EGAMI_HEVC_PROBE_OUT, with
    /// each file's Annex B stream and Egami's own decode beside it, for other decoders to check.
    /// </summary>
    [Fact]
    public void WriteFiles()
    {
        if (Environment.GetEnvironmentVariable("EGAMI_HEIC_WRITE_PROBE") is not string folder)
        {
            return;
        }

        string target = Environment.GetEnvironmentVariable("EGAMI_HEVC_PROBE_OUT") ?? Path.GetTempPath();
        EgamiContext context = new EgamiContext().WithHeic();
        foreach (string file in Directory.GetFiles(folder, "*.png").Order())
        {
            string name = Path.GetFileNameWithoutExtension(file);
            using Raster loaded = Raster.Load(file, context);
            (string Suffix, HeicWriterOptions Options)[] ways =
            [
                ("q50", new HeicWriterOptions()),
                ("q90-444", new HeicWriterOptions { Quality = 90, Chroma = ChromaFormat.Yuv444 }),
                ("lossless", new HeicWriterOptions { Lossless = true }),
                ("grid", new HeicWriterOptions { GridTileSize = new Size(256, 256), ThumbnailSize = 160 }),
                ("10bit", new HeicWriterOptions { BitDepth = 10, Quality = 70 }),
            ];
            foreach ((string suffix, HeicWriterOptions options) in ways)
            {
                using Raster<Rgba8> raster = loaded.As<Rgba8>();
                string path = Path.Combine(target, $"{name}-{suffix}.heic");
                using (FileStream stream = File.Create(path))
                {
                    raster.Save(stream, "heic", options);
                }

                using Raster<Rgba8> decoded = Raster.Load<Rgba8>(path, context);
                decoded.Save(Path.ChangeExtension(path, ".egami.png"));
            }
        }
    }
}
