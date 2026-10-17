using System.Numerics;

using Egami.Drawing;
using Egami.Operations;
using Egami.Pixels;
using Egami.Svg.Output;

namespace Egami.Svg;

public sealed partial class SvgCanvas
{
    private PictureRecorder? _recorder;
    private int _recordDepth;
    private string? _recordReason;

    /// <inheritdoc path="/*[not(self::example)]"/>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Paints.cs" region="layers"/>
    /// </example>
    protected override void OnSaveLayer(LayerOptions options)
    {
        PushLevel();
        if (_recorder is not null)
        {
            _recorder.SetMatrix(Matrix);
            _recorder.SaveLayer(options);
            _recordDepth++;
            return;
        }

        if (_quiet > 0)
        {
            return;
        }

        string? reason = options.Filter is not null ? "a layer with a filter"
            : options.Backdrop is not null ? "a layer with a backdrop filter"
            : options.Mask?.Transfer is not null ? "a layer with a soft mask's transfer function"
            : null;
        if (reason is not null)
        {
            // The layer's content is recorded and drawn by Egami at its Restore, then placed as one image.
            _recorder = new PictureRecorder(new RectF(0, 0, Width, Height));
            _recorder.SetMatrix(Matrix);
            _recorder.SaveLayer(options);
            _recordDepth = 1;
            _recordReason = reason;
            return;
        }

        if (options.Bounds is RectF bounds)
        {
            WriteClip(Path.Rect(bounds), ClipOp.Intersect, antialias: true);
        }

        (bool skip, string? blend) = Blend(options.BlendMode, null);
        SvgAttrs a = new SvgAttrs()
            .Add("clip-path", ClipRef)
            .Add("opacity", skip ? "0" : options.Opacity < 1 ? SvgNumbers.Unit(options.Opacity) : null)
            .Add("mask", options.Mask is SoftMask mask ? $"url(#{MaskDefinition(mask)})" : null)
            .Add("style", Style(BlendDeclaration(blend), options.Isolated ? "isolation:isolate" : null));
        OpenGroup(a);

        // The group is clipped: what is drawn in it needs only the clips made inside it.
        _clips[^1] = null;
    }

    /// <inheritdoc/>
    protected override void OnRestoreLayer() => OnRestore();

    /// <inheritdoc/>
    protected override void OnDrawMesh(Mesh mesh, Paint paint, BlendMode colorBlend, ReadOnlySpan<GradientStop> valueStops)
    {
        if (Recording())
        {
            _recorder!.DrawMesh(mesh, paint, colorBlend, valueStops);
            return;
        }

        if (_quiet > 0)
        {
            return;
        }

        (bool skip, string? blend) = Blend(paint.BlendMode, paint);
        if (skip)
        {
            return;
        }

        GradientStop[] stops = valueStops.ToArray();
        Paint flat = paint with { BlendMode = blend is null ? paint.BlendMode : BlendMode.SourceOver, Opacity = 1 };
        Fallback(mesh.Bounds, 1, c => c.DrawMesh(mesh, flat, colorBlend, stops), "a mesh", blend, paint.Opacity);
    }

    /// <inheritdoc/>
    protected override void OnDrawPicture(Picture picture, Matrix3x2 matrix, Paint? paint, bool cacheAsImage)
    {
        if (Recording())
        {
            _recorder!.DrawPicture(picture, matrix, paint, cacheAsImage);
            return;
        }

        if (_quiet > 0)
        {
            return;
        }

        string? blend = null;
        float opacity = 1;
        if (paint is not null)
        {
            (bool skip, blend) = Blend(paint.BlendMode, null);
            if (skip)
            {
                return;
            }

            opacity = paint.Opacity;
        }

        Matrix3x2 full = matrix * Matrix;
        if (!float.IsFinite(full.GetDeterminant()))
        {
            return;
        }

        string id = PictureDefinition(picture, MathF.Sqrt(MathF.Abs(full.GetDeterminant())));
        bool clipped = OpenClip();
        _body.Empty("use", new SvgAttrs()
            .Add("xlink:href", "#" + id)
            .Add("transform", Num.Transform(full))
            .Add("opacity", opacity < 1 ? SvgNumbers.Unit(opacity) : null)
            .Add("style", Style(BlendDeclaration(blend))));
        CloseClip(clipped);
    }

