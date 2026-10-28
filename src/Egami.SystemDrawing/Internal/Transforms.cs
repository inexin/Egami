using System.Runtime.InteropServices;

using Egami.Operations;
using Egami.Pixels;
using Egami.SystemDrawing.Imaging;

namespace Egami.SystemDrawing.Internal;

/// <summary>Whole-frame operations: rotating and flipping, drawing into a new size, and the color key of MakeTransparent.</summary>
internal static class Transforms
{
    /// <summary>A rotated and flipped copy, pixel bits moved as they are, so every format, indexed or not, keeps its values.</summary>
    public static Frame RotateFlip(Frame frame, RotateFlipType type)
    {
        int rotation = (int)type & 3;
        bool flip = ((int)type & 4) != 0;
        int width = rotation % 2 == 0 ? frame.Width : frame.Height;
        int height = rotation % 2 == 0 ? frame.Height : frame.Width;
        Frame result = new(width, height, frame.Format) { Palette = (uint[])frame.Palette.Clone(), PaletteFlags = frame.PaletteFlags, Delay = frame.Delay };
        int bits = GdiPixels.Bits(frame.Format);
        int bytes = bits / 8;
        for (int y = 0; y < height; y++)
        {
            Span<byte> target = result.Row(y);
            for (int x = 0; x < width; x++)
            {
                // Where the target pixel comes from: rotate clockwise, then flip left to right.
                int tx = flip ? width - 1 - x : x;
                (int sx, int sy) = rotation switch
                {
                    1 => (y, frame.Height - 1 - tx),
                    2 => (frame.Width - 1 - tx, frame.Height - 1 - y),
                    3 => (frame.Width - 1 - y, tx),
                    _ => (tx, y),
                };
                ReadOnlySpan<byte> source = frame.Row(sy);
                if (bits < 8)
                {
                    GdiPixels.SetIndex(frame.Format, target, x, GdiPixels.Index(frame.Format, source, sx));
                }
                else
                {
                    source.Slice(sx * bytes, bytes).CopyTo(target[(x * bytes)..]);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// The frame drawn into a new 32-bit frame at a size, as GDI+'s DrawImage draws it: through premultiplied 8-bit color, with
    /// bilinear filtering when the size changes.
    /// </summary>
    public static Frame Resize(Frame frame, int width, int height, PixelFormat format)
    {
        if (width <= 0 || height <= 0)
        {
            throw GdiErrors.InvalidParameter();
        }

        Frame premultiplied = frame.Format == PixelFormat.Format16bppGrayScale
            ? Gray16ToArgb(frame).Convert(new System.Drawing.Rectangle(0, 0, frame.Width, frame.Height), PixelFormat.Format32bppPArgb)
            : frame.Convert(new System.Drawing.Rectangle(0, 0, frame.Width, frame.Height), PixelFormat.Format32bppPArgb);
        if (width != frame.Width || height != frame.Height)
        {
            using Raster<PremulBgra8> source = Raster.Create<PremulBgra8>(frame.Width, frame.Height);
            for (int y = 0; y < frame.Height; y++)
            {
                MemoryMarshal.Cast<byte, PremulBgra8>(premultiplied.Row(y)).CopyTo(source.Row(y));
            }

            using Raster<PremulBgra8> resized = source.Derive(o => o.Resize(width, height, ResampleFilter.Bilinear));
            premultiplied = new Frame(width, height, PixelFormat.Format32bppPArgb);
            for (int y = 0; y < height; y++)
            {
                MemoryMarshal.AsBytes(resized.Row(y)).CopyTo(premultiplied.Row(y));
            }
        }

        return format == PixelFormat.Format32bppPArgb ? premultiplied : premultiplied.Convert(new System.Drawing.Rectangle(0, 0, width, height), format);
    }

    /// <summary>A thumbnail as GDI+ makes one: 32-bit premultiplied, each pixel taken from source pixel floor(x × source width / width).</summary>
    public static Frame Thumbnail(Frame frame, int width, int height)
    {
        Frame premultiplied = Resize(frame, frame.Width, frame.Height, PixelFormat.Format32bppPArgb);
        Frame thumbnail = new(width, height, PixelFormat.Format32bppPArgb);
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<uint> source = MemoryMarshal.Cast<byte, uint>(premultiplied.Row((int)((long)y * frame.Height / height)));
            Span<uint> target = MemoryMarshal.Cast<byte, uint>(thumbnail.Row(y));
            for (int x = 0; x < width; x++)
            {
                target[x] = source[(int)((long)x * frame.Width / width)];
            }
        }

        return thumbnail;
    }

    /// <summary>MakeTransparent: a 32-bit ARGB copy drawn through premultiplied color, with the key color's pixels transparent black.</summary>
    public static Frame ColorKey(Frame frame, uint key)
    {
        Frame drawn = Resize(frame, frame.Width, frame.Height, PixelFormat.Format32bppArgb);
        for (int y = 0; y < frame.Height; y++)
        {
            ReadOnlySpan<byte> source = frame.Row(y);
            Span<uint> target = MemoryMarshal.Cast<byte, uint>(drawn.Row(y));
            for (int x = 0; x < frame.Width; x++)
            {
                if (GdiPixels.Read(frame.Format, source, x, frame.Palette) == key)
                {
                    target[x] = 0;
                }
            }
        }

        return drawn;
    }

    /// <summary>A 16-bit gray frame as 32-bit ARGB, from the high byte of each value.</summary>
    public static Frame Gray16ToArgb(Frame frame)
    {
        Frame argb = new(frame.Width, frame.Height, PixelFormat.Format32bppArgb);
        for (int y = 0; y < frame.Height; y++)
        {
            ReadOnlySpan<byte> source = frame.Row(y);
            Span<byte> target = argb.Row(y);
            for (int x = 0; x < frame.Width; x++)
            {
                GdiPixels.Write(PixelFormat.Format32bppArgb, target, x, GdiPixels.Read(frame.Format, source, x, []));
            }
        }

        return argb;
    }
}
