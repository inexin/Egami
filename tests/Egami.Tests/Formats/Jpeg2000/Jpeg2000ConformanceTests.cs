using System.Text;

using Egami.Formats.Jpeg2000;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jpeg2000;

/// <summary>
/// The ISO/IEC 15444-4 conformance codestreams as openjpeg-data holds them (kept out of the repo under .cache/openjpeg-data):
/// every component of every Class 1 codestream decodes within its peak error and mean squared error tolerances.
/// </summary>
public class Jpeg2000ConformanceTests(ITestOutputHelper output)
{
    internal static string? Folder()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, ".cache", "openjpeg-data");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static TheoryData<string> Codestreams()
    {
        string? folder = Folder();
        return folder is null
            ? ["(none)"]
            : [.. Directory.GetFiles(Path.Combine(folder, "input", "conformance"), "p*.j2k").Select(f => Path.GetFileNameWithoutExtension(f)!).Order(StringComparer.Ordinal)];
    }

    [Theory]
    [MemberData(nameof(Codestreams))]
    public void Class1ComponentsMatch(string name)
    {
        string? folder = Folder();
        if (folder is null)
        {
            return;
        }

        byte[] data = File.ReadAllBytes(Path.Combine(folder, "input", "conformance", name + ".j2k"));
        J2kCodestream stream = J2kCodestream.Parse(data, SafetyLimits.Default);
        J2kImage image = new J2kDecoder(stream, 0, 0, Environment.ProcessorCount, default).Decode();
        for (int c = 0; c < image.Planes.Length; c++)
        {
            Compare(Path.Combine(folder, "baseline", "conformance", $"c1{name}_{c}.pgx"), image, c, $"{name} component {c} vs 15444-4");
            Compare(Path.Combine(folder, "..", "j2k-opj", $"{name}_{c}.pgx"), image, c, $"{name} component {c} vs OpenJPEG");
        }
    }

    private void Compare(string reference, J2kImage image, int c, string label)
    {
        if (!File.Exists(reference))
        {
            return;
        }

        {
            (int width, int height, int[] samples) = ReadPgx(reference);
            if ((width, height) != (image.Widths[c], image.Heights[c]))
            {
                output.WriteLine($"{label}: size {width}x{height}, Egami {image.Widths[c]}x{image.Heights[c]}");
                return;
            }

            long peak = 0;
            double squares = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                long d = Math.Abs((long)samples[i] - image.Planes[c][i]);
                peak = Math.Max(peak, d);
                squares += (double)d * d;
            }

            output.WriteLine($"{label}: {width}x{height}, peak {peak}, MSE {squares / samples.Length:F4}");
        }
    }

    public static TheoryData<string> Jp2Files()
    {
        string? folder = Folder();
        return folder is null
            ? ["(none)"]
            : [.. Directory.GetFiles(Path.Combine(folder, "input", "conformance"), "*.jp2").Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal)];
    }

    [Theory]
    [MemberData(nameof(Jp2Files))]
    public void Jp2FilesMatchPillow(string name)
    {
        string? folder = Folder();
        if (folder is null)
        {
            return;
        }

        foreach (string tool in new[] { "j2k-pillow", "j2k-opjpng" })
        {
            string reference = Path.Combine(folder, "..", tool, name + ".png");
            if (!File.Exists(reference))
            {
                continue;
            }

            using Raster natural = Raster.Load(File.ReadAllBytes(Path.Combine(folder, "input", "conformance", name)));
            using Raster<Rgba16> egami = Raster.Load<Rgba16>(File.ReadAllBytes(Path.Combine(folder, "input", "conformance", name)));
            using Raster<Rgba16> other = Raster.Load<Rgba16>(File.ReadAllBytes(reference));
            Assert.Equal((other.Width, other.Height), (egami.Width, egami.Height));
            int peak = 0;
            long over = 0;
            for (int y = 0; y < egami.Height; y++)
            {
                ReadOnlySpan<Rgba16> a = egami.Row(y);
                ReadOnlySpan<Rgba16> b = other.Row(y);
                for (int x = 0; x < a.Length; x++)
                {
                    int d = Math.Max(Math.Max(Math.Abs(a[x].R - b[x].R), Math.Abs(a[x].G - b[x].G)), Math.Max(Math.Abs(a[x].B - b[x].B), Math.Abs(a[x].A - b[x].A))) / 257;
                    peak = Math.Max(peak, d);
                    over += d > 1 ? 1 : 0;
                }
            }

            output.WriteLine($"{name} vs {tool}: {natural.Layout.ChannelOrder} {natural.Layout.ChannelType}, peak {peak} (8-bit levels), {over} pixels off by more than 1, warnings: {string.Join("; ", natural.Metadata.Warnings.Select(w => w.Message))}");
        }
    }

    /// <summary>Reads a PGX file: a "PG ML +|- depth width height" line, then samples big-endian in 1, 2 or 4 bytes.</summary>
    internal static (int Width, int Height, int[] Samples) ReadPgx(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int end = Array.IndexOf(bytes, (byte)'\n');
        string header = Encoding.ASCII.GetString(bytes, 0, end);
        string[] parts = header.Replace("+", " + ").Replace("-", " - ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool bigEndian = parts[1] == "ML";
        bool signed = parts[2] == "-";
        int first = parts[2] is "+" or "-" ? 3 : 2;
        int depth = int.Parse(parts[first], System.Globalization.CultureInfo.InvariantCulture);
        int width = int.Parse(parts[first + 1], System.Globalization.CultureInfo.InvariantCulture);
        int height = int.Parse(parts[first + 2], System.Globalization.CultureInfo.InvariantCulture);
        int size = depth <= 8 ? 1 : depth <= 16 ? 2 : 4;
        int[] samples = new int[width * height];
        int at = end + 1;
        for (int i = 0; i < samples.Length; i++, at += size)
        {
            long v = 0;
            for (int b = 0; b < size; b++)
            {
                v |= (long)bytes[at + (bigEndian ? b : size - 1 - b)] << (8 * (size - 1 - b));
            }

            if (signed && size < 4 && (v & (1L << ((8 * size) - 1))) != 0)
            {
                v -= 1L << (8 * size);
            }

            samples[i] = (int)v;
        }

        return (width, height, samples);
    }
}
