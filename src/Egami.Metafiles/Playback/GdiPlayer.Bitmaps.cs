using System.Buffers.Binary;
using System.Numerics;

using Egami.Drawing;
using Egami.Operations;
using Egami.Pixels;

using static Egami.Metafiles.Reading.MetafileReader;

namespace Egami.Metafiles.Playback;

/// <summary>
/// Bitmaps and raster operations (design question 7): DIBs decode through Egami's BMP reader; the ROP3 codes Egami can express
/// draw as copies, fills, inversions or the blend that gives the same result on black-and-white masks (AND as Multiply, OR as
/// Lighten, XOR as Difference), and the transparent-bitmap idioms (SRCAND then SRCPAINT; SRCINVERT, SRCAND, SRCINVERT) become
/// one masked image so they stay right over transparency; the rest draw as copies with one warning.
/// </summary>
internal sealed partial class GdiPlayer
{
    private const uint SrcCopy = 0x00CC0020, SrcPaint = 0x00EE0086, SrcAnd = 0x008800C6, SrcInvert = 0x00660046, NotSrcCopy = 0x00330008;
    private const uint PatCopy = 0x00F00021, PatInvert = 0x005A0049, DstInvert = 0x00550009, Blackness = 0x00000042, Whiteness = 0x00FF0062;

    /// <summary>A blit waiting for the next record, which may finish a transparent-bitmap idiom.</summary>
    private Blit? _pending;

    /// <summary>One bitmap draw: the source pixels, the source rectangle in them, the destination and the raster operation.</summary>
    private sealed record Blit(Raster<Rgba8> Image, RectF Source, RectF Destination, uint Rop, Blit? Previous = null);

    // ---- EMF ----

