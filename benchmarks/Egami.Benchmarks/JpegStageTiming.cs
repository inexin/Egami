using Egami.Formats.Jpeg;
using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>
/// Step 2.4.10 stage 0: the baseline JPEG encoder's stages run one at a time over a whole image, with the encoder's own kernels and
/// buffer shapes (one MCU row of planes, as JpegWriter's workspace holds), so ImageTiming and count-instructions.py can time and
/// count each alone. The stages are colour conversion (with RgbRow's Rgba8-to-Rgb8 copy as its own stage, since an Rgb8 source
/// needs none), edge padding, chroma downsampling, FDCT with quantization, Huffman coding and the symbol counting of optimized
/// tables. The data-independent stages (all but coding and counting) work on the real photo rows but leave their output in the one
/// workspace, as the encoder does; coding and counting read every quantized block of the photo, made once beforehand. What the
/// whole encode spends beyond the sum of the stages is the MCU traversal, row hand-over and headers.
/// </summary>
internal sealed class JpegStageTiming
{
    /// <summary>The stage names, as the scenario names end.</summary>
    internal static readonly string[] Stages = ["rgbrow-rgba8", "color", "pad", "downsample", "fdct", "count", "huffman"];

    private readonly Raster<Rgb8> _image;
    private readonly Raster<Rgba8> _rgba;
    private readonly int _factor;
    private readonly int _width;
    private readonly int _height;
    private readonly int _paddedWidth;
    private readonly int _mcusX;
    private readonly int _mcusY;
    private readonly int _rows;
    private readonly byte[] _luma;
    private readonly byte[] _fullCb;
    private readonly byte[] _fullCr;
    private readonly byte[] _cb;
    private readonly byte[] _cr;
    private readonly byte[] _rgb;
    private readonly int[][] _divisors;
    private readonly ushort[][] _reciprocals;
    private readonly short[][] _blocks;
    private readonly short[] _scratch;
    private readonly JpegHuffmanCodes[] _dc;
    private readonly JpegHuffmanCodes[] _ac;

    internal JpegStageTiming(Raster<Rgb8> image, Raster<Rgba8> rgba, int quality, bool subsample, bool optimize)
    {
        _image = image;
        _rgba = rgba;
        _factor = subsample ? 2 : 1;
        _width = image.Width;
        _height = image.Height;
        _mcusX = (_width + (8 * _factor) - 1) / (8 * _factor);
        _mcusY = (_height + (8 * _factor) - 1) / (8 * _factor);
        _paddedWidth = _mcusX * 8 * _factor;
        _rows = 8 * _factor;
        _luma = new byte[_paddedWidth * _rows];
        _fullCb = new byte[_paddedWidth * _rows];
        _fullCr = new byte[_paddedWidth * _rows];
        _cb = new byte[_paddedWidth / _factor * 8];
        _cr = new byte[_paddedWidth / _factor * 8];
        _rgb = new byte[_width * 3];
        int factor = quality < 50 ? 5000 / quality : 200 - (2 * quality);
        _divisors = [Divisors(JpegTables.LumaQuant, factor), Divisors(JpegTables.ChromaQuant, factor)];
        _reciprocals = [JpegFdct.Reciprocals(_divisors[0]), JpegFdct.Reciprocals(_divisors[1])];

        // Every MCU row's quantized blocks, in MCU order (luma blocks, then Cb, then Cr), for the coding and counting stages.
        _blocks = new short[_mcusY][];
        for (int my = 0; my < _mcusY; my++)
        {
            Convert(my, color: true, pad: true, downsample: true);
            _blocks[my] = new short[BlocksPerMcu * _mcusX * 64];
            Transform(my, _blocks[my]);
        }

        // The FDCT stage's own output: the coding stages read the photo's blocks.
        _scratch = new short[_blocks[0].Length];
        _dc = [JpegHuffmanCodes.DcLuma, JpegHuffmanCodes.DcChroma];
        _ac = [JpegHuffmanCodes.AcLuma, JpegHuffmanCodes.AcChroma];
        if (optimize)
        {
            long[] counts = Count();
            for (int t = 0; t < 2; t++)
            {
                _dc[t] = JpegHuffmanCodes.Optimal(counts.AsSpan(t * 512, 256));
                _ac[t] = JpegHuffmanCodes.Optimal(counts.AsSpan((t * 512) + 256, 256));
            }
        }
    }

