using BenchmarkDotNet.Attributes;

using Egami.Formats;
using Egami.Formats.Jbig2;

namespace Egami.Benchmarks;

/// <summary>
/// The primitives JBIG2 and JPEG 2000 share (2.3.4 task 3): MQ decoding of 1 MB of skewed decisions in 16 contexts, and
/// compositing a 300 dpi letter-size stripe onto a page at an unaligned offset with each operator.
/// </summary>
public class BilevelBenchmarks
{
    private readonly byte[] _coded = new byte[1 << 20];
    private readonly byte[] _contexts = new byte[16];
    private readonly MqDecoder _mq = new();
    private Jbig2Bitmap _page = null!;
    private Jbig2Bitmap _region = null!;

    [Params(0, 4)]
    public int Op { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(5);
        for (int i = 0; i < _coded.Length; i++)
        {
            // Mostly small bytes keep most decisions on the MPS path, as in scanned text.
            _coded[i] = (byte)(random.Next(8) == 0 ? random.Next(0x90) : random.Next(16));
        }

        _page = new Jbig2Bitmap(2550, 3300);
        _region = new Jbig2Bitmap(2400, 400);
        for (int y = 0; y < _region.Height; y++)
        {
            for (int x = 0; x < _region.Width; x += 3)
            {
                _region.Set(x, y, random.Next(2));
            }
        }
    }

    [Benchmark]
    public int MqDecode()
    {
        _mq.Start(_coded, 0, _coded.Length);
        Array.Clear(_contexts);
        int sum = 0;
        for (int i = 0; i < 8 << 20; i++)
        {
            sum += _mq.Decode(ref _contexts[i & 15]);
        }

        return sum;
    }

    [Benchmark]
    public void Compose()
    {
        _page.Compose(_region, 77, 1000, (Jbig2Combine)Op);
    }
}
