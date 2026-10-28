using System.Buffers.Binary;
using System.Numerics;

using Egami.Drawing;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.GdiPlus.Tests;

/// <summary>The serialized EMF+ forms, string formats and image attributes.</summary>
public class FormatAndAttributeTests
{
    [Fact]
    public void PathDataBuildsFiguresFromPointTypes()
    {
        // A closed triangle, then an open Bezier: version, count, flags, eight float points, eight types, padding.
        Vector2[] points = [new(0, 0), new(10, 0), new(10, 10), new(20, 0), new(21, 1), new(22, 2), new(23, 3)];
        byte[] types = [0, 1, 1 | GdiPlusData.PointClose, 0, 3, 3, 3];
        List<byte> d = [.. Int(1), .. Int(points.Length), .. Int(0)];
        foreach (Vector2 p in points)
        {
            d.AddRange(Float(p.X));
            d.AddRange(Float(p.Y));
        }

        d.AddRange(types);
        d.Add(0);
        int at = 0;
        Path path = GdiPlusData.PathData(d.ToArray(), ref at);
        Assert.Equal(d.Count, at);
        Assert.Equal([PathVerb.Move, PathVerb.Line, PathVerb.Line, PathVerb.Close, PathVerb.Move, PathVerb.Cubic], path.Verbs.ToArray());
        Assert.Equal(FillRule.EvenOdd, path.FillRule);
    }

    [Fact]
    public void RelativeAndCompressedPointsDecode()
    {
        // Two relative points: (+3, -2) in 7 bits, then (+300, +1) with x in 15 bits.
        byte[] relative = [0x03, 0x7E, 0x81, 0x2C, 0x01, 0, 0, 0];
        int at = 0;
        Vector2[] points = GdiPlusData.ReadPoints(relative, ref at, 2, compressed: false, relative: true);
        Assert.Equal([new Vector2(3, -2), new Vector2(303, -1)], points);
        Assert.Equal(8, at);

        byte[] compressed = [.. Short(-5), .. Short(7)];
        at = 0;
        Assert.Equal([new Vector2(-5, 7)], GdiPlusData.ReadPoints(compressed, ref at, 1, compressed: true, relative: false));
        Assert.Throws<ArgumentException>(() =>
        {
            int x = 0;
            GdiPlusData.ReadPoints(compressed, ref x, 2, compressed: true, relative: false);
        });
    }

    [Fact]
    public void RegionTreesCombineTheirNodes()
    {
        // Exclude(rect 0,0,10,10; rect 5,0,10,10), then an infinite and an empty region.
        List<byte> d = [.. Int(0), .. Int(3), .. Int(GdiPlusRegions.Exclude), .. Int(0x10000000), .. Rect(0, 0, 10, 10), .. Int(0x10000000), .. Rect(5, 0, 10, 10)];
        int at = 0;
        Assert.Equal(new RectF(0, 0, 5, 10), GdiPlusData.Region(d.ToArray(), ref at)!.Bounds);
        at = 0;
        Assert.Null(GdiPlusData.Region([.. Int(0), .. Int(1), .. Int(0x10000003)], ref at));
        at = 0;
        Assert.True(GdiPlusData.Region([.. Int(0), .. Int(1), .. Int(0x10000002)], ref at)!.IsEmpty);
        at = 0;
        Assert.Throws<ArgumentException>(() =>
        {
            int x = 0;
            GdiPlusData.Region([.. Int(0), .. Int(1), .. Int(9)], ref x);
        });
    }

    [Theory]
    [InlineData(GdiPlusStringFormat.HotkeyNone, "&File && Edit", "&File && Edit")]
    [InlineData(GdiPlusStringFormat.HotkeyShow, "&File && Edit", "File & Edit")]
    [InlineData(GdiPlusStringFormat.HotkeyHide, "&File && Edit", "File & Edit")]
    public void TheHotkeyPrefixDropsAmpersands(int prefix, string text, string shown)
    {
        Assert.Equal(shown, new GdiPlusStringFormat { HotkeyPrefix = prefix }.DisplayText(text));
    }

    [Fact]
    public void TabStopsAddUpFromTheFirstOffset()
    {
        GdiPlusStringFormat format = new() { FirstTabOffset = 10, TabStops = [20, 30, 5] };
        Assert.Equal([30f, 60f, 65f], format.TabPositions());
        Assert.True(new GdiPlusStringFormat { Trimming = GdiPlusStringFormat.TrimEllipsisPath }.Ellipsis);
        Assert.False(new GdiPlusStringFormat { Trimming = GdiPlusStringFormat.TrimWord }.Ellipsis);
    }

    [Fact]
    public void ColorMatricesAreRowVectorsTimesTheMatrix()
    {
        // Swap red and blue and add 0.2 to green.
        float[] m = [0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0.2f, 0, 0, 1];
        Rgba8 c = new(200, 100, 50, 255);
        Assert.Equal(new Rgba8(50, 151, 200, 255), GdiPlusImageAttributes.Transform(c, m));

        Raster<Rgba8> one = Raster.Create<Rgba8>(1, 1);
        one.Row(0)[0] = c;
        using Raster<Rgba8> egami = one.Derive(o => o.ColorMatrix(GdiPlusImageAttributes.ToColorMatrix(m)), TestContext.Current.CancellationToken);
        Assert.Equal(new Rgba8(50, 151, 200, 255), egami.Row(0)[0]);
        one.Dispose();
    }

