namespace Egami.Nal.Tests;

/// <summary>Codes bins with <see cref="CabacEncoder"/> and decodes them with <see cref="CabacEngine"/>, which is held to HM.</summary>
public class CabacEncoderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void DecoderReadsWhatTheEncoderWrote(int seed)
    {
        Random random = new(seed);
        List<(int Kind, int Context, uint Bins, int Count)> bins = [];
        for (int i = 0; i < 50000; i++)
        {
            int kind = random.Next(20) switch { < 14 => 0, < 17 => 1, < 19 => 3, _ => 2 };
            int context = random.Next(8);

            // Skewed bins per context, so states climb towards 62 and fall back to 0 and the MPS flips.
            uint bin = kind == 2 ? 0u : random.NextDouble() < (context < 4 ? 0.97 : 0.3) ? (uint)(context & 1) : (uint)((context & 1) ^ 1);
            int count = 1;
            if (kind == 3)
            {
                // Runs of bypass bins, many of them all ones, which make the 0xFF bytes a carry runs through.
                count = random.Next(1, 33);
                bin = random.Next(3) == 0 ? uint.MaxValue >> (32 - count) : (uint)random.NextInt64(0, 1L << count);
            }

            bins.Add((kind, context, bin, count));
        }

        CabacEncoder encoder = new();
        encoder.Start();
        byte[] states = InitialStates(seed);
        foreach ((int kind, int context, uint value, int count) in bins)
        {
            switch (kind)
            {
                case 0:
                    encoder.Encode(ref states[context], (int)value);
                    break;
                case 1:
                    encoder.Bypass((int)value);
                    break;
                case 2:
                    encoder.Terminate(0);
                    break;
                default:
                    encoder.Bypass(value, count);
                    break;
            }
        }

        encoder.Terminate(1);
        byte[] coded = encoder.Finish().ToArray();
        byte[] data = [0x55, .. coded, 0xAA, 0xAA];

        CabacEngine engine = new();
        engine.Start(data, 1, 1 + coded.Length);
        states = InitialStates(seed);
        foreach ((int kind, int context, uint value, int count) in bins)
        {
            uint decoded = kind switch
            {
                0 => (uint)engine.Decode(ref states[context]),
                1 => (uint)engine.Bypass(),
                2 => (uint)engine.Terminate(),
                _ => engine.Bypass(count),
            };
            Assert.Equal(value, decoded);
        }

        Assert.Equal(1, engine.Terminate());
        Assert.Equal(1 + coded.Length, engine.Position);
        Assert.False(engine.Overrun);
    }

    [Fact]
    public void SubstreamsFollowOneAnother()
    {
        // As wavefront substreams: each ends with a terminating 1 bin and byte_alignment(), the next starts on the byte after.
        List<byte> data = [];
        List<int> starts = [];
        CabacEncoder encoder = new();
        for (int s = 0; s < 5; s++)
        {
            encoder.Start();
            byte state = 60;
            for (int i = 0; i < 100 * (s + 1); i++)
            {
                encoder.Encode(ref state, (i * 7 % 5 == 0) ? 1 : 0);
                encoder.Bypass(i & 1);
            }

            encoder.Terminate(1);
            starts.Add(data.Count);
            data.AddRange(encoder.Finish().ToArray());
        }

        byte[] bytes = [.. data];
        CabacEngine engine = new();
        for (int s = 0; s < 5; s++)
        {
            engine.Start(bytes, starts[s], bytes.Length);
            byte state = 60;
            for (int i = 0; i < 100 * (s + 1); i++)
            {
                Assert.Equal((i * 7 % 5 == 0) ? 1 : 0, engine.Decode(ref state));
                Assert.Equal(i & 1, engine.Bypass());
            }

            Assert.Equal(1, engine.Terminate());
            Assert.Equal(s + 1 < 5 ? starts[s + 1] : bytes.Length, engine.Position);
        }
    }

    [Fact]
    public void EmptySubstreamIsOneByte()
    {
        CabacEncoder encoder = new();
        encoder.Start();
        encoder.Terminate(1);
        byte[] coded = encoder.Finish().ToArray();
        CabacEngine engine = new();
        engine.Start([.. coded, 0, 0], 0, coded.Length);
        Assert.Equal(1, engine.Terminate());
        Assert.Equal(coded.Length, engine.Position);
    }

    [Fact]
    public void CostsMatchTheCodedLength()
    {
        // The estimated cost of a long run of bins is within a percent of the bits the encoder writes.
        Random random = new(7);
        CabacEncoder encoder = new();
        encoder.Start();
        byte[] states = new byte[4];
        byte[] counted = new byte[4];
        long cost = 0;
        for (int i = 0; i < 200000; i++)
        {
            int context = random.Next(4);
            int bin = random.NextDouble() < 0.1 + (0.25 * context) ? 1 : 0;
            encoder.Encode(ref states[context], bin);
            cost += CabacCost.Update(ref counted[context], bin);
        }

        Assert.Equal(states, counted);
        encoder.Terminate(1);
        long bits = encoder.Finish().Length * 8L;
        Assert.InRange(cost / (double)CabacCost.Bit, bits * 0.99, bits * 1.01);
    }

    private static byte[] InitialStates(int seed)
    {
        Random random = new(seed * 31);
        byte[] states = new byte[8];
        for (int i = 0; i < states.Length; i++)
        {
            states[i] = (byte)random.Next(126);
        }

        return states;
    }
}
