using Egami.Formats.Jpeg2000;

namespace Egami.Tests.Formats.Jpeg2000;

/// <summary>
/// The ISO/IEC 15444-4 HTJ2K conformance set (https://gitlab.com/wg1/htj2k-codestreams, kept out of the repo under
/// .cache/htj2k-codestreams; its licence allows use for conformance testing only). Each HT codestream there is a transcode of a
/// Part 1 conformance codestream, so it decodes to the same samples as its source when its bit-depth B holds every magnitude,
/// and to no more error than the set's own statistics give when B cuts magnitudes short. Where OpenJPEG decodes a file
/// (.cache/htj2k-opj), Egami matches it: reversible exactly, irreversible within 1. The JPH files' codestreams match OpenJPEG's
/// decode, and file10 the set's references.
/// </summary>
public class HtJ2kConformanceTests(ITestOutputHelper output)
{
    /// <summary>
    /// The transcodes whose bit-depth B is too small for some magnitudes, with the mean squared error per component the
    /// set's stats.txt gives against the source (a component not listed is exact). Egami's error must not exceed it by more
    /// than 10 % (and 0.0005, for the tiny ones): the irreversible sources of profile 1 round a little differently. For p0_03
    /// and p0_15 the stats are of 8-bit PGM output, which adds clipping Egami's samples don't have.
    /// </summary>
    private static readonly Dictionary<string, double[]> Truncated = new()
    {
        ["ds0_ht_02_b11"] = [0.000992],
        ["ds0_ht_03_b11"] = [27.726566],
        ["ds0_ht_04_b11"] = [0.059733, 0.053141, 0.054290],
        ["ds0_ht_05_b11"] = [0, 0.000081, 0.000011, 0.000111],
        ["ds0_hm_06_b11"] = [0, 0, 0, 1677.825416],
        ["ds0_ht_06_b11"] = [49765.935365, 0, 0, 1677.825416],
        ["ds0_ht_06_b15"] = [0, 0, 0, 6.659204],
        ["ds0_ht_07_b11"] = [18.864944, 17.385110, 18.595790],
        ["ds0_ht_07_b15"] = [0.047184, 0.020661, 0.045872],
        ["ds0_ht_08_b11"] = [35.281310, 20.025873, 33.243848],
        ["ds0_ht_08_b15"] = [0, 0.000466, 0],
        ["ds0_ht_15_b11"] = [27.726566],
        ["ds1_ht_01_b11"] = [0.000331],
        ["ds1_ht_02_b11"] = [0.062126, 0.054938, 0.056699],
        ["ds1_ht_03_b11"] = [0, 0.016738, 0.025322, 0.000111],
    };

