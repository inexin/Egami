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
        if (!LocalCorpus.Enabled("jpeg2000"))
        {
            return null;
        }

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
            throw LocalCorpus.Skip("jpeg2000");
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

    /// <summary>
    /// Files Pillow (OpenJPEG 2.5.4 inside) reads differently by design: file2 orders its channels by its channel definitions,
    /// which Pillow ignores, and file6 is 12-bit gray, which Pillow clips converting its 16-bit mode to RGBA.
    /// </summary>
    private static readonly HashSet<string> PillowDiffers = ["file2.jp2", "file6.jp2"];

    /// <summary>
    /// Every JP2 file within 1 level of Pillow's decode (references made in Docker under .cache/j2k-pillow), or 4 for sYCC:
    /// chroma is repeated as OpenJPEG does, but OpenJPEG truncates where Egami rounds, and a lossy file's components, each within
    /// 1 of OpenJPEG's, combine in R = Y + 1.402 Cr. Pixels are as stored: ICC profiles stay in the metadata, as Pillow leaves
    /// them.
    /// </summary>
    [Theory]
    [MemberData(nameof(Jp2Files))]
    public void Jp2FilesMatchPillow(string name)
    {
        string? folder = Folder();
        string reference = Path.Combine(folder ?? "", "..", "j2k-pillow", name + ".png");
        if (folder is null)
        {
            throw LocalCorpus.Skip("jpeg2000");
        }

        if (!File.Exists(reference) || PillowDiffers.Contains(name))
        {
            return;
        }

        byte[] data = File.ReadAllBytes(Path.Combine(folder, "input", "conformance", name));
        using Raster<Rgba8> egami = Raster.Load<Rgba8>(data, options: new Jpeg2000ReaderOptions { FastUpsampling = true });
        using Raster<Rgba8> pillow = Raster.Load<Rgba8>(File.ReadAllBytes(reference));
        Assert.Equal((pillow.Width, pillow.Height), (egami.Width, egami.Height));
        int peak = 0;
        for (int y = 0; y < egami.Height; y++)
        {
            ReadOnlySpan<Rgba8> a = egami.Row(y);
            ReadOnlySpan<Rgba8> b = pillow.Row(y);
            for (int x = 0; x < a.Length; x++)
            {
                peak = Math.Max(peak, Math.Max(Math.Max(Math.Abs(a[x].R - b[x].R), Math.Abs(a[x].G - b[x].G)), Math.Max(Math.Abs(a[x].B - b[x].B), Math.Abs(a[x].A - b[x].A))));
            }
        }

        output.WriteLine($"{name}: peak {peak}");
        bool sycc = egami.Metadata.FormatDetails is Jpeg2000Details { ColorSpace: Jpeg2000ColorSpace.Sycc };
        Assert.True(peak <= (sycc ? 4 : 1), $"{name} differs from Pillow by {peak} levels.");
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
