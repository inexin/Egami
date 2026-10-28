using System.Runtime.InteropServices;

using Egami.Drawing;
using Egami.Pixels;
using Egami.SystemDrawing.Imaging;
using Egami.SystemDrawing.Internal;

namespace Egami.SystemDrawing;

/// <summary>The way from a bitmap to Egami's own rasters and back.</summary>
public sealed partial class Bitmap
{
    /// <summary>
    /// The bitmap as an Egami raster. For 32-bit ARGB and premultiplied ARGB, 24-bit RGB and the 16-bit RGB and ARGB 1555 formats
    /// the raster is a view of the bitmap's own pixels, no copy: writes show in the bitmap. The view keeps the pixels it was made
    /// over alive, so it stays safe to use, but once RotateFlip, MakeTransparent, ConvertFormat or SelectActiveFrame replaces the
    /// bitmap's pixels it no longer shows them; fetch it again then. Every other format gives a converted 8-bit RGBA copy.
    /// </summary>
    /// <returns>The raster: <c>Raster&lt;Bgra8&gt;</c>, <c>Raster&lt;PremulBgra8&gt;</c>, <c>Raster&lt;Bgr8&gt;</c>, <c>Raster&lt;Bgr565&gt;</c>, <c>Raster&lt;Bgr555&gt;</c> or <c>Raster&lt;Bgra5551&gt;</c> over the pixels, else a <c>Raster&lt;Rgba8&gt;</c> copy.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="escape-hatches"/>
    /// </example>
    public Raster AsRaster()
    {
        Frame frame = Unlocked();
        return frame.Format switch
        {
            PixelFormat.Format32bppArgb => View<Bgra8>(frame),
            PixelFormat.Format32bppPArgb => View<PremulBgra8>(frame),
            PixelFormat.Format24bppRgb => View<Bgr8>(frame),
            PixelFormat.Format16bppRgb565 => View<Bgr565>(frame),
            PixelFormat.Format16bppRgb555 => View<Bgr555>(frame),
            PixelFormat.Format16bppArgb1555 => View<Bgra5551>(frame),
            _ => ToRgba8(),
        };
    }

    /// <summary>
    /// A bitmap from an Egami raster, copied: Bgra8 into 32-bit ARGB, PremulBgra8 into premultiplied ARGB, Bgr8 into 24-bit RGB,
    /// the packed 16-bit types into their GDI+ formats, and every other pixel type through 8-bit RGBA into 32-bit ARGB.
    /// </summary>
    /// <param name="raster">The raster.</param>
    /// <returns>The bitmap, at 96 dpi or the raster's own resolution.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="escape-hatches"/>
    /// </example>
    public static Bitmap FromRaster(Raster raster)
    {
        ArgumentNullException.ThrowIfNull(raster);
        Bitmap bitmap = raster switch
        {
            Raster<Bgra8> r => Copy(r, PixelFormat.Format32bppArgb),
            Raster<PremulBgra8> r => Copy(r, PixelFormat.Format32bppPArgb),
            Raster<Bgr8> r => Copy(r, PixelFormat.Format24bppRgb),
            Raster<Bgr565> r => Copy(r, PixelFormat.Format16bppRgb565),
            Raster<Bgr555> r => Copy(r, PixelFormat.Format16bppRgb555),
            Raster<Bgra5551> r => Copy(r, PixelFormat.Format16bppArgb1555),
            _ => FromConverted(raster),
        };
        if (raster.Metadata.Resolution?.ToPerInch() is { X: > 0, Y: > 0 } dpi)
        {
            bitmap.SetResolution((float)dpi.X, (float)dpi.Y);
        }

        return bitmap;
    }

    private static Raster<TPixel> View<TPixel>(Frame frame)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        // Bitmap memory is pinned for the bitmap's life, so a view over it stays put.
        PointerMemory memory = new(frame.Scan0, Math.Abs(frame.Stride) * frame.Height, frame);
        return Raster.Wrap<TPixel>(memory.Memory, frame.Width, frame.Height, frame.Stride, memory);
    }

    private static Bitmap Copy<TPixel>(Raster<TPixel> raster, PixelFormat format)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Bitmap bitmap = new(raster.Width, raster.Height, format);
        Frame frame = bitmap.Current;
        for (int y = 0; y < raster.Height; y++)
        {
            MemoryMarshal.AsBytes(raster.Row(y)).CopyTo(frame.Row(y));
        }

        return bitmap;
    }

    private static Bitmap FromConverted(Raster raster)
    {
        using Raster<Bgra8> converted = raster.As<Bgra8>();
        return Copy(converted, PixelFormat.Format32bppArgb);
    }
}

/// <summary>The way from a graphics to Egami's own canvas.</summary>
public sealed partial class Graphics
{
    private Canvas? _canvas;
    private object? _canvasFrame;

    /// <summary>
    /// Gets an Egami canvas over the bitmap's own pixels, set to the graphics' current transform and clip, for drawing with
    /// Egami's API in between System.Drawing calls. It needs a 32-bit ARGB or premultiplied ARGB bitmap; for other formats use
    /// <see cref="Bitmap.AsRaster"/>. Its coordinates are the world coordinates of the moment it is fetched; fetch it again after
    /// changing the transform or clip. A canvas is made again when the bitmap's pixels are replaced (by RotateFlip, MakeTransparent,
    /// ConvertFormat or SelectActiveFrame), so always use the one this property returns.
    /// </summary>
    /// <exception cref="InvalidOperationException">The bitmap is not 32-bit ARGB or premultiplied ARGB.</exception>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The bitmap is locked, as for every other drawing call.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="escape-hatches"/>
    /// </example>
    public Canvas Canvas
    {
        get
        {
            // Checks the lock every time, and builds the canvas again when the bitmap's pixels were replaced.
            Frame frame = Alive()._image.UnlockedFrame();
            if (!ReferenceEquals(frame, _canvasFrame))
            {
                _canvas?.Dispose();
                _canvas = null;
                _canvasFrame = null;
                _canvas = frame.Format switch
                {
                    PixelFormat.Format32bppArgb => new RasterCanvas<Bgra8>((Raster<Bgra8>)_image.AsRaster()),
                    PixelFormat.Format32bppPArgb => new RasterCanvas<PremulBgra8>((Raster<PremulBgra8>)_image.AsRaster()),
                    _ => throw new InvalidOperationException("Graphics.Canvas needs a 32-bit ARGB or premultiplied ARGB bitmap; use Bitmap.AsRaster for other formats."),
                };
                _canvasFrame = frame;
            }

            Canvas canvas = _canvas!;
            canvas.RestoreToCount(1);
            canvas.Save();
            if (EffectiveClip is Path clip)
            {
                canvas.ClipPath(clip, ClipOp.Intersect, antialias: false);
            }

            canvas.SetMatrix(ShapeTransform);
            return canvas;
        }
    }
}
