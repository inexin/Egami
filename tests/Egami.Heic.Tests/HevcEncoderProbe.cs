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
}