    private void PlayEmfBitmap(int type, ReadOnlySpan<byte> r, long offset)
    {
        switch (type)
        {
            case 76: // EMR_BITBLT
            case 77: // EMR_STRETCHBLT
                {
                    int x = I32(r, 24), y = I32(r, 28), cx = I32(r, 32), cy = I32(r, 36);
                    uint rop = U32(r, 40);
                    int xSrc = I32(r, 44), ySrc = I32(r, 48);
                    Raster<Rgba8>? image = r.Length >= 100 && U32(r, 88) > 0 ? Dib(r, U32(r, 80), I32(r, 84), I32(r, 88), I32(r, 92), I32(r, 96)) : null;
                    int cxSrc = type == 77 && r.Length >= 108 ? I32(r, 100) : cx, cySrc = type == 77 && r.Length >= 108 ? I32(r, 104) : cy;
                    Matrix3x2 xform = XForm(r, 52);
                    RectF source = SourceRect(xform, xSrc, ySrc, cxSrc, cySrc);
                    Blt(image, source, x, y, cx, cy, rop, offset);
                    break;
                }

            case 81: // EMR_STRETCHDIBITS
                {
                    Raster<Rgba8>? image = Dib(r, U32(r, 64), I32(r, 48), I32(r, 52), I32(r, 56), I32(r, 60));
                    if (image is not null)
                    {
                        int xSrc = I32(r, 32), ySrc = I32(r, 36), cxSrc = I32(r, 40), cySrc = I32(r, 44);
                        Blt(image, DibSource(image, xSrc, ySrc, cxSrc, cySrc, r, 48), I32(r, 24), I32(r, 28), I32(r, 72), I32(r, 76), U32(r, 68), offset);
                    }

                    break;
                }

            case 80: // EMR_SETDIBITSTODEVICE: the DIB's scan lines at the destination, one device pixel per bitmap pixel
                {
                    Raster<Rgba8>? image = Dib(r, U32(r, 64), I32(r, 48), I32(r, 52), I32(r, 56), I32(r, 60));
                    if (image is not null)
                    {
                        int xSrc = I32(r, 32), ySrc = I32(r, 36), cxSrc = I32(r, 40), cySrc = I32(r, 44);
                        RectF source = DibSource(image, xSrc, ySrc, cxSrc, cySrc, r, 48);
                        Vector2 at = new(I32(r, 24), I32(r, 28));
                        DrawPixels(image, source, at);
                    }

                    break;
                }

            case 78: // EMR_MASKBLT
                {
                    int x = I32(r, 24), y = I32(r, 28), cx = I32(r, 32), cy = I32(r, 36);
                    uint rop4 = U32(r, 40);
                    Raster<Rgba8>? image = Dib(r, U32(r, 80), I32(r, 84), I32(r, 88), I32(r, 92), I32(r, 96));
                    Raster<Rgba8>? mask = r.Length >= 128 ? Dib(r, U32(r, 108), I32(r, 112), I32(r, 116), I32(r, 120), I32(r, 124)) : null;
                    RectF source = SourceRect(XForm(r, 52), I32(r, 44), I32(r, 48), cx, cy);
                    MaskBlt(image, mask, source, new Vector2(I32(r, 100), I32(r, 104)), x, y, cx, cy, rop4, offset);
                    break;
                }

            case 79: // EMR_PLGBLT
                {
                    Vector2 p0 = new(I32(r, 24), I32(r, 28)), p1 = new(I32(r, 32), I32(r, 36)), p2 = new(I32(r, 40), I32(r, 44));
                    Raster<Rgba8>? image = Dib(r, U32(r, 92), I32(r, 96), I32(r, 100), I32(r, 104), I32(r, 108));
                    if (image is not null)
                    {
                        RectF source = SourceRect(XForm(r, 64), I32(r, 48), I32(r, 52), I32(r, 56), I32(r, 60));
                        Raster<Rgba8>? mask = r.Length >= 140 && I32(r, 128) > 0 ? Dib(r, U32(r, 120), I32(r, 124), I32(r, 128), I32(r, 132), I32(r, 136)) : null;
                        if (mask is not null)
                        {
                            image = Masked(image, source, mask, new Vector2(I32(r, 112), I32(r, 116)));
                        }

                        Parallelogram(image, source, p0, p1, p2);
                    }

                    break;
                }

            case 114: // EMR_ALPHABLEND
            case 116: // EMR_TRANSPARENTBLT
                {
                    int x = I32(r, 24), y = I32(r, 28), cx = I32(r, 32), cy = I32(r, 36);
                    uint extra = U32(r, 40);
                    Raster<Rgba8>? image = type == 114 ? AlphaDib(r, extra) : Dib(r, U32(r, 80), I32(r, 84), I32(r, 88), I32(r, 92), I32(r, 96));
                    if (image is null)
                    {
                        break;
                    }

                    RectF source = SourceRect(XForm(r, 52), I32(r, 44), I32(r, 48), I32(r, 100), I32(r, 104));
                    if (type == 116)
                    {
                        image = Keyed(image, GdiPen.ColorRef(extra));
                        Draw(new Blit(image, source, DestRect(x, y, cx, cy), SrcCopy));
                    }
                    else
                    {
                        float constant = ((extra >> 16) & 0xFF) / 255f;
                        Draw(new Blit(image, source, DestRect(x, y, cx, cy), SrcCopy), constant);
                    }

                    break;
                }
        }
    }

    /// <summary>The source rectangle in the source bitmap: the record's source corner and size through its source transform.</summary>
    private static RectF SourceRect(Matrix3x2 xform, int x, int y, int cx, int cy)
    {
        Vector2 a = Vector2.Transform(new Vector2(x, y), xform), b = Vector2.Transform(new Vector2(x + cx, y + cy), xform);
        return new RectF(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Abs(b.X - a.X), MathF.Abs(b.Y - a.Y));
    }

    /// <summary>
    /// The source rectangle of StretchDIBits and SetDIBitsToDevice: their y counts from the bitmap's bottom for a bottom-up DIB.
    /// </summary>
    private static RectF DibSource(Raster<Rgba8> image, int x, int y, int cx, int cy, ReadOnlySpan<byte> r, int offBmiAt)
    {
        int offBmi = I32(r, offBmiAt);
        bool topDown = offBmi >= 0 && offBmi + 12 <= r.Length && I32(r, offBmi + 8) < 0;
        float top = topDown ? y : image.Height - y - cy;
        return new RectF(x, top, cx, cy);
    }

