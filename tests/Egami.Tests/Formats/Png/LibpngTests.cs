using System.Diagnostics;
using System.Runtime.InteropServices;

using Egami.Formats;
using Egami.Formats.Png;
using Egami.Pixels;

namespace Egami.Tests.Formats.Png;

/// <summary>
/// Checks Egami against libpng, the reference PNG library. These tests need tools that only the CI "libpng" job installs:
/// EGAMI_LIBPNG_DECODER (tools/libpng-check/pngdecode.c, built against libpng) and EGAMI_PNGCHECK (pngcheck).
/// Without them the tests are skipped, unless EGAMI_REQUIRE_LIBPNG=1, when they fail.
/// </summary>
public sealed class LibpngTests : IDisposable
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "pngsuite");
    private static readonly ReaderOptions Strict = new() { Recovery = RecoveryMode.Strict };
    private readonly string _directory = Directory.CreateTempSubdirectory("egami-libpng-").FullName;

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    /// <summary>Every valid PngSuite file decodes to exactly libpng's 16-bit pixels.</summary>
    [Fact]
    public void PngSuiteMatchesLibpngExactly()
    {
        string decoder = Tool("EGAMI_LIBPNG_DECODER");
        List<string> failures = [];
        foreach (string path in Directory.GetFiles(Images, "*.png").Where(p => !Path.GetFileName(p).StartsWith('x')).Order(StringComparer.Ordinal))
        {
            using Raster<Rgba16> egami = Raster.Load<Rgba16>(path, options: Strict);
            if (!Matches(decoder, path, egami, out string problem))
            {
                failures.Add($"{Path.GetFileName(path)}: {problem}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// Files Egami writes pass pngcheck and decode in libpng to exactly the pixels Egami reads back, and lossless forms
    /// to exactly the source pixels.
    /// </summary>
    [Fact]
    public void FilesEgamiWritesAreValidForLibpng()
    {
        string decoder = Tool("EGAMI_LIBPNG_DECODER");
        string pngcheck = Tool("EGAMI_PNGCHECK");
        (string Name, PngWriterOptions Options, bool Lossless)[] forms =
        [
            ("default", new PngWriterOptions(), true),
            ("reduce", new PngWriterOptions { Reduce = true }, true),
            ("rgba16", new PngWriterOptions { ColorType = PngColorType.Rgba, BitDepth = 16 }, true),
            ("level0", new PngWriterOptions { CompressionLevel = 0, MaxIdatSize = 4096 }, true),
            ("paeth9", new PngWriterOptions { Filter = PngFilterMode.Paeth, CompressionLevel = 9 }, true),
            ("rgb8", new PngWriterOptions { ColorType = PngColorType.Rgb }, false),
            ("gray2", new PngWriterOptions { ColorType = PngColorType.Gray, BitDepth = 2 }, false),
            ("grayAlpha16", new PngWriterOptions { ColorType = PngColorType.GrayAlpha, BitDepth = 16 }, false),
            ("interlace", new PngWriterOptions { Interlace = true }, true),
            ("interlace-reduce", new PngWriterOptions { Interlace = true, Reduce = true }, true),
            ("interlace-gray2", new PngWriterOptions { Interlace = true, ColorType = PngColorType.Gray, BitDepth = 2 }, false),
            ("parallel", new PngWriterOptions { ParallelCompression = true, ParallelThreshold = 0, ParallelBandSize = 1000 }, true),
            ("interlace-parallel", new PngWriterOptions { Interlace = true, Reduce = true, ParallelCompression = true, ParallelThreshold = 0, ParallelBandSize = 300 }, true),
        ];

        List<string> failures = [];
        foreach (string source in Directory.GetFiles(Images, "*.png").Where(p => !Path.GetFileName(p).StartsWith('x')).Order(StringComparer.Ordinal))
        {
            // pngcheck rejects a few valid PngSuite files outright (cm7n0g04 has a tIME year of 1970), and Egami copies such
            // chunks unchanged, so pngcheck is only enforced on files written from sources it accepts.
            bool sourcePassesPngcheck = Run(pngcheck, "-q", source).ExitCode == 0;
            using Raster original = Raster.Load(source, options: Strict);
            using Raster<Rgba16> expected = original.As<Rgba16>();
            foreach ((string name, PngWriterOptions options, bool lossless) in forms)
            {
                string path = Path.Combine(_directory, $"{Path.GetFileNameWithoutExtension(source)}-{name}.png");
                using (FileStream file = File.Create(path))
                {
                    original.Save(file, PngFormat.Info, options);
                }

                string label = $"{Path.GetFileName(source)} as {name}";
                (int exit, string output) = Run(pngcheck, "-q", path);
                if (exit != 0 && sourcePassesPngcheck)
                {
                    failures.Add($"{label}: pngcheck: {output.Trim()}");
                    continue;
                }

                using Raster<Rgba16> readBack = Raster.Load<Rgba16>(path, options: Strict);
                if (!Matches(decoder, path, readBack, out string problem))
                {
                    failures.Add($"{label}: {problem}");
                }
                else if (lossless && !SamePixels(expected, readBack))
                {
                    failures.Add($"{label}: the written file does not hold the source pixels");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>Returns the path of a CI tool from an environment variable, or skips the test when it is not set.</summary>
    private static string Tool(string variable)
    {
        string? path = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrEmpty(path))
        {
            return path;
        }

        string message = $"{variable} is not set; this test runs in the CI libpng job.";
        if (Environment.GetEnvironmentVariable("EGAMI_REQUIRE_LIBPNG") == "1")
        {
            Assert.Fail(message);
        }

        Assert.Skip(message);
        return string.Empty;
    }

    /// <summary>Decodes <paramref name="path"/> with libpng and compares it with Egami's pixels.</summary>
    private bool Matches(string decoder, string path, Raster<Rgba16> egami, out string problem)
    {
        string output = Path.Combine(_directory, Path.GetFileName(path) + ".rgba16");
        (int exit, string message) = Run(decoder, path, output);
        if (exit != 0)
        {
            problem = $"libpng could not decode it (exit {exit}): {message.Trim()}";
            return false;
        }

        ReadOnlySpan<Rgba16> reference = MemoryMarshal.Cast<byte, Rgba16>(File.ReadAllBytes(output));
        if (reference.Length != egami.Width * egami.Height)
        {
            problem = $"libpng gave {reference.Length} pixels, Egami {egami.Width * egami.Height}";
            return false;
        }

        for (int y = 0; y < egami.Height; y++)
        {
            ReadOnlySpan<Rgba16> expected = reference.Slice(y * egami.Width, egami.Width);
            Span<Rgba16> actual = egami.Row(y);
            for (int x = 0; x < egami.Width; x++)
            {
                if (expected[x] != actual[x])
                {
                    problem = $"pixel ({x}, {y}) is {actual[x]} in Egami and {expected[x]} in libpng";
                    return false;
                }
            }
        }

        problem = string.Empty;
        return true;
    }

    private static bool SamePixels(Raster<Rgba16> a, Raster<Rgba16> b)
    {
        for (int y = 0; y < a.Height; y++)
        {
            if (!a.Row(y).SequenceEqual(b.Row(y)))
            {
                return false;
            }
        }

        return true;
    }

    private static (int ExitCode, string Output) Run(string tool, params string[] arguments)
    {
        ProcessStartInfo start = new(tool) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }
}
