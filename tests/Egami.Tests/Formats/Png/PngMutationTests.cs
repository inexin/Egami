using Egami.Formats;

namespace Egami.Tests.Formats.Png;

/// <summary>
/// A quick in-process fuzz pass that runs with every test run: thousands of randomly damaged PngSuite files must each load,
/// or fail with an Egami exception, never anything else. The nightly fuzz job (tests/Egami.Fuzz) goes much deeper.
/// </summary>
public class PngMutationTests
{
    /// <summary>Mutations per seed file. EGAMI_MUTATIONS raises it for a longer local run.</summary>
    private static readonly int MutationsPerFile = int.TryParse(Environment.GetEnvironmentVariable("EGAMI_MUTATIONS"), out int n) ? n : 120;

    private static readonly EgamiContext Context = EgamiContext.Default with
    {
        Limits = SafetyLimits.Default with { MaxPixels = 4_000_000, MaxTotalBytes = 256L * 1024 * 1024, MaxMetadataBytes = 1024 * 1024 },
    };

    public static TheoryData<string> Seeds()
    {
        return [.. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "images", "pngsuite"), "*.png").Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal)];
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void DamagedFilesFailOnlyWithEgamiExceptions(string seed)
    {
        byte[] original = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "images", "pngsuite", seed));
        // A fixed seed per file, so a failure reproduces on every run and machine.
        Random random = new(seed.Aggregate(17, (hash, c) => unchecked((hash * 31) + c)));
        for (int i = 0; i < MutationsPerFile; i++)
        {
            byte[] data = Mutate(original, random);
            foreach (ReaderOptions options in new[] { ReaderOptions.Default, new ReaderOptions { Recovery = RecoveryMode.Strict } })
            {
                try
                {
                    using Raster raster = Raster.Load(data, Context, options);
                    _ = raster.As<Egami.Pixels.Rgba8>();
                    Raster.Probe(data, Context);
                }
                catch (EgamiException)
                {
                }
                catch (Exception e)
                {
                    string path = Path.Combine(Path.GetTempPath(), $"egami-crash-{seed}-{i}.png");
                    File.WriteAllBytes(path, data);
                    Assert.Fail($"Mutation {i} of {seed} ({options.Recovery}) threw {e.GetType().Name}: {e.Message}. Saved to {path}.{Environment.NewLine}{e.StackTrace}");
                }
            }
        }
    }

    /// <summary>Flips bits, overwrites bytes with interesting values, cuts, or duplicates a range.</summary>
    internal static byte[] Mutate(byte[] original, Random random)
    {
        byte[] data = (byte[])original.Clone();
        int edits = 1 + random.Next(4);
        for (int e = 0; e < edits && data.Length > 0; e++)
        {
            int at = random.Next(data.Length);
            switch (random.Next(6))
            {
                case 0:
                    data[at] ^= (byte)(1 << random.Next(8));
                    break;
                case 1:
                    data[at] = (byte)random.Next(256);
                    break;
                case 2:
                    byte[] interesting = [0x00, 0x01, 0x7F, 0x80, 0xFF];
                    data[at] = interesting[random.Next(interesting.Length)];
                    break;
                case 3:
                    // Damage a 4-byte big-endian field, such as a chunk length, width or height.
                    for (int k = 0; k < 4 && at + k < data.Length; k++)
                    {
                        data[at + k] = random.Next(2) == 0 ? (byte)0xFF : (byte)random.Next(256);
                    }

                    break;
                case 4:
                    data = data[..at];
                    break;
                default:
                    int length = Math.Min(random.Next(1, 64), data.Length - at);
                    data = [.. data[..(at + length)], .. data[at..]];
                    break;
            }
        }

        return data;
    }
}
