namespace Egami.Formats.Extended.Tests.JpegLs;

/// <summary>
/// The ITU-T T.87 conformance streams (annex E) decode to their source images: exactly when lossless, within NEAR = 3 when not.
/// They are fetched into .cache/jpegls by tools/make-jpegls-references.py and read when EGAMI_CORPUS names jpegls.
/// </summary>
public sealed class JpegLsConformanceTests
{
    public static TheoryData<string, string, int> Streams => new()
    {
        { "t8c0e0.jls", "test8.ppm", 0 },
        { "t8c0e3.jls", "test8.ppm", 3 },
        { "t8c1e0.jls", "test8.ppm", 0 },
        { "t8c1e3.jls", "test8.ppm", 3 },
        { "t8c2e0.jls", "test8.ppm", 0 },
        { "t8c2e3.jls", "test8.ppm", 3 },
        { "t16e0.jls", "test16.pgm", 0 },
        { "t16e3.jls", "test16.pgm", 3 },
        { "t8nde0.jls", "test8bs2.pgm", 0 },
        { "t8nde3.jls", "test8bs2.pgm", 3 },
    };

    [Theory]
    [MemberData(nameof(Streams))]
    public void DecodesToSource(string stream, string source, int near)
    {
        string? folder = JpegLsTesting.Conformance;
        Assert.SkipWhen(folder is null, "Set EGAMI_CORPUS=jpegls and run tools/make-jpegls-references.py.");
        (_, ushort[][] planes, List<string> warnings) = JpegLsTesting.Decode(File.ReadAllBytes(Path.Combine(folder, stream)));
        (_, _, int channels, _, ushort[] expected) = JpegLsTesting.ReadPnm(Path.Combine(folder, source));
        Assert.Empty(warnings);
        Assert.Equal(channels, planes.Length);
        for (int c = 0; c < channels; c++)
        {
            int error = JpegLsTesting.MaxError(JpegLsTesting.Component(expected, channels, c), planes[c]);
            Assert.True(error <= near, $"component {c} is off by {error}");
        }
    }

    /// <summary>The subsampled streams: red full size, green a quarter as tall, blue half as wide and tall, line-interleaved.</summary>
    [Theory]
    [InlineData("t8sse0.jls", 0)]
    [InlineData("t8sse3.jls", 3)]
    public void DecodesSubsampled(string stream, int near)
    {
        string? folder = JpegLsTesting.Conformance;
        Assert.SkipWhen(folder is null, "Set EGAMI_CORPUS=jpegls and run tools/make-jpegls-references.py.");
        (_, ushort[][] planes, List<string> warnings) = JpegLsTesting.Decode(File.ReadAllBytes(Path.Combine(folder, stream)));
        Assert.Empty(warnings);
        string[] sources = ["test8r.pgm", "test8gr4.pgm", "test8bs2.pgm"];
        for (int c = 0; c < 3; c++)
        {
            ushort[] expected = JpegLsTesting.ReadPnm(Path.Combine(folder, sources[c])).Samples;
            Assert.True(JpegLsTesting.MaxError(expected, planes[c]) <= near, $"component {c}");
        }
    }

    /// <summary>The conformance streams load through the format: the RGB ones as Rgb8, the 12-bit one as Gray16.</summary>
    [Theory]
    [InlineData("t8c1e0.jls")]
    [InlineData("t16e0.jls")]
    [InlineData("t8sse0.jls")]
    public void Loads(string stream)
    {
        string? folder = JpegLsTesting.Conformance;
        Assert.SkipWhen(folder is null, "Set EGAMI_CORPUS=jpegls and run tools/make-jpegls-references.py.");
        using Raster raster = Raster.Load(Path.Combine(folder, stream), TestImages.Context);
        Assert.Equal(256, raster.Width);
        Assert.Equal(256, raster.Height);
        Assert.Empty(raster.Metadata.Warnings);
    }
}
