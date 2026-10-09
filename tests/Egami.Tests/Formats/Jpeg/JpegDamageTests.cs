using System.Diagnostics;

using Egami.Formats;
using Egami.Formats.Jpeg;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jpeg;

/// <summary>
/// Damaged copies of every corpus file (JPEG design, Testing): truncated, bit-flipped and with markers taken out. Lenient
/// reading returns pixels or throws an <see cref="EgamiException"/>, strict reading throws <see cref="CorruptDataException"/> or
/// loads, and nothing else is ever thrown; no input may take long.
/// </summary>
public class JpegDamageTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "jpeg");

    private static readonly EgamiContext Context = EgamiContext.Default with
    {
        MaxParallelism = 1,
        Limits = SafetyLimits.Default with { MaxPixels = 4_000_000, MaxTotalBytes = 256L * 1024 * 1024 },
    };

    public static TheoryData<string> Files() => [.. Directory.GetFiles(Images, "*.jpg").Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Files))]
    public void DamagedCopiesFailOnlyWithEgamiExceptions(string file)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Images, file));
        Random random = new(file.GetHashCode(StringComparison.Ordinal));
        foreach (byte[] damaged in Damage(data, random))
        {
            foreach (ReaderOptions options in (ReaderOptions[])[new() { Recovery = RecoveryMode.Lenient }, new() { Recovery = RecoveryMode.Strict }, new() { TargetSize = new Size(8, 8) }])
            {
                Stopwatch clock = Stopwatch.StartNew();
                try
                {
                    _ = Raster.Probe(damaged, Context, options);
                    using Raster raster = Raster.Load(damaged, Context, options);
                    using Raster<Rgba8> converted = raster.As<Rgba8>();
                    converted.Save(Stream.Null, JpegFormat.Info);
                }
                catch (EgamiException)
                {
                }

                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"{file}: a damaged copy took {clock.Elapsed.TotalSeconds:F1} s");
            }
        }
    }

    [Fact]
    public void FilesWithoutAnyMarkersAreRejected()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Images, "duck-420.jpg"));
        byte[] stripped = [.. data.Where(b => b != 0xFF)];
        Assert.ThrowsAny<EgamiException>(() => Raster.Load(stripped, Context));
        Assert.ThrowsAny<EgamiException>(() => Raster.Load(data.AsMemory(0, 2), Context));
        Assert.ThrowsAny<EgamiException>(() => Raster.Load(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }, Context));
    }

    /// <summary>Truncations, bit flips, removed markers, wrong segment lengths and spliced-in garbage.</summary>
    private static IEnumerable<byte[]> Damage(byte[] data, Random random)
    {
        for (int i = 1; i <= 8; i++)
        {
            yield return data[..(data.Length * i / 9)];
        }

        for (int i = 0; i < 24; i++)
        {
            byte[] copy = (byte[])data.Clone();
            for (int n = random.Next(1, 8); n > 0; n--)
            {
                copy[random.Next(copy.Length)] ^= (byte)(1 << random.Next(8));
            }

            yield return copy;
        }

        // Each marker in turn taken out (its two bytes), and each segment length set to 0, 2 and 0xFFFF.
        List<int> markers = [.. Enumerable.Range(0, data.Length - 1).Where(i => data[i] == 0xFF && data[i + 1] is not (0x00 or 0xFF) and not (>= 0xD0 and <= 0xD7))];
        foreach (int at in markers.Take(24))
        {
            yield return [.. data[..at], .. data[(at + 2)..]];
            if (at + 3 < data.Length && data[at + 1] is not (0xD8 or 0xD9))
            {
                foreach (ushort length in (ushort[])[0, 2, 0xFFFF])
                {
                    byte[] copy = (byte[])data.Clone();
                    copy[at + 2] = (byte)(length >> 8);
                    copy[at + 3] = (byte)length;
                    yield return copy;
                }
            }
        }

        for (int i = 0; i < 8; i++)
        {
            byte[] garbage = new byte[random.Next(1, 200)];
            random.NextBytes(garbage);
            int at = random.Next(data.Length);
            yield return [.. data[..at], .. garbage, .. data[at..]];
        }
    }
}
