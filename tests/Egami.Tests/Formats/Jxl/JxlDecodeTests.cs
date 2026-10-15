using System.Text.Json;

using Egami.Formats;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jxl;

/// <summary>
/// Phase 2.9.3 decoding gates: Egami's decode against djxl's (libjxl 0.12.0) on the committed corpus, and against the 18181-3
/// reference images within their thresholds on the conformance suite. Files whose features later tasks bring are listed as
/// not yet decoded; <see cref="Expected"/> names what must decode by now.
/// </summary>
public class JxlDecodeTests(ITestOutputHelper output)
{
    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "images", "jxl");
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "jxl");
    private static readonly Lazy<JsonElement> Manifest = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(References, "manifest.json"))).RootElement.GetProperty("files").Clone());

    /// <summary>Gets the corpus files that must decode exactly like djxl at this stage: the lossless Modular ones without XYB.</summary>
    private static bool Expected(JsonElement entry)
    {
        string info = entry.GetProperty("jxlinfo").GetString()!;
        return info.Contains("(possibly) lossless", StringComparison.Ordinal) && !info.Contains("JPEG bitstream reconstruction", StringComparison.Ordinal)
            && !info.Contains("animation", StringComparison.Ordinal);
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
            Assert.Equal((djxl.Width, djxl.Height), (egami.Width, egami.Height));
            float[] a = Floats(egami);
            float[] b = Floats(djxl, egami.Layout.ChannelCount);
            Assert.Equal(b.Length, a.Length);
            double peak = 0;
            for (int i = 0; i < a.Length; i++)
            {
                peak = Math.Max(peak, Math.Abs(a[i] - b[i]));
            }

            output.WriteLine($"{file}: peak difference {peak}");
            if (expected)
            {
                Assert.Equal(0, peak);
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
            JsonElement frame = spec.RootElement.GetProperty("frames")[0];
            Assert.Equal((reference.Shape[2], reference.Shape[1]), (egami.Width, egami.Height));
            int channels = reference.Shape[3];
            float[] ours = Floats(egami, channels);
            double peak = 0;
            double[] squares = new double[channels];
            int pixels = egami.Width * egami.Height;
            for (int i = 0; i < pixels * channels; i++)
            {
                double d = Math.Abs(ours[i] - reference.Data[i]);
                peak = Math.Max(peak, d);
                squares[i % channels] += d * d;
            }

            double rms = squares.Max(s => Math.Sqrt(s / pixels));
            output.WriteLine($"{name}: peak {peak}, RMS {rms}");
            Assert.True(peak <= frame.GetProperty("peak_error").GetDouble(), $"peak {peak}");
            Assert.True(rms <= frame.GetProperty("rms_error").GetDouble(), $"RMS {rms}");
        }
    }

    /// <summary>Gets the samples as floats in the raster's channel count: gray as one, RGB as three, with alpha as four.</summary>
    private static float[] Floats(Raster raster, int? channels = null)
    {
        int count = channels ?? raster.Layout.ChannelCount;
        using Raster<RgbaF32> f = raster.As<RgbaF32>();
        float[] values = new float[raster.Width * raster.Height * count];
        int n = 0;
        for (int y = 0; y < f.Height; y++)
        {
            foreach (RgbaF32 p in f.Row(y))
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
