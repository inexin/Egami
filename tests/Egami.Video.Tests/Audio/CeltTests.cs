using System.Buffers.Binary;

using Egami.Video.Audio.Codecs.Opus;

namespace Egami.Video.Tests.Audio;

/// <summary>
/// The CELT decoder translated from libopus 1.5.2: its FFT and inverse MDCT against direct sums, the PVQ codeword decoding,
/// and whole CELT-only streams against libopus's own decode (tests/video/corpus/opus-celt, see tools/video/opusref/make-celt-reference.c).
/// </summary>
public class CeltTests
{
    private static string ReferencePath => Path.Combine(AppContext.BaseDirectory, "corpus", "opus-celt", "celt-ref.bin");

    [Theory]
    [InlineData(0, 480)]
    [InlineData(1, 240)]
    [InlineData(2, 120)]
    [InlineData(3, 60)]
    public void FftMatchesTheDirectDft(int state, int n)
    {
        Random random = new(state);
        double[] re = new double[n];
        double[] im = new double[n];
        for (int i = 0; i < n; i++)
        {
            re[i] = random.NextDouble() - 0.5;
            im[i] = random.NextDouble() - 0.5;
        }

        // The FFT takes its input in bit-reversed order (the MDCT stores it that way).
        ReadOnlySpan<short> bitrev = state switch
        {
            0 => CeltTables.FftBitrev480,
            1 => CeltTables.FftBitrev240,
            2 => CeltTables.FftBitrev120,
            _ => CeltTables.FftBitrev60,
        };
        float[] data = new float[2 * n];
        for (int i = 0; i < n; i++)
        {
            data[2 * bitrev[i]] = (float)re[i];
            data[(2 * bitrev[i]) + 1] = (float)im[i];
        }

        CeltMdct.Fft(state, data);
        double maxError = 0;
        for (int k = 0; k < n; k++)
        {
            double sr = 0, si = 0;
            for (int i = 0; i < n; i++)
            {
                double a = -2 * Math.PI * i * k / n;
                sr += (re[i] * Math.Cos(a)) - (im[i] * Math.Sin(a));
                si += (re[i] * Math.Sin(a)) + (im[i] * Math.Cos(a));
            }

            maxError = Math.Max(maxError, Math.Max(Math.Abs(sr - data[2 * k]), Math.Abs(si - data[(2 * k) + 1])));
        }

        Assert.True(maxError < 1e-4, $"max error {maxError}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void InverseMdctMatchesTheDirectSum(int shift)
    {
        // Without the window (overlap 0), clt_mdct_backward computes y[n] = sum_k X[k] cos(pi/N2 (n + 1/2 + N2/2)(k + 1/2)),
        // N2 = N/2 outputs, which this checks with a direct sum.
        int nMdct = CeltTables.MdctSize >> shift;
        int n2 = nMdct / 2;
        Random random = new(shift + 10);
        float[] input = new float[n2];
        for (int i = 0; i < n2; i++)
        {
            input[i] = (float)(random.NextDouble() - 0.5);
        }

        float[] output = new float[n2];
        CeltMdct.Backward(input, output, default, 0, shift, 1);
        double maxError = 0;
        double scale = 0;
        for (int n = 0; n < n2; n++)
        {
            double s = 0;
            for (int k = 0; k < n2; k++)
            {
                s += input[k] * Math.Cos(Math.PI / n2 * (n + 0.5 + n2) * (k + 0.5));
            }

            scale = Math.Max(scale, Math.Abs(s));
            maxError = Math.Max(maxError, Math.Abs(s - output[n]));
        }

        Assert.True(maxError < 1e-4 * scale, $"max error {maxError} of {scale}");
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 4)]
    [InlineData(4, 3)]
    [InlineData(5, 6)]
    [InlineData(8, 2)]
    [InlineData(6, 9)]
    public void CwrsDecodesEveryCodewordToADistinctPulseVector(int n, int k)
    {
        uint count = CeltVq.PvqV(n, k);
        Assert.Equal(CountPvq(n, k), count);
        HashSet<string> seen = [];
        int[] y = new int[n];
        for (uint i = 0; i < count; i++)
        {
            float yy = CeltVq.Cwrsi(n, k, i, y);
            Assert.Equal(k, y.Sum(Math.Abs));
            Assert.Equal(y.Sum(v => v * v), (int)yy);
            Assert.True(seen.Add(string.Join(",", y)));
        }
    }

    [Fact]
    public void DecodesCeltStreamsLikeLibopus()
    {
        // EGAMI_CELT_REF may name a full-length file with float samples that make-celt-reference.c writes without "small".
        string path = Environment.GetEnvironmentVariable("EGAMI_CELT_REF") is { Length: > 0 } env ? env : ReferencePath;
        byte[] file = File.ReadAllBytes(path);
        bool shorts = file.AsSpan(0, 4).SequenceEqual("CEL6"u8);
        Assert.True(shorts || file.AsSpan(0, 4).SequenceEqual("CELT"u8));
        int pos = 4;
        int streams = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(pos));
        pos += 4;
        List<string> failures = [];
        for (int s = 0; s < streams; s++)
        {
            ReadOnlySpan<byte> header = file.AsSpan(pos, 14);
            int channels = BinaryPrimitives.ReadUInt16LittleEndian(header);
            int dch = BinaryPrimitives.ReadUInt16LittleEndian(header[2..]);
            int fs = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
            int frames = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
            bool raw = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]) != 0;
            int start = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
            int end = BinaryPrimitives.ReadUInt16LittleEndian(header[12..]);
            pos += 14;
            CeltDecoder decoder = new(dch);
            if (raw)
            {
                decoder.StartBand = start;
                decoder.EndBand = end;
                decoder.StreamChannels = channels;
            }