    /// <summary>A picture's calls written once in defs, as a group in the picture's own coordinates.</summary>
    private string PictureDefinition(Picture picture, float scale)
    {
        return _out.DefineOnce(picture, "pic", id =>
        {
            RectF b = picture.Bounds;
            float fallback = _fallbackScale * (float.IsFinite(scale) ? Math.Clamp(scale, 0.25f, 8) : 1);
            SvgCanvas inner = Fragment(b.Width, b.Height, SvgOutput.DefDepth + 1, fallback);
            inner.SetMatrix(Matrix3x2.CreateTranslation(-b.X, -b.Y));
            picture.Playback(inner);
            return SvgMarkup.Element("g", new SvgAttrs().Add("id", id).Add("transform", Num.Transform(Matrix3x2.CreateTranslation(b.X, b.Y))), inner.Finish(), _out.Compact, SvgOutput.DefDepth);
        });
    }

    /// <summary>A soft mask drawn into a mask element in device coordinates, over the layer's clip bounds.</summary>
    private string MaskDefinition(SoftMask mask)
    {
        RectF region = DeviceClipBounds;
        SvgCanvas inner = Fragment(Width, Height, SvgOutput.DefDepth + 1, _fallbackScale);
        if (!region.IsEmpty)
        {
            inner.ClipRect(region);
            RgbaF32 backdrop = mask.Backdrop;
            if (mask.Kind == SoftMaskKind.Luminosity && (backdrop.R > 0 || backdrop.G > 0 || backdrop.B > 0))
            {
                inner.FillRect(region, new SolidPaint(new RgbaF32(backdrop.R, backdrop.G, backdrop.B, 1)));
            }

            inner.SetMatrix(Matrix);
            mask.Draw(inner);
        }

        string content = inner.Finish();
        SvgAttrs place = new SvgAttrs()
            .Add("x", Num.F(region.X)).Add("y", Num.F(region.Y)).Add("width", Num.F(region.Width)).Add("height", Num.F(region.Height));
        if (mask.Kind == SoftMaskKind.Alpha && content.Length > 0)
        {
            // Every color to white, alpha kept: the luminance mask SVG 1.1 has becomes an alpha mask.
            SvgAttrs filterAttrs = new SvgAttrs().Add("filterUnits", "userSpaceOnUse");
            filterAttrs.AddRange(place);
            string white = _out.Define("f", "filter", filterAttrs, Child(SvgOutput.DefDepth + 1, "feColorMatrix", new SvgAttrs()
                .Add("type", "matrix")
                .Add("values", "0 0 0 0 1 0 0 0 0 1 0 0 0 0 1 0 0 0 1 0")));
            content = Child(SvgOutput.DefDepth + 1, "g", new SvgAttrs().Add("filter", $"url(#{white})"), content);
        }

        SvgAttrs a = new SvgAttrs().Add("maskUnits", "userSpaceOnUse");
        a.AddRange(place);
        a.Add("color-interpolation", "sRGB");
        return _out.Define("m", "mask", a, content);
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
        Fallback(null, 0, picture.Playback, _recordReason!, null, 1);
    }

    /// <summary>
    /// Draws a call into an image through <see cref="Canvas.RasterizeFallback"/> at <see cref="SvgWriteOptions.FallbackScale"/> and
    /// writes it in device coordinates: over the local bounds given (grown by an outset in device units), or over the whole clip.
    /// With bounds, <paramref name="draw"/> draws in local coordinates; without, in device coordinates.
    /// </summary>
    private void Fallback(RectF? localBounds, float outset, Action<Canvas> draw, string what, string? blend, float opacity)
    {
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
                _fallbackScale);
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
            RectF r = image.DeviceBounds;
            _body.Empty("image", new SvgAttrs()
                .Add("x", Num.F(r.X)).Add("y", Num.F(r.Y)).Add("width", Num.F(r.Width)).Add("height", Num.F(r.Height))
                .Add("preserveAspectRatio", "none")
                .Add("clip-path", ClipRef)
                .Add("opacity", opacity < 1 ? SvgNumbers.Unit(opacity) : null)
                .Add("style", Style(BlendDeclaration(blend)))
                .Add("xlink:href", _out.ImageUri(image.Image, allowJpeg: false)));
        }

        _out.Report(DiagnosticLevel.Info, "Svg.FallbackImage", $"SVG has no form for {what}; it was drawn as an image.");
    }
}
