using System.Numerics;

using Egami.Drawing;
using Egami.Operations;
using Egami.Pixels;

using static Egami.Metafiles.Reading.MetafileReader;

namespace Egami.Metafiles.Playback;

/// <summary>
/// EMF+ images (design question 12): bitmaps decode through Raster.Load, and JPEG and PNG go to the canvas as EncodedImage so a
/// PDF canvas embeds them unchanged; metafile images play as vectors, nested up to depth 8. Image attributes give the wrap mode;
/// the image effects GDI+ 1.1 records before a DrawImage map onto Egami operations (question 13).
/// </summary>
internal sealed partial class EmfPlusPlayer
{
    /// <summary>How deep metafiles inside metafiles play (design question 12).</summary>
    internal const int MaxDepth = 8;

    /// <summary>The effect the next DrawImage applies (an EmfPlusSerializableObject), or null.</summary>
    private Func<Raster<Rgba8>, Raster<Rgba8>>? _effect;

    private Metafile? LoadMetafile(byte[] bytes)
    {
        if (_depth + 1 >= MaxDepth)
        {
            WarnOnce("Metafile.TooDeep", $"A metafile nested deeper than {MaxDepth} levels is not drawn.");
            return null;
        }

        try
        {
            return Metafile.Load(new MemoryStream(bytes), new MetafileLoadOptions { Context = (_options.Context ?? _metafile.Context) with { Diagnostics = null } });
        }
        catch (Exception e) when (e is EgamiException or ArgumentException)
        {
            WarnOnce("Metafile.BadBitmap", "A metafile image inside the file cannot be read; it is not drawn.", "image-metafile");
            return null;
        }
    }

    private Raster<Rgba8>? LoadImage(byte[] bytes)
    {
        try
        {
            return Raster.Load<Rgba8>(bytes, (_options.Context ?? _metafile.Context) with { Diagnostics = null });
        }
        catch (Exception e) when (e is EgamiException or ArgumentException or InvalidOperationException)
        {
            WarnOnce("Metafile.BadBitmap", "An image inside the file cannot be decoded; it is not drawn.", "image-bitmap");
            return null;
        }
    }

    /// <summary>DrawImage (a destination rectangle) and DrawImagePoints (a parallelogram's first three corners).</summary>
    private void DrawImage(ushort type, ushort flags, ReadOnlySpan<byte> d)
    {
        Func<Raster<Rgba8>, Raster<Rgba8>>? effect = _effect;
        _effect = null;
        if (_objects[flags & 0xFF] is not PlusImage image)
        {
            return;
        }

        PlusImageAttributes? attributes = U32(d, 0) < 64 ? _objects[U32(d, 0)] as PlusImageAttributes : null;
        int srcUnit = I32(d, 4);
        RectF src = new(F32(d, 8), F32(d, 12), F32(d, 16), F32(d, 20));
        Matrix3x2 map;
        int at = 24;
        if (type == 0x401A)
        {
            RectF dest = PlusObjectReader.Rect(d, ref at, (flags & Compressed) != 0);
            if (src.Width == 0 || src.Height == 0)
            {
                return;
            }

            map = Matrix3x2.CreateTranslation(-src.X, -src.Y) * Matrix3x2.CreateScale(dest.Width / src.Width, dest.Height / src.Height) * Matrix3x2.CreateTranslation(dest.X, dest.Y);
        }
        else
        {
            at = 28; // after the point count
            Vector2[] p = PlusObjectReader.ReadPoints(d, ref at, I32(d, 24) is 3 ? 3 : throw new ArgumentException("DrawImagePoints needs three points."), (flags & Compressed) != 0, (flags & Relative) != 0);
            if (src.Width == 0 || src.Height == 0)
            {
                return;
            }

            // The source's top-left, top-right and bottom-left corners go to the three points.
            map = Matrix3x2.CreateTranslation(-src.X, -src.Y)
                * new Matrix3x2((p[1].X - p[0].X) / src.Width, (p[1].Y - p[0].Y) / src.Width, (p[2].X - p[0].X) / src.Height, (p[2].Y - p[0].Y) / src.Height, p[0].X, p[0].Y);
        }

        _ = srcUnit;
        Matrix3x2 full = map * Full;
        int count = _canvas.Save();
        try
        {
            if (image.Metafile is Metafile nested)
            {
                // A metafile image's source rectangle is in its own pixels at its frame; drawn into the source rectangle's box.
                _canvas.ClipPath(Path.Rect(src).Transform(full), ClipOp.Intersect, antialias: Smooth);
                _canvas.Concat(full);
                Vector2 pixels = new Vector2(nested.Frame.Width, nested.Frame.Height) * (_gdi.PixelsPerMm.X * 25.4f / 72);
                nested.DrawCore(_canvas, new RectF(0, 0, pixels.X, pixels.Y), _options, _report, _depth + 1);
                return;
            }

            _canvas.Concat(full);
            SolidPaint paint = new(new Rgba8(0, 0, 0, 255)) { Antialias = Smooth, BlendMode = Blend };
            if (effect is null && image.Encoded is byte[] encoded && IsJpegOrPng(encoded) && (attributes is null || attributes.WrapMode != 4))
            {
                // JPEG and PNG pass through to a PDF canvas unchanged (question 12); a raster canvas decodes them.
                EncodedImage passthrough = EncodedImage.Load(encoded, (_options.Context ?? _metafile.Context) with { Diagnostics = null });
                _canvas.DrawImage(passthrough, src, src, Sampling, paint);
                return;
            }

            if (image.Bitmap is Raster<Rgba8> bitmap)
            {
                Raster<Rgba8> drawn = effect is null ? bitmap : effect(bitmap);
                if (attributes is not null && attributes.WrapMode != 4 && !new RectF(0, 0, bitmap.Width, bitmap.Height).Contains(src))
                {
                    // A source rectangle beyond the image tiles it by the attributes' wrap mode.
                    (TileMode tx, TileMode ty) = Wrap(attributes.WrapMode);
                    _canvas.FillRect(src, new ImagePaint(drawn, tx, ty, Sampling) { Antialias = Smooth, BlendMode = Blend });
                }
                else
                {
                    _canvas.DrawImage(drawn, src, src, Sampling, paint);
                }
            }
        }
        finally
        {
            _canvas.RestoreToCount(count);
        }
    }

