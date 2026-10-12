using System.Text;

using Egami.Fuzz;

namespace Egami.Css.Tests;

/// <summary>
/// Runs the css fuzz target's harness (tests/Egami.Fuzz/CssFuzzTarget.cs) in process: the seeds, then a few thousand random
/// mutations of them. Egami.Css never throws on input, so any exception fails the test with the input that caused it.
/// </summary>
public sealed class FuzzHarnessTests
{
    private static readonly byte[] s_interesting = Encoding.UTF8.GetBytes("{}()[];:,.#@!%*+-/\\\"'<>=~|^$&?0123456789eE \n\tuU-_avr");

    private static List<byte[]> Seeds()
    {
        string folder = Path.Combine(TestData.Root, "tests", "images", "fuzz", "css-seeds");
        return [.. Directory.GetFiles(folder).Order(StringComparer.Ordinal).Select(File.ReadAllBytes)];
    }

    [Fact]
    public void SeedsRun()
    {
        List<byte[]> seeds = Seeds();
        Assert.NotEmpty(seeds);
        foreach (byte[] seed in seeds)
        {
            CssFuzzTarget.Run(seed);
        }
    }

    [Fact]
    public void RandomMutationsNeverThrow()
    {
        List<byte[]> seeds = Seeds();
        Random random = new(2010);
        for (int iteration = 0; iteration < 3000; iteration++)
        {
            byte[] input = Mutate(seeds[random.Next(seeds.Count)], seeds[random.Next(seeds.Count)], random);
            try
            {
                CssFuzzTarget.Run(input);
            }
            catch (Exception e)
            {
                Assert.Fail($"Iteration {iteration} threw {e.GetType().Name}: {e.Message}\nInput (base64): {Convert.ToBase64String(input)}\n{e.StackTrace}");
            }
        }
    }

    [Theory]
    [InlineData("((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((((")]
    [InlineData(":is(:is(:is(:is(:is(:is(:is(:is(:is(:is(:is(:is(:is(:is(:is(:is(:is(:is(:is(:is(:is(:is(:is(:is(:is(:is(:is(:is(:is(:is(:is(:is(:is(:is(:is(:is(:is(a){}")]
    [InlineData("@media{@media{@media{@media{@media{@media{@media{@media{@media{@media{@media{@media{@media{@media{@media{@media{a{b:c}")]
    [InlineData("a{--a:var(--b);--b:var(--c);--c:var(--a);color:var(--a,var(--b,var(--c)))}")]
    [InlineData("\0𐏿\r\n\f\\")]
    [InlineData("a{b:calc(calc(calc(calc(calc(1px/0)*1e308)*1e308)))}")]
    public void HostileInputs(string css)
    {
        CssFuzzTarget.Run(Encoding.UTF8.GetBytes(css));
    }

    [Fact]
    public void LongCustomPropertyChains()
    {
        StringBuilder css = new("svg {");
        for (int i = 0; i < 5000; i++)
        {
            css.Append(System.Globalization.CultureInfo.InvariantCulture, $"--p{i}: var(--p{i + 1});");
        }

        css.Append("--p5000: 1px; color: var(--p0) }");
        CssFuzzTarget.Run(Encoding.UTF8.GetBytes(css.ToString()));
    }

    private static byte[] Mutate(byte[] seed, byte[] other, Random random)
    {
        List<byte> data = [.. seed];
        int edits = 1 + random.Next(8);
        for (int i = 0; i < edits; i++)
        {
            int position = data.Count == 0 ? 0 : random.Next(data.Count);
            switch (random.Next(6))
            {
                case 0 when data.Count > 0:
                    data[position] = (byte)random.Next(256);
                    break;
                case 1:
                    data.Insert(position, s_interesting[random.Next(s_interesting.Length)]);
                    break;
                case 2 when data.Count > 0:
                    data.RemoveRange(position, Math.Min(data.Count - position, 1 + random.Next(16)));
                    break;
                case 3 when other.Length > 0:
                    int start = random.Next(other.Length);
                    data.InsertRange(position, other.AsSpan(start, Math.Min(other.Length - start, 1 + random.Next(64))).ToArray());
                    break;
                case 4 when data.Count > 0:
                    int length = Math.Min(data.Count - position, 1 + random.Next(32));
                    data.InsertRange(position, data.GetRange(position, length));
                    break;
                default:
                    data.Insert(position, s_interesting[random.Next(s_interesting.Length)]);
                    break;
            }
        }

        return [.. data];
    }
}