    internal static string? Folder()
    {
        if (!LocalCorpus.Enabled("htj2k"))
        {
            return null;
        }

        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, ".cache", "htj2k-codestreams");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static TheoryData<string> Transcodes()
    {
        string? folder = Folder();
        return folder is null
            ? ["(none)"]
            : [.. Directory.GetFiles(folder, "*.j2k", SearchOption.AllDirectories)
                .Where(f => Path.GetFileName(Path.GetDirectoryName(f)!).EndsWith("_bset", StringComparison.Ordinal))
                .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))
                .Order(StringComparer.Ordinal)];
    }

    [Theory]
    [MemberData(nameof(Transcodes))]
    public void TranscodesMatchTheirSources(string path)
    {
        string? folder = Folder();
        if (folder is null)
        {
            throw LocalCorpus.Skip("htj2k");
        }

        // htj2k_bsets_profile0/p0_06_bset/ds0_ht_06_b11.j2k is a transcode of codestreams_profile0/p0_06.j2k.
        string name = Path.GetFileNameWithoutExtension(path);
        string set = Path.GetFileName(Path.GetDirectoryName(path)!);
        string profile = path.Split('/')[0][^1..];
        J2kImage ht = Decode(File.ReadAllBytes(Path.Combine(folder, path)), out J2kCodestream stream);
        Assert.True(stream.HighThroughput);
        J2kImage source = Decode(File.ReadAllBytes(Path.Combine(folder, $"codestreams_profile{profile}", set[..^"_bset".Length] + ".j2k")), out _);
        Assert.Equal(source.Planes.Length, ht.Planes.Length);
        double[]? bounds = Truncated.GetValueOrDefault(name);
        for (int c = 0; c < ht.Planes.Length; c++)
        {
            Assert.Equal((source.Widths[c], source.Heights[c]), (ht.Widths[c], ht.Heights[c]));
            int count = ht.Widths[c] * ht.Heights[c];
            long peak = 0;
            double squares = 0;
            for (int i = 0; i < count; i++)
            {
                long d = Math.Abs((long)source.Planes[c][i] - ht.Planes[c][i]);
                peak = Math.Max(peak, d);
                squares += (double)d * d;
            }

            double mse = squares / count;
            double bound = bounds is null || c >= bounds.Length ? 0 : bounds[c];
            if (bound > 0)
            {
                output.WriteLine($"{name} component {c}: peak {peak}, MSE {mse:F6}, the set's MSE {bound:F6}");
            }

            Assert.True(mse <= (bound * 1.1) + 0.0005, $"{name} component {c}: peak {peak}, MSE {mse:F6} against at most {bound:F6}.");
        }

        CompareWithOpenJpeg(folder, name, ht, stream.MainCod.Reversible);
    }

    public static TheoryData<string> JphFiles()
    {
        string? folder = Folder();
        return folder is null
            ? ["(none)"]
            : [.. Directory.GetFiles(Path.Combine(folder, "testfiles_jph"), "*.jph").Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal)];
    }

    [Theory]
    [MemberData(nameof(JphFiles))]
    public void JphFilesMatch(string name)
    {
        string? folder = Folder();
        if (folder is null)
        {
            throw LocalCorpus.Skip("htj2k");
        }

        byte[] bytes = File.ReadAllBytes(Path.Combine(folder, "testfiles_jph", name));
        Jp2File file = Jp2File.Parse(bytes);
        Assert.Equal(Jpeg2000Container.Jph, file.Container);
        J2kImage image = Decode(file.Codestream, out J2kCodestream stream);
        string stem = Path.GetFileNameWithoutExtension(name);
        CompareWithOpenJpeg(folder, stem, image, stream.MainCod.Reversible);

        // file10_b11 is jph_10 of reference_jph, one PGM per component.
        string number = stem[4..stem.IndexOf('_', StringComparison.Ordinal)];
        for (int c = 0; c < image.Planes.Length; c++)
        {
            string reference = Path.Combine(folder, "reference_jph", $"jph_{number}_{c}.pgm");
            if (File.Exists(reference))
            {
                (int width, int height, int[] samples) = ReadPgm(reference);
                Assert.Equal((width, height), (image.Widths[c], image.Heights[c]));
                int peak = 0;
                for (int i = 0; i < samples.Length; i++)
                {
                    peak = Math.Max(peak, Math.Abs(samples[i] - image.Planes[c][i]));
                }

                // The references are the source image; both bit-depths of file10 decode within 2 of it, in OpenJPEG as in Egami.
                output.WriteLine($"{name} component {c}: peak {peak} against jph_{number}_{c}.pgm");
                Assert.True(peak <= 2, $"{name} component {c}: off jph_{number}_{c}.pgm by {peak}.");
            }
        }

        using Raster raster = Raster.Load(bytes);
        Assert.True(((Jpeg2000Details)raster.Metadata.FormatDetails!).HighThroughput);
    }

    private void CompareWithOpenJpeg(string folder, string stem, J2kImage image, bool reversible)
    {
        string opj = Path.Combine(folder, "..", "htj2k-opj");
        if (!File.Exists(Path.Combine(opj, $"{stem}_0.pgx")))
        {
            output.WriteLine($"{stem}: OpenJPEG does not decode it.");
            return;
        }

        List<(int Width, int Height, int[] Samples)> planes = [];
        for (int c = 0; c < image.Planes.Length; c++)
        {
            (int width, int height, int[] samples) = Jpeg2000ConformanceTests.ReadPgx(Path.Combine(opj, $"{stem}_{c}.pgx"));
            if ((width, height) != (image.Widths[c], image.Heights[c]))
            {
                // opj_decompress converts subsampled three-component codestreams to RGB at full size.
                output.WriteLine($"{stem}: OpenJPEG wrote {width}x{height} for component {c}, not its samples.");
                return;
            }

            planes.Add((width, height, samples));
        }

        for (int c = 0; c < image.Planes.Length; c++)
        {
            int[] samples = planes[c].Samples;
            int peak = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                peak = Math.Max(peak, Math.Abs(samples[i] - image.Planes[c][i]));
            }

            Assert.True(peak <= (reversible ? 0 : 1), $"{stem} component {c}: off OpenJPEG by {peak}.");
        }
    }

    private static J2kImage Decode(ReadOnlyMemory<byte> data, out J2kCodestream stream)
    {
        stream = J2kCodestream.Parse(data, SafetyLimits.Default);
        return new J2kDecoder(stream, 0, 0, Environment.ProcessorCount, default).Decode();
    }

    /// <summary>Reads a binary PGM, 8 or 16 bits.</summary>
    private static (int Width, int Height, int[] Samples) ReadPgm(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int at = 2;
        int[] header = new int[3];
        for (int i = 0; i < 3; i++)
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

            while (char.IsAsciiDigit((char)bytes[at]))
            {
                header[i] = (header[i] * 10) + (bytes[at++] - '0');
            }
        }

        at++;
        int[] samples = new int[header[0] * header[1]];
        bool wide = header[2] > 255;
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = wide ? (bytes[at + (2 * i)] << 8) | bytes[at + (2 * i) + 1] : bytes[at + i];
        }

        return (header[0], header[1], samples);
    }
}