    [Fact]
    public void GraysCanSkipTheMatrixOrUseTheGrayMatrix()
    {
        float[] invert = [-1, 0, 0, 0, 0, 0, -1, 0, 0, 0, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, 1, 1, 1, 0, 1];
        float[] black = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1];
        Rgba8[] pixels = [new(10, 20, 30, 255), new(90, 90, 90, 255)];
        GdiPlusColorAdjust skip = new() { ColorMatrix = invert, MatrixFlags = GdiPlusColorAdjust.MatrixSkipGrays };
        Rgba8[] skipped = [.. pixels];
        GdiPlusImageAttributes.Apply(skipped, skip);
        Assert.Equal([new Rgba8(245, 235, 225, 255), new Rgba8(90, 90, 90, 255)], skipped);

        GdiPlusColorAdjust alt = new() { ColorMatrix = invert, GrayMatrix = black, MatrixFlags = GdiPlusColorAdjust.MatrixAltGray };
        Rgba8[] alternate = [.. pixels];
        GdiPlusImageAttributes.Apply(alternate, alt);
        Assert.Equal([new Rgba8(245, 235, 225, 255), new Rgba8(0, 0, 0, 255)], alternate);
    }

    [Fact]
    public void GammaThresholdColorKeyRemapAndOutputChannelAdjustColors()
    {
        Rgba8[] gamma = [new(128, 0, 255, 200)];
        GdiPlusImageAttributes.Apply(gamma, new GdiPlusColorAdjust { Gamma = 2 });
        Assert.Equal(new Rgba8(64, 0, 255, 200), gamma[0]);

        Rgba8[] threshold = [new(230, 140, 100, 255)];
        GdiPlusImageAttributes.Apply(threshold, new GdiPlusColorAdjust { Threshold = 0.5f });
        Assert.Equal(new Rgba8(255, 255, 0, 255), threshold[0]);

        Rgba8[] keyed = [new(100, 100, 100, 255), new(100, 200, 100, 255)];
        GdiPlusImageAttributes.Apply(keyed, new GdiPlusColorAdjust { ColorKeyLow = new Rgba8(90, 90, 90, 0), ColorKeyHigh = new Rgba8(110, 110, 110, 255) });
        Assert.Equal([default, new Rgba8(100, 200, 100, 255)], keyed);

        Rgba8[] remapped = [new(1, 2, 3, 255), new(1, 2, 3, 254)];
        GdiPlusImageAttributes.Apply(remapped, new GdiPlusColorAdjust { RemapTable = [(new Rgba8(1, 2, 3, 255), new Rgba8(9, 9, 9, 9))] });
        Assert.Equal([new Rgba8(9, 9, 9, 9), new Rgba8(1, 2, 3, 254)], remapped);

        // Cyan ink of a teal: cyan 255 - 0, black shared min(255, 127, 127) = 127, so 128 cyan shows as gray 127.
        Rgba8[] cyan = [new(0, 128, 128, 255)];
        GdiPlusImageAttributes.Apply(cyan, new GdiPlusColorAdjust { OutputChannel = 0 });
        Assert.Equal(new Rgba8(127, 127, 127, 255), cyan[0]);
    }

    [Fact]
    public void ATypeWithoutSettingsUsesTheDefaultTypeAndNoOpTurnsItOff()
    {
        GdiPlusImageAttributes attributes = new();
        attributes[GdiPlusImageAttributes.TypeDefault].Threshold = 0.5f;
        Assert.Same(attributes[GdiPlusImageAttributes.TypeDefault], attributes.For(GdiPlusImageAttributes.TypeBrush));
        attributes[GdiPlusImageAttributes.TypeBitmap].NoOp = true;
        Assert.Same(attributes[GdiPlusImageAttributes.TypeBitmap], attributes.For(GdiPlusImageAttributes.TypeBitmap));

        using Raster<Rgba8> image = Raster.Create<Rgba8>(2, 1);
        image.Row(0).Fill(new Rgba8(200, 50, 50, 255));
        Assert.Same(image, attributes.Apply(image, GdiPlusImageAttributes.TypeBitmap));
        using Raster<Rgba8> brush = attributes.Apply(image, GdiPlusImageAttributes.TypeBrush);
        Assert.NotSame(image, brush);
        Assert.Equal(new Rgba8(255, 0, 0, 255), brush.Row(0)[1]);
        Assert.Equal(new Rgba8(200, 50, 50, 255), image.Row(0)[1]);
        Assert.Equal(GdiPlusBrushes.WrapClamp, attributes.WrapMode);
    }

    private static byte[] Int(int v)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, v);
        return b;
    }

    private static byte[] Float(float v)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(b, v);
        return b;
    }

    private static byte[] Short(short v)
    {
        byte[] b = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(b, v);
        return b;
    }

    private static byte[] Rect(float x, float y, float w, float h) => [.. Float(x), .. Float(y), .. Float(w), .. Float(h)];
}
