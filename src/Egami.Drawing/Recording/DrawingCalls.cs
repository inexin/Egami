using System.Numerics;

using Egami.Fonts;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Recording;

/// <summary>
/// One recorded hook call. Replaying it makes the public call that ends in the same hook, so the target's own checks, transform
/// and clip apply; a recorded transform is replayed after the transform the picture is replayed under.
/// </summary>
internal abstract record class DrawingCall
{
    internal abstract void Replay(Canvas canvas, Matrix3x2 under);
}

internal sealed record class SaveCall : DrawingCall
{
    internal static SaveCall Instance { get; } = new();

    internal override void Replay(Canvas canvas, Matrix3x2 under) => canvas.Save();
}

internal sealed record class RestoreCall : DrawingCall
{
    internal static RestoreCall Instance { get; } = new();

    internal override void Replay(Canvas canvas, Matrix3x2 under) => canvas.Restore();
}

internal sealed record class SaveLayerCall(LayerOptions Options) : DrawingCall
{
    internal override void Replay(Canvas canvas, Matrix3x2 under) => canvas.SaveLayer(Options);
}

internal sealed record class SetMatrixCall(Matrix3x2 Matrix) : DrawingCall
{
    internal override void Replay(Canvas canvas, Matrix3x2 under) => canvas.SetMatrix(Canvas.Compose(Matrix, under));
}

internal sealed record class ClipRectCall(RectF Rect, ClipOp Op, bool Antialias) : DrawingCall
{
    internal override void Replay(Canvas canvas, Matrix3x2 under) => canvas.ClipRect(Rect, Op, Antialias);
}

internal sealed record class ClipRoundRectCall(RoundRect RoundRect, ClipOp Op, bool Antialias) : DrawingCall
{
    internal override void Replay(Canvas canvas, Matrix3x2 under) => canvas.ClipRoundRect(RoundRect, Op, Antialias);
}

internal sealed record class ClipPathCall(Path Path, ClipOp Op, bool Antialias) : DrawingCall
{
    internal override void Replay(Canvas canvas, Matrix3x2 under) => canvas.ClipPath(Path, Op, Antialias);
}

internal sealed record class DrawPaintCall(Paint Paint) : DrawingCall
{
    internal override void Replay(Canvas canvas, Matrix3x2 under) => canvas.DrawPaint(Paint);
}

internal sealed record class DrawRectCall(RectF Rect, Stroke? Stroke, Paint Paint) : DrawingCall
{
    internal override void Replay(Canvas canvas, Matrix3x2 under)
    {
        if (Stroke is null)
        {
            canvas.FillRect(Rect, Paint);
        }
        else
        {
            canvas.StrokeRect(Rect, Stroke, Paint);
        }
    }
}

internal sealed record class DrawRoundRectCall(RoundRect RoundRect, Stroke? Stroke, Paint Paint) : DrawingCall
{
    internal override void Replay(Canvas canvas, Matrix3x2 under)
    {
        if (Stroke is null)
        {
            canvas.FillRoundRect(RoundRect, Paint);
        }
        else
        {
            canvas.StrokeRoundRect(RoundRect, Stroke, Paint);
        }
    }
}

internal sealed record class DrawOvalCall(RectF Oval, Stroke? Stroke, Paint Paint) : DrawingCall
{
    internal override void Replay(Canvas canvas, Matrix3x2 under)
    {
        if (Stroke is null)
        {
            canvas.FillOval(Oval, Paint);
        }
        else
        {
            canvas.StrokeOval(Oval, Stroke, Paint);
        }
    }
}

internal sealed record class DrawPathCall(Path Path, Stroke? Stroke, Paint Paint) : DrawingCall
{
    internal override void Replay(Canvas canvas, Matrix3x2 under)
    {
        if (Stroke is null)
        {
            canvas.FillPath(Path, Paint);
        }
        else
        {
            canvas.StrokePath(Path, Stroke, Paint);
        }
    }
}

internal sealed record class DrawPointsCall(PointMode Mode, Vector2[] Points, Stroke Stroke, Paint Paint) : DrawingCall
{
    internal override void Replay(Canvas canvas, Matrix3x2 under) => canvas.DrawPoints(Mode, Points, Stroke, Paint);
}

internal sealed record class DrawImageCall(Raster Image, RectF Source, RectF Destination, SamplingOptions Sampling, Paint? Paint) : DrawingCall
{
    internal override void Replay(Canvas canvas, Matrix3x2 under) => canvas.DrawImage(Image, Source, Destination, Sampling, Paint);
}

internal sealed record class DrawGlyphRunCall(GlyphRun Run, Vector2 Origin, Paint Paint) : DrawingCall
{
    internal override void Replay(Canvas canvas, Matrix3x2 under) => canvas.DrawGlyphRun(Run, Origin, Paint);
}

internal sealed record class DrawLatticeCall(Raster Image, Lattice Lattice, RectF Destination, SamplingOptions Sampling, Paint? Paint) : DrawingCall
{
    internal override void Replay(Canvas canvas, Matrix3x2 under) => canvas.DrawImageLattice(Image, Lattice, Destination, Sampling, Paint);
}

internal sealed record class DrawAtlasCall(Raster Image, SpriteTransform[] Transforms, RectF[] Sources, RgbaF32[] Colors, BlendMode ColorBlend, SamplingOptions Sampling, Paint? Paint) : DrawingCall
{
    internal override void Replay(Canvas canvas, Matrix3x2 under) => canvas.DrawAtlas(Image, Transforms, Sources, Colors, ColorBlend, Sampling, null, Paint);
}

internal sealed record class DrawMeshCall(Mesh Mesh, Paint Paint, BlendMode ColorBlend, GradientStop[] Stops) : DrawingCall
{
    internal override void Replay(Canvas canvas, Matrix3x2 under) => canvas.DrawMesh(Mesh, Paint, ColorBlend, Stops);
}

internal sealed record class DrawPatchCall(Patch Patch, Paint Paint, BlendMode ColorBlend, GradientStop[] Stops) : DrawingCall
{
    internal override void Replay(Canvas canvas, Matrix3x2 under) => canvas.DrawPatch(Patch, Paint, ColorBlend, Stops);
}

internal sealed record class DrawPictureCall(Picture Picture, Matrix3x2 Matrix, Paint? Paint, bool CacheAsImage) : DrawingCall
{
    internal override void Replay(Canvas canvas, Matrix3x2 under) => canvas.DrawPicture(Picture, Matrix, Paint, CacheAsImage);
}
