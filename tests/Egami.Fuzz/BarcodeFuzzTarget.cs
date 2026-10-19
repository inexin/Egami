using System.Text;

using Egami.Barcodes;
using Egami.Pixels;

namespace Egami.Fuzz;

/// <summary>
/// The barcode fuzz targets (Phase 2.11 task 21): one per encoder and one for the reader. An encoder target takes an options
/// byte and the text; the encoder may only throw the <see cref="ArgumentException"/> or <see cref="FormatException"/> it
/// documents, and every symbol it makes must read back, rendered pure, as the text it was given. The reader target takes a
/// small luminance image (effort, flags, width, then the pixels) and must never throw.
/// </summary>
internal static class BarcodeFuzzTarget
{
    public static void Encode(string kind, byte[] data)
    {
        if (data.Length < 2)
        {
            return;
        }

        int o = data[0];
        string text = Encoding.Latin1.GetString(data, 1, data.Length - 1);
        Barcode barcode;
        string? expected = text;
        try
        {
            switch (kind)
            {
                case "qr":
                    barcode = Barcode.Qr(text, new QrOptions { ErrorCorrection = (QrErrorCorrection)(o & 3), Version = (o & 4) != 0 ? 1 + (o >> 3) : null });
                    break;
                case "datamatrix":
                    barcode = (o & 8) != 0
                        ? Barcode.DataMatrix(Gs1.Parse(text), new DataMatrixOptions { Shape = (DataMatrixShape)(o % 3) })
                        : Barcode.DataMatrix(text, new DataMatrixOptions { Shape = (DataMatrixShape)(o % 3), AllowDmre = (o & 4) != 0 });
                    if ((o & 8) != 0)
                    {
                        expected = null;
                    }

                    break;
                case "pdf417":
                    barcode = Barcode.Pdf417(text, new Pdf417Options { Compact = (o & 1) != 0, ErrorLevel = (o & 2) != 0 ? (o >> 2) % 9 : null, Columns = (o & 128) != 0 ? 1 + ((o >> 2) % 30) : null });
                    break;
                case "aztec":
                    barcode = (o & 8) != 0
                        ? Barcode.AztecRune(o + data[^1])
                        : Barcode.Aztec(text, new AztecOptions { Compact = (o & 3) switch { 0 => null, 1 => true, _ => false }, ErrorCorrectionPercent = (o & 4) != 0 ? 5 + ((o >> 4) * 5) : null });
                    if ((o & 8) != 0)
                    {
                        expected = ((o + data[^1]) % 256).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }

                    break;
                case "code128":
                    barcode = (o & 1) != 0 ? Barcode.Gs1Code128(text) : Barcode.Code128(text);
                    if ((o & 1) != 0)
                    {
                        expected = null;
                    }

                    break;
                case "code39":
                    // Without Full ASCII forced, a text with $, %, / or + pairs reads back as the characters they stand for.
                    barcode = Barcode.Code39(text, new Code39Options { CheckDigit = (o & 1) != 0, FullAscii = (o & 2) != 0 ? true : null });
                    expected = null;
                    break;
                case "itf":
                    barcode = (o & 1) != 0 ? Barcode.Itf14(text) : Barcode.Itf(text, new ItfOptions { BearerBars = (o & 2) != 0 });
                    expected = null;
                    break;
                default:
                    barcode = (o % 4) switch
                    {
                        0 => Barcode.Ean13(text, new EanUpcOptions { AddOn = (o & 4) != 0 ? "12" : null }),
                        1 => Barcode.Ean8(text),
                        2 => Barcode.UpcA(text),
                        _ => Barcode.UpcE(text),
                    };
                    expected = null;
                    break;
            }
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            return;
        }

        // The symbol reads back as what went in; the reader takes ITF of 4 digits or more only, as shorter ones are too weak.
        if (barcode.Format == BarcodeFormat.Itf && barcode.HumanReadableText?.Length < 4)
        {
            return;
        }

        using Raster<Gray8> raster = barcode.ToRaster(pixelsPerModule: 2);
        BarcodeResult? read = raster.ReadBarcode(new BarcodeReaderOptions { PureBarcode = true, Formats = barcode.Format });
        if (read is null)
        {
            throw new InvalidOperationException($"{barcode.Format} symbol did not read back.");
        }

        // The bytes, not the text: without an ECI the reader takes bytes that happen to be valid UTF-8 as UTF-8, as documented.
        if (expected is not null && !read.Bytes.Span.SequenceEqual(Encoding.Latin1.GetBytes(expected)) && read.Text != expected)
        {
            throw new InvalidOperationException($"{barcode.Format} symbol read back as '{read.Text}' instead of '{expected}'.");
        }
    }

    public static void Read(byte[] data)
    {
        if (data.Length < 5)
        {
            return;
        }

        int effort = data[0] % 11, flags = data[1];
        int width = 1 + ((data[2] | (data[3] << 8)) % 256);
        int height = Math.Min((data.Length - 4) / width, 256);
        if (height < 1)
        {
            return;
        }

        var options = new BarcodeReaderOptions
        {
            Effort = effort,
            PureBarcode = (flags & 1) != 0,
            TryInverted = (flags & 2) != 0,
            Formats = (flags & 4) != 0 ? BarcodeFormat.Matrix : (flags & 8) != 0 ? BarcodeFormat.Linear : BarcodeFormat.All,
            Region = (flags & 16) != 0 ? new Rect(width / 4, height / 4, width / 2 + 1, height / 2 + 1) : null,
        };
        _ = BarcodeReader.ReadBarcodes(data.AsSpan(4, width * height), width, height, width, options);
    }
}