    /// <summary>A DIB carried in an EMF record at the given offsets, or null.</summary>
    private Raster<Rgba8>? Dib(ReadOnlySpan<byte> r, uint usage, int offBmi, int cbBmi, int offBits, int cbBits)
    {
        if (cbBmi <= 0 || offBmi < 0 || offBits < 0 || cbBits < 0 || offBmi > r.Length - cbBmi || offBits > r.Length - cbBits)
        {
            return null;
        }

        Raster<Rgba8>? image = DecodeDib(r.Slice(offBmi, cbBmi), r.Slice(offBits, cbBits), (int)usage);
        if (image is null)
        {
            WarnOnce("Metafile.BadBitmap", "A bitmap in a record cannot be decoded; it is not drawn.");
        }

        return image;
    }

    /// <summary>AlphaBlend's source with per-pixel alpha: 32-bit BI_RGB pixels are premultiplied BGRA, which the BMP reader would read as opaque.</summary>
    private Raster<Rgba8>? AlphaDib(ReadOnlySpan<byte> r, uint blend)
    {
        int offBmi = I32(r, 84), cbBmi = I32(r, 88), offBits = I32(r, 92), cbBits = I32(r, 96);
        bool perPixel = ((blend >> 24) & 1) != 0;
        if (perPixel && cbBmi >= 40 && offBmi >= 0 && offBmi <= r.Length - 40 && I32(r, offBmi + 16) is 0 or 3 && BinaryPrimitives.ReadUInt16LittleEndian(r[(offBmi + 14)..]) == 32)
        {
            int w = I32(r, offBmi + 4), h = I32(r, offBmi + 8);
            int absH = Math.Abs(h);
            if (w > 0 && absH > 0 && offBits >= 0 && (long)w * absH * 4 <= cbBits && offBits <= r.Length - cbBits)
            {
                Raster<Rgba8> image = Raster.Create<Rgba8>(w, absH, DibContext);
                for (int y = 0; y < absH; y++)
                {
                    ReadOnlySpan<byte> src = r.Slice(offBits + ((h > 0 ? absH - 1 - y : y) * w * 4), w * 4);
                    Span<Rgba8> row = image.Row(y);
                    for (int x = 0; x < w; x++)
                    {
                        byte a = src[(x * 4) + 3];
                        row[x] = a == 0 ? default : new Rgba8(Unpremultiply(src[(x * 4) + 2], a), Unpremultiply(src[(x * 4) + 1], a), Unpremultiply(src[x * 4], a), a);
                    }
                }

                return image;
            }
        }

        return Dib(r, U32(r, 80), offBmi, cbBmi, offBits, cbBits);
    }

    private static byte Unpremultiply(byte c, byte a) => (byte)Math.Min(255, ((c * 255) + (a / 2)) / a);

    /// <summary>A copy of the image with one color made transparent (TransparentBlt).</summary>
    private static Raster<Rgba8> Keyed(Raster<Rgba8> image, Rgba8 key)
    {
        Raster<Rgba8> copy = image.Copy();
        for (int y = 0; y < copy.Height; y++)
        {
            Span<Rgba8> row = copy.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                if (row[x].R == key.R && row[x].G == key.G && row[x].B == key.B)
                {
                    row[x] = default;
                }
            }
        }

