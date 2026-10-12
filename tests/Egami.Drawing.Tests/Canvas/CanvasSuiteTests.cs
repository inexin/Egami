using System.Numerics;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Canvas;

/// <summary>
/// The canvas suite: state, transforms, clip bounds and argument checks, run on the raster backend and on the trace backend, so
/// everything the base class promises holds for any backend.
/// </summary>
public class CanvasSuiteTests
{
    public static TheoryData<string> Backends() => ["raster", "trace"];

    [Theory]
    [MemberData(nameof(Backends))]
    public void SaveAndRestoreBringBackTheTransformAndClip(string backend)
    {
        using Egami.Drawing.Canvas canvas = Create(backend);
        Assert.Equal(1, canvas.SaveCount);
        Assert.Equal(new RectF(0, 0, 64, 48), canvas.DeviceClipBounds);
        int first = canvas.Save();
        Assert.Equal(1, first);
        canvas.Translate(10, 5);
        canvas.ClipRect(new RectF(0, 0, 20, 20));
        Assert.Equal(new RectF(10, 5, 20, 20), canvas.DeviceClipBounds);
        Assert.Equal(new RectF(0, 0, 20, 20), canvas.LocalClipBounds);
        int second = canvas.Save();
        Assert.Equal(2, second);
        canvas.Scale(2);
        canvas.ClipRect(new RectF(0, 0, 5, 5));
        Assert.Equal(new RectF(10, 5, 10, 10), canvas.DeviceClipBounds);
        Assert.Equal(3, canvas.SaveCount);
        canvas.Restore();
        Assert.Equal(Matrix3x2.CreateTranslation(10, 5), canvas.Matrix);
        Assert.Equal(new RectF(10, 5, 20, 20), canvas.DeviceClipBounds);
        canvas.Save();
        canvas.Save();
        canvas.RestoreToCount(first);
        Assert.Equal(1, canvas.SaveCount);
        Assert.Equal(Matrix3x2.Identity, canvas.Matrix);
        Assert.Equal(new RectF(0, 0, 64, 48), canvas.DeviceClipBounds);
        canvas.RestoreToCount(1);
        Assert.Throws<InvalidOperationException>(canvas.Restore);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void TransformsCompose(string backend)
    {
        using Egami.Drawing.Canvas canvas = Create(backend);
        canvas.Translate(10, 20);
        canvas.Rotate(90);
        Vector2 p = Vector2.Transform(new Vector2(1, 0), canvas.Matrix);
        Assert.Equal(10, p.X, 4);
        Assert.Equal(21, p.Y, 4);
        canvas.ResetMatrix();
        canvas.Rotate(90, new Vector2(5, 5));
        Vector2 q = Vector2.Transform(new Vector2(5, 5), canvas.Matrix);
        Assert.Equal(5, q.X, 4);
        Assert.Equal(5, q.Y, 4);
        canvas.SetMatrix(Matrix3x2.CreateScale(3));
        canvas.Concat(Matrix3x2.CreateTranslation(1, 1));
        Assert.Equal(new Vector2(6, 6), Vector2.Transform(new Vector2(1, 1), canvas.Matrix));
        canvas.ResetMatrix();
        canvas.Skew(45, 0);
        Assert.Equal(new Vector2(11, 10), Vector2.Transform(new Vector2(1, 10), canvas.Matrix));
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void ClipBoundsShrinkOnlyForIntersections(string backend)
    {
        using Egami.Drawing.Canvas canvas = Create(backend);
        canvas.ClipRect(new RectF(4, 4, 30, 30), ClipOp.Difference);
        Assert.Equal(new RectF(0, 0, 64, 48), canvas.DeviceClipBounds);
        canvas.ClipPath(Path.Circle(new Vector2(20, 20), 10));
        Assert.Equal(10, canvas.DeviceClipBounds.Left, 3);
        Assert.Equal(30, canvas.DeviceClipBounds.Bottom, 3);
        Assert.True(canvas.QuickReject(new RectF(40, 40, 5, 5)));
        Assert.False(canvas.QuickReject(new RectF(15, 15, 5, 5)));
        Assert.True(canvas.QuickReject(Path.Empty));
        canvas.ClipRoundRect(new RoundRect(new RectF(50, 0, 10, 10), 2));
        Assert.True(canvas.DeviceClipBounds.IsEmpty);
        Assert.True(canvas.LocalClipBounds.IsEmpty);
        Assert.True(canvas.QuickReject(new RectF(15, 15, 5, 5)));
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void BadArgumentsThrowAndEmptyShapesDrawNothing(string backend)
    {
        using Egami.Drawing.Canvas canvas = Create(backend);
        Assert.Throws<ArgumentException>(() => canvas.Translate(float.NaN, 0));
        Assert.Throws<ArgumentException>(() => canvas.FillRect(new RectF(float.NaN, 0, 1, 1), Rgba8.Red));
        Assert.Throws<ArgumentException>(() => canvas.FillCircle(new Vector2(0, float.PositiveInfinity), 3, Rgba8.Red));
        Assert.Throws<ArgumentException>(() => canvas.SetMatrix(new Matrix3x2(float.NaN, 0, 0, 1, 0, 0)));
        Assert.Throws<ArgumentNullException>(() => canvas.FillPath(null!, Rgba8.Red));
        Assert.Throws<ArgumentNullException>(() => canvas.StrokePath(Path.Empty, null!, Rgba8.Red));
        Assert.Throws<ArgumentOutOfRangeException>(() => canvas.ClipRect(new RectF(0, 0, 1, 1), (ClipOp)9));
        Assert.Throws<ArgumentOutOfRangeException>(() => canvas.DrawPoints((PointMode)9, [Vector2.Zero], new Stroke(), Rgba8.Red));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SolidPaint(Rgba8.Red) { Opacity = 1.5f });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Stroke(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Stroke(2) { MiterLimit = 0.5f });
        canvas.FillPath(Path.Empty, Rgba8.Red);
        canvas.FillRect(new RectF(5, 5, 0, 10), Rgba8.Red);
        canvas.FillCircle(new Vector2(5, 5), 0, Rgba8.Red);
        if (canvas is TraceCanvas trace)
        {
            Assert.Empty(trace.Calls);
        }
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void DisposeRestoresAndThenDrawingThrows(string backend)
    {
        Egami.Drawing.Canvas canvas = Create(backend);
        canvas.Save();
        canvas.Save();
        canvas.Dispose();
        Assert.Throws<ObjectDisposedException>(() => canvas.FillRect(new RectF(0, 0, 1, 1), Rgba8.Red));
        Assert.Throws<ObjectDisposedException>(() => canvas.Save());
        canvas.Dispose();
        if (canvas is TraceCanvas trace)
        {
            Assert.Equal(["Save", "Save", "Restore", "Restore"], trace.Calls);
            Assert.True(trace.Disposed);
        }
    }

    /// <summary>Every public drawing, clipping and state call reaches exactly one hook of a backend that overrides them all.</summary>
    [Fact]
    public void EveryPublicCallReachesExactlyOneHook()
    {
        using Raster<Rgba8> image = Raster.Create<Rgba8>(8, 6);
        (string Call, Action<Egami.Drawing.Canvas> Act, string Hook)[] calls =
        [
            ("Save", c => c.Save(), "Save"),
            ("Translate", c => c.Translate(1, 2), "SetMatrix"),
            ("Scale", c => c.Scale(2, 3), "SetMatrix"),
            ("Rotate", c => c.Rotate(10), "SetMatrix"),
            ("Skew", c => c.Skew(5, 0), "SetMatrix"),
            ("Concat", c => c.Concat(Matrix3x2.Identity), "SetMatrix"),
            ("ResetMatrix", c => c.ResetMatrix(), "SetMatrix"),
            ("ClipRect", c => c.ClipRect(new RectF(0, 0, 10, 10)), "ClipRect"),
            ("ClipRoundRect", c => c.ClipRoundRect(new RoundRect(new RectF(0, 0, 10, 10), 2)), "ClipRoundRect"),
            ("ClipPath", c => c.ClipPath(Path.Circle(Vector2.One, 3), ClipOp.Difference, false), "ClipPath"),
            ("FillRect", c => c.FillRect(new RectF(0, 0, 3, 3), Rgba8.Red), "DrawRect"),
            ("StrokeRect", c => c.StrokeRect(new RectF(0, 0, 3, 3), new Stroke(2), Rgba8.Red), "DrawRect"),
            ("FillRoundRect", c => c.FillRoundRect(new RoundRect(new RectF(0, 0, 9, 9), 2), Rgba8.Red), "DrawRoundRect"),
            ("StrokeRoundRect", c => c.StrokeRoundRect(new RoundRect(new RectF(0, 0, 9, 9), 2), new Stroke(), Rgba8.Red), "DrawRoundRect"),
            ("FillOval", c => c.FillOval(new RectF(0, 0, 9, 5), Rgba8.Red), "DrawOval"),
            ("StrokeOval", c => c.StrokeOval(new RectF(0, 0, 9, 5), new Stroke(), Rgba8.Red), "DrawOval"),
            ("FillCircle", c => c.FillCircle(Vector2.One, 3, Rgba8.Red), "DrawOval"),
            ("StrokeCircle", c => c.StrokeCircle(Vector2.One, 3, new Stroke(), Rgba8.Red), "DrawOval"),
            ("FillArc", c => c.FillArc(new RectF(0, 0, 9, 9), 0, 90, Rgba8.Red), "DrawPath"),
            ("StrokeArc", c => c.StrokeArc(new RectF(0, 0, 9, 9), 0, 90, new Stroke(), Rgba8.Red), "DrawPath"),
            ("FillRing", c => c.FillRing(new RoundRect(new RectF(0, 0, 9, 9), 2), new RoundRect(new RectF(2, 2, 5, 5), 1), Rgba8.Red), "DrawPath"),
            ("FillPath", c => c.FillPath(Path.Star(new Vector2(5, 5), 4, 2, 5), Rgba8.Red), "DrawPath"),
            ("StrokePath", c => c.StrokePath(Path.Star(new Vector2(5, 5), 4, 2, 5), new Stroke(), Rgba8.Red), "DrawPath"),
            ("DrawLine", c => c.DrawLine(Vector2.Zero, Vector2.One, new Stroke(), Rgba8.Red), "DrawPoints"),
            ("DrawPoints", c => c.DrawPoints(PointMode.Polygon, [Vector2.Zero, Vector2.One, new Vector2(3, 1)], Stroke.Hairline, Rgba8.Red), "DrawPoints"),
            ("DrawPaint", c => c.DrawPaint(Rgba8.Red), "DrawPaint"),
            ("Clear", c => c.Clear(Rgba8.White), "DrawPaint"),
            ("DrawImage", c => c.DrawImage(image, new Vector2(2, 2)), "DrawImage"),
            ("DrawImage rect", c => c.DrawImage(image, new RectF(0, 0, 20, 20)), "DrawImage"),
            ("DrawImage source", c => c.DrawImage(image, new RectF(1, 1, 4, 4), new RectF(0, 0, 20, 20), new SamplingOptions { Filter = ResampleFilter.Bilinear }, new SolidPaint(Rgba8.Black) { Opacity = 0.5f }), "DrawImage"),
            ("DrawText", c => c.DrawText("Hi", new Vector2(2, 9), new Fonts.Font(TextTests.Noto.Value, 9), Rgba8.Red), "DrawGlyphRun"),
            ("DrawGlyphRun", c => c.DrawGlyphRun(new Fonts.Font(TextTests.Noto.Value, 9).Shape("Hi"), Vector2.Zero, Rgba8.Red), "DrawGlyphRun"),
            ("Restore", c => c.Restore(), "Restore"),
        ];

        using TraceCanvas canvas = new(100, 100);
        canvas.Save();
        foreach ((string call, Action<Egami.Drawing.Canvas> act, string hook) in calls)
        {
            canvas.Calls.Clear();
            act(canvas);
            Assert.True(canvas.Calls.Count == 1, $"{call} reached {canvas.Calls.Count} hooks: {string.Join("; ", canvas.Calls)}");
            Assert.StartsWith(hook, canvas.Calls[0]);
        }
    }

    /// <summary>Calls carry their meaning to the backend: shapes, strokes and paints arrive as data, not pixels.</summary>
    [Fact]
    public void HooksReceiveShapesAndSettingsAsData()
    {
        using TraceCanvas canvas = new(100, 100);
        canvas.StrokeRect(new RectF(1, 2, 3, 4), new Stroke(2) { Cap = LineCap.Round, Join = LineJoin.Bevel, MiterLimit = 10 }, new SolidPaint(new RgbaF32(0.5f, 0.25f, 1, 1)) { BlendMode = BlendMode.Multiply, Opacity = 0.5f });
        canvas.FillRing(new RoundRect(new RectF(0, 0, 10, 10), 0), new RoundRect(new RectF(2, 2, 6, 6), 0), Rgba8.Black);
        Assert.Equal("DrawRect [1 2 3 4] stroke 2 Round Bevel 10 solid 0.5 0.25 1 1 Multiply 0.5 True", canvas.Calls[0]);
        Assert.Equal("DrawPath 10 EvenOdd fill solid 0 0 0 1 SourceOver 1 True", canvas.Calls[1]);
    }

    /// <summary>A backend that keeps only DrawPath and the abstract hooks still gets every shape, through the defaults.</summary>
    [Fact]
    public void DefaultHooksRouteShapesToDrawPath()
    {
        using PathOnlyCanvas canvas = new();
        canvas.FillRect(new RectF(0, 0, 4, 4), Rgba8.Red);
        canvas.FillRoundRect(new RoundRect(new RectF(0, 0, 4, 4), 1), Rgba8.Red);
        canvas.FillOval(new RectF(0, 0, 4, 4), Rgba8.Red);
        canvas.DrawPoints(PointMode.Points, [Vector2.Zero, Vector2.One], new Stroke(2), Rgba8.Red);
        canvas.DrawPoints(PointMode.Lines, [Vector2.Zero, Vector2.One, Vector2.One, new Vector2(3, 3), new Vector2(9, 9)], new Stroke(2), Rgba8.Red);
        canvas.DrawPaint(Rgba8.Red);
        canvas.ClipRect(new RectF(0, 0, 4, 4));
        canvas.ClipRoundRect(new RoundRect(new RectF(0, 0, 4, 4), 1));
        Assert.Equal(8, canvas.Paths);
        Assert.Equal(2, canvas.Clips);
    }

    [Fact]
    public void RasterizeFallbackDrawsOneCallAtTheDeviceClip()
    {
        using TraceCanvas canvas = new(20, 10);
        canvas.ClipRect(new RectF(5, 0, 10, 10));
        canvas.Translate(5, 0);
        FallbackImage? fallback = canvas.Fallback(c => c.FillRect(new RectF(0, 0, 5, 10), Rgba8.Red), 2);
        Assert.NotNull(fallback);
        using Raster<RgbaF32> image = fallback.Image;
        Assert.Equal(new RectF(5, 0, 10, 10), fallback.DeviceBounds);
        Assert.Equal(20, image.Width);
        Assert.Equal(20, image.Height);
        Assert.Equal(1, image.GetPixel(9, 10).A);
        Assert.Equal(0, image.GetPixel(10, 10).A);
    }

    private static Egami.Drawing.Canvas Create(string backend) => backend == "raster" ? Raster.Create<Rgba8>(64, 48).CreateCanvas() : new TraceCanvas(64, 48);

    private sealed class PathOnlyCanvas() : Egami.Drawing.Canvas(10, 10)
    {
        public int Paths { get; private set; }

        public int Clips { get; private set; }

        protected override void OnClipPath(Path path, ClipOp op, bool antialias) => Clips++;

        protected override void OnDrawPath(Path path, Stroke? stroke, Paint paint) => Paths++;

        protected override void OnDrawImage(Raster image, RectF source, RectF destination, SamplingOptions sampling, Paint? paint)
        {
        }
    }
}