            float[] pcm = new float[fs * dch];
            double maxError = 0;
            double signal = 0, noise = 0;
            for (int f = 0; f < frames; f++)
            {
                int len = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(pos));
                bool lost = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(pos + 2)) != 0;
                pos += 4;
                byte[] packet = file.AsSpan(pos, len).ToArray();
                pos += len;
                uint range = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(pos));
                pos += 4;
                int ret;
                if (lost)
                {
                    ret = decoder.Decode(null, 0, 0, pcm, fs, null, false);
                }
                else if (raw)
                {
                    ret = decoder.Decode(packet, 0, len, pcm, fs, null, false);
                }
                else
                {
                    // An Opus CELT-only packet with one frame: the TOC byte gives the bandwidth and the coded channels.
                    int config = packet[0] >> 3;
                    Assert.True(config >= 16 && (packet[0] & 3) == 0);
                    decoder.StreamChannels = (packet[0] & 4) != 0 ? 2 : 1;
                    decoder.EndBand = config switch
                    {
                        < 20 => 13,
                        < 24 => 17,
                        < 28 => 19,
                        _ => 21,
                    };
                    ret = decoder.Decode(packet, 1, len - 1, pcm, fs, null, false);
                }

                Assert.Equal(fs, ret);
                if (!lost && decoder.FinalRange != range)
                {
                    failures.Add($"stream {s} frame {f}: final range {decoder.FinalRange:x8}, libopus {range:x8}");
                }

                for (int i = 0; i < fs * dch; i++)
                {
                    double expected;
                    double actual = pcm[i];
                    if (shorts)
                    {
                        expected = BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(pos + (2 * i))) / 32768.0;
                        actual = Math.Clamp(Math.Round(pcm[i] * 32768.0, MidpointRounding.ToEven), -32768, 32767) / 32768.0;
                    }
                    else
                    {
                        expected = BitConverter.ToSingle(file, pos + (4 * i));
                    }

                    double d = actual - expected;
                    maxError = Math.Max(maxError, Math.Abs(d));
                    signal += expected * expected;
                    noise += d * d;
                }

                pos += (shorts ? 2 : 4) * fs * dch;
            }

            // The decode matches libopus's x86-64 float build bit for bit today; the tolerance leaves room for other math libraries.
            double snr = noise == 0 ? double.PositiveInfinity : 10 * Math.Log10(signal / noise);
            if (maxError > 1e-4 || snr < 80)
            {
                failures.Add($"stream {s}: max error {maxError:g3}, SNR {snr:f1} dB");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static uint CountPvq(int n, int k)
    {
        // V(N, K) = V(N-1, K) + V(N, K-1) + V(N-1, K-1), V(N, 0) = 1, V(0, K>0) = 0.
        ulong[,] v = new ulong[n + 1, k + 1];
        for (int i = 0; i <= n; i++)
        {
            v[i, 0] = 1;
        }

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= k; j++)
            {
                v[i, j] = v[i - 1, j] + v[i, j - 1] + v[i - 1, j - 1];
            }
        }

        return (uint)v[n, k];
    }
}
