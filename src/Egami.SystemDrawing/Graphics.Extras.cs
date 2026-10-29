using System.Runtime.InteropServices;

using Egami.SystemDrawing.Drawing2D;
using Egami.SystemDrawing.Imaging;
using Egami.SystemDrawing.Imaging.Effects;
using Egami.SystemDrawing.Internal;

namespace Egami.SystemDrawing;

/// <summary>Metafile records, cached bitmaps and effects.</summary>
public sealed partial class Graphics
{
    /// <summary>Called for each record while a metafile is enumerated.</summary>
    /// <param name="recordType">The record's type.</param>
    /// <param name="flags">Its flags (EMF+ records only).</param>
    /// <param name="dataSize">The size of its data in bytes.</param>
    /// <param name="data">Its data, valid during the call only.</param>
    /// <param name="callbackData">Always null: Egami does not play single records.</param>
    /// <returns><see langword="true"/> to go on.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="metafiles"/>
    /// </example>
    public delegate bool EnumerateMetafileProc(EmfPlusRecordType recordType, int flags, int dataSize, IntPtr data, PlayRecordCallback? callbackData);

    /// <summary>Does nothing: comments are for metafiles being recorded, and Egami draws on bitmaps.</summary>
    /// <param name="data">The comment.</param>
    public void AddMetafileComment(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Alive();
    }

    /// <summary>Draws a cached bitmap at device pixels, ignoring the transform but its translation, as GDI+ draws it.</summary>
    /// <param name="cachedBitmap">The cached bitmap.</param>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    public void DrawCachedBitmap(CachedBitmap cachedBitmap, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(cachedBitmap);
        GraphicsState saved = Save();
        try
        {
            System.Numerics.Matrix3x2 m = ImageTransform;
            ResetTransform();
            PageUnit = GraphicsUnit.Pixel;
            PageScale = 1;
            InterpolationMode = InterpolationMode.NearestNeighbor;
            PixelOffsetMode = PixelOffsetMode.Half;
            Bitmap bitmap = cachedBitmap.Bitmap;
            DrawImage(bitmap, new RectangleF(x + m.M31, y + m.M32, bitmap.Width, bitmap.Height));
        }
        finally
        {
            Restore(saved);
        }
    }

    /// <summary>Draws an image with an effect applied, at its physical size.</summary>
    /// <param name="image">The image.</param>
    /// <param name="effect">The effect.</param>
    public void DrawImage(Image image, Effect effect)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(effect);
        using Bitmap applied = new(image);
        applied.ApplyEffect(effect, System.Drawing.Rectangle.Empty);
        DrawImage(applied, 0f, 0f);
    }

    /// <summary>Draws part of an image with an effect applied, through a transform.</summary>
    /// <param name="image">The image.</param>
    /// <param name="effect">The effect.</param>
    /// <param name="srcRect">The part of the image.</param>
    /// <param name="transform">A transform for the image, or null.</param>
    /// <param name="srcUnit">The unit of the part.</param>
    /// <param name="imageAttr">Color adjustments, or null.</param>
    public void DrawImage(Image image, Effect effect, RectangleF srcRect = default, Matrix? transform = null, GraphicsUnit srcUnit = GraphicsUnit.Pixel, ImageAttributes? imageAttr = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(effect);
        using Bitmap applied = new(image);
        applied.ApplyEffect(effect, System.Drawing.Rectangle.Empty);
        RectangleF source = srcRect.IsEmpty ? new RectangleF(0, 0, image.Width, image.Height) : srcRect;
        GraphicsState saved = Save();
        try
        {
            if (transform is not null)
            {
                MultiplyTransform(transform);
            }

            DrawImageRect(applied, new RectangleF(0, 0, source.Width, source.Height), source, srcUnit, imageAttr);
        }
        finally
        {
            Restore(saved);
        }
    }

    /// <summary>
    /// Calls the callback for each record, its data pinned for the call, until it returns false; then, if the callback played a
    /// record, draws the metafile.
    /// </summary>
    private void Enumerate(Metafile metafile, EnumerateMetafileProc callback, Action draw)
    {
        ArgumentNullException.ThrowIfNull(metafile);
        ArgumentNullException.ThrowIfNull(callback);
        Alive();
        byte[] data = metafile.Scan.Data;
        GCHandle pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            IntPtr start = pin.AddrOfPinnedObject();
            foreach (MetafileRecord record in metafile.Scan.Records)
            {
                IntPtr at = record.Size > 0 ? start + record.Offset : IntPtr.Zero;
                if (!callback(record.Type, record.Flags, record.Size, at, null))
                {
                    break;
                }
            }
        }
        finally
        {
            pin.Free();
        }

        if (metafile.TakePlayed())
        {
            draw();
        }
    }
}
