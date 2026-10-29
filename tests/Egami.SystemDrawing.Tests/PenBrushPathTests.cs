using System.Runtime.InteropServices;

using Egami.SystemDrawing.Drawing2D;

namespace Egami.SystemDrawing.Tests;

using Point = System.Drawing.Point;

/// <summary>
/// Pens, brushes and paths, checked against what GDI+ gives on Windows (egami-compare, sysdrawing PathProbes and BrushPenProbes):
/// the expected values here are copied from those measurements.
/// </summary>
public class PenBrushPathTests
{
    private static string Dump(GraphicsPath path) => string.Join(" ", path.PathPoints.Zip(path.PathTypes, (p, t) => $"{p.X:0.###},{p.Y:0.###}:{t}"));

    [Fact]
    public void BuildersJoinOpenFiguresAndCloseShapes()
    {
        using GraphicsPath joined = new();
        joined.AddLine(1, 2, 10, 20);
        joined.AddLine(10, 20, 50, 60);
        Assert.Equal("1,2:0 10,20:1 50,60:1", Dump(joined));

        using GraphicsPath mixed = new();
        mixed.AddLine(0, 0, 5, 5);
        mixed.AddRectangle(new Rectangle(10, 10, 5, 5));
        mixed.AddLine(20, 20, 25, 25);
        Assert.Equal("0,0:0 5,5:1 10,10:0 15,10:1 15,15:1 10,15:129 20,20:0 25,25:1", Dump(mixed));

        using GraphicsPath polygon = new();
        polygon.AddPolygon([new Point(5, 40), new Point(32, 3), new Point(60, 40), new Point(5, 40)]);
        Assert.Equal("5,40:0 32,3:1 60,40:129", Dump(polygon));
        Assert.Throws<ArgumentException>(() => polygon.AddPolygon([new Point(5, 40), new Point(32, 3)]));

        using GraphicsPath empty = new();
        empty.AddRectangle(new RectangleF(1, 2, 0, 20));
        empty.AddArc(0, 0, 40, 20, 10, 0);
        Assert.Equal(0, empty.PointCount);
    }

    [Fact]
    public void ArcsSplitEveryNinetyDegreesOfTheEllipsesOwnAngle()
    {
        using GraphicsPath arc = new();
        arc.AddArc(0, 0, 40, 20, 30, 200);
        Assert.Equal(10, arc.PointCount);
        PointF[] p = arc.PathPoints;
        Assert.Equal(33.093075, p[0].X, 4);
        Assert.Equal(4.8814306, p[3].X, 4);
        Assert.Equal(6.906912, p[6].X, 4);

        using GraphicsPath pie = new();
        pie.AddPie(0, 0, 40, 20, 10, 100);
        Assert.Equal(5, pie.PointCount);

        using GraphicsPath ellipse = new();
        ellipse.AddEllipse(0, 0, 40, 20);
        Assert.Equal(13, ellipse.PointCount);
        Assert.Equal(15.522847, ellipse.PathPoints[1].Y, 4);
    }

    [Fact]
    public void ReverseKeepsSegmentKindsClosesAndMarkers()
    {
        using GraphicsPath path = new();
        path.AddLine(0, 0, 5, 5);
        path.AddBezier(5, 5, 6, 7, 8, 9, 10, 10);
        path.CloseFigure();
        path.AddLine(20, 20, 30, 30);
        path.SetMarkers();
        path.AddLine(40, 40, 50, 30);
        path.Reverse();
        Assert.Equal("50,30:0 40,40:33 30,30:1 20,20:1 10,10:0 8,9:3 6,7:3 5,5:3 0,0:129", Dump(path));
    }

    [Fact]
    public void IteratorWalksFiguresAndMarkersAsGdiPlus()
    {
        using GraphicsPath path = new();
        path.AddLine(0, 0, 5, 5);
        path.AddBezier(5, 5, 6, 7, 8, 9, 10, 10);
        path.SetMarkers();
        path.AddRectangle(new Rectangle(1, 1, 2, 2));
        path.AddLine(20, 20, 30, 30);
        path.SetMarkers();
        path.AddLine(40, 40, 50, 30);
        using GraphicsPathIterator it = new(path);
        Assert.Equal((13, 3, true), (it.Count, it.SubpathCount, it.HasCurve()));
        Assert.Equal(5, it.NextSubpath(out int s, out int e, out bool closed));
        Assert.Equal((0, 4, false), (s, e, closed));
        Assert.Equal(4, it.NextSubpath(out s, out e, out closed));
        Assert.Equal((5, 8, true), (s, e, closed));
        it.NextSubpath(out _, out _, out _);
        Assert.Equal(0, it.NextSubpath(out s, out e, out closed));
        Assert.Equal((0, 0, true), (s, e, closed));
        it.Rewind();
        Assert.Equal(0, it.NextPathType(out _, out _, out _));
        Assert.Equal(5, it.NextMarker(out s, out e));
        Assert.Equal(6, it.NextMarker(out s, out e));
        Assert.Equal((5, 10), (s, e));
    }

