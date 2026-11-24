using System.Runtime.InteropServices;

using Egami.Formats.Ico;
using Egami.Formats.Jpeg;
using Egami.Pixels;

namespace Egami.Formats.Eps;

/// <summary>
/// Photoshop's own image in an EPS file, which its <c>%ImageData</c> comment describes (Adobe's Photoshop file format
/// specification; Pillow reads the same comment): 1 or 8 bits a channel in gray or bitmap, RGB or CMYK, pixel-interleaved
/// (one byte a block) or line-interleaved (a row a block), binary or hex, or a JPEG (DCT) stream when Photoshop saved it so.
/// The samples are those the PostScript image operator draws: gray and bitmap 0 black, CMYK 0 no ink.
/// </summary>
internal sealed partial class EpsReader
{
    /// <summary>Decodes Photoshop's image in its natural pixel type: <see cref="Gray8"/>, <see cref="Rgb8"/> or <see cref="Cmyk8"/>.</summary>
    private static Raster PhotoshopNatural(EgamiInput input, EpsFile file, ReaderOptions options)
    {
        PhotoshopImageSection image = file.Photoshop!;
        if (Jpeg(file) is ReadOnlyMemory<byte> jpeg)
        {
            return IcoReader.WithInput(input, jpeg, sub => JpegReader.Instance.Read(sub, Inner(options)));
        }

        return image.Mode switch
        {
            3 => Photoshop<Rgb8>(input, file, options),
            4 => Photoshop<Cmyk8>(input, file, options),
            _ => Photoshop<Gray8>(input, file, options),
        };
    }