    private int BlocksPerMcu => (_factor * _factor) + 2;

    /// <summary>Returns the operation that runs <paramref name="stage"/> over the whole image once.</summary>
    internal Action Stage(string stage) => stage switch
    {
        "rgbrow-rgba8" => RgbRows,
        "color" => () => Rows(color: true, pad: false, downsample: false),
        "pad" => () => Rows(color: false, pad: true, downsample: false),
        "downsample" => () => Rows(color: false, pad: false, downsample: true),
        "fdct" => TransformAll,
        "count" => () => Count(),
        "huffman" => Code,
        _ => throw new ArgumentException($"No JPEG stage {stage}.", nameof(stage)),
    };

    /// <summary>RgbRow's copy of an Rgba8 source to Rgb8, row by row; an Rgb8 source is used as it is.</summary>
    private void RgbRows()
    {
        for (int y = 0; y < _mcusY * _rows; y++)
        {
            PixelConvert.Convert<Rgba8, Rgb8>(_rgba.Row(Math.Min(y, _height - 1)), System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Rgb8>(_rgb.AsSpan()));
        }
    }

    private void TransformAll()
    {
        for (int my = 0; my < _mcusY; my++)
        {
            Transform(my, _scratch);
        }
    }

    /// <summary>Huffman-codes every block, as CodeRow does, to a null stream.</summary>
    private void Code()
    {
        using JpegBitWriter writer = new(Stream.Null);
        Span<int> predictors = stackalloc int[3];
        for (int my = 0; my < _mcusY; my++)
        {
            short[] row = _blocks[my];
            int b = 0;
            for (int mx = 0; mx < _mcusX; mx++)
            {
                for (int i = 0; i < 3; i++)
                {
                    int t = i == 0 ? 0 : 1;
                    for (int n = 0, count = i == 0 ? _factor * _factor : 1; n < count; n++, b++)
                    {
                        JpegBlockCoder.Encode(writer, row.AsSpan(b * 64, 64), ref predictors[i], _dc[t], _ac[t]);
                    }
                }
            }
        }

        writer.Align();
        writer.Flush();
    }

    private void Rows(bool color, bool pad, bool downsample)
    {
        for (int my = 0; my < _mcusY; my++)
        {
            Convert(my, color, pad, downsample);
        }
    }

    /// <summary>ConvertRows for one MCU row of an Rgb8 image: the stages asked for, as JpegWriter runs them.</summary>
    private void Convert(int my, bool color, bool pad, bool downsample)
    {
        bool subsampled = _factor > 1;
        for (int r = 0; r < _rows; r++)
        {
            int y = Math.Min((my * _rows) + r, _height - 1);
            Span<byte> lumaRow = _luma.AsSpan(r * _paddedWidth, _paddedWidth);
            Span<byte> cb = subsampled ? _fullCb.AsSpan(r * _paddedWidth, _paddedWidth) : _cb.AsSpan(r * _paddedWidth, _paddedWidth);
            Span<byte> cr = subsampled ? _fullCr.AsSpan(r * _paddedWidth, _paddedWidth) : _cr.AsSpan(r * _paddedWidth, _paddedWidth);
            if (color)
            {
                ReadOnlySpan<byte> rgb = System.Runtime.InteropServices.MemoryMarshal.AsBytes(_image.Row(y));
                JpegEncodeKernels.YCbCr(rgb, lumaRow[.._width], cb[.._width], cr[.._width]);
            }

            if (pad)
            {
                lumaRow[_width..].Fill(lumaRow[_width - 1]);
                cb[_width..].Fill(cb[_width - 1]);
                cr[_width..].Fill(cr[_width - 1]);
            }
        }

        if (downsample && subsampled)
        {
            Downsample(_fullCb, my, _cb);
            Downsample(_fullCr, my, _cr);
        }
    }