        return copy;
    }

    /// <summary>The source with its pixels where the monochrome mask is 0 made transparent (MaskBlt and PlgBlt with a mask).</summary>
    private static Raster<Rgba8> Masked(Raster<Rgba8> image, RectF source, Raster<Rgba8> mask, Vector2 maskOrigin)
    {
        Raster<Rgba8> copy = image.Copy();
        int dx = (int)(maskOrigin.X - source.X), dy = (int)(maskOrigin.Y - source.Y);
        for (int y = 0; y < copy.Height; y++)
        {
            Span<Rgba8> row = copy.Row(y);
            int my = y + dy;
            for (int x = 0; x < row.Length; x++)
            {
                int mx = x + dx;
                bool on = my >= 0 && my < mask.Height && mx >= 0 && mx < mask.Width && mask.Row(my)[mx].R > 127;
                if (!on)
                {
                    row[x] = default;
                }
            }
        }

        return copy;
    }

    /// <summary>A destination rectangle in device space; aliased, its corners in whole pixels as GDI places bitmaps.</summary>
    private RectF DestRect(float x, float y, float cx, float cy)
    {
        Vector2 a = Dev(new Vector2(x, y)), b = Dev(new Vector2(x + cx, y + cy));
        return new RectF(a.X, a.Y, b.X - a.X, b.Y - a.Y);
    }

    /// <summary>A blit by raster operation; a source-less one fills the destination with the brush, black, white or an inversion.</summary>
    private void Blt(Raster<Rgba8>? image, RectF source, float x, float y, float cx, float cy, uint rop, long offset)
    {
        RectF dest = DestRect(x, y, cx, cy);
        if (image is null)
        {
            PatBlt(dest, rop, offset);
            return;
        }

        Blit blit = new(image, source, dest, rop);
        if (_pending is Blit pending)
        {
            _pending = null;
            if (TryIdiom(pending, blit))
            {
                return;
            }

            Draw(pending);
        }

        if (rop is SrcAnd or SrcInvert)
        {
            _pending = blit;
            return;
        }

        Draw(blit);
    }

    /// <summary>
    /// The transparent-bitmap idioms: SRCAND of a mask then SRCPAINT of an image (black outside the shape) draws the image where
    /// the mask is black; SRCINVERT, SRCAND, SRCINVERT of an image and a mask draws the image where the mask is black.
    /// </summary>
    private bool TryIdiom(Blit first, Blit next)
    {
        if (!Same(first.Destination, next.Destination))
        {
            return false;
        }

        if (first.Rop == SrcAnd && first.Previous is null && next.Rop == SrcPaint)
        {
            Draw(new Blit(MaskWhereBlack(next.Image, next.Source, first.Image, first.Source), next.Source, next.Destination, SrcCopy));
            return true;
        }

        if (first.Rop == SrcInvert && first.Previous is null && next.Rop == SrcAnd)
        {
            _pending = next with { Previous = first };
            return true;
        }

        if (first.Rop == SrcAnd && first.Previous is Blit xor && next.Rop == SrcInvert)
        {
            Draw(new Blit(MaskWhereBlack(xor.Image, xor.Source, first.Image, first.Source), xor.Source, next.Destination, SrcCopy));
            return true;
        }

        return false;
    }

    private static bool Same(RectF a, RectF b) =>
        MathF.Abs(a.X - b.X) < 0.5f && MathF.Abs(a.Y - b.Y) < 0.5f && MathF.Abs(a.Width - b.Width) < 0.5f && MathF.Abs(a.Height - b.Height) < 0.5f;

    /// <summary>The image's source part, transparent where the mask (sampled over the same destination) is not black.</summary>
    private static Raster<Rgba8> MaskWhereBlack(Raster<Rgba8> image, RectF source, Raster<Rgba8> mask, RectF maskSource)
    {
        Raster<Rgba8> copy = image.Copy();
        for (int y = 0; y < copy.Height; y++)
        {
            Span<Rgba8> row = copy.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                float u = source.Width > 0 ? (x + 0.5f - source.X) / source.Width : 0, v = source.Height > 0 ? (y + 0.5f - source.Y) / source.Height : 0;
                int mx = (int)MathF.Floor(maskSource.X + (u * maskSource.Width)), my = (int)MathF.Floor(maskSource.Y + (v * maskSource.Height));
                bool black = mx >= 0 && my >= 0 && mx < mask.Width && my < mask.Height && mask.Row(my)[mx] is { R: < 128, G: < 128, B: < 128 };
                if (!black)
                {
                    row[x] = default;
                }
            }
        }

        return copy;
    }

    /// <summary>Flushes a blit that waited for an idiom that did not come.</summary>
    internal void FlushPending()
    {
        if (_pending is Blit pending)
        {
            _pending = null;
            if (pending.Previous is Blit previous)
            {
                Draw(previous);
            }

            Draw(pending);
        }
    }

    /// <summary>Draws a blit by its raster operation.</summary>
    private void Draw(Blit blit, float opacity = 1)
    {
        if (blit.Destination.Width == 0 || blit.Destination.Height == 0 || blit.Source.Width <= 0 || blit.Source.Height <= 0)
        {
            return;
        }

        Raster<Rgba8> image = blit.Image;
        BlendMode mode = BlendMode.SourceOver;
        switch (blit.Rop)
        {
            case SrcCopy:
                break;
            case SrcAnd:
                mode = BlendMode.Multiply;
                break;
            case SrcPaint:
                mode = BlendMode.Lighten;
                break;
            case SrcInvert:
                mode = BlendMode.Difference;
                break;
            case NotSrcCopy:
                image = Inverted(image);
                break;
            default:
                WarnOnce("Metafile.RasterOperation", $"The raster operation 0x{blit.Rop:X8} has no Egami equivalent; the bitmap is drawn as a copy (design question 7).", key: "rop3:" + blit.Rop);
                break;
        }

        // A destination given right to left or bottom to top mirrors the image.
        RectF d = blit.Destination;
        int count = _canvas.Save();
        try
        {
            Matrix3x2 mirror = Matrix3x2.CreateScale(MathF.Sign(d.Width), MathF.Sign(d.Height), new Vector2(d.X, d.Y));
            _canvas.Concat(mirror);
            RectF target = new(d.X, d.Y, MathF.Abs(d.Width), MathF.Abs(d.Height));
            SolidPaint paint = new(new Rgba8(0, 0, 0, 255)) { BlendMode = mode, Opacity = opacity, Antialias = !_aliased };
            _canvas.DrawImage(image, blit.Source, target, Sampling, paint);
        }
        finally
        {
            _canvas.RestoreToCount(count);
        }
    }

    /// <summary>Nearest pixels when drawing aliased or in COLORONCOLOR mode, as GDI stretches; smooth with HALFTONE or antialiased.</summary>
    private SamplingOptions Sampling => _aliased && _dc.StretchMode != 4 ? new SamplingOptions { Filter = ResampleFilter.Nearest } : new SamplingOptions { Filter = ResampleFilter.Bilinear };

    private static Raster<Rgba8> Inverted(Raster<Rgba8> image)
    {
        Raster<Rgba8> copy = image.Copy();
        for (int y = 0; y < copy.Height; y++)
        {
            Span<Rgba8> row = copy.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                row[x] = new Rgba8((byte)(255 - row[x].R), (byte)(255 - row[x].G), (byte)(255 - row[x].B), row[x].A);
            }
        }

        return copy;
    }

    /// <summary>SetDIBitsToDevice: the source at one device pixel per bitmap pixel from the destination corner.</summary>
    private void DrawPixels(Raster<Rgba8> image, RectF source, Vector2 at)
    {
        Vector2 origin = Dev(at);
        Vector2 size = new Vector2(source.Width, source.Height) * Vector2.Abs(new Vector2(_device.M11, _device.M22));
        FlushPending();
        Draw(new Blit(image, source, new RectF(origin.X, origin.Y, size.X, size.Y), SrcCopy));
    }

    /// <summary>PlgBlt: the source rectangle mapped onto the parallelogram whose first three corners are given.</summary>
    private void Parallelogram(Raster<Rgba8> image, RectF source, Vector2 p0, Vector2 p1, Vector2 p2)
    {
        FlushPending();
        Matrix3x2 full = Full;
        Vector2 a = Vector2.Transform(p0, full), b = Vector2.Transform(p1, full), c = Vector2.Transform(p2, full);
        if (source.Width <= 0 || source.Height <= 0)
        {
            return;
        }

        Matrix3x2 map = new((b.X - a.X) / source.Width, (b.Y - a.Y) / source.Width, (c.X - a.X) / source.Height, (c.Y - a.Y) / source.Height, a.X, a.Y);
        int count = _canvas.Save();
        try
        {
            _canvas.Concat(map);
            _canvas.DrawImage(image, source, new RectF(0, 0, source.Width, source.Height), Sampling, new SolidPaint(new Rgba8(0, 0, 0, 255)) { Antialias = !_aliased });
        }
        finally
        {
            _canvas.RestoreToCount(count);
        }
    }

    /// <summary>
    /// MaskBlt: where the mask is 1 the foreground operation (low ROP3 of the ROP4), where it is 0 the background one; the common
    /// forms, a copy against keeping the destination, draw the source through the mask.
    /// </summary>
    private void MaskBlt(Raster<Rgba8>? image, Raster<Rgba8>? mask, RectF source, Vector2 maskOrigin, int x, int y, int cx, int cy, uint rop4, long offset)
    {
        FlushPending();
        uint fore = ((rop4 & 0x00FF0000) | (rop4 & 0xFFFF)) & 0x00FFFFFF, backCode = (rop4 >> 24) & 0xFF;
        if (mask is null || image is null)
        {
            Blt(image, source, x, y, cx, cy, fore, offset);
            return;
        }

        if (backCode == 0xAA && (fore >> 16) == 0xCC)
        {
            Draw(new Blit(Masked(image, source, mask, maskOrigin), source, DestRect(x, y, cx, cy), SrcCopy));
            return;
        }

        WarnOnce("Metafile.RasterOperation", $"MaskBlt's raster operation 0x{rop4:X8} has no Egami equivalent; the source is drawn as a copy (design question 7).", offset, "rop4:" + rop4);
        Draw(new Blit(image, source, DestRect(x, y, cx, cy), SrcCopy));
    }

    /// <summary>A blit without a source: PATCOPY fills with the brush, BLACKNESS and WHITENESS fill, DSTINVERT and PATINVERT invert.</summary>
    private void PatBlt(RectF dest, uint rop, long offset)
    {
        FlushPending();
        RectF rect = new(MathF.Min(dest.X, dest.Right), MathF.Min(dest.Y, dest.Bottom), MathF.Abs(dest.Width), MathF.Abs(dest.Height));
        if (Stipple(rect, rop, offset))
        {
            return;
        }

        PatBltNow(rect, rop, offset);
    }

    /// <summary>
    /// The stipple idiom of older programs: PATINVERT with a brush, DPa (0x00A000C9) with a pattern, PATINVERT with the same brush
    /// over one rectangle paints the brush color where the pattern is 0 and leaves the rest. It is drawn as that, since the XOR
    /// steps alone have no Egami equivalent.
    /// </summary>
    private bool Stipple(RectF rect, uint rop, long offset)
    {
        if (_stipple is (RectF r0, GdiBrush first, var pattern) && Same(r0, rect))
        {
            if (pattern is null && rop == 0x00A000C9)
            {
                _stipple = (r0, first, _dc.Brush);
                return true;
            }

            if (pattern is not null && rop == PatInvert && _dc.Brush.Style == GdiBrush.Solid && _dc.Brush.Color.Equals(first.Color))
            {
                _stipple = null;
                if (pattern.Image is Raster<Rgba8> tile)
                {
                    Raster<Rgba8> mask = tile.Copy();
                    for (int y = 0; y < mask.Height; y++)
                    {
                        Span<Rgba8> row = mask.Row(y);
                        for (int x = 0; x < row.Length; x++)
                        {
                            bool zero = pattern.Bits is bool[] bits ? !bits[(y * mask.Width) + x] : row[x] is { R: < 128, G: < 128, B: < 128 };
                            row[x] = zero ? first.Color : default;
                        }
                    }

                    _canvas.FillRect(rect, new ImagePaint(mask, TileMode.Repeat, TileMode.Repeat, new SamplingOptions { Filter = ResampleFilter.Nearest }) { LocalMatrix = TileMatrix, Antialias = !_aliased });
                }
                else
                {
                    _canvas.FillRect(rect, new SolidPaint(first.Color) { Antialias = !_aliased });
                }

                return true;
            }
        }

        FlushStipple(offset);
        if (rop == PatInvert && _dc.Brush.Style == GdiBrush.Solid)
        {
            _stipple = (rect, _dc.Brush, null);
            return true;
        }

        return false;
    }

    /// <summary>Draws the steps of a stipple idiom that did not finish, one by one.</summary>
    internal void FlushStipple(long offset = 0)
    {
        if (_stipple is not (RectF rect, GdiBrush first, var pattern))
        {
            return;
        }

        _stipple = null;
        GdiBrush current = _dc.Brush;
        _dc.Brush = first;
        PatBltNow(rect, PatInvert, offset);
        if (pattern is not null)
        {
            _dc.Brush = pattern;
            PatBltNow(rect, 0x00A000C9, offset);
        }

        _dc.Brush = current;
    }

    private (RectF Rect, GdiBrush First, GdiBrush? Pattern)? _stipple;

    private void PatBltNow(RectF rect, uint rop, long offset)
    {
        Paint? paint = rop switch
        {
            PatCopy => BrushPaint(),
            Blackness => new SolidPaint(new Rgba8(0, 0, 0, 255)) { Antialias = !_aliased },
            Whiteness => new SolidPaint(new Rgba8(255, 255, 255, 255)) { Antialias = !_aliased },
            DstInvert => new SolidPaint(new Rgba8(255, 255, 255, 255)) { Antialias = !_aliased, BlendMode = BlendMode.Difference },
            PatInvert => BrushPaint() is SolidPaint s ? s with { BlendMode = BlendMode.Difference } : null,
            0x00AA0029 => null, // D: the destination is left as it is
            _ => Unknown(),
        };
        if (paint is not null)
        {
            _canvas.FillRect(rect, paint);
        }

        Paint? Unknown()
        {
            WarnOnce("Metafile.RasterOperation", $"The raster operation 0x{rop:X8} without a source has no Egami equivalent; the brush is drawn (design question 7).", offset, "rop3:" + rop);
            return BrushPaint();
        }
    }

    // ---- WMF ----

    private void PlayWmfBitmap(int function, ReadOnlySpan<byte> p, long offset)
    {
        static short S(ReadOnlySpan<byte> s, int i) => I16(s, i * 2);
        switch (function)
        {
            case 0x061D: // META_PATBLT: RasterOperation, Height, Width, YLeft, XLeft
                PatBlt(DestRect(S(p, 5), S(p, 4), S(p, 3), S(p, 2)), U32(p, 0), offset);
                break;
            case 0x0940: // META_DIBBITBLT
            case 0x0B41: // META_DIBSTRETCHBLT
                {
                    bool stretch = function == 0x0B41;
                    int words = stretch ? 8 : 6; // the fields after RasterOperation
                    uint rop = U32(p, 0);
                    if (p.Length <= (words + 3) * 2) // RasterOperation, a reserved word and the fields: no bitmap
                    {
                        // No bitmap: a reserved word comes first, then the fields as for a pattern blit.
                        int k = 3;
                        PatBlt(DestRect(S(p, k + (stretch ? 7 : 5)), S(p, k + (stretch ? 6 : 4)), S(p, k + (stretch ? 5 : 3)), S(p, k + (stretch ? 4 : 2))), rop, offset);
                        break;
                    }

                    ReadOnlySpan<byte> dib = p[((words + 2) * 2)..];
                    int ySrc, xSrc, cySrc, cxSrc, cy, cx, yDest, xDest;
                    if (stretch)
                    {
                        (cySrc, cxSrc, ySrc, xSrc, cy, cx, yDest, xDest) = (S(p, 2), S(p, 3), S(p, 4), S(p, 5), S(p, 6), S(p, 7), S(p, 8), S(p, 9));
                    }
                    else
                    {
                        (ySrc, xSrc, cy, cx, yDest, xDest) = (S(p, 2), S(p, 3), S(p, 4), S(p, 5), S(p, 6), S(p, 7));
                        (cxSrc, cySrc) = (cx, cy);
                    }

                    Raster<Rgba8>? image = PackedDib(dib, 0);
                    if (image is not null)
                    {
                        Blt(image, new RectF(xSrc, ySrc, cxSrc, cySrc), xDest, yDest, cx, cy, rop, offset);
                    }

                    break;
                }

            case 0x0F43: // META_STRETCHDIB: RasterOperation, ColorUsage, SrcHeight, SrcWidth, YSrc, XSrc, DestHeight, DestWidth, yDst, xDst, DIB
                {
                    uint rop = U32(p, 0);
                    int usage = U16(p, 4);
                    Raster<Rgba8>? image = PackedDib(p[22..], usage);
                    if (image is not null)
                    {
                        int cySrc = S(p, 3), cxSrc = S(p, 4), ySrc = S(p, 5), xSrc = S(p, 6);
                        bool topDown = p.Length >= 22 + 12 && I32(p, 22 + 8) < 0;
                        RectF source = new(xSrc, topDown ? ySrc : image.Height - ySrc - cySrc, cxSrc, cySrc);
                        Blt(image, source, S(p, 10), S(p, 9), S(p, 8), S(p, 7), rop, offset);
                    }

                    break;
                }

            case 0x0D33: // META_SETDIBTODEV: ColorUsage, ScanCount, StartScan, yDib, xDib, Height, Width, yDest, xDest, DIB
                {
                    Raster<Rgba8>? image = PackedDib(p[18..], U16(p, 0));
                    if (image is not null)
                    {
                        int yDib = S(p, 3), xDib = S(p, 4), h = S(p, 5), w = S(p, 6);
                        bool topDown = p.Length >= 18 + 12 && I32(p, 18 + 8) < 0;
                        DrawPixels(image, new RectF(xDib, topDown ? yDib : image.Height - yDib - h, w, h), new Vector2(S(p, 8), S(p, 7)));
                    }

                    break;
                }

            default:
                Skip($"Device-dependent bitmap (WMF function 0x{function:X4})", offset);
                break;
        }
    }

    /// <summary>A packed DIB (BITMAPINFO then bits) from a WMF record.</summary>
    private Raster<Rgba8>? PackedDib(ReadOnlySpan<byte> dib, int usage)
    {
        int infoSize = PackedInfoSize(dib, usage);
        if (infoSize <= 0 || infoSize > dib.Length)
        {
            WarnOnce("Metafile.BadBitmap", "A bitmap in a record cannot be decoded; it is not drawn.");
            return null;
        }

        Raster<Rgba8>? image = DecodeDib(dib[..infoSize], dib[infoSize..], usage);
        if (image is null)
        {
            WarnOnce("Metafile.BadBitmap", "A bitmap in a record cannot be decoded; it is not drawn.");
        }

        return image;
    }

    // ---- Gradients ----

    /// <summary>EMR_GRADIENTFILL: rectangles shaded left to right (mode 0) or top to bottom (1), or Gouraud triangles (2).</summary>
    private void GradientFill(ReadOnlySpan<byte> r, long offset)
    {
        FlushPending();
        int nVer = (int)Math.Min(U32(r, 24), (uint)((r.Length - 36) / 16)), nTri = I32(r, 28);
        uint mode = U32(r, 32);
        Vector2[] points = new Vector2[nVer];
        RgbaF32[] colors = new RgbaF32[nVer];
        for (int i = 0; i < nVer; i++)
        {
            int at = 36 + (i * 16);
            points[i] = Vector2.Transform(new Vector2(I32(r, at), I32(r, at + 4)), Full);
            colors[i] = new RgbaF32(U16(r, at + 8) / 65280f, U16(r, at + 10) / 65280f, U16(r, at + 12) / 65280f, 1);
        }

        int at0 = 36 + (nVer * 16);
        if (mode is 0 or 1)
        {
            int n = Math.Clamp(nTri, 0, (r.Length - at0) / 8);
            for (int i = 0; i < n; i++)
            {
                uint a = U32(r, at0 + (i * 8)), b = U32(r, at0 + (i * 8) + 4);
                if (a >= nVer || b >= nVer)
                {
                    continue;
                }

                RectF rect = new(MathF.Min(points[a].X, points[b].X), MathF.Min(points[a].Y, points[b].Y), MathF.Abs(points[b].X - points[a].X), MathF.Abs(points[b].Y - points[a].Y));
                Vector2 end = mode == 0 ? new Vector2(points[b].X, points[a].Y) : new Vector2(points[a].X, points[b].Y);
                _canvas.FillRect(rect, new LinearGradient(points[a], end, colors[a], colors[b]) { Antialias = !_aliased });
            }
        }
        else if (mode == 2)
        {
            int n = Math.Clamp(nTri, 0, (r.Length - at0) / 12);
            List<int> indices = [];
            for (int i = 0; i < n; i++)
            {
                uint a = U32(r, at0 + (i * 12)), b = U32(r, at0 + (i * 12) + 4), c = U32(r, at0 + (i * 12) + 8);
                if (a < nVer && b < nVer && c < nVer)
                {
                    indices.AddRange([(int)a, (int)b, (int)c]);
                }
            }

            if (indices.Count > 0)
            {
                _canvas.DrawMesh(new Mesh(MeshMode.Triangles, points, colors, indices: [.. indices]), new SolidPaint(new Rgba8(255, 255, 255, 255)) { Antialias = !_aliased });
            }
        }
        else
        {
            Skip($"Gradient fill mode {mode}", offset);
        }
    }
}
