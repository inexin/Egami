using System.Numerics;

using Egami.Drawing;
using Egami.Operations;

namespace Egami.Svg.Rendering;

/// <summary>
/// Draws a render tree through the public canvas API only. Groups that need opacity, blending, isolation, a mask or a filter get
/// a layer; a lone shape with opacity and a single paint has the opacity folded into the paint instead, as browsers do.
/// Rendering never throws on content: what could not be drawn was reported when the tree was built.
/// </summary>
internal static class SvgRenderer
{
    /// <summary>How deep nested documents (SVG images) and masks may draw each other before drawing stops.</summary>
    private const int MaxDepth = 32;

    public static void Draw(Canvas canvas, SvgNode node, SvgRenderOptions options, int depth = 0)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        if (node.Opacity <= 0 || (node.Clip is SvgClip clip && clip.Content is null && clip.Path.IsEmpty))
        {
            return;
        }

        int count = canvas.Save();
        try
        {
            if (!node.Transform.IsIdentity)
            {
                if (!IsFinite(node.Transform))
                {
                    return;
                }

                canvas.Concat(node.Transform);
            }

            if (node.Clip is SvgClip c)
            {
                ApplyClip(canvas, c, options, depth);
            }

            bool fold = node is SvgPath p && CanFoldOpacity(p);
            if (node.NeedsLayer && !fold)
            {
                canvas.SaveLayer(new LayerOptions
                {
                    Bounds = node.Filter is SvgFilter region ? region.Region : SvgVisualBounds.Of(node),
                    Opacity = Math.Clamp(node.Opacity, 0, 1),
                    BlendMode = node.BlendMode,
                    Isolated = true,
                    Mask = node.Mask is SvgMask mask ? MaskOf(mask, options, depth) : null,
                });
            }

            if (node.Filter is SvgFilter filter)
            {
                // The filter runs on the content first; the clip, mask and opacity set up above then apply to its result.
                SvgFilterRenderer.Draw(canvas, filter, c => Content(c, node, 1, options, depth), options, depth);
            }
            else
            {
                Content(canvas, node, fold ? node.Opacity : 1, options, depth);
            }
        }
        finally
        {
            canvas.RestoreToCount(count);
        }
    }

    /// <summary>What a node itself draws, without its transform, clip, mask, opacity or filter.</summary>
    private static void Content(Canvas canvas, SvgNode node, float opacity, SvgRenderOptions options, int depth)
    {
        switch (node)
        {
            case SvgPath path:
                DrawPath(canvas, path, opacity, options, depth);
                break;
            case SvgGroup group:
                if (group.ClipRect is RectF rect)
                {
                    canvas.ClipRect(rect);
                }

                foreach (SvgNode child in group.Children)
                {
                    Draw(canvas, child, options, depth);
                }

                break;
            case SvgImage image:
                DrawImage(canvas, image, options, depth);
                break;
            case SvgTextRun text:
                DrawText(canvas, text, options);
                break;
        }
    }

    /// <summary>A glyph run: filled with DrawGlyphRun (or as outlines for TextAsPaths), stroked as outlines, in paint order.</summary>
    private static void DrawText(Canvas canvas, SvgTextRun node, SvgRenderOptions options)
    {
        Path? outlines = null;
        Path Outlines() => outlines ??= node.Run.ToPath(Vector2.Zero);
        void FillRun()
        {
            if (node.Fill is Paint fill)
            {
                Paint paint = node.Antialias ? fill : fill with { Antialias = false };
                if (options.TextAsPaths)
                {
                    canvas.FillPath(Outlines(), paint);
                }
                else
                {
                    canvas.DrawGlyphRun(node.Run, Vector2.Zero, paint);
                }
            }
        }

        void StrokeRun()
        {
            if (node.StrokePaint is Paint paint && node.Stroke is Stroke stroke)
            {
                canvas.StrokePath(Outlines(), stroke, node.Antialias ? paint : paint with { Antialias = false });
            }
        }

        if (node.PaintOrder is SvgPaintOrder.StrokeFillMarkers or SvgPaintOrder.StrokeMarkersFill or SvgPaintOrder.MarkersStrokeFill)
        {
            StrokeRun();
            FillRun();
        }
        else
        {
            FillRun();
            StrokeRun();
        }
    }

    private static bool CanFoldOpacity(SvgPath path) =>
        path.Opacity < 1 && path.BlendMode == BlendMode.SourceOver && !path.Isolated && path.Mask is null && path.Filter is null &&
        path.Markers is null && (path.Fill is null || path.StrokePaint is null || path.Stroke is null);

    private static void DrawPath(Canvas canvas, SvgPath node, float opacity, SvgRenderOptions options, int depth)
    {
        switch (node.PaintOrder)
        {
            case SvgPaintOrder.FillStrokeMarkers:
                Fill(canvas, node, opacity);
                StrokeShape(canvas, node, opacity);
                Markers(canvas, node, options, depth);
                break;
            case SvgPaintOrder.FillMarkersStroke:
                Fill(canvas, node, opacity);
                Markers(canvas, node, options, depth);
                StrokeShape(canvas, node, opacity);
                break;
            case SvgPaintOrder.StrokeFillMarkers:
                StrokeShape(canvas, node, opacity);
                Fill(canvas, node, opacity);
                Markers(canvas, node, options, depth);
                break;
            case SvgPaintOrder.StrokeMarkersFill:
                StrokeShape(canvas, node, opacity);
                Markers(canvas, node, options, depth);
                Fill(canvas, node, opacity);
                break;
            case SvgPaintOrder.MarkersFillStroke:
                Markers(canvas, node, options, depth);
                Fill(canvas, node, opacity);
                StrokeShape(canvas, node, opacity);
                break;
            default:
                Markers(canvas, node, options, depth);
                StrokeShape(canvas, node, opacity);
                Fill(canvas, node, opacity);
                break;
        }
    }

    private static void Fill(Canvas canvas, SvgPath node, float opacity)
    {
        if (node.Fill is Paint paint)
        {
            canvas.FillPath(node.Path, Adjust(paint, opacity, node.Antialias));
        }
    }

    private static void StrokeShape(Canvas canvas, SvgPath node, float opacity)
    {
        if (node.StrokePaint is not Paint paint || node.Stroke is not Stroke stroke)
        {
            return;
        }

        paint = Adjust(paint, opacity, node.Antialias);
        if (!node.NonScalingStroke)
        {
            canvas.StrokePath(node.Path, stroke, paint);
            return;
        }

        // A non-scaling stroke is drawn in device pixels: the path goes to the device, the paint keeps its user space.
        Matrix3x2 matrix = canvas.Matrix;
        int count = canvas.Save();
        canvas.ResetMatrix();
        canvas.StrokePath(node.Path.Transform(matrix), stroke, paint with { LocalMatrix = paint.LocalMatrix * matrix });
        canvas.RestoreToCount(count);
    }

    private static void Markers(Canvas canvas, SvgPath node, SvgRenderOptions options, int depth)
    {
        if (node.Markers is SvgGroup markers)
        {
            Draw(canvas, markers, options, depth);
        }
    }

    private static Paint Adjust(Paint paint, float opacity, bool antialias)
    {
        if (opacity < 1)
        {
            paint = paint with { Opacity = paint.Opacity * Math.Clamp(opacity, 0, 1) };
        }

        return antialias ? paint : paint with { Antialias = false };
    }

    private static void DrawImage(Canvas canvas, SvgImage image, SvgRenderOptions options, int depth)
    {
        if (image.ClipRect is RectF clip)
        {
            canvas.ClipRect(clip);
        }

        if (image.Raster is EncodedImage raster)
        {
            canvas.DrawImage(raster, image.Source, image.Destination, image.Sampling);
        }
        else if (image.Document is SvgDocument document && image.Source.Width > 0 && image.Source.Height > 0)
        {
            Matrix3x2 fit = new(
                image.Destination.Width / image.Source.Width, 0,
                0, image.Destination.Height / image.Source.Height,
                image.Destination.X - (image.Source.X * image.Destination.Width / image.Source.Width),
                image.Destination.Y - (image.Source.Y * image.Destination.Height / image.Source.Height));
            if (!IsFinite(fit))
            {
                return;
            }

            canvas.Concat(fit);
            Draw(canvas, document.RenderTree, options, depth + 1);
        }
    }

    /// <summary>Whether a transform can be drawn with: every element finite and the scale not collapsed to nothing.</summary>
    internal static bool IsFinite(Matrix3x2 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M31) && float.IsFinite(m.M32) &&
        float.IsFinite(m.GetDeterminant());

    /// <summary>Clips to one path when the clip is one; otherwise draws the clip's content as an alpha mask in a layer.</summary>
    private static void ApplyClip(Canvas canvas, SvgClip clip, SvgRenderOptions options, int depth)
    {
        if (clip.Content is SvgGroup content)
        {
            canvas.SaveLayer(new LayerOptions { Mask = new SoftMask(c => Draw(c, content, options, depth + 1), SoftMaskKind.Alpha) });
            return;
        }

        canvas.ClipPath(clip.Path);
    }

    private static SoftMask MaskOf(SvgMask mask, SvgRenderOptions options, int depth) => new(c =>
    {
        int count = c.Save();
        c.ClipRect(mask.Region);
        if (mask.Mask is SvgMask inner)
        {
            c.SaveLayer(new LayerOptions { Mask = MaskOf(inner, options, depth + 1) });
        }

        Draw(c, mask.Content, options, depth + 1);
        c.RestoreToCount(count);
    }, mask.Kind);
}
