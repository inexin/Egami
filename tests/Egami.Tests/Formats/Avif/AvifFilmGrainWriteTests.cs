using Egami.Formats.Avif;
using Egami.Pixels;

namespace Egami.Tests.Formats.Avif;

/// <summary>
/// Film grain on AVIF encode (task 9): FilmGrainDenoise takes grain out and writes AV1 film grain parameters that put it back, a
/// clean picture gets none, and a raster loaded without its grain gets the same parameters written back on save.
/// </summary>
public class AvifFilmGrainWriteTests
{
    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "images", "avif");
    private static readonly string PhotoPath = Path.Combine(AppContext.BaseDirectory, "images", "jpeg", "duck-q100.jpg");

    [Fact]
    public void GrainLoadedWithoutItIsWrittenBack()
    {
        using Raster clean = Raster.Load(Path.Combine(Corpus, "duck-grain.avif"), options: new AvifReaderOptions { FilmGrain = false });
        AvifFilmGrain original = ((AvifDetails)clean.Metadata.FormatDetails!).FilmGrain!;
        byte[] file = AvifWriterTests.Save(clean, new AvifWriterOptions { Quality = 80 });
        using Raster again = Raster.Load(file, options: new AvifReaderOptions { FilmGrain = false });
        AvifDetails details = (AvifDetails)again.Metadata.FormatDetails!;
        Assert.True(details.HasFilmGrain);
        AssertSame(original, details.FilmGrain!);

        // Loaded with its grain added, the pixels carry it already: none is written.
        using Raster grained = Raster.Load(Path.Combine(Corpus, "duck-grain.avif"));
        using Raster plain = Raster.Load(AvifWriterTests.Save(grained, new AvifWriterOptions { Quality = 80 }));
        Assert.Null(((AvifDetails)plain.Metadata.FormatDetails!).FilmGrain);
    }

    [Fact]
    public void DenoisingWritesGrainAndASmallerFile()
    {
        using Raster<Rgb8> noisy = Noisy(256, 192, 6);
        byte[] plain = AvifWriterTests.Save(noisy, new AvifWriterOptions { Quality = 70 });
        byte[] denoised = AvifWriterTests.Save(noisy, new AvifWriterOptions { Quality = 70, FilmGrainDenoise = 25 });
        Assert.True(denoised.Length < plain.Length * 0.8, $"{denoised.Length} bytes denoised, {plain.Length} plain");

        using Raster loaded = Raster.Load(denoised, options: new AvifReaderOptions { FilmGrain = false });
        AvifFilmGrain grain = ((AvifDetails)loaded.Metadata.FormatDetails!).FilmGrain!;
        Assert.NotEmpty(grain.ScalingPointsY);
        Assert.Equal(3, grain.ArCoeffLag);

        // With the grain added back the picture is about as noisy as the source; without it, much smoother.
        using Raster<Rgb8> withGrain = Raster.Load<Rgb8>(denoised);
        using Raster<Rgb8> without = loaded.As<Rgb8>();
        double source = Roughness(noisy), synthesized = Roughness(withGrain), smooth = Roughness(without);
        Assert.True(smooth < source * 0.6, $"denoised roughness {smooth:F2}, source {source:F2}");
        Assert.InRange(synthesized, source * 0.5, source * 1.6);
    }

    [Fact]
    public void ACleanPictureGetsNoGrain()
    {
        using Raster<Rgb8> clean = AvifWriterTests.Pattern<Rgb8>(256, 192, (x, y) => new Rgb8((byte)x, (byte)(y + 30), 100));
        byte[] plain = AvifWriterTests.Save(clean, new AvifWriterOptions { Quality = 70 });
        byte[] denoised = AvifWriterTests.Save(clean, new AvifWriterOptions { Quality = 70, FilmGrainDenoise = 25 });
        Assert.Equal(plain, denoised);
    }

    private static void AssertSame(AvifFilmGrain expected, AvifFilmGrain actual)
    {
        Assert.Equal(expected.RandomSeed, actual.RandomSeed);
        Assert.Equal(expected.ScalingPointsY, actual.ScalingPointsY);
        Assert.Equal(expected.ScalingPointsCb, actual.ScalingPointsCb);
        Assert.Equal(expected.ScalingPointsCr, actual.ScalingPointsCr);
        Assert.Equal(expected.ScalingShift, actual.ScalingShift);
        Assert.Equal(expected.ArCoeffLag, actual.ArCoeffLag);
        Assert.Equal(expected.ArCoeffsY, actual.ArCoeffsY);
        Assert.Equal(expected.ArCoeffsCb, actual.ArCoeffsCb);
        Assert.Equal(expected.ArCoeffsCr, actual.ArCoeffsCr);
        Assert.Equal(expected.ArCoeffShift, actual.ArCoeffShift);
        Assert.Equal((expected.Cb, expected.Cr, expected.Overlap), (actual.Cb, actual.Cr, actual.Overlap));
    }

    /// <summary>A photo with Gaussian noise of <paramref name="sigma"/> levels added, the same every run.</summary>
    private static Raster<Rgb8> Noisy(int width, int height, double sigma)
    {
        using Raster<Rgb8> photo = Raster.Load<Rgb8>(PhotoPath);
        using Raster<Rgb8> small = photo.Derive(r => r.Resize(width, height), TestContext.Current.CancellationToken);
        Random random = new(12345);
        return AvifWriterTests.Pattern<Rgb8>(width, height, (x, y) =>
        {
            Rgb8 p = small.Row(y)[x];
            double n = sigma * Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
            return new Rgb8(Clamp(p.R + n), Clamp(p.G + n), Clamp(p.B + n));
        });

        static byte Clamp(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
    }

    /// <summary>The mean absolute difference between horizontal neighbors' green, which noise raises.</summary>
    private static double Roughness(Raster<Rgb8> image)
    {
        double sum = 0;
        for (int y = 0; y < image.Height; y++)
        {
            ReadOnlySpan<Rgb8> row = image.Row(y);
            for (int x = 1; x < row.Length; x++)
            {
                sum += Math.Abs(row[x].G - row[x - 1].G);
            }
        }

        return sum / (image.Height * (image.Width - 1));
    }
}
