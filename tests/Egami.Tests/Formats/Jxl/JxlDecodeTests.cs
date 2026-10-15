using System.Text.Json;

using Egami.Formats;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jxl;

/// <summary>
/// Phase 2.9.3 decoding gates: Egami's decode against djxl's (libjxl 0.12.0) on the committed corpus, and against the 18181-3
/// reference images within their thresholds on the conformance suite. Files whose features later tasks bring are listed as
/// not yet decoded; <see cref="Expected"/> names what must decode by now.
/// </summary>
public partial class JxlDecodeTests(ITestOutputHelper output)
{
    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "images", "jxl");
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "jxl");
    private static readonly Lazy<JsonElement> Manifest = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(References, "manifest.json"))).RootElement.GetProperty("files").Clone());

    /// <summary>Gets the corpus files that must decode exactly like djxl: the lossless ones, stills and animations.</summary>
    private static bool Expected(JsonElement entry)
    {
        string info = entry.GetProperty("jxlinfo").GetString()!;
        // Splines are drawn in float, so a "lossless" file with splines rounds like a lossy one.
        string covers = entry.TryGetProperty("covers", out JsonElement c) ? c.GetString()! : string.Empty;
        return info.Contains("(possibly) lossless", StringComparison.Ordinal) && !info.Contains("JPEG bitstream reconstruction", StringComparison.Ordinal)
            && !covers.Contains("spline", StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(JxlReferenceTests.CorpusFiles), MemberType = typeof(JxlReferenceTests))]
    public void CorpusDecodesLikeDjxl(string file)
    {
        JsonElement entry = Manifest.Value.GetProperty(file);
        if (!entry.TryGetProperty("reference", out JsonElement reference))
        {
            return;
        }

        bool expected = Expected(entry);
        Raster egami;
        try
        {
            egami = Raster.Load(Path.Combine(Corpus, file));
        }
        catch (UnsupportedFormatException e) when (!expected)
        {
            output.WriteLine($"{file}: not decoded yet ({e.Message})");
            return;
        }

        using (egami)
        {
            using Raster djxl = Raster.Load(Path.Combine(References, reference.GetString()!));
            int orientation = int.Parse(OrientationPattern().Match(entry.GetProperty("jxlinfo").GetString()!).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            (int ow, int oh) = orientation > 4 ? (egami.Height, egami.Width) : (egami.Width, egami.Height);
            Assert.Equal((djxl.Width, djxl.Height), (ow, oh));
            Assert.Equal(djxl.FrameCount, egami.FrameCount);
            double peak = 0;
            for (int frame = 0; frame < egami.FrameCount; frame++)
            {
                float[] a = Orient(Floats(egami, null, frame), egami.Width, egami.Height, egami.Layout.ChannelCount, orientation);
                float[] b = Floats(djxl, egami.Layout.ChannelCount, frame);
                Assert.Equal(b.Length, a.Length);
                for (int i = 0; i < a.Length; i++)
                {
                    peak = Math.Max(peak, Math.Abs(a[i] - b[i]));
                }
            }

            output.WriteLine($"{file}: peak difference {peak}");
            if (expected)
            {
                Assert.Equal(0, peak);
            }
            else if (!reference.GetString()!.EndsWith(".pfm", StringComparison.Ordinal))
            {
                // Lossy: within one 8-bit level of djxl (design question 16).
                Assert.InRange(peak, 0, 1.001 / 255);
            }
        }
    }

    [Theory]
    [MemberData(nameof(JxlHeaderTests.CachedFiles), MemberType = typeof(JxlHeaderTests))]
    public void ConformanceDecodesWithinThresholds(string name)
    {
        if (JxlReferenceTests.Cache() is not string cache || !name.StartsWith("conformance/", StringComparison.Ordinal))
        {
            return;
        }

        string folder = Path.Combine(cache, "conformance", "testcases", name[12..]);
        using JsonDocument spec = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "test.json")));
        Raster egami;
        try
        {
            // Float output keeps samples outside 0 to 1, as the reference images do.
            using FileStream stream = File.OpenRead(Path.Combine(folder, "input.jxl"));
            EgamiInput input = new(stream, EgamiContext.Default, TestContext.Current.CancellationToken);
            egami = Egami.Formats.Jxl.JxlImageDecoder.DecodeFloat(input);
            input.Dispose();
        }
        catch (UnsupportedFormatException e)
        {
            output.WriteLine($"{name}: not decoded yet ({e.Message})");
            return;
        }

        using (egami)
        {
            JxlReferenceTests.Npy reference = JxlReferenceTests.Npy.Read(Path.Combine(folder, "reference_image.npy"));
            // The references are upright.
            int orientation = (int)(egami.Metadata.Exif?.Orientation ?? Egami.Metadata.ImageOrientation.Normal);
            (int ow, int oh) = orientation > 4 ? (egami.Height, egami.Width) : (egami.Width, egami.Height);
            Assert.Equal((reference.Shape[0], reference.Shape[2], reference.Shape[1]), (egami.FrameCount, ow, oh));
            int channels = reference.Shape[3];
            int pixels = egami.Width * egami.Height;
            for (int f = 0; f < egami.FrameCount; f++)
            {
                JsonElement frame = spec.RootElement.GetProperty("frames")[f];
                float[] ours = Orient(Floats(egami, channels, f), egami.Width, egami.Height, channels, orientation);
                double peak = 0;
                double[] squares = new double[channels];
                int start = f * pixels * channels;
                for (int i = 0; i < pixels * channels; i++)
                {
                    double d = Math.Abs(ours[i] - reference.Data[start + i]);
                    peak = Math.Max(peak, d);
                    squares[i % channels] += d * d;
                }

                double rms = squares.Max(s => Math.Sqrt(s / pixels));
                output.WriteLine($"{name} frame {f}: peak {peak}, RMS {rms}");
                Assert.True(peak <= frame.GetProperty("peak_error").GetDouble(), $"frame {f} peak {peak}");
                Assert.True(rms <= frame.GetProperty("rms_error").GetDouble(), $"frame {f} RMS {rms}");
            }
        }
    }

    /// <summary>Turns samples the way an EXIF orientation says (djxl's references are upright).</summary>
    private static float[] Orient(float[] v, int w, int h, int channels, int orientation)
    {
        if (orientation == 1)
        {
            return v;
        }

        (int ow, int oh) = orientation > 4 ? (h, w) : (w, h);
        float[] o = new float[v.Length];
        for (int y = 0; y < oh; y++)
        {
            for (int x = 0; x < ow; x++)
            {
                (int sx, int sy) = orientation switch
                {
                    2 => (w - 1 - x, y),
                    3 => (w - 1 - x, h - 1 - y),
                    4 => (x, h - 1 - y),
                    5 => (y, x),
                    6 => (y, h - 1 - x),
                    7 => (w - 1 - y, h - 1 - x),
                    _ => (w - 1 - y, x),
                };
                Array.Copy(v, ((sy * w) + sx) * channels, o, ((y * ow) + x) * channels, channels);
            }
        }

        return o;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"Orientation: (\d)")]
    private static partial System.Text.RegularExpressions.Regex OrientationPattern();

    /// <summary>Gets the samples as floats in the raster's channel count: gray as one, RGB as three, with alpha as four.</summary>
    private static float[] Floats(Raster raster, int? channels = null, int frame = 0)
    {
        int count = channels ?? raster.Layout.ChannelCount;
        using Raster<RgbaF32> f = raster.As<RgbaF32>();
        float[] values = new float[raster.Width * raster.Height * count];
        int n = 0;
        for (int y = 0; y < f.Height; y++)
        {
            foreach (RgbaF32 p in f.Frames[frame].Row(y))
            {
                ReadOnlySpan<float> v = [p.R, p.G, p.B, p.A];
                if (count == 1)
                {
                    values[n++] = v[0];
                }
                else if (count == 2)
                {
                    values[n++] = v[0];
                    values[n++] = v[3];
                }
                else
                {
                    for (int c = 0; c < count; c++)
                    {
                        values[n++] = v[c];
                    }
                }
            }
        }

        return values;
    }
}
