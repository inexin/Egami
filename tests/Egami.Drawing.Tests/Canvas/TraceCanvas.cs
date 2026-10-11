using System.Globalization;
using System.Numerics;

using Egami.Operations;

namespace Egami.Drawing.Tests.Canvas;

/// <summary>
/// A backend that never touches pixels: it writes each hook call as a line of text, the way a PDF page would write operators.
/// The canvas suite runs on it as well as on the raster canvas, which shows the hook contract works for a non-raster backend.
/// </summary>
internal sealed class TraceCanvas(float width, float height) : Drawing.Canvas(width, height)
{
    public List<string> Calls { get; } = [];

    public bool Disposed { get; private set; }

    protected override void OnSave() => Calls.Add("Save");

    protected override void OnRestore() => Calls.Add("Restore");

    protected override void OnSetMatrix(Matrix3x2 matrix) => Calls.Add(F($"SetMatrix {matrix.M11} {matrix.M12} {matrix.M21} {matrix.M22} {matrix.M31} {matrix.M32}"));

    protected override void OnClipRect(RectF rect, ClipOp op, bool antialias) => Calls.Add(F($"ClipRect {R(rect)} {op} {antialias}"));

    protected override void OnClipRoundRect(RoundRect roundRect, ClipOp op, bool antialias) => Calls.Add(F($"ClipRoundRect {R(roundRect.Bounds)} {op} {antialias}"));

    protected override void OnClipPath(Path path, ClipOp op, bool antialias) => Calls.Add(F($"ClipPath {path.Verbs.Length} {op} {antialias}"));

    protected override void OnDrawPaint(Paint paint) => Calls.Add($"DrawPaint {P(paint)}");

    protected override void OnDrawRect(RectF rect, Stroke? stroke, Paint paint) => Calls.Add($"DrawRect {R(rect)} {S(stroke)} {P(paint)}");

    protected override void OnDrawRoundRect(RoundRect roundRect, Stroke? stroke, Paint paint) => Calls.Add($"DrawRoundRect {R(roundRect.Bounds)} {S(stroke)} {P(paint)}");

    protected override void OnDrawOval(RectF oval, Stroke? stroke, Paint paint) => Calls.Add($"DrawOval {R(oval)} {S(stroke)} {P(paint)}");

    protected override void OnDrawPath(Path path, Stroke? stroke, Paint paint) => Calls.Add(F($"DrawPath {path.Verbs.Length} {path.FillRule} {S(stroke)} {P(paint)}"));

    protected override void OnDrawPoints(PointMode mode, ReadOnlySpan<Vector2> points, Stroke stroke, Paint paint) => Calls.Add(F($"DrawPoints {mode} {points.Length} {S(stroke)} {P(paint)}"));

    protected override void OnDrawImage(Raster image, RectF source, RectF destination, SamplingOptions sampling, Paint? paint)
        => Calls.Add(F($"DrawImage {image.Width}x{image.Height} {R(source)} {R(destination)} {sampling.Filter} {(paint is null ? "default" : P(paint))}"));

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }

    internal FallbackImage? Fallback(Action<Drawing.Canvas> draw, float scale) => RasterizeFallback(draw, scale);

    private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    private static string R(RectF r) => F($"[{r.X} {r.Y} {r.Width} {r.Height}]");

    private static string S(Stroke? s) => s is null ? "fill" : F($"stroke {s.Width} {s.Cap} {s.Join} {s.MiterLimit}");

    private static string P(Paint p) => p is SolidPaint s ? F($"solid {s.Color.R} {s.Color.G} {s.Color.B} {s.Color.A} {p.BlendMode} {p.Opacity} {p.Antialias}") : p.GetType().Name;
}
