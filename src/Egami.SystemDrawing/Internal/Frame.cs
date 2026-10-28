using System.Runtime.InteropServices;

using Egami.SystemDrawing.Imaging;

namespace Egami.SystemDrawing.Internal;

/// <summary>
/// One frame of a bitmap: its pixels in GDI+'s memory layout (rows of the format's bits, each padded to a multiple of 4 bytes)
/// in pinned memory, or in the caller's memory for <c>new Bitmap(width, height, stride, format, scan0)</c>, and its palette.
/// </summary>
internal sealed unsafe class Frame
{
    private readonly byte[]? _owned;

    /// <summary>Creates a frame with its own zeroed, pinned pixels.</summary>
    public Frame(int width, int height, PixelFormat format)
    {
        Width = width;
        Height = height;
        Format = format;
        Stride = GdiPixels.Stride(width, format);
        _owned = GC.AllocateArray<byte>(checked(Stride * height), pinned: true);
        Scan0 = Marshal.UnsafeAddrOfPinnedArrayElement(_owned, 0);
        SetPalette(GdiPalettes.Default(format));
    }

    /// <summary>Creates a frame over the caller's memory, which must stay valid as long as the bitmap; the stride may be negative.</summary>
    public Frame(int width, int height, PixelFormat format, int stride, IntPtr scan0)
    {
        Width = width;
        Height = height;
        Format = format;
        Stride = stride;
        Scan0 = scan0;
        SetPalette(GdiPalettes.Default(format));
    }

    public int Width { get; }

    public int Height { get; }

    public PixelFormat Format { get; }

    public int Stride { get; }

    public IntPtr Scan0 { get; }

    /// <summary>Gets or sets the palette as 32-bit ARGB values.</summary>
    public uint[] Palette { get; set; } = [];

    /// <summary>Gets or sets the palette's flags.</summary>
    public int PaletteFlags { get; set; }

    /// <summary>Gets or sets the time the frame shows in an animation, in hundredths of a second.</summary>
    public int Delay { get; set; }

    /// <summary>Gets the bytes of a row's pixels.</summary>
    public Span<byte> Row(int y) => new((byte*)Scan0 + ((long)y * Stride), GdiPixels.RowBytes(Width, Format));

    public void SetPalette(ColorPalette palette)
    {
        Palette = [.. palette.Entries.Select(c => (uint)c.ToArgb())];
        PaletteFlags = palette.Flags;
    }

    public ColorPalette GetPalette() => new(PaletteFlags, [.. Palette.Select(c => Color.FromArgb((int)c))]);

    /// <summary>A copy with its own pixels.</summary>
    public Frame Copy()
    {
        Frame copy = new(Width, Height, Format) { Palette = (uint[])Palette.Clone(), PaletteFlags = PaletteFlags, Delay = Delay };
        for (int y = 0; y < Height; y++)
        {
            Row(y).CopyTo(copy.Row(y));
        }

        return copy;
    }

    /// <summary>
    /// A copy of an area in another format, converted as GDI+ converts. An indexed copy in the same format keeps the palette;
    /// in another indexed format it gets the halftone palette Clone uses, or with <paramref name="lockPalette"/> the format's
    /// default palette, as LockBits uses.
    /// </summary>
    public Frame Convert(System.Drawing.Rectangle area, PixelFormat format, bool lockPalette = false)
    {
        Frame copy = new(area.Width, area.Height, format);
        if (GdiPixels.IsIndexed(format) && format == Format)
        {
            copy.Palette = (uint[])Palette.Clone();
            copy.PaletteFlags = PaletteFlags;
        }
        else if (GdiPixels.IsIndexed(format) && !lockPalette && !GdiPixels.IsIndexed(Format))
        {
            copy.SetPalette(GdiPalettes.ForClone(format));
        }

        byte[]? table = GdiPixels.IsIndexed(format) ? GdiPalettes.InverseTable(copy.Palette) : null;

        int bits = GdiPixels.Bits(Format);
        byte[] shifted = new byte[GdiPixels.RowBytes(area.Width, Format)];
        for (int y = 0; y < area.Height; y++)
        {
            ReadOnlySpan<byte> source = Row(area.Y + y);
            if ((area.X * bits) % 8 == 0)
            {
                source = source[(area.X * bits / 8)..];
            }
            else
            {
                // Sub-byte formats starting mid-byte: move the indices to the start.
                for (int x = 0; x < area.Width; x++)
                {
                    GdiPixels.SetIndex(Format, shifted, x, GdiPixels.Index(Format, source, area.X + x));
                }

                source = shifted;
            }

            if (format == Format)
            {
                source[..GdiPixels.RowBytes(area.Width, Format)].CopyTo(copy.Row(y));
            }
            else
            {
                GdiPixels.ConvertRow(Format, source, Palette, format, copy.Row(y), table, area.Width);
            }
        }

        return copy;
    }
}
