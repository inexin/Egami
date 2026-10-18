using Egami.Formats.Av1;

namespace Egami.Tests.Formats.Av1;

/// <summary>
/// The AV1 range encoder: symbols coded against every default CDF, adapting, with bits, literals and the other descriptors mixed
/// in, decode to the same values through Egami's symbol decoder, with valid trailing bits and the same bit count.
/// </summary>
public class Av1SymbolEncoderTests
{
    [Theory]
    [InlineData(1, 0, false)]
    [InlineData(2, 1, false)]
    [InlineData(3, 2, false)]
    [InlineData(4, 0, true)]
    public void MillionSymbolsOnEveryCdfRoundTrip(int seed, int skew, bool disableCdfUpdate)
    {
        (int Offset, int Symbols)[] cdfs = [.. Av1FrameContext.Cdfs];
        foreach (int q in new[] { 0, 100 })
        {
            Random random = new(seed + q);
            Av1FrameContext encoderContext = Av1FrameContext.CreateDefault(q);
            Av1SymbolEncoder encoder = new(disableCdfUpdate);
            List<(int Kind, int A, int B, int Value)> coded = [];
            int count = q == 0 ? 1_000_000 : 100_000;
            for (int i = 0; i < count; i++)
            {
                int kind = random.Next(16);
                if (kind < 11)
                {
                    int index = random.Next(cdfs.Length);
                    (int offset, int n) = cdfs[index];
                    int symbol = skew switch
                    {
                        0 => random.Next(n),
                        1 => random.Next(8) == 0 ? random.Next(n) : 0,
                        _ => random.Next(8) == 0 ? random.Next(n) : n - 1,
                    };
                    encoder.Symbol(encoderContext.Cdf, n, offset, symbol);
                    coded.Add((0, offset, n, symbol));
                }
                else if (kind == 11)
                {
                    int bits = random.Next(1, 17);
                    int value = random.Next(1 << bits);
                    encoder.Literal(value, bits);
                    coded.Add((1, bits, 0, value));
                }
                else if (kind == 12)
                {
                    int n = random.Next(1, 300);
                    int value = random.Next(n);
                    encoder.Uniform(n, value);
                    coded.Add((2, n, 0, value));
                }
                else if (kind == 13)
                {
                    uint value = (uint)(random.Next(4) == 0 ? random.Next(1 << 20) : random.Next(20));
                    encoder.Golomb(value);
                    coded.Add((3, 0, 0, (int)value));
                }
                else if (kind == 14)
                {
                    int n = random.Next(2, 200), k = random.Next(0, 5);
                    int reference = random.Next(n), value = random.Next(n);
                    encoder.RefSubexpFin(n, k, reference, value);
                    coded.Add((4, n, (k << 16) | reference, value));
                }
                else
                {
                    // A fixed CDF, as the partition at the frame's edge gathers one.
                    int p = random.Next(1, 32767);
                    int value = random.Next(2);
                    encoder.SymbolFixed([(ushort)p, 0], 2, value);
                    coded.Add((5, p, 0, value));
                }
            }

            int bitsUsed = encoder.Tell;
            byte[] data = encoder.Finish();
            Av1FrameContext decoderContext = Av1FrameContext.CreateDefault(q);
            Av1SymbolDecoder decoder = new(data, 0, data.Length, disableCdfUpdate);
            for (int i = 0; i < coded.Count; i++)
            {
                (int kind, int a, int b, int value) = coded[i];
                int read = kind switch
                {
                    0 => decoder.Symbol(decoderContext.Cdf, b, a),
                    1 => decoder.Literal(a),
                    2 => decoder.Uniform(a),
                    3 => (int)decoder.Golomb(),
                    4 => decoder.RefSubexpFin(a, b >> 16, b & 0xFFFF),
                    _ => decoder.SymbolFixed([(ushort)a, 0], 2),
                };
                if (read != value)
                {
                    Assert.Fail($"Symbol {i} (kind {kind}) decoded as {read}, not {value}.");
                }
            }

            Assert.Equal(bitsUsed, decoder.Tell);
            Assert.True(decoder.TrailingBitsValid());
            Assert.Equal(encoderContext.Cdf, decoderContext.Cdf);
            Assert.True(data.Length * 8 - bitsUsed < 16, $"{data.Length} bytes for {bitsUsed} bits.");
        }
    }

    [Fact]
    public void ShortTilesEndInValidTrailingBits()
    {
        Random random = new(9);
        for (int length = 0; length < 200; length++)
        {
            Av1SymbolEncoder encoder = new();
            Av1FrameContext context = Av1FrameContext.CreateDefault(0);
            int at = Av1FrameContext.SkipTxfm[0];
            List<int> values = [];
            for (int i = 0; i < length; i++)
            {
                int value = random.Next(2);
                encoder.Symbol(context.Cdf, 2, at, value);
                values.Add(value);
            }

            int bits = encoder.Tell;
            byte[] data = encoder.Finish();
            Assert.NotEmpty(data);
            Av1FrameContext decoding = Av1FrameContext.CreateDefault(0);
            Av1SymbolDecoder decoder = new(data, 0, data.Length, false);
            foreach (int value in values)
            {
                Assert.Equal(value, decoder.Symbol(decoding.Cdf, 2, at));
            }

            Assert.Equal(bits, decoder.Tell);
            Assert.True(decoder.TrailingBitsValid(), $"{length} symbols");
        }
    }

    [Fact]
    public void CountersResetBesideTheTerminator()
    {
        Av1FrameContext context = Av1FrameContext.CreateDefault(0);
        Av1SymbolEncoder encoder = new();
        int at = Av1FrameContext.SkipTxfm[1];
        for (int i = 0; i < 40; i++)
        {
            encoder.Symbol(context.Cdf, 2, at, 1);
        }

        Assert.Equal(32, context.Cdf[at + 2]);
        context.ResetCounters();
        Assert.Equal(0, context.Cdf[at + 2]);
        Assert.Equal(0, context.Cdf[at + 1]);
    }
}
