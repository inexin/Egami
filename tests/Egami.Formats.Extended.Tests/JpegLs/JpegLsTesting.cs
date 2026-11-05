using System.Text;

using Egami.Formats.Extended.JpegLs;

namespace Egami.Formats.Extended.Tests.JpegLs;

/// <summary>Decodes JPEG-LS to raw component planes, reads PGM and PPM, and finds the conformance set.</summary>
internal static class JpegLsTesting
{
    /// <summary>Gets the conformance folder (tools/make-jpegls-references.py), or <see langword="null"/> when this run does not read it.</summary>
    public static string? Conformance
    {
        get
        {
            string asked = Environment.GetEnvironmentVariable("EGAMI_CORPUS") ?? "";
            if (!asked.Split(',', ';', ' ').Any(a => a.Trim() is "jpegls" or "all"))
            {
                return null;
            }

            string? dir = AppContext.BaseDirectory;
            while (dir is not null && !File.Exists(Path.Combine(dir, "Egami.slnx")))
            {
                dir = Path.GetDirectoryName(dir);
            }

            string folder = Path.Combine(dir ?? ".", ".cache", "jpegls", "conformance");
            return Directory.Exists(folder) ? folder : null;
        }
    }

    /// <summary>Decodes every scan of <paramref name="data"/> into one plane per component, at each component's own size.</summary>
    public static (JpegLsHeader Header, ushort[][] Planes, List<string> Warnings) Decode(byte[] data, bool strict = true)
    {
        List<string> warnings = [];
        JpegLsDecoder decoder = new(data, data.Length, (name, message) => warnings.Add($"{name}: {message}"));
        Planes sink = new(decoder.Header);
        decoder.DecodeAll(sink, strict);
        return (decoder.Header, sink.Data, warnings);
    }

    /// <summary>Reads a binary PGM or PPM: its size, maximum and samples, interleaved.</summary>
    public static (int Width, int Height, int Channels, int Max, ushort[] Samples) ReadPnm(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int at = 0;
        string Token()
        {
            while (char.IsWhiteSpace((char)bytes[at]) || bytes[at] == '#')
            {
                if (bytes[at] == '#')
                {
                    while (bytes[at] != '\n')
                    {
                        at++;
                    }
                }

                at++;
            }

            int start = at;
            while (!char.IsWhiteSpace((char)bytes[at]))
            {
                at++;
            }

            return Encoding.ASCII.GetString(bytes, start, at - start);
        }

        string magic = Token();
        int width = int.Parse(Token(), System.Globalization.CultureInfo.InvariantCulture), height = int.Parse(Token(), System.Globalization.CultureInfo.InvariantCulture), max = int.Parse(Token(), System.Globalization.CultureInfo.InvariantCulture);
        at++;
        int channels = magic == "P6" ? 3 : 1;
        ushort[] samples = new ushort[width * height * channels];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = max > 255 ? (ushort)((bytes[at + (2 * i)] << 8) | bytes[at + (2 * i) + 1]) : bytes[at + i];
        }

        return (width, height, channels, max, samples);
    }

    /// <summary>Returns component <paramref name="c"/> of interleaved samples.</summary>
    public static ushort[] Component(ushort[] samples, int channels, int c)
    {
        return [.. samples.Where((_, i) => i % channels == c)];
    }

    /// <summary>Returns the largest difference between two sample arrays of one length.</summary>
    public static int MaxError(ReadOnlySpan<ushort> expected, ReadOnlySpan<ushort> actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        int most = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            most = Math.Max(most, Math.Abs(expected[i] - actual[i]));
        }

        return most;
    }

    /// <summary>Codes samples with the public sample writer.</summary>
    public static byte[] Encode(ushort[] samples, int components, int width, int bits, JpegLsWriterOptions? options = null)
    {
        using MemoryStream output = new();
        JpegLsWriter.Write(output, samples, components, width, bits, options);
        return output.ToArray();
    }

    /// <summary>Decodes to interleaved samples, every component at full size.</summary>
    public static ushort[] Interleaved(byte[] data)
    {
        (JpegLsHeader header, ushort[][] planes, _) = Decode(data);
        int n = planes.Length;
        ushort[] samples = new ushort[header.Width * header.Height * n];
        for (int c = 0; c < n; c++)
        {
            for (int i = 0; i < header.Width * header.Height; i++)
            {
                samples[(i * n) + c] = planes[c][i];
            }
        }

        return samples;
    }

    private sealed class Planes(JpegLsHeader header) : JpegLsLineSink
    {
        public ushort[][] Data { get; } = [.. header.Components.Select(c => new ushort[c.Width * c.Height])];

        public override void Line(int component, int y, ReadOnlySpan<int> samples)
        {
            int width = header.Components[component].Width;
            for (int x = 0; x < width; x++)
            {
                Data[component][(y * width) + x] = (ushort)samples[x];
            }
        }
    }
}