    [Fact]
    public void BoundsAndHitTestsFollowGdiPlus()
    {
        using GraphicsPath path = new();
        path.AddEllipse(10, 10, 40, 20);
        path.AddLine(0, 0, 5, 5);
        using Matrix rotate = new();
        rotate.Rotate(30);
        using Pen pen = new(Color.Black, 6);
        Assert.Equal(new RectangleF(0, 0, 50, 30), path.GetBounds());
        RectangleF b = path.GetBounds(rotate);
        Assert.Equal(-15, b.X, 3);
        Assert.Equal(58.30126, b.Width, 3);
        Assert.Equal(new RectangleF(-30, -30, 110, 90), path.GetBounds(null, pen));
        pen.LineJoin = LineJoin.Round;
        Assert.Equal(new RectangleF(-6, -6, 62, 42), path.GetBounds(null, pen));
        Assert.True(path.IsVisible(10.5f, 20));
        Assert.False(path.IsVisible(49.9f, 20));
        Assert.False(path.IsVisible(30, 10));
        using Pen thin = new(Color.Black, 1);
        Assert.True(path.IsOutlineVisible(10.4f, 20, thin));
        Assert.False(path.IsOutlineVisible(10.6f, 20, thin));
    }

    [Fact]
    public void WarpMapsTheRectangleOntoTheCorners()
    {
        using GraphicsPath path = new();
        path.AddRectangle(new RectangleF(0, 0, 10, 10));
        path.AddLine(2, 3, 8, 7);
        path.Warp([new PointF(0, 0), new PointF(20, 2), new PointF(-2, 15), new PointF(25, 18)], new RectangleF(0, 0, 10, 10));
        PointF[] p = path.PathPoints;
        Assert.Equal(new PointF(25, 18).X, p[2].X, 3);
        Assert.Equal(3.799843, p[4].X, 3);
        Assert.Equal(11.599762, p[5].Y, 3);
    }

    [Fact]
    public void PenSettingsBehaveAsInGdiPlus()
    {
        using Pen pen = new(Color.Red, 3);
        Assert.Throws<InvalidOperationException>(() => pen.DashPattern);
        Assert.Empty(pen.CompoundArray);
        pen.DashStyle = DashStyle.DashDotDot;
        Assert.Equal([3f, 1, 1, 1, 1, 1], pen.DashPattern);
        pen.DashStyle = DashStyle.Custom;
        Assert.Equal([3f, 1, 1, 1, 1, 1], pen.DashPattern);
        Assert.Throws<ArgumentException>(() => pen.DashPattern = [1, 0]);
        Assert.Throws<ArgumentException>(() => pen.CompoundArray = [0, 0.3f, 0.7f]);
        Assert.Throws<ArgumentException>(() => pen.CompoundArray = []);
        pen.MiterLimit = 0.5f;
        Assert.Equal(1, pen.MiterLimit);
        Assert.Throws<ArgumentException>(() => pen.CustomStartCap);
        pen.CustomEndCap = new AdjustableArrowCap(3, 4);
        Assert.Equal(LineCap.Custom, pen.EndCap);
        Assert.Throws<ArgumentException>(() => Pens.Red.Width = 3);
        Assert.Throws<NotImplementedException>(() => new CustomLineCap(new GraphicsPath(), null));
        using Pen hatch = new(new HatchBrush(HatchStyle.Cross, Color.Red));
        Assert.Equal(PenType.HatchFill, hatch.PenType);
        Assert.Throws<ArgumentException>(() => hatch.Color);
    }

