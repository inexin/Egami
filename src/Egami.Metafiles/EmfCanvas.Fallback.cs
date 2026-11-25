using System.Numerics;

using Egami.Drawing;
using Egami.Formats.Png;
using Egami.GdiPlus;
using Egami.Metafiles.Writing;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Metafiles;

public sealed partial class EmfCanvas
{
    private PictureRecorder? _recorder;
    private int _recordDepth;
    private string? _recordReason;

    // Folding a layer's opacity into its shapes: whether the layer being recorded still may be, its opacity, the device boxes of
    // its shapes so far, the opacity applied while it is played back, and whether the next SaveLayer is the folded layer's own.
    private readonly List<RectF> _foldBoxes = [];
    private bool _foldable;
    private float _foldOpacity = 1;
    private float _fold = 1;
    private bool _foldEnter;

    /// <inheritdoc/>
    protected override void OnSaveLayer(LayerOptions options)
    {
        if (Recording())
        {
            _recorder!.SaveLayer(options);
            _recordDepth++;
            _foldable = false;
            return;
        }

        if (_quiet > 0)
        {
            return;
        }

        if (_foldEnter)
        {
            // The folded layer itself, played back: a saved state clipped to its bounds, its opacity now in each shape.
            _foldEnter = false;
            WriteSave();
            if (options.Bounds is RectF folded)
            {
                WriteClip(Path.Rect(folded), folded, ClipOp.Intersect, antialias: true);
            }

            return;
        }

        string? reason = options.Filter is not null ? "a layer with a filter"
            : options.Backdrop is not null ? "a layer with a backdrop filter"
            : options.Mask is not null ? "a layer with a soft mask"
            : options.ColorSpace is not null ? "a layer in another color space"
            : options.Knockout ? "a knockout layer"
            : options.BlendMode != BlendMode.SourceOver ? $"a layer with the {options.BlendMode} blend mode"
            : options.Opacity < 1 ? "a layer with opacity"
            : null;
        if (reason is null)
        {
            // A plain layer draws as its content does: a saved state, clipped to its bounds.
            WriteSave();
            if (options.Bounds is RectF bounds)
            {
                WriteClip(Path.Rect(bounds), bounds, ClipOp.Intersect, antialias: true);
            }

            return;
        }

        // The layer's content is recorded. At its Restore a layer with only an opacity whose shapes do not overlap is played back
        // with the opacity in each shape's paint; any other is drawn by Egami and placed as one image.
        _foldable = options.Opacity < 1 && reason == "a layer with opacity";
        _foldOpacity = options.Opacity;
        _foldBoxes.Clear();
        _recorder = new PictureRecorder(new RectF(0, 0, Width, Height));
        _recorder.SetMatrix(Matrix);
        _recorder.SaveLayer(options);
        _recordDepth = 1;
        _recordReason = reason;
    }

    /// <inheritdoc/>
    protected override void OnDrawImage(Raster image, RectF source, RectF destination, SamplingOptions sampling, Paint? paint)
    {
        if (Recording())
        {
            _recorder!.DrawImage(image, source, destination, sampling, paint);
            _foldable = false;
        }
        else if (_quiet == 0)
        {
            Fallback(destination, 1, c => c.DrawImage(image, source, destination, sampling, paint), "an image");
        }
    }

    /// <inheritdoc/>
    protected override void OnDrawImageMask(Raster mask, RectF destination, Paint paint, SamplingOptions sampling)
    {
        if (Recording())
        {
            _recorder!.DrawImageMask(mask, destination, paint, sampling);
            _foldable = false;
        }
        else if (_quiet == 0)
        {
            Fallback(destination, 1, c => c.DrawImageMask(mask, destination, paint, sampling), "an image mask");
        }
    }

    /// <inheritdoc/>
    protected override void OnDrawMesh(Mesh mesh, Paint paint, BlendMode colorBlend, ReadOnlySpan<GradientStop> valueStops)
    {
        GradientStop[] stops = valueStops.ToArray();
        if (Recording())
        {
            _recorder!.DrawMesh(mesh, paint, colorBlend, stops);
            _foldable = false;
        }
        else if (_quiet == 0)
        {
            Fallback(mesh.Bounds, 1, c => c.DrawMesh(mesh, paint, colorBlend, stops), "a mesh");
        }
    }

    /// <summary>Whether calls go to the recorder of a layer being drawn as an image; if so, its transform is brought up to date.</summary>
    private bool Recording()
    {
        if (_recorder is null)
        {
            return false;
        }

        _recorder.SetMatrix(Matrix);
        return true;
    }

    private void FinishRecording()
    {
        Picture picture = _recorder!.Finish();
        _recorder = null;
        if (!_foldable)
        {
            Fallback(null, 0, picture.Playback, _recordReason!);
            return;
        }

        // Shapes that cover no pixel in common look the same drawn one by one at the layer's opacity: no image needed.
        _foldable = false;
        float before = _fold;
        _fold *= _foldOpacity;
        _foldEnter = true;
        int count = Save();
        try
        {
            ResetMatrix();
            picture.Playback(this);
        }
        finally
        {
            RestoreToCount(count);
            _fold = before;
            _foldEnter = false;
        }
    }

