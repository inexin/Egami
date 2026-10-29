using System.Buffers;
using System.Runtime.InteropServices;

using Egami.Drawing;
using Egami.Pixels;
using Egami.SystemDrawing.Imaging;

namespace Egami.SystemDrawing.Internal;

/// <summary>
/// Where a Graphics draws: a canvas over the bitmap's own memory for premultiplied 32-bit ARGB, or over a premultiplied copy of the
/// area a call touches for every other format, converted in and out as GDI+ converts.
/// </summary>
internal static class Surface
{
    /// <summary>Runs a drawing call on a canvas whose coordinates are the bitmap's device pixels.</summary>
    /// <param name="frame">The frame to draw on.</param>
    /// <param name="bounds">The device area the call can touch, or null for all of it.</param>
    /// <param name="options">The canvas settings: GDI+'s antialiasing and compositing as the Graphics has them.</param>
    /// <param name="draw">The call.</param>
    public static void Draw(Frame frame, RectF? bounds, CanvasOptions options, Action<Canvas> draw)
    {
        if (frame.Stride > 0 && frame.Format == PixelFormat.Format32bppPArgb)
        {
            using PointerMemory memory = new(frame.Scan0, frame.Stride * frame.Height, frame);
            using Raster<PremulBgra8> raster = Raster.Wrap<PremulBgra8>(memory.Memory, frame.Width, frame.Height, frame.Stride);
            using RasterCanvas<PremulBgra8> canvas = new(raster, options);
            draw(canvas);
            return;
        }

        // Other formats: draw on a premultiplied copy of the area, then write it back.
        int x0 = 0, y0 = 0, x1 = frame.Width, y1 = frame.Height;
        if (bounds is RectF b)
        {
            x0 = Math.Clamp((int)MathF.Floor(b.X) - 2, 0, frame.Width);
            y0 = Math.Clamp((int)MathF.Floor(b.Y) - 2, 0, frame.Height);
            x1 = Math.Clamp((int)MathF.Ceiling(b.Right) + 2, 0, frame.Width);
            y1 = Math.Clamp((int)MathF.Ceiling(b.Bottom) + 2, 0, frame.Height);
        }

        if (x1 <= x0 || y1 <= y0)
        {
            return;
        }

        // GDI+ draws in premultiplied ARGB: with alpha it premultiplies and unpremultiplies by its own rounding, and without
        // alpha it keeps the premultiplied bytes, so a translucent copy darkens.
        bool alpha = GdiPixels.HasAlpha(frame.Format);
        using Raster<PremulBgra8> shadow = Raster.Create<PremulBgra8>(x1 - x0, y1 - y0);
        for (int y = y0; y < y1; y++)
        {
            ReadOnlySpan<byte> row = frame.Row(y);
            Span<uint> target = MemoryMarshal.Cast<PremulBgra8, uint>(shadow.Row(y - y0));
            for (int x = x0; x < x1; x++)
            {
                uint v = GdiPixels.Read(frame.Format, row, x, frame.Palette);
                target[x - x0] = alpha ? Premultiply(v) : v;
            }
        }

        using (RasterCanvas<PremulBgra8> canvas = new(shadow, options))
        {
            canvas.Translate(-x0, -y0);
            draw(canvas);
        }

        for (int y = y0; y < y1; y++)
        {
            Span<byte> row = frame.Row(y);
            ReadOnlySpan<uint> source = MemoryMarshal.Cast<PremulBgra8, uint>(shadow.Row(y - y0));
            for (int x = x0; x < x1; x++)
            {
                uint v = source[x - x0];
                GdiPixels.Write(frame.Format, row, x, alpha ? Unpremultiply(v) : v | 0xFF000000);
            }
        }
    }

