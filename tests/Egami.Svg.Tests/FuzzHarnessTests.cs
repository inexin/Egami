using System.Text;

using Egami.Fuzz;

namespace Egami.Svg.Tests;

/// <summary>
/// Runs the svg fuzz target's harness (tests/Egami.Fuzz/SvgFuzzTarget.cs) in process: the seeds, then random mutations of them.
/// Anything but an EgamiException from a lenient load fails the test with the input that caused it.
/// </summary>
public sealed class FuzzHarnessTests
{
    private static readonly byte[] s_interesting = Encoding.UTF8.GetBytes("<>/=\"' #():;,.-+e0123456789%urlxyzwhrdMLCZ&;!?[]");

    private static List<byte[]> Seeds()
    {
        string folder = Path.Combine(TestSvg.Root, "tests", "images", "fuzz", "svg-seeds");
        return [.. Directory.GetFiles(folder).Order(StringComparer.Ordinal).Select(File.ReadAllBytes)];
    }

    [Fact]
    public void SeedsRun()
    {
        List<byte[]> seeds = Seeds();
        Assert.True(seeds.Count >= 40);
        foreach (byte[] seed in seeds)
        {
            SvgFuzzTarget.Run(seed);
        }
    }

    [Fact]
    public void RandomMutationsNeverCrash()
    {
        List<byte[]> seeds = Seeds();
        Random random = new(2010);
        for (int iteration = 0; iteration < 1500; iteration++)
        {
            byte[] input = Mutate(seeds[random.Next(seeds.Count)], random);
            try
            {
                SvgFuzzTarget.Run(input);
            }
            catch (Exception e)
            {
                Assert.Fail($"Iteration {iteration} threw {e.GetType().Name}: {e.Message}\nInput (base64): {Convert.ToBase64String(input)}\n{e.StackTrace}");
            }
        }
    }

    private static byte[] Mutate(byte[] seed, Random random)
    {
        List<byte> bytes = [.. seed];
        int edits = random.Next(1, 8);
        for (int e = 0; e < edits && bytes.Count > 0; e++)
        {
            int at = random.Next(bytes.Count);
            switch (random.Next(4))
            {
                case 0:
                    bytes[at] = s_interesting[random.Next(s_interesting.Length)];
                    break;
                case 1:
                    bytes.Insert(at, s_interesting[random.Next(s_interesting.Length)]);
                    break;
                case 2:
                    bytes.RemoveRange(at, Math.Min(random.Next(1, 16), bytes.Count - at));
                    break;
                default:
                    int from = random.Next(bytes.Count), length = Math.Min(random.Next(1, 64), bytes.Count - from);
                    bytes.InsertRange(at, bytes.GetRange(from, length));
                    break;
            }
        }

        return [.. bytes];
    }

    [Fact]
    public void DamagedImageDataIsReportedAtLoadNotThrownWhenDrawing()
    {
        // Found by the harness: a PNG whose IDAT CRC is wrong made drawing throw.
        byte[] png = File.ReadAllBytes(Path.Combine(TestSvg.Root, "tests", "svg", "resvg", "resources", "image.png"));
        int idat = Encoding.ASCII.GetString(png).IndexOf("IDAT", StringComparison.Ordinal);
        png[idat + 10] ^= 0x5A;
        string svg = $"<svg xmlns='http://www.w3.org/2000/svg' width='20' height='20'><image width='20' height='20' href='data:image/png;base64,{Convert.ToBase64String(png)}'/></svg>";
        SvgFuzzTarget.Run(Encoding.UTF8.GetBytes(svg));
        SvgDocument document = SvgDocument.Parse(svg);
        Assert.Contains(document.Warnings, w => w.Id == "Svg.ImageNotLoaded");
    }

    [Fact]
    public void CrashesFoundByAflStayFixed()
    {
        // Each file made the harness throw once: huge-viewbox.svg drew with a matrix of infinities.
        string folder = Path.Combine(TestSvg.Root, "tests", "images", "fuzz", "svg-crashes");
        foreach (string file in Directory.GetFiles(folder).Order(StringComparer.Ordinal))
        {
            SvgFuzzTarget.Run(File.ReadAllBytes(file));
        }
    }
}
