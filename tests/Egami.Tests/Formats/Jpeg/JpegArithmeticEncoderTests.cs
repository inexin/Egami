using Egami.Formats.Jpeg;

namespace Egami.Tests.Formats.Jpeg;

/// <summary>
/// The QM-coder of the arithmetic JPEG writer (T.81 annex D.1) against the decoder of annex D.2: random decisions in many
/// contexts, runs that carry into stacked 0xFF bytes, byte stuffing, and restart markers that flush and start the coder again.
/// </summary>
public class JpegArithmeticEncoderTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 50_000)]
    [InlineData(11, 200_000)]
    public void RandomDecisionsRoundTrip(int seed, int count)
    {
        Random random = new(seed);
        (int Context, int Bit)[] decisions = new (int, int)[count];
        for (int i = 0; i < count; i++)
        {
            // Skewed contexts, as real models have, plus some at even odds; context 0 is the fixed estimate.
            int context = random.Next(17);
            double ones = context switch { 0 => 0.5, < 6 => 0.02 * context, < 12 => 1 - (0.03 * (context - 5)), _ => 0.5 };
            decisions[i] = (context, random.NextDouble() < ones ? 1 : 0);
        }

        RoundTrip(decisions, restartEvery: 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(1000)]
    public void RestartsFlushAndStartAgain(int restartEvery)
    {
        Random random = new(5);
        (int, int)[] decisions = [.. Enumerable.Range(0, 20_000).Select(_ => (random.Next(8), random.Next(5) == 0 ? 1 : 0))];
        RoundTrip(decisions, restartEvery);
    }

    [Fact]
    public void LongRunsOfOneSymbol()
    {
        // All MPS shrinks the code toward the top of the interval, so carries run through many 0xFF bytes; then all LPS.
        (int, int)[] decisions = [.. Enumerable.Repeat((1, 0), 30_000), .. Enumerable.Repeat((1, 1), 3_000), .. Enumerable.Repeat((2, 1), 30_000)];
        byte[] data = RoundTrip(decisions, restartEvery: 0);
        Assert.Contains(data.Select((b, i) => (b, i)), p => p.b == 0xFF && p.i + 1 < data.Length && data[p.i + 1] == 0);
    }

    [Fact]
    public void StuffsEveryFfAndCarries()
    {
        // Search seeds for streams that hold stuffed bytes and carries over stacked 0xFF bytes; each must decode.
        int stuffed = 0;
        for (int seed = 0; seed < 200; seed++)
        {
            Random random = new(seed);
            (int, int)[] decisions = [.. Enumerable.Range(0, 2_000).Select(_ => (0, random.Next(2)))];
            byte[] data = RoundTrip(decisions, restartEvery: 0);
            for (int i = 0; i + 1 < data.Length - 2; i++)
            {
                if (data[i] == 0xFF)
                {
                    Assert.Equal(0, data[i + 1]);
                    stuffed++;
                    i++;
                }
            }
        }

        Assert.True(stuffed > 0);
    }

    /// <summary>
    /// Encodes the decisions, each in its own context (context 0 at the fixed estimate), with a restart marker every
    /// <paramref name="restartEvery"/> decisions, decodes them back and returns the entropy-coded bytes.
    /// </summary>
    private static byte[] RoundTrip((int Context, int Bit)[] decisions, int restartEvery)
    {
        MemoryStream stream = new();
        using (JpegBitWriter writer = new(stream))
        {
            JpegArithmeticEncoder encoder = new(writer);
            byte[] contexts = Fresh();
            int restart = 0;
            for (int i = 0; i < decisions.Length; i++)
            {
                if (restartEvery > 0 && i > 0 && i % restartEvery == 0)
                {
                    encoder.Finish();
                    writer.Marker((byte)(JpegMarker.Rst0 + (restart++ & 7)));
                    encoder.Restart();
                    contexts = Fresh();
                }

                encoder.EncodeBit(ref contexts[decisions[i].Context], decisions[i].Bit);
            }

            encoder.Finish();
            writer.Marker(JpegMarker.Eoi);
            writer.Flush();
        }

        byte[] data = stream.ToArray();
        using JpegBitReader reader = new(data, 0, data.Length);
        JpegArithmeticDecoder decoder = new(reader, new JpegHeader());
        decoder.ResetCoder();
        byte[] decoding = Fresh();
        int expected = 0;
        for (int i = 0; i < decisions.Length; i++)
        {
            if (restartEvery > 0 && i > 0 && i % restartEvery == 0)
            {
                Assert.True(reader.Restart(expected++), $"restart {expected - 1}");
                decoder.ResetCoder();
                decoding = Fresh();
            }

            Assert.True(decisions[i].Bit == decoder.DecodeBit(ref decoding[decisions[i].Context]), $"decision {i}");
        }

        return data;
    }

    private static byte[] Fresh()
    {
        byte[] contexts = new byte[17];
        contexts[0] = JpegQmTables.FixedState;
        return contexts;
    }
}
