using Egami.Operations;
using Egami.Operations.Compose;
using Egami.Pixels;

namespace Egami.Tests.Operations.Blending;

/// <summary>
/// The kernels against <see cref="BlendReference"/> on every 8-bit value pair: the integer modes bit for bit, the float modes
/// within a level with alpha exact, at every vector width the machine has and on the scalar path used with intrinsics off.
/// </summary>
public class BlendKernelTests
{
    private static readonly int[] Widths = [0, 128, 256, 512];

    public static TheoryData<BlendMode> Modes()
    {
        return [.. Enum.GetValues<BlendMode>()];
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void EveryValuePairMatchesTheReference(BlendMode mode)
    {
        bool integer = BlendKernels.IsInteger(mode);
        for (int level = 0; level < BlendCorpus.AlphaLevels.Length; level++)
        {
            foreach (int opacity in new[] { 255, 77 })
            {
                (Rgba8[] straightS, Rgba8[] straightD) = BlendCorpus.PairGrid(level);
                uint[] s = Premultiplied(straightS), d = Premultiplied(straightD);
                uint[] expected = new uint[d.Length];
                for (int i = 0; i < d.Length; i++)
                {
                    expected[i] = Pack(BlendReference.Blend8(mode, Unpack(s[i]), Unpack(d[i]), opacity));
                }

                foreach (int bits in Widths)
                {
                    uint[] actual = (uint[])d.Clone();
                    BlendKernels.Blend8(mode, s, actual, default, opacity, bgr: false, bits);
                    for (int i = 0; i < actual.Length; i++)
                    {
                        if (actual[i] != expected[i])
                        {
                            Assert.True(!integer && Close(actual[i], expected[i]), $"{mode} at {bits} bits, opacity {opacity}: s {Unpack(s[i])} d {Unpack(d[i])} gave {Unpack(actual[i])}, reference {Unpack(expected[i])}");
                        }
                    }
                }
            }
        }
    }

    /// <summary>Random valid premultiplied pixels, with every alpha, in both channel orders.</summary>
    [Theory]
    [MemberData(nameof(Modes))]
    public void RandomPixelsMatchTheReferenceInBothOrders(BlendMode mode)
    {
        Random random = new((int)mode);
        uint[] s = new uint[4099], d = new uint[4099];
        for (int i = 0; i < s.Length; i++)
        {
            s[i] = RandomPremultiplied(random);
            d[i] = RandomPremultiplied(random);
        }

        foreach (bool bgr in new[] { false, true })
        {
            uint[] expected = new uint[d.Length];
            for (int i = 0; i < d.Length; i++)
            {
                // The reference works in RGBA; a BGRA pixel is the same bytes with red and blue swapped.
                expected[i] = bgr ? Swap(Pack(BlendReference.Blend8(mode, Unpack(Swap(s[i])), Unpack(Swap(d[i]))))) : Pack(BlendReference.Blend8(mode, Unpack(s[i]), Unpack(d[i])));
            }

            foreach (int bits in Widths)
            {
                uint[] actual = (uint[])d.Clone();
                BlendKernels.Blend8(mode, s, actual, default, 255, bgr, bits);
                for (int i = 0; i < actual.Length; i++)
                {
                    Assert.True(actual[i] == expected[i] || (!BlendKernels.IsInteger(mode) && Close(actual[i], expected[i])), $"{mode} bgr={bgr} at {bits} bits: gave {Unpack(actual[i])}, reference {Unpack(expected[i])}");
                }
            }
        }
    }

    /// <summary>A solid color and per-pixel coverage: each result is the full blend mixed into the original by the coverage.</summary>
    [Theory]
    [InlineData(BlendMode.Multiply)]
    [InlineData(BlendMode.SourceIn)]
    [InlineData(BlendMode.Hue)]
    [InlineData(BlendMode.Clear)]
    public void CoverageMixesTheResultIntoTheOriginal(BlendMode mode)
    {
        Random random = new(5);
        uint color = RandomPremultiplied(random);
        uint[] d = new uint[1000];
        byte[] coverage = new byte[d.Length];
        for (int i = 0; i < d.Length; i++)
        {
            d[i] = RandomPremultiplied(random);
            coverage[i] = (byte)(i % 3 == 0 ? 0 : i % 3 == 1 ? 255 : random.Next(256));
        }

        foreach (int bits in Widths)
        {
            uint[] full = (uint[])d.Clone();
            BlendKernels.Blend8(mode, [color], full, default, 255, bgr: false, bits);
            uint[] covered = (uint[])d.Clone();
            BlendKernels.Blend8(mode, [color], covered, coverage, 255, bgr: false, bits);
            for (int i = 0; i < d.Length; i++)
            {
                uint expected = 0;
                for (int shift = 0; shift < 32; shift += 8)
                {
                    uint r = (full[i] >> shift) & 0xFF, o = (d[i] >> shift) & 0xFF;
                    expected |= (uint)Math.Round(((r * coverage[i]) + (o * (255 - coverage[i]))) / 255.0, MidpointRounding.AwayFromZero) << shift;
                }

                Assert.Equal(expected, covered[i]);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void FloatRowsMatchTheReference(BlendMode mode)
    {
        Random random = new(100 + (int)mode);
        float[] s = new float[4 * 2000], d = new float[4 * 2000];
        for (int i = 0; i < s.Length; i += 4)
        {
            RandomFloat(random, s.AsSpan(i, 4));
            RandomFloat(random, d.AsSpan(i, 4));
        }

        float[] actual = (float[])d.Clone();
        BlendKernels.BlendFloat(mode, s, actual, default, 0.8f);
        for (int i = 0; i < s.Length; i += 4)
        {
            var expected = BlendReference.Blend(mode, (s[i], s[i + 1], s[i + 2], s[i + 3]), (d[i], d[i + 1], d[i + 2], d[i + 3]), 0.8f);
            Assert.Equal(expected.R, actual[i], 2e-5);
            Assert.Equal(expected.G, actual[i + 1], 2e-5);
            Assert.Equal(expected.B, actual[i + 2], 2e-5);
            Assert.Equal(expected.A, actual[i + 3], 2e-5);
        }
    }

    /// <summary>A straight source premultiplied as it loads gives what premultiplying it first gives, on both paths and every width.</summary>
    [Theory]
    [InlineData(BlendMode.Multiply)]
    [InlineData(BlendMode.SourceOver)]
    [InlineData(BlendMode.Xor)]
    [InlineData(BlendMode.SoftLight)]
    [InlineData(BlendMode.Hue)]
    public void StraightSourcesArePremultipliedOnLoad(BlendMode mode)
    {
        Random random = new(11);
        uint[] straight = new uint[1001], d = new uint[1001];
        for (int i = 0; i < straight.Length; i++)
        {
            straight[i] = (uint)random.Next() | ((uint)random.Next(256) << 24);
            d[i] = RandomPremultiplied(random);
        }

        uint[] premultiplied = Premultiplied(System.Runtime.InteropServices.MemoryMarshal.Cast<uint, Rgba8>(straight).ToArray());
        foreach (int bits in Widths)
        {
            uint[] expected = (uint[])d.Clone(), actual = (uint[])d.Clone();
            BlendKernels.Blend8(mode, premultiplied, expected, default, 200, bgr: false, bits);
            BlendKernels.Blend8(mode, straight, actual, default, 200, bgr: false, bits, straightSource: true);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void DestinationChangesNothingAndClearClears()
    {
        uint[] d = [0x80402010, 0xFFFFFFFF];
        BlendKernels.Blend8(BlendMode.Destination, [0xFF0000FFu, 0xFF00FF00u], d, default, 255, bgr: false);
        Assert.Equal([0x80402010u, 0xFFFFFFFFu], d);
        BlendKernels.Blend8(BlendMode.Clear, [0xFF0000FFu, 0xFF00FF00u], d, default, 255, bgr: false);
        Assert.Equal([0u, 0u], d);
    }

    internal static uint[] Premultiplied(Rgba8[] straight)
    {
        uint[] result = new uint[straight.Length];
        for (int i = 0; i < straight.Length; i++)
        {
            Rgba8 p = straight[i];
            result[i] = Pack((Mul(p.R, p.A), Mul(p.G, p.A), Mul(p.B, p.A), p.A));
        }

        return result;

        static byte Mul(byte c, byte a) => (byte)(((c * a) + 127) / 255);
    }

    internal static uint Pack((byte R, byte G, byte B, byte A) p)
    {
        return p.R | ((uint)p.G << 8) | ((uint)p.B << 16) | ((uint)p.A << 24);
    }

    internal static (byte R, byte G, byte B, byte A) Unpack(uint p)
    {
        return ((byte)p, (byte)(p >> 8), (byte)(p >> 16), (byte)(p >> 24));
    }

    internal static uint RandomPremultiplied(Random random)
    {
        int a = random.Next(5) switch { 0 => 0, 1 => 255, _ => random.Next(256) };
        return Pack(((byte)random.Next(a + 1), (byte)random.Next(a + 1), (byte)random.Next(a + 1), (byte)a));
    }

    /// <summary>Gets whether two pixels have the same alpha and colors within a level.</summary>
    internal static bool Close(uint a, uint b)
    {
        if (a >> 24 != b >> 24)
        {
            return false;
        }

        for (int shift = 0; shift < 24; shift += 8)
        {
            if (Math.Abs((int)((a >> shift) & 0xFF) - (int)((b >> shift) & 0xFF)) > 1)
            {
                return false;
            }
        }

        return true;
    }

    private static uint Swap(uint p)
    {
        return (p & 0xFF00FF00u) | ((p >> 16) & 0xFF) | ((p & 0xFF) << 16);
    }

    private static void RandomFloat(Random random, Span<float> pixel)
    {
        float a = random.Next(5) switch { 0 => 0, 1 => 1, _ => random.NextSingle() };
        pixel[0] = random.NextSingle() * a;
        pixel[1] = random.NextSingle() * a;
        pixel[2] = random.NextSingle() * a;
        pixel[3] = a;
    }
}