    /// <summary>Decodes Photoshop's image into <typeparamref name="TPixel"/>.</summary>
    private static Raster<TPixel> Photoshop<TPixel>(EgamiInput input, EpsFile file, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PhotoshopImageSection image = file.Photoshop!;
        if (Jpeg(file) is ReadOnlyMemory<byte> jpeg)
        {
            return IcoReader.WithInput(input, jpeg, sub => JpegReader.Instance.Read<TPixel>(sub, Inner(options)));
        }

        input.Limits.CheckFrameSize(image.Width, image.Height);
        int channels = image.Mode switch { 3 => 3, 4 => 4, _ => 1 };
        int stored = channels + image.PadChannels;
        long rowBytes = (((long)image.Width * image.Depth) + 7) / 8;
        bool interleaved = image.BlockSize == 1 && image.Depth == 8 && stored > 1;
        if (!interleaved && image.BlockSize != rowBytes)
        {
            throw new CorruptDataException(EpsFormat.Info.Name, -1, $"The %ImageData block size {image.BlockSize} is neither 1 nor a row's {rowBytes} bytes.");
        }

        long fileRow = interleaved ? (long)image.Width * stored : rowBytes * stored;
        if (fileRow > Array.MaxLength)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxWidth), input.Limits.MaxWidth, image.Width);
        }

        ImageData data = new(file.PostScript.Span[image.DataStart..], image.Encoding == 2);
        Raster<TPixel> raster = input.CreateRaster<TPixel>(image.Width, image.Height);
        try
        {
            byte[] row = new byte[fileRow];
            byte[] pixels = new byte[(long)image.Width * channels];
            bool missing = false;
            for (int y = 0; y < image.Height; y++)
            {
                input.CancellationToken.ThrowIfCancellationRequested();
                int read = data.Read(row);
                if (read < row.Length)
                {
                    row.AsSpan(read).Clear();
                    missing = true;
                }

                if (image.Depth == 1)
                {
                    // A bitmap: 1 is white, as the image operator draws one bit with its default decode.
                    for (int x = 0; x < image.Width; x++)
                    {
                        pixels[x] = (row[x >> 3] & (0x80 >> (x & 7))) != 0 ? (byte)255 : (byte)0;
                    }
                }
                else if (interleaved)
                {
                    for (int x = 0; x < image.Width; x++)
                    {
                        row.AsSpan(x * stored, channels).CopyTo(pixels.AsSpan(x * channels));
                    }
                }
                else
                {
                    for (int c = 0; c < channels; c++)
                    {
                        ReadOnlySpan<byte> plane = row.AsSpan((int)(c * rowBytes), image.Width);
                        for (int x = 0; x < image.Width; x++)
                        {
                            pixels[(x * channels) + c] = plane[x];
                        }
                    }
                }

                Span<TPixel> target = raster.Row(y);
                switch (channels)
                {
                    case 3:
                        PixelConvert.Convert<Rgb8, TPixel>(MemoryMarshal.Cast<byte, Rgb8>(pixels), target);
                        break;
                    case 4:
                        PixelConvert.Convert<Cmyk8, TPixel>(MemoryMarshal.Cast<byte, Cmyk8>(pixels), target);
                        break;
                    default:
                        PixelConvert.Convert<Gray8, TPixel>(MemoryMarshal.Cast<byte, Gray8>(pixels), target);
                        break;
                }
            }

            if (missing)
            {
                if (options.Recovery == RecoveryMode.Strict)
                {
                    throw new CorruptDataException(EpsFormat.Info.Name, -1, "Photoshop's image data ends before its last row.") { IsTruncation = true };
                }

                input.Warn(raster, "Eps.ImageDataTruncated", "Photoshop's image data ends before its last row; the missing samples are 0.");
            }

            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Returns Photoshop's image as a JPEG stream when the data after its start line is one (FF D8 FF), binary or hex, or null.
    /// A hex stream runs to the first line that is not hex.
    /// </summary>
    private static ReadOnlyMemory<byte>? Jpeg(EpsFile file)
    {
        PhotoshopImageSection image = file.Photoshop!;
        ReadOnlyMemory<byte> data = file.PostScript[image.DataStart..];
        ReadOnlySpan<byte> span = data.Span;
        if (span.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return data;
        }

        int first = span.IndexOfAnyExcept(" \t\r\n%"u8);
        if (first < 0 || !span[first..].StartsWith("FFD8FF"u8) && !span[first..].StartsWith("ffd8ff"u8))
        {
            return null;
        }

        int end = first;
        while (end < span.Length)
        {
            int lineEnd = EpsFile.LineEnd(span, end);
            ReadOnlySpan<byte> line = span[end..lineEnd].Trim(" \t%"u8);
            if (line.IndexOfAnyExcept(EpsFile.HexDigits) >= 0)
            {
                break;
            }

            end = EpsFile.NextLine(span, lineEnd);
        }

        byte[] bytes = new byte[EpsFile.CountHex(span[first..end]) / 2];
        EpsFile.DecodeHex(span[first..end], bytes);
        return bytes;
    }

    /// <summary>Photoshop's image data from its start line on: bytes as they are, or hex digits two to a byte, skipping the rest.</summary>
    private ref struct ImageData(ReadOnlySpan<byte> data, bool hex)
    {
        private ReadOnlySpan<byte> _data = data;

        public int Read(Span<byte> destination)
        {
            if (!hex)
            {
                int n = Math.Min(destination.Length, _data.Length);
                _data[..n].CopyTo(destination);
                _data = _data[n..];
                return n;
            }

            int written = 0, high = -1, i = 0;
            for (; i < _data.Length && written < destination.Length; i++)
            {
                int c = _data[i];
                int nibble = c switch
                {
                    >= '0' and <= '9' => c - '0',
                    >= 'A' and <= 'F' => c - 'A' + 10,
                    >= 'a' and <= 'f' => c - 'a' + 10,
                    _ => -1,
                };
                if (nibble < 0)
                {
                    continue;
                }

                if (high < 0)
                {
                    high = nibble;
                }
                else
                {
                    destination[written++] = (byte)((high << 4) | nibble);
                    high = -1;
                }
            }

            _data = _data[i..];
            return written;
        }
    }
}