    /// <summary>
    /// Runs a drawing call on an opaque 8-bit RGB copy of an area, for ClearType text, which Egami blends per channel only onto
    /// opaque RGB; the copy is written back opaque. Nothing is drawn, and false returned, when a pixel of the area is not opaque.
    /// </summary>
    /// <param name="frame">The frame to draw on.</param>
    /// <param name="bounds">The device area the call can touch.</param>
    /// <param name="options">The canvas options, such as Egami.Drawing's GDI+ ClearType preset.</param>
    /// <param name="draw">The call.</param>
    /// <returns>Whether the area was opaque and the call ran.</returns>
    public static bool DrawOpaque(Frame frame, RectF bounds, CanvasOptions options, Action<Canvas> draw)
    {
        int x0 = Math.Clamp((int)MathF.Floor(bounds.X) - 2, 0, frame.Width);
        int y0 = Math.Clamp((int)MathF.Floor(bounds.Y) - 2, 0, frame.Height);
        int x1 = Math.Clamp((int)MathF.Ceiling(bounds.Right) + 2, 0, frame.Width);
        int y1 = Math.Clamp((int)MathF.Ceiling(bounds.Bottom) + 2, 0, frame.Height);
        if (x1 <= x0 || y1 <= y0)
        {
            return true;
        }

        using Raster<Rgb8> shadow = Raster.Create<Rgb8>(x1 - x0, y1 - y0);
        for (int y = y0; y < y1; y++)
        {
            ReadOnlySpan<byte> row = frame.Row(y);
            Span<Rgb8> target = shadow.Row(y - y0);
            for (int x = x0; x < x1; x++)
            {
                uint v = GdiPixels.Read(frame.Format, row, x, frame.Palette);
                if (v >> 24 != 255)
                {
                    return false;
                }

                target[x - x0] = new Rgb8((byte)(v >> 16), (byte)(v >> 8), (byte)v);
            }
        }

        using (RasterCanvas<Rgb8> canvas = new(shadow, options))
        {
            canvas.Translate(-x0, -y0);
            draw(canvas);
        }

        for (int y = y0; y < y1; y++)
        {
            Span<byte> row = frame.Row(y);
            ReadOnlySpan<Rgb8> source = shadow.Row(y - y0);
            for (int x = x0; x < x1; x++)
            {
                Rgb8 c = source[x - x0];
                GdiPixels.Write(frame.Format, row, x, 0xFF000000u | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B);
            }
        }

        return true;
    }

    private static uint Premultiply(uint argb)
    {
        byte a = (byte)(argb >> 24);
        if (a == 255)
        {
            return argb;
        }

        return ((uint)a << 24) | ((uint)GdiPixels.Premultiply((byte)(argb >> 16), a) << 16) | ((uint)GdiPixels.Premultiply((byte)(argb >> 8), a) << 8) | GdiPixels.Premultiply((byte)argb, a);
    }

    private static uint Unpremultiply(uint argb)
    {
        byte a = (byte)(argb >> 24);
        if (a is 255 or 0)
        {
            return a == 0 ? 0 : argb;
        }

        return ((uint)a << 24) | ((uint)GdiPixels.Unpremultiply((byte)(argb >> 16), a) << 16) | ((uint)GdiPixels.Unpremultiply((byte)(argb >> 8), a) << 8) | GdiPixels.Unpremultiply((byte)argb, a);
    }
}

/// <summary>
/// Memory over a pointer to pinned or caller-owned pixels. It holds on to the object that owns them (a frame's pinned array lives
/// only while the frame does), so the pointer stays valid for as long as the memory is in use.
/// </summary>
internal sealed unsafe class PointerMemory(IntPtr pointer, int length, object? owner = null) : MemoryManager<byte>
{
    /// <summary>Gets the object that keeps the pixels alive.</summary>
    public object? Owner { get; } = owner;

    public override Span<byte> GetSpan() => new((void*)pointer, length);

    public override MemoryHandle Pin(int elementIndex = 0) => new((byte*)pointer + elementIndex);

    public override void Unpin()
    {
    }

    protected override void Dispose(bool disposing)
    {
    }
}
