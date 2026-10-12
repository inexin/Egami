using System.Globalization;

using Egami.Pixels;

namespace Egami.Formats.Jpeg;

/// <summary>Coefficient mode, for lossless transforms: every scan decoded into the quantized blocks, and no further.</summary>
internal sealed partial class JpegDecoder<TPixel>
    where TPixel : unmanaged, IPixelType<TPixel>
{
    /// <summary>Gets the MCUs across the frame.</summary>
    internal int McusX => _mcusX;

    /// <summary>Gets the MCU rows of the frame.</summary>
    internal int McusY => _mcusY;

    /// <summary>
    /// Decodes the scans from the first, whose header has just been read, to the end into the coefficients of every component,
    /// as a progressive file is, whatever the file's mode; no block goes through the IDCT. Damage is handled as in a load: the
    /// blocks not reached stay zero, with a warning.
    /// </summary>
    internal void DecodeCoefficients(EgamiInput input)
    {
        JpegBitReader reader = new(input);
        try
        {
            _arithmetic = _header.Arithmetic ? new JpegArithmeticDecoder(reader, _header) : null;
            foreach (Component c in _components)
            {
                c.AllocateCoefficients(input.Memory, _mcusX * c.H, _mcusY * c.V);
            }

            JpegScan scan = _header.FirstScan!;
            int scans = 0;
            while (true)
            {
                if (++scans > MaxScans)
                {
                    throw new CorruptDataException(JpegFormat.Info.Name, -1, string.Create(CultureInfo.InvariantCulture, $"The file has more than {MaxScans:N0} scans."));
                }

                input.CancellationToken.ThrowIfCancellationRequested();
                if (!DecodeBufferedScan(reader, scan) || NextScan(input, reader) is not JpegScan next)
                {
                    break;
                }

                scan = next;
            }

            for (int i = 0; i < _components.Length; i++)
            {
                LatchQuant(i);
            }
        }
        finally
        {
            reader.Dispose();
        }
    }

    /// <summary>Returns the sampling factors the decoder uses for component <paramref name="index"/>: 1 × 1 for a one-component frame.</summary>
    internal (int H, int V) Sampling(int index) => (_components[index].H, _components[index].V);

    /// <summary>Returns the quantized coefficients of a block of component <paramref name="index"/>, in zigzag order.</summary>
    internal Span<short> CoefficientBlock(int index, int bx, int by) => _components[index].Block(bx, by);
}