    private void Downsample(byte[] full, int my, byte[] plane)
    {
        int width = _paddedWidth / 2;
        int last = ((_height + 1) / 2) - 1 - (my * 8);
        for (int r = 0; r < 8; r++)
        {
            Span<byte> output = plane.AsSpan(r * width, width);
            if (r > last)
            {
                plane.AsSpan(last * width, width).CopyTo(output);
                continue;
            }

            JpegEncodeKernels.Downsample(full.AsSpan(r * 2 * _paddedWidth, _paddedWidth), full.AsSpan(((r * 2) + 1) * _paddedWidth, _paddedWidth), true, output);
        }
    }

    /// <summary>TransformRow's FDCT and quantization of the workspace as MCU row <paramref name="my"/> into <paramref name="blocks"/>, in MCU order.</summary>
    private void Transform(int my, short[] blocks)
    {
        int blocksWide = (_width + 7) / 8, blocksHigh = (_height + 7) / 8;
        int chromaStride = _paddedWidth / _factor;
        int b = 0;
        for (int mx = 0; mx < _mcusX; mx++)
        {
            for (int v = 0; v < _factor; v++)
            {
                for (int h = 0; h < _factor; h++, b++)
                {
                    int bx = (mx * _factor) + h, by = (my * _factor) + v;
                    if (bx >= blocksWide || by >= blocksHigh)
                    {
                        // Padding blocks outside the image (libjpeg's dummy blocks): flat, with the DC of the block before.
                        blocks.AsSpan(b * 64, 64).Clear();
                        blocks[b * 64] = blocks[(b - 1) * 64];
                        continue;
                    }

                    Forward(_luma.AsSpan((v * 8 * _paddedWidth) + (((mx * _factor) + h) * 8)), _paddedWidth, 0, blocks.AsSpan(b * 64, 64));
                }
            }

            Forward(_cb.AsSpan(mx * 8), chromaStride, 1, blocks.AsSpan(b++ * 64, 64));
            Forward(_cr.AsSpan(mx * 8), chromaStride, 1, blocks.AsSpan(b++ * 64, 64));
        }
    }

    private void Forward(ReadOnlySpan<byte> samples, int stride, int table, Span<short> block)
    {
        if (System.Runtime.Intrinsics.X86.Avx2.IsSupported)
        {
            JpegFdct.ForwardAvx2(samples, stride, _reciprocals[table], block);
        }
        else
        {
            JpegFdct.Forward(samples, stride, _divisors[table], block);
        }
    }

    /// <summary>The symbol counts of every block, as the optimized tables' first pass makes them: DC then AC, per table.</summary>
    private long[] Count()
    {
        long[] counts = new long[4 * 256];
        Span<int> predictors = stackalloc int[3];
        for (int my = 0; my < _mcusY; my++)
        {
            short[] row = _blocks[my];
            int b = 0;
            for (int mx = 0; mx < _mcusX; mx++)
            {
                for (int i = 0; i < 3; i++)
                {
                    int t = i == 0 ? 0 : 1;
                    for (int n = 0, count = i == 0 ? _factor * _factor : 1; n < count; n++, b++)
                    {
                        JpegBlockCoder.Count(row.AsSpan(b * 64, 64), ref predictors[i], counts.AsSpan(t * 512, 256), counts.AsSpan((t * 512) + 256, 256));
                    }
                }
            }
        }

        return counts;
    }

    private static int[] Divisors(ReadOnlySpan<byte> example, int factor)
    {
        int[] divisors = new int[64];
        for (int i = 0; i < 64; i++)
        {
            divisors[i] = Math.Clamp(((example[i] * factor) + 50) / 100, 1, 255) * 8;
        }

        return divisors;
    }
}
