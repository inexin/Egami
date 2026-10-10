using System.Numerics;

using Egami.Pixels;

namespace Egami.Tests.Pixels;

/// <summary>
/// RgbaF16, RgbF32 and GrayF32 (Phase 2.1.1 task 5), and the half-float kernels. The exhaustive float test fills every core, so
/// the class runs on its own rather than starving the thread pool under tests that measure allocations.
/// </summary>
[Collection(ExhaustiveGroup.Name)]
public class FloatTypeTests
{
    [Fact]
    public void EveryHalfConvertsToTheSameFloatAsTheCast()
    {
        ushort[] halves = new ushort[65536];
        for (int i = 0; i < halves.Length; i++)
        {
            halves[i] = (ushort)i;
        }

        foreach (int bits in VectorWidths())
        {
            uint[] singles = new uint[halves.Length];
            int done = HalfKernels.ToSingle(halves, singles, bits);
            Assert.True(done > 65000, $"{bits}-bit kernel converted only {done}");
            for (int i = 0; i < done; i++)
            {
                uint expected = BitConverter.SingleToUInt32Bits((float)BitConverter.UInt16BitsToHalf(halves[i]));
                Assert.True(expected == singles[i], $"{bits} bits: half 0x{i:X4} gave 0x{singles[i]:X8}, the cast 0x{expected:X8}");
            }
        }
    }

    [Fact]
    public void EveryHalfRoundTripsThroughFloat()
    {
        RgbaF16[] halves = new RgbaF16[16384];
        for (int i = 0; i < halves.Length; i++)
        {
            halves[i] = new RgbaF16(H(i * 4), H((i * 4) + 1), H((i * 4) + 2), H((i * 4) + 3));
        }

        RgbaF32[] singles = new RgbaF32[halves.Length];
        RgbaF16[] back = new RgbaF16[halves.Length];
        PixelConvert.Convert<RgbaF16, RgbaF32>(halves, singles);
        PixelConvert.Convert<RgbaF32, RgbaF16>(singles, back);
        // Every value comes back bit for bit, except that a signaling NaN comes back quiet, as the casts make it.
        for (int i = 0; i < halves.Length; i++)
        {
            Assert.Equal(Quiet(Bits(halves[i])), Bits(back[i]));
        }

        static ulong Quiet(ulong pixel)
        {
            ulong result = 0;
            for (int c = 0; c < 64; c += 16)
            {
                ulong h = (pixel >> c) & 0xFFFF;
                result |= ((h & 0x7C00) == 0x7C00 && (h & 0x3FF) != 0 ? h | 0x200 : h) << c;
            }

            return result;
        }

        static Half H(int bits) => BitConverter.UInt16BitsToHalf((ushort)bits);
    }

    [Fact]
    public void EveryFloatConvertsToTheSameHalfAsTheCast()
    {
        int[] widths = VectorWidths();
        if (widths.Length == 0)
        {
            return;
        }

        // All 2^32 bit patterns, in parallel blocks of 2^16.
        int failures = 0;
        string? first = null;
        Parallel.For(0, 65536, new ParallelOptions { CancellationToken = TestContext.Current.CancellationToken }, block =>
        {
            uint[] singles = new uint[65536];
            ushort[] halves = new ushort[65536];
            for (int i = 0; i < singles.Length; i++)
            {
                singles[i] = ((uint)block << 16) | (uint)i;
            }

            foreach (int bits in widths)
            {
                int done = HalfKernels.ToHalf(singles, halves, bits);
                for (int i = 0; i < done; i++)
                {
                    ushort expected = BitConverter.HalfToUInt16Bits((Half)BitConverter.UInt32BitsToSingle(singles[i]));
                    if (expected != halves[i] && Interlocked.Increment(ref failures) == 1)
                    {
                        first = $"{bits} bits: float 0x{singles[i]:X8} gave 0x{halves[i]:X4}, the cast 0x{expected:X4}";
                    }
                }
            }
        });
        Assert.True(failures == 0, $"{failures} mismatches; first: {first}");
    }

    [Fact]
    public void HalfKeepsHdrValuesAndRoundsToByteForIntegerTypes()
    {
        RgbaF16 bright = RgbaF16.FromScaled(new Vector4(2.5f, -0.25f, 70000f, 0.5f));
        Assert.Equal(2.5f, (float)bright.R);
        Assert.Equal(-0.25f, (float)bright.G);
        Assert.True(Half.IsPositiveInfinity(bright.B));
        Assert.Equal(new Rgba8(255, 0, 255, 128), bright.ToRgba8());

        for (int v = 0; v < 256; v++)
        {
            Rgba8 color = new((byte)v, (byte)(255 - v), (byte)v, (byte)v);
            Assert.Equal(color, RgbaF16.FromRgba8(color).ToRgba8());
            Assert.Equal(color with { A = 255 }, RgbF32.FromRgba8(color).ToRgba8());
        }
    }

    [Fact]
    public void GrayF32TakesLinearLumaAndKeepsHdr()
    {
        Assert.Equal(Gray16.FromRgba8(Rgba8.Red).L / 65535.0, GrayF32.FromRgba8(Rgba8.Red).L, 4);
        Assert.Equal(3f, GrayF32.FromScaled(new Vector4(3f, 3f, 3f, 1f)).L);
        Assert.True(GrayF32.FromScaled(new Vector4(2f, 1f, 1f, 1f)).L > 1f);
        Assert.Equal(new Rgba8(255, 255, 255, 255), new GrayF32(1.7f).ToRgba8());
    }

    [Fact]
    public void LayoutsDescribeTheStructs()
    {
        Assert.Equal(8, RgbaF16.Layout.BytesPerPixel);
        Assert.Equal(12, RgbF32.Layout.BytesPerPixel);
        Assert.Equal(4, GrayF32.Layout.BytesPerPixel);
        Assert.Equal(PixelChannelType.Float16, RgbaF16.Layout.ChannelType);
        Assert.Equal("GrayF32(0.5)", new GrayF32(0.5f).ToString());
    }

    private static int[] VectorWidths()
    {
        return PixelKernels.HardwareBits >= 256 ? [256, 128] : PixelKernels.HardwareBits >= 128 ? [128] : [];
    }

    private static ulong Bits(RgbaF16 p)
    {
        return BitConverter.HalfToUInt16Bits(p.R) | ((ulong)BitConverter.HalfToUInt16Bits(p.G) << 16)
            | ((ulong)BitConverter.HalfToUInt16Bits(p.B) << 32) | ((ulong)BitConverter.HalfToUInt16Bits(p.A) << 48);
    }
}

/// <summary>Tests that use every core for a while; they run alone, after the parallel ones.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ExhaustiveGroup
{
    public const string Name = "Exhaustive";
}