    private static bool IsJpegOrPng(byte[] b) =>
        (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) || (b.Length > 8 && b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G');

    // ---- Effects ----

    /// <summary>
    /// EmfPlusSerializableObject: an image effect by its GUID and parameters ([MS-EMFPLUS] 2.2.3), kept for the next DrawImage.
    /// Ten map onto Egami operations; red-eye correction is skipped with a warning (design question 13).
    /// </summary>
    private void StoreEffect(ReadOnlySpan<byte> d)
    {
        Guid id = new(d[..16]);
        byte[] p = d[20..].ToArray();
        _effect = id.ToString() switch
        {
            "633c80a4-1843-482b-9ef2-be2834c5fdd4" => Effect(o => o.Blur(Math.Max(0.01f, F32(p, 0) / 2))),
            "63cbf3ee-c526-402c-8f71-62c540bf5142" => Effect(o => o.Sharpen(F32(p, 4) / 100 * 2, Math.Max(0.1f, F32(p, 0) / 2))),
            "718f2615-7933-40e3-a511-5f68fe14dd74" => Effect(o => o.ColorMatrix(Matrix5(p))),
            "d3a1dbe1-8ec4-4c17-9f4c-ea97ad1c343d" => Effect(o => o.Brightness(Math.Max(0, 1 + (I32(p, 0) / 255f))).Contrast(Math.Max(0, 1 + (I32(p, 4) / 100f)))),
            "8b2dd6c3-eb07-4d87-a5f0-7108e26a9c5f" => Effect(o => o.HueRotate(I32(p, 0)).Saturation(Math.Max(0, 1 + (I32(p, 4) / 100f))).Brightness(Math.Max(0, 1 + (I32(p, 8) / 100f)))),
            "99c354ec-2a31-4f3a-8c34-17a803b33a25" => Effect(o => o.Level(Math.Clamp(I32(p, 8) / 100f, 0, 0.99f), Math.Clamp(I32(p, 0) / 100f, 0.01f, 1), MathF.Pow(2, -I32(p, 4) / 100f))),
            "1077af00-2848-4441-9489-44ad4c2d7a2c" => Effect(o => o.Tint(HueColor(I32(p, 0)), Math.Clamp(Math.Abs(I32(p, 4)) / 100f, 0, 1))),
            "537e597d-251e-48da-9664-29ca496b70f8" => Effect(o => o.ColorMatrix(new ColorMatrix(
                [1, 0, 0, 0, I32(p, 0) / 255f, 0, 1, 0, 0, I32(p, 4) / 255f, 0, 0, 1, 0, I32(p, 8) / 255f, 0, 0, 0, 1, 0]))),
            "dd6a0022-58e4-4a67-9d9b-d48eb881a53d" => Effect(o => o.Brightness(Math.Max(0, 1 + (I32(p, 8) / 255f)))),
            "a7ce72a9-0f7f-40d7-b3cc-d0c02d5c3212" => image => Lookup(image, p),
            _ => null,
        };
        if (_effect is null)
        {
            WarnOnce("Metafile.SkippedRecord", "An image effect Egami has no equivalent for (red-eye correction, or an unknown one) was skipped (design question 13).", "effect:" + id);
        }
    }

    private static Func<Raster<Rgba8>, Raster<Rgba8>> Effect(Action<RasterOps> ops) => image => image.Derive(ops);

    /// <summary>A GDI+ 5 by 5 color matrix (row vectors: a color times the matrix) as Egami's four rows of five.</summary>
    private static ColorMatrix Matrix5(byte[] p)
    {
        float M(int row, int col) => F32(p, ((row * 5) + col) * 4);
        float[] v = new float[20];
        for (int output = 0; output < 4; output++)
        {
            for (int input = 0; input < 4; input++)
            {
                v[(output * 5) + input] = M(input, output);
            }

            v[(output * 5) + 4] = M(4, output);
        }

        return new ColorMatrix(v);
    }

    private static Rgba8 HueColor(int degrees)
    {
        float h = ((degrees % 360) + 360) % 360 / 60f;
        float x = 1 - MathF.Abs((h % 2) - 1);
        (float r, float g, float b) = (int)h switch { 0 => (1f, x, 0f), 1 => (x, 1f, 0f), 2 => (0f, 1f, x), 3 => (0f, x, 1f), 4 => (x, 0f, 1f), _ => (1f, 0f, x) };
        return new Rgba8((byte)(r * 255), (byte)(g * 255), (byte)(b * 255), 255);
    }

    /// <summary>The color lookup table effect: 256-entry tables for blue, green, red and alpha.</summary>
    private static Raster<Rgba8> Lookup(Raster<Rgba8> image, byte[] p)
    {
        if (p.Length < 1024)
        {
            return image;
        }

        byte[] table = p;
        Raster<Rgba8> copy = image.Copy();
        for (int y = 0; y < copy.Height; y++)
        {
            Span<Rgba8> row = copy.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                Rgba8 c = row[x];
                row[x] = new Rgba8(table[512 + c.R], table[256 + c.G], table[c.B], table[768 + c.A]);
            }
        }

        return copy;
    }
}
