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
                if (((Egami.Formats.Jxl.JxlDetails)egami.Metadata.FormatDetails!).ExtraChannels.Any(e => e.AlphaPremultiplied))
                {
                    // djxl writes premultiplied color as coded; Load gives it straight (design question 7), so compare the coded channels.
                    a = Coded(Path.Combine(Corpus, file), frame, egami.Layout.ChannelCount);
                }

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
        byte[] data = File.ReadAllBytes(Path.Combine(folder, "input.jxl"));
        List<Egami.Formats.Jxl.JxlPlanes> frames;
        try
        {
            // Raw float channels, unclamped, as the reference images are.
            using MemoryStream stream = new(data);
            EgamiInput input = new(stream, EgamiContext.Default, TestContext.Current.CancellationToken);
            frames = Egami.Formats.Jxl.JxlImageDecoder.DecodeChannels(input);
            input.Dispose();
        }
        catch (UnsupportedFormatException e)
        {
            output.WriteLine($"{name}: not decoded yet ({e.Message})");
            return;
        }

        // The references are upright.
        int orientation = JxlInfoText.Read(data).Header.Orientation;
        JxlReferenceTests.Npy reference = JxlReferenceTests.Npy.Read(Path.Combine(folder, "reference_image.npy"));
        int width = frames[0].Width;
        int height = frames[0].Height;
        (int ow, int oh) = orientation > 4 ? (height, width) : (width, height);
        int channels = reference.Shape[3];
        Assert.Equal((reference.Shape[0], reference.Shape[2], reference.Shape[1], channels), (frames.Count, ow, oh, frames[0].Planes.Length));
        int pixels = width * height;
        for (int f = 0; f < frames.Count; f++)
        {
            JsonElement frame = spec.RootElement.GetProperty("frames")[f];
            float[] interleaved = new float[pixels * channels];
            for (int i = 0; i < pixels; i++)
            {
                for (int c = 0; c < channels; c++)
                {
                    interleaved[(i * channels) + c] = frames[f].Planes[c][i];
                }
            }

            float[] ours = Orient(interleaved, width, height, channels, orientation);
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

    /// <summary>Gets one frame's coded channels, interleaved and rounded to 8 bits as a PNG would hold them.</summary>
    private static float[] Coded(string path, int frame, int channels)
    {
        using FileStream stream = File.OpenRead(path);
        EgamiInput input = new(stream, EgamiContext.Default, TestContext.Current.CancellationToken);
        Egami.Formats.Jxl.JxlPlanes planes = Egami.Formats.Jxl.JxlImageDecoder.DecodeChannels(input)[frame];
        input.Dispose();
        int pixels = planes.Width * planes.Height;
        float[] values = new float[pixels * channels];
        for (int i = 0; i < pixels; i++)
        {
            for (int c = 0; c < channels; c++)
            {
                values[(i * channels) + c] = MathF.Round(Math.Clamp(planes.Planes[c][i], 0, 1) * 255) / 255;
            }
        }

        return values;
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