    /// <summary>
    /// Notes a shape drawn into a layer being recorded: the layer stays foldable while every shape is drawn over with a plain paint
    /// and the whole pixels they reach do not meet.
    /// </summary>
    private void NoteLayerShape(RectF local, float outset, Paint paint)
    {
        if (!_foldable)
        {
            return;
        }

        if (paint.BlendMode != BlendMode.SourceOver || paint.Blur is not null || !Invertible(Matrix))
        {
            _foldable = false;
            return;
        }

        RectF box = MapRect(local, Matrix).Inflate(outset, outset).Intersect(DeviceClipBounds);
        if (box.IsEmpty)
        {
            return;
        }

        box = RectF.FromEdges(MathF.Floor(box.Left), MathF.Floor(box.Top), MathF.Ceiling(box.Right), MathF.Ceiling(box.Bottom));
        foreach (RectF other in _foldBoxes)
        {
            if (!other.Intersect(box).IsEmpty)
            {
                _foldable = false;
                return;
            }
        }

        _foldBoxes.Add(box);
    }

    /// <summary>
    /// Draws a call into an image through <see cref="Canvas.RasterizeFallback"/> at <see cref="EmfWriteOptions.FallbackScale"/> and
    /// writes it in device coordinates: over the local bounds given (grown by an outset in device units), or over the whole clip.
    /// With bounds, <paramref name="draw"/> draws in local coordinates; without, in device coordinates.
    /// </summary>
    private void Fallback(RectF? localBounds, float outset, Action<Canvas> draw, string what, bool plus = true)
    {
        if (!plus && !_gdi)
        {
            return;
        }

        Matrix3x2 m = Matrix;
        RectF device = DeviceClipBounds;
        if (localBounds is RectF lb)
        {
            device = device.Intersect(MapRect(lb, m).Inflate(outset, outset));
        }

        if (device.IsEmpty)
        {
            return;
        }

        FallbackImage? image;
        _quiet++;
        int count = Save();
        try
        {
            ResetMatrix();
            if (localBounds is not null)
            {
                ClipRect(device);
            }

            image = RasterizeFallback(
                localBounds is null ? draw : c =>
                {
                    c.SetMatrix(m * c.Matrix);
                    draw(c);
                },
                Options.FallbackScale);
        }
        finally
        {
            RestoreToCount(count);
            _quiet--;
        }

        if (image is null)
        {
            return;
        }

        using (image.Image)
        {
            WriteImage(image.Image, image.DeviceBounds, plus && _plus);
        }

        // A warning when a reader of the file gets the image: always but when only the GDI copy of an exact EMF+ drawing is one.
        if (plus || !_plus)
        {
            Warn(what);
        }
    }

    /// <summary>
    /// Writes an image at a device rectangle: an EMF+ image object (PNG) drawn by DrawImage, and a GDI AlphaBlend of a 32-bit DIB.
    /// </summary>
    private void WriteImage(Raster<RgbaF32> image, RectF device, bool plus)
    {
        using Raster<Rgba8> rgba = image.As<Rgba8>();
        if (plus)
        {
            int id = _w.PlusObject(5, ImageObject(rgba));
            SetPlusWorld(Matrix3x2.Identity);
            _w.Plus(0x401A, (ushort)id, _w.Data()
                .U32(0).I32(2) // no image attributes, UnitPixel
                .F32(0).F32(0).F32(rgba.Width).F32(rgba.Height)
                .F32(device.X).F32(device.Y).F32(device.Width).F32(device.Height).Span);
        }

        if (_gdi)
        {
            SetGdiWorld(Matrix3x2.Identity);
            int w = rgba.Width, h = rgba.Height;
            RecordBuffer b = new((w * h * 4) + 160);
            b.Bytes(Bounds16(device));
            b.I32(Fix(device.X)).I32(Fix(device.Y)).I32(Fix(device.Width)).I32(Fix(device.Height));
            b.U32(0x01FF0000); // BLENDFUNCTION: AC_SRC_OVER, constant alpha 255, AC_SRC_ALPHA
            b.I32(0).I32(0).Matrix(Matrix3x2.Identity).U32(0).U32(0); // source corner, source transform, background, DIB_RGB_COLORS
            const int Header = 108, Info = 40;
            b.U32(Header).U32(Info).U32(Header + Info).U32((uint)(w * h * 4)).I32(w).I32(h);

            // BITMAPINFOHEADER of a bottom-up 32-bit BI_RGB DIB, then its premultiplied BGRA rows.
            b.U32(Info).I32(w).I32(h).U16(1).U16(32).U32(0).U32((uint)(w * h * 4)).I32(0).I32(0).U32(0).U32(0);
            for (int y = h - 1; y >= 0; y--)
            {
                foreach (Rgba8 p in rgba.Row(y))
                {
                    b.Bytes([(byte)(((p.B * p.A) + 127) / 255), (byte)(((p.G * p.A) + 127) / 255), (byte)(((p.R * p.A) + 127) / 255), p.A]);
                }
            }

            _w.Gdi(114, b.Span); // EMR_ALPHABLEND
        }

        RectF clipped = device.Intersect(new RectF(0, 0, Width, Height));
        if (!clipped.IsEmpty)
        {
            _drawn = _drawn.IsEmpty ? clipped : RectF.FromEdges(MathF.Min(_drawn.Left, clipped.Left), MathF.Min(_drawn.Top, clipped.Top), MathF.Max(_drawn.Right, clipped.Right), MathF.Max(_drawn.Bottom, clipped.Bottom));
        }
    }
}
