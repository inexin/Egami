namespace Egami.Nal.Tests;

/// <summary>
/// Decodes bins coded by the arithmetic encoder of H.264 9.3.4.2, written here from the standard's description, so the shared
/// engine is checked against the coding it inverts rather than against itself.
/// </summary>
public class CabacEngineTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DecodesWhatTheEncoderCoded(int seed)
    {
        Random random = new(seed);
        List<(int Kind, int Context, int Bin)> bins = [];
        for (int i = 0; i < 20000; i++)
        {
            int kind = random.Next(10) switch { < 7 => 0, < 9 => 1, _ => 2 };
            int context = random.Next(8);

            // Skewed bins per context, so states climb towards 62 and fall back to 0 and the MPS flips.
            int bin = kind == 2 ? 0 : random.NextDouble() < (context < 4 ? 0.95 : 0.3) ? context & 1 : (context & 1) ^ 1;
            bins.Add((kind, context, bin));
        }

        Encoder encoder = new();
        byte[] states = InitialStates(seed);
        foreach ((int kind, int context, int bin) in bins)
        {
            switch (kind)
            {
                case 0:
                    encoder.Decision(ref states[context], bin);
                    break;
                case 1:
                    encoder.Bypass(bin);
                    break;
                default:
                    encoder.Terminate(0);
                    break;
            }
        }

        encoder.Terminate(1);
        byte[] coded = encoder.Finish(out int codedLength);
        byte[] data = [0x55, 0x55, .. coded, 0xAA, 0xAA];

        CabacEngine engine = new();
        engine.Start(data, 2, 2 + coded.Length);
        states = InitialStates(seed);
        foreach ((int kind, int context, int bin) in bins)
        {
            int decoded = kind switch
            {
                0 => engine.Decode(ref states[context]),
                1 => engine.Bypass(),
                _ => engine.Terminate(),
            };
            Assert.Equal(bin, decoded);
        }

        Assert.Equal(1, engine.Terminate());
        Assert.Equal(2 + codedLength, engine.Position);
        Assert.False(engine.Overrun);
    }

    [Fact]
    public void DecodesBypassRuns()
    {
        Encoder encoder = new();
        uint[] values = [0, 1, 0xABCDE, 0xFFFFFFFF, 12345678];
        foreach (uint value in values)
        {
            for (int i = 31; i >= 0; i--)
            {
                encoder.Bypass((int)((value >> i) & 1));
            }
        }

        encoder.Terminate(1);
        byte[] coded = encoder.Finish(out _);
        CabacEngine engine = new();
        engine.Start(coded, 0, coded.Length);
        foreach (uint value in values)
        {
            Assert.Equal(value, engine.Bypass(32));
        }

        Assert.Equal(1, engine.Terminate());
    }

    [Fact]
    public void ReadsRawBitsAfterATerminatingBin()
    {
        // pcm_flag: a terminating 1, then the samples from the next byte-aligned byte.
        Encoder encoder = new();
        encoder.Terminate(1);
        byte[] coded = encoder.Finish(out int codedLength);
        byte[] data = [.. coded, 0b1011_0011, 0b0100_0000];
        CabacEngine engine = new();
        engine.Start(data, 0, data.Length);
        Assert.Equal(1, engine.Terminate());
        Assert.Equal(codedLength, engine.Position);
        CabacEngine.RawReader raw = new(data, engine.Position);
        Assert.Equal(0b1011, raw.Read(4));
        Assert.Equal(0b0011_01, raw.Read(6));
        Assert.Equal(codedLength + 2, raw.BytePosition);
    }

    [Fact]
    public void FlagsReadingPastTheEnd()
    {
        CabacEngine engine = new();
        engine.Start([0x12, 0x34], 0, 2);
        for (int i = 0; i < 64; i++)
        {
            engine.Bypass();
        }

        Assert.True(engine.Overrun);
    }

    private static byte[] InitialStates(int seed)
    {
        Random random = new(seed + 100);
        byte[] states = new byte[8];
        for (int i = 0; i < states.Length; i++)
        {
            states[i] = (byte)((random.Next(63) << 1) | random.Next(2));
        }

        return states;
    }

    /// <summary>The arithmetic encoder of H.264 9.3.4.2, with the same state layout as the engine: (pStateIdx &lt;&lt; 1) | valMPS.</summary>
    private sealed class Encoder
    {
        private static readonly byte[,] RangeLps = BuildRangeLps();
        private static readonly byte[] TransitionLps =
        [
            0, 0, 1, 2, 2, 4, 4, 5, 6, 7, 8, 9, 9, 11, 11, 12, 13, 13, 15, 15, 16, 16, 18, 18, 19, 19, 21, 21, 22, 22, 23, 24,
            24, 25, 26, 26, 27, 27, 28, 29, 29, 30, 30, 30, 31, 32, 32, 33, 33, 33, 34, 34, 35, 35, 35, 36, 36, 36, 37, 37, 37, 38, 38, 63,
        ];

        private readonly BitWriter _out = new();
        private int _low;
        private int _range = 510;
        private bool _firstBit = true;
        private int _outstanding;

        internal void Decision(ref byte state, int bin)
        {
            int p = state >> 1, mps = state & 1;
            int lps = RangeLps[p, (_range >> 6) & 3];
            _range -= lps;
            if (bin != mps)
            {
                _low += _range;
                _range = lps;
                if (p == 0)
                {
                    mps = 1 - mps;
                }

                p = TransitionLps[p];
            }
            else
            {
                p = Math.Min(p + 1, 62);
            }

            state = (byte)((p << 1) | mps);
            Renormalize();
        }

        internal void Bypass(int bin)
        {
            _low <<= 1;
            if (bin != 0)
            {
                _low += _range;
            }

            if (_low >= 1024)
            {
                PutBit(1);
                _low -= 1024;
            }
            else if (_low < 512)
            {
                PutBit(0);
            }
            else
            {
                _low -= 512;
                _outstanding++;
            }
        }

        internal void Terminate(int bin)
        {
            _range -= 2;
            if (bin != 0)
            {
                _low += _range;

                // EncodeFlush: the last bit written is rbsp_stop_one_bit.
                _range = 2;
                Renormalize();
                PutBit((_low >> 9) & 1);
                _out.U(2, ((_low >> 7) & 3) | 1);
            }
            else
            {
                Renormalize();
            }
        }

        /// <summary>Pads to the byte boundary and returns the bytes.</summary>
        internal byte[] Finish(out int length)
        {
            while (_out.BitCount % 8 != 0)
            {
                _out.Bit(0);
            }

            byte[] bytes = _out.ToArray();
            length = bytes.Length;
            return bytes;
        }

        private void Renormalize()
        {
            while (_range < 256)
            {
                if (_low < 256)
                {
                    PutBit(0);
                }
                else if (_low >= 512)
                {
                    _low -= 512;
                    PutBit(1);
                }
                else
                {
                    _low -= 256;
                    _outstanding++;
                }

                _range <<= 1;
                _low <<= 1;
            }
        }

        private void PutBit(int bit)
        {
            if (_firstBit)
            {
                _firstBit = false;
            }
            else
            {
                _out.Bit(bit);
            }

            for (; _outstanding > 0; _outstanding--)
            {
                _out.Bit(1 - bit);
            }
        }

        private static byte[,] BuildRangeLps()
        {
            // H.264 Table 9-44, rows of pStateIdx.
            byte[] table =
            [
                128, 176, 208, 240, 128, 167, 197, 227, 128, 158, 187, 216, 123, 150, 178, 205, 116, 142, 169, 195, 111, 135, 160, 185,
                105, 128, 152, 175, 100, 122, 144, 166, 95, 116, 137, 158, 90, 110, 130, 150, 85, 104, 123, 142, 81, 99, 117, 135,
                77, 94, 111, 128, 73, 89, 105, 122, 69, 85, 100, 116, 66, 80, 95, 110, 62, 76, 90, 104, 59, 72, 86, 99,
                56, 69, 81, 94, 53, 65, 77, 89, 51, 62, 73, 85, 48, 59, 69, 80, 46, 56, 66, 76, 43, 53, 63, 72,
                41, 50, 59, 69, 39, 48, 56, 65, 37, 45, 54, 62, 35, 43, 51, 59, 33, 41, 48, 56, 32, 39, 46, 53,
                30, 37, 43, 50, 29, 35, 41, 48, 27, 33, 39, 45, 26, 31, 37, 43, 24, 30, 35, 41, 23, 28, 33, 39,
                22, 27, 32, 37, 21, 26, 30, 35, 20, 24, 29, 33, 19, 23, 27, 31, 18, 22, 26, 30, 17, 21, 25, 28,
                16, 20, 23, 27, 15, 19, 22, 25, 14, 18, 21, 24, 14, 17, 20, 23, 13, 16, 19, 22, 12, 15, 18, 21,
                12, 14, 17, 20, 11, 14, 16, 19, 11, 13, 15, 18, 10, 12, 15, 17, 10, 12, 14, 16, 9, 11, 13, 15,
                9, 11, 12, 14, 8, 10, 12, 14, 8, 9, 11, 13, 7, 9, 11, 12, 7, 9, 10, 12, 7, 8, 10, 11,
                6, 8, 9, 11, 6, 7, 9, 10, 6, 7, 8, 9, 2, 2, 2, 2,
            ];
            byte[,] result = new byte[64, 4];
            for (int i = 0; i < table.Length; i++)
            {
                result[i / 4, i % 4] = table[i];
            }

            return result;
        }
    }
}
