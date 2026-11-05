using System.Runtime.InteropServices;

using Egami.Pixels;

namespace Egami.Formats.Extended.JpegLs;

/// <summary>
/// Writes JPEG-LS from samples that are not pixels, as DICOM and other containers store JPEG-LS streams: any bit depth from 2 to
/// 16 and 1 to 4 components, coded exactly as saving the same samples as pixels with <see cref="JpegLsWriterOptions"/> codes
/// them.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="jpegls-samples"/>
/// </example>
public static class JpegLsWriter
{
    /// <summary>
    /// Writes <paramref name="samples"/>, rows of <paramref name="width"/> pixels of <paramref name="components"/> samples each,
    /// side by side, as a whole JPEG-LS stream: SOI, the frame header, the scans and EOI, with no other segment except the colour
    /// transform's APP8 when one is asked for. Every sample must be below 2^<paramref name="bitsPerSample"/>. The components
    /// get the ids 1 to <paramref name="components"/>. <see cref="JpegLsWriterOptions.BitsPerSample"/> is not used.
    /// </summary>
    /// <param name="output">Where to write. It is left open.</param>
    /// <param name="samples">The samples, row after row, the components of each pixel next to each other.</param>
    /// <param name="components">The components of a pixel, 1 to 4.</param>
    /// <param name="width">The pixels in a row, at least 1.</param>
    /// <param name="bitsPerSample">The bits of a sample, 2 to 16.</param>
    /// <param name="options">NEAR, the interleave mode, the colour transform and the restart interval; <see langword="null"/> for the defaults.</param>
    /// <exception cref="ArgumentOutOfRangeException">A count, the bits or NEAR is out of range.</exception>
    /// <exception cref="ArgumentException"><paramref name="samples"/> is not whole rows, or a sample is too large.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="jpegls-samples"/>
    /// </example>
    public static void Write(Stream output, ReadOnlySpan<ushort> samples, int components, int width, int bitsPerSample, JpegLsWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfLessThan(components, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(components, 4);
        ArgumentOutOfRangeException.ThrowIfLessThan(bitsPerSample, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bitsPerSample, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        options ??= JpegLsWriterOptions.Default;
        int near = CheckNear(options.Near ?? 0, bitsPerSample, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegative(options.RestartInterval, nameof(options));
        long stride = (long)width * components;
        if (samples.IsEmpty || samples.Length % stride != 0)
        {
            throw new ArgumentException($"JPEG-LS: {samples.Length} samples are not whole rows of {stride}.", nameof(samples));
        }

        int max = (1 << bitsPerSample) - 1;
        if (bitsPerSample < 16 && samples.ContainsAnyExceptInRange((ushort)0, (ushort)max))
        {
            throw new ArgumentException($"JPEG-LS: a sample is above {max}, the largest {bitsPerSample}-bit value.", nameof(samples));
        }

        int height = (int)(samples.Length / stride);
        JpegLsEncoder encoder = new(output, width, height, components, bitsPerSample, near, options.Interleave, options.ColorTransform, options.RestartInterval);
        for (int y = 0; y < height; y++)
        {
            encoder.Row(samples.Slice((int)(y * stride), (int)stride));
        }

        encoder.Complete();
    }

    /// <summary>Returns NEAR after checking it against the sample depth (T.87 C.2.3: at most min(255, MAXVAL / 2)).</summary>
    internal static int CheckNear(int near, int bits, string parameter)
    {
        int most = Math.Min(255, ((1 << bits) - 1) / 2);
        if (near < 0 || near > most)
        {
            throw new ArgumentOutOfRangeException(parameter, near, $"JPEG-LS: NEAR is 0 to {most} for {bits}-bit samples.");
        }

        return near;
    }
}

/// <summary>
/// Saves rasters as JPEG-LS with their channels: gray, gray and alpha, RGB or RGBA, at 8 bits or (for pixels wider than 8 bits)
/// 16, or at <see cref="JpegLsWriterOptions.BitsPerSample"/>. Line- and sample-interleaved files are coded as the rows come;
/// non-interleaved ones keep the rows until the end.
/// </summary>
internal sealed class JpegLsFormatWriter : ExtendedWriter
{
    protected override FormatInfo Format => JpegLsFormat.Info;

    public override RowEncoder<TPixel> Start<TPixel>(Stream output, EncodeStart<TPixel> start)
    {
        JpegLsWriterOptions options = start.Options as JpegLsWriterOptions ?? JpegLsWriterOptions.Default;
        start.ReportLosses("JPEG-LS", SaveKeeps.Alpha | SaveKeeps.Wide);
        PixelLayoutInfo layout = TPixel.Layout;
        bool wide = layout.BitsPerChannel > 8;
        int channels = layout.ChannelOrder switch
        {
            PixelChannelOrder.Gray => 1,
            PixelChannelOrder.GrayAlpha => 2,
            _ => layout.HasAlpha ? 4 : 3,
        };
        JpegLsDetails? details = start.Metadata.FormatDetails as JpegLsDetails;
        int bits = options.BitsPerSample
            ?? (details is { BitsPerSample: >= 2 and <= 16 } d && (d.BitsPerSample > 8) == wide ? d.BitsPerSample : wide ? 16 : 8);
        ArgumentOutOfRangeException.ThrowIfLessThan(bits, 2, nameof(start));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bits, 16, nameof(start));
        // NEAR from the details is a hint, kept within what the depth allows; asked for in the options, it must fit.
        int near = options.Near is int asked
            ? JpegLsWriter.CheckNear(asked, bits, nameof(start))
            : Math.Clamp(details?.Near ?? 0, 0, Math.Min(255, ((1 << bits) - 1) / 2));
        return (channels, wide) switch
        {
            (1, false) => new Encoder<TPixel, Gray8>(output, start, options, bits, near),
            (2, false) => new Encoder<TPixel, GrayAlpha8>(output, start, options, bits, near),
            (3, false) => new Encoder<TPixel, Rgb8>(output, start, options, bits, near),
            (4, false) => new Encoder<TPixel, Rgba8>(output, start, options, bits, near),
            (1, true) => new Encoder<TPixel, Gray16>(output, start, options, bits, near),
            (2, true) => new Encoder<TPixel, GrayAlpha16>(output, start, options, bits, near),
            (3, true) => new Encoder<TPixel, Rgb16>(output, start, options, bits, near),
            _ => new Encoder<TPixel, Rgba16>(output, start, options, bits, near),
        };
    }

    private sealed class Encoder<TPixel, TFile> : RowEncoder<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
        where TFile : unmanaged, IPixelType<TFile>
    {
        private readonly JpegLsEncoder _encoder;
        private readonly TFile[] _row;
        private readonly ushort[] _samples;
        private readonly ushort[]? _narrow;
        private readonly bool _wide;

        public Encoder(Stream output, EncodeStart<TPixel> start, JpegLsWriterOptions options, int bits, int near)
        {
            int channels = TFile.Layout.ChannelCount;
            _wide = TFile.Layout.BitsPerChannel > 8;
            _row = new TFile[start.Width];
            _samples = new ushort[start.Width * channels];
            int source = _wide ? 16 : 8;
            if (bits != source)
            {
                // Round each sample to the written depth.
                int from = (1 << source) - 1, to = (1 << bits) - 1;
                _narrow = new ushort[from + 1];
                for (int v = 0; v <= from; v++)
                {
                    _narrow[v] = (ushort)((((long)v * to) + (from >> 1)) / from);
                }
            }

            _encoder = new JpegLsEncoder(output, start.Width, start.Height, channels, bits, near, options.Interleave, options.ColorTransform, options.RestartInterval);
        }

        public override void WriteRow(ReadOnlySpan<TPixel> row)
        {
            PixelConvert.Convert<TPixel, TFile>(row, _row);
            Span<ushort> samples = _samples;
            if (_wide)
            {
                MemoryMarshal.Cast<TFile, ushort>(_row).CopyTo(samples);
            }
            else
            {
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(_row.AsSpan());
                for (int i = 0; i < samples.Length; i++)
                {
                    samples[i] = bytes[i];
                }
            }

            if (_narrow is not null)
            {
                for (int i = 0; i < samples.Length; i++)
                {
                    samples[i] = _narrow[samples[i]];
                }
            }

            _encoder.Row(samples);
        }

        public override void Complete() => _encoder.Complete();
    }
}