    [Fact]
    public void LinearGradientsGetGdiPlusTransforms()
    {
        using LinearGradientBrush points = new(new PointF(10, 5), new PointF(50, 25), Color.Red, Color.Blue);
        Assert.Equal(new RectangleF(10, 5, 40, 20), points.Rectangle);
        float[] e = points.Transform.Elements;
        Assert.Equal([1f, 0.5f, -0.8f, 1.6f, 12, -24], [.. e.Select(v => MathF.Round(v, 4))]);
        using LinearGradientBrush horizontal = new(new PointF(0, 0), new PointF(10, 0), Color.Red, Color.Blue);
        Assert.Equal(new RectangleF(0, -5, 10, 10), horizontal.Rectangle);
        using LinearGradientBrush diagonal = new(new RectangleF(5, 6, 40, 20), Color.Red, Color.Blue, LinearGradientMode.ForwardDiagonal);
        Assert.Equal([1f, 0.5f, -2, 1, 32, -12.5f], [.. diagonal.Transform.Elements.Select(v => MathF.Round(v, 4))]);
        using LinearGradientBrush scaled = new(new RectangleF(5, 6, 40, 20), Color.Red, Color.Blue, 30f, true);
        Assert.Equal(1.183012, scaled.Transform.Elements[0], 4);
        Assert.Equal(-1.366026, scaled.Transform.Elements[2], 4);
        Assert.Throws<ArgumentException>(() => points.WrapMode = WrapMode.Clamp);
        Assert.Throws<ExternalException>(() => new LinearGradientBrush(new PointF(1, 1), new PointF(1, 1), Color.Red, Color.Blue));
        Assert.Equal(Color.FromArgb(255, 255, 0, 0), points.LinearColors[0]);
    }

    [Fact]
    public void BlendShapesAreGdiPlusSamples()
    {
        using LinearGradientBrush b = new(new PointF(0, 0), new PointF(10, 0), Color.Red, Color.Blue);
        b.SetSigmaBellShape(0.5f);
        Assert.Equal(511, b.Blend!.Factors.Length);
        Assert.Equal(0.1279, b.Blend.Factors[60], 3);
        Assert.Equal(0.5f / 255, b.Blend.Positions[1], 6);
        b.SetBlendTriangularShape(0.3f);
        Assert.Equal([0f, 1, 0], b.Blend.Factors);
        b.SetBlendTriangularShape(0, 0.5f);
        Assert.Equal([0.5f, 0], b.Blend.Factors);
        b.InterpolationColors = new ColorBlend { Colors = [Color.Red, Color.Blue], Positions = [0, 1] };
        Assert.Null(b.Blend);
        Assert.Throws<ArgumentException>(() => b.InterpolationColors = new ColorBlend { Colors = [Color.Red, Color.Blue], Positions = [0.1f, 1] });
    }

    [Fact]
    public void PathGradientDefaultsAreGdiPlus()
    {
        using PathGradientBrush polygon = new([new PointF(0, 0), new PointF(40, 0), new PointF(30, 30)]);
        Assert.Equal(23.333332, polygon.CenterPoint.X, 4);
        Assert.Equal(Color.FromArgb(255, 0, 0, 0), polygon.CenterColor);
        Assert.Equal([Color.FromArgb(255, 255, 255, 255)], polygon.SurroundColors);
        Assert.Equal(WrapMode.Clamp, polygon.WrapMode);
        Assert.Throws<ArgumentException>(() => polygon.SurroundColors = [Color.Red, Color.Green, Color.Blue, Color.White]);
        using GraphicsPath ellipse = new();
        ellipse.AddEllipse(0, 0, 40, 20);
        using PathGradientBrush fromPath = new(ellipse);
        Assert.Equal(21.538458, fromPath.CenterPoint.X, 4);
        Assert.Equal(Color.FromArgb(255, 255, 255, 255), fromPath.CenterColor);
    }

    [Fact]
    public void DiagonalHatchesAreSmoothedAsGdiPlusDrawsThem()
    {
        using Bitmap b = new(8, 8);
        using (Graphics g = Graphics.FromImage(b))
        {
            using HatchBrush brush = new(HatchStyle.ForwardDiagonal, Color.Black, Color.White);
            g.FillRectangle(brush, 0, 0, 8, 8);
        }

        Assert.Equal(0x15, b.GetPixel(0, 0).R);
        Assert.Equal(0xBF, b.GetPixel(1, 0).R);
        Assert.Equal(0xBF, b.GetPixel(7, 0).R);
        Assert.Equal(0xFF, b.GetPixel(3, 0).R);
    }

    [Fact]
    public void TextureBrushWithAttributesIsDrawnOnce()
    {
        using Bitmap tile = new(4, 3);
        using ImageAttributes attributes = new();
        using TextureBrush brush = new(tile, new Rectangle(0, 0, 4, 3), attributes);
        Assert.Equal(WrapMode.Clamp, brush.WrapMode);
        Assert.Throws<ExternalException>(() => new TextureBrush(tile, new Rectangle(2, 2, 5, 5)));
    }
}
