using Egami.Fuzz;

namespace Egami.Drawing.Tests.Tracing;

/// <summary>
/// Runs the trace fuzz target (tests/Egami.Fuzz/TraceFuzzTarget.cs) in process: its seeds, among them inputs AFL++ found whose
/// curves reach past the image by a restored corner and the fit tolerance, then random mutations of the seeds' options and pixels.
/// </summary>
public sealed class TraceFuzzHarnessTests
{
    private static List<byte[]> Seeds()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "Egami.slnx")))
        {
            dir = dir.Parent;
        }

        string folder = System.IO.Path.Combine(dir!.FullName, "tests", "images", "fuzz", "trace-seeds");
        return [.. Directory.GetFiles(folder).Order(StringComparer.Ordinal).Select(File.ReadAllBytes)];
    }

    [Fact]
    public void SeedsRun()
    {
        List<byte[]> seeds = Seeds();
        Assert.True(seeds.Count >= 10);
        foreach (byte[] seed in seeds)
        {
            TraceFuzzTarget.Run(seed);
        }
    }

    [Fact]
    public void RandomMutationsNeverCrash()
    {
        List<byte[]> seeds = Seeds();
        Random random = new(2066);
        for (int iteration = 0; iteration < 200; iteration++)
        {
            byte[] input = [.. seeds[random.Next(seeds.Count)]];

            // New options, and a few changed bytes past the PNG header (a damaged image fails to load, which is fine).
            random.NextBytes(input.AsSpan(0, 8));
            for (int e = random.Next(4); e > 0; e--)
            {
                int at = 8 + 33 + random.Next(Math.Max(1, input.Length - 41));
                if (at < input.Length)
                {
                    input[at] = (byte)random.Next(256);
                }
            }

            try
            {
                TraceFuzzTarget.Run(input);
            }
            catch (Exception e)
            {
                Assert.Fail($"Iteration {iteration} threw {e.GetType().Name}: {e.Message}\nInput (base64): {Convert.ToBase64String(input)}\n{e.StackTrace}");
            }
        }
    }
}
