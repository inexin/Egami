using System.Buffers.Binary;

using Egami.Formats.Psd;
using Egami.Pixels;

using static Egami.Tests.Formats.Psd.PsdDescriptorWriter;

namespace Egami.Tests.Formats.Psd;

/// <summary>
/// Layer effects on files written here byte by byte: a 32-pixel white canvas and an opaque red square from (8, 8) to (24, 24),
/// with one effect each, checked where each effect must and must not reach.
/// </summary>
public class PsdEffectsTests
{
    private const int Size = 32, Left = 8, Right = 24;

    private static readonly Rgba8 White = new(255, 255, 255, 255), Red = new(255, 0, 0, 255);

    /// <summary>A color overlay paints the square and nothing around it.</summary>
    [Fact]
    public void ColorOverlayPaintsTheShape()
    {
        using Raster<Rgba8> pixels = Flatten(Effects(("SoFi", Effect("SoFi", ("Clr ", Rgb(0, 0, 255))))));
        Assert.Equal(new Rgba8(0, 0, 255, 255), pixels.Row(16)[16]);
        Assert.Equal(new Rgba8(0, 0, 255, 255), pixels.Row(Left)[Left]);
        Assert.Equal(White, pixels.Row(Left - 1)[16]);
    }

    /// <summary>A stroke lies outside, inside or across the edge, its size wide.</summary>
    [Theory]
    [InlineData("OutF", 3, 0)]
    [InlineData("InsF", 0, 3)]
    [InlineData("CtrF", 2, 2)]
    public void StrokeLiesWhereItsPositionSays(string position, int outside, int inside)
    {
        int size = outside + inside;
        using Raster<Rgba8> pixels = Flatten(Effects(("FrFX", Effect(
            "FrFX",
            ("Styl", new EnumValue("FStl", position)),
            ("PntT", new EnumValue("FrFl", "SClr")),
            ("Sz  ", new Unit("#Pxl", size)),
            ("Clr ", Rgb(0, 0, 255))))));
        Rgba8 blue = new(0, 0, 255, 255);
        for (int x = Left - outside; x < Left + inside; x++)
        {
            Assert.Equal(blue, pixels.Row(16)[x]);
        }

        Assert.Equal(White, pixels.Row(16)[Left - outside - 1]);
        Assert.Equal(Red, pixels.Row(16)[Left + inside]);
        Assert.Equal(Red, pixels.Row(16)[16]);
    }

    /// <summary>A drop shadow falls away from the light, under the content, which hides it.</summary>
    [Fact]
    public void DropShadowFallsAwayFromTheLight()
    {
        // Light from the top (90°): the shadow falls 4 pixels down.
        using Raster<Rgba8> pixels = Flatten(Effects(("DrSh", Effect(
            "DrSh",
            ("Md  ", new EnumValue("BlnM", "Nrml")),
            ("Clr ", Rgb(0, 0, 0)),
            ("Opct", new Unit("#Prc", 100)),
            ("uglg", false),
            ("lagl", new Unit("#Ang", 90)),
            ("Dstn", new Unit("#Pxl", 4)),
            ("Ckmt", new Unit("#Prc", 100)),
            ("blur", new Unit("#Pxl", 0))))));
        Assert.Equal(new Rgba8(0, 0, 0, 255), pixels.Row(Right + 2)[16]);
        Assert.Equal(White, pixels.Row(Right + 5)[16]);
        Assert.Equal(White, pixels.Row(Left - 2)[16]);
        Assert.Equal(Red, pixels.Row(Right - 1)[16]);
    }

    /// <summary>An inner shadow darkens the inside along the edge facing the light, and leaves the outside.</summary>
    [Fact]
    public void InnerShadowDarkensTheLitEdgeInside()
    {
        using Raster<Rgba8> pixels = Flatten(Effects(("IrSh", Effect(
            "IrSh",
            ("Md  ", new EnumValue("BlnM", "Nrml")),
            ("Clr ", Rgb(0, 0, 0)),
            ("Opct", new Unit("#Prc", 100)),
            ("uglg", false),
            ("lagl", new Unit("#Ang", 90)),
            ("Dstn", new Unit("#Pxl", 3)),
            ("Ckmt", new Unit("#Prc", 100)),
            ("blur", new Unit("#Pxl", 0))))));
        Assert.Equal(new Rgba8(0, 0, 0, 255), pixels.Row(Left + 1)[16]);
        Assert.Equal(Red, pixels.Row(Right - 2)[16]);
        Assert.Equal(Red, pixels.Row(16)[16]);
        Assert.Equal(White, pixels.Row(Left - 1)[16]);
    }

    /// <summary>An outer glow tints around the shape and fades out within its size.</summary>
    [Fact]
    public void OuterGlowFadesAroundTheShape()
    {
        using Raster<Rgba8> pixels = Flatten(Effects(("OrGl", Glow("OrGl", "SrcE"))));
        Assert.True(pixels.Row(16)[Left - 1].B < 200, $"{pixels.Row(16)[Left - 1]}");
        Assert.Equal(White, pixels.Row(16)[0]);
        Assert.Equal(Red, pixels.Row(16)[16]);
    }

    /// <summary>An inner glow from the edge tints inside along the edge, not the middle; from the center, the other way.</summary>
    [Theory]
    [InlineData("SrcE")]
    [InlineData("SrcC")]
    public void InnerGlowTintsFromItsSource(string source)
    {
        using Raster<Rgba8> pixels = Flatten(Effects(("IrGl", Glow("IrGl", source))));
        Rgba8 edge = pixels.Row(16)[Left], middle = pixels.Row(16)[16];
        Assert.Equal(source == "SrcE", edge.G > middle.G);
        Assert.Equal(White, pixels.Row(16)[Left - 1]);
    }

    /// <summary>Satin shades inside the shape only.</summary>
    [Fact]
    public void SatinStaysInside()
    {
        using Raster<Rgba8> plain = Flatten(Effects());
        using Raster<Rgba8> pixels = Flatten(Effects(("ChFX", Effect(
            "ChFX",
            ("Md  ", new EnumValue("BlnM", "Nrml")),
            ("Clr ", Rgb(0, 0, 0)),
            ("Opct", new Unit("#Prc", 100)),
            ("lagl", new Unit("#Ang", 0)),
            ("Dstn", new Unit("#Pxl", 4)),
            ("blur", new Unit("#Pxl", 2)),
            ("Invr", false)))));
        Assert.NotEqual(plain.Row(16)[Left + 2], pixels.Row(16)[Left + 2]);
        for (int x = 0; x < Left; x++)
        {
            Assert.Equal(White, pixels.Row(16)[x]);
        }
    }

    /// <summary>An inner bevel lit from the top left lightens that edge and darkens the opposite one; the middle stays.</summary>
    [Fact]
    public void BevelLightsTheEdgeFacingTheLight()
    {
        using Raster<Rgba8> pixels = Flatten(Effects(("ebbl", Effect(
            "ebbl",
            ("hglM", new EnumValue("BlnM", "Scrn")),
            ("hglC", Rgb(255, 255, 255)),
            ("hglO", new Unit("#Prc", 100)),
            ("sdwM", new EnumValue("BlnM", "Mltp")),
            ("sdwC", Rgb(0, 0, 0)),
            ("sdwO", new Unit("#Prc", 100)),
            ("bvlT", new EnumValue("bvlT", "SfBL")),
            ("bvlS", new EnumValue("BESl", "InrB")),
            ("uglg", false),
            ("lagl", new Unit("#Ang", 135)),
            ("Lald", new Unit("#Ang", 30)),
            ("srgR", new Unit("#Prc", 100)),
            ("blur", new Unit("#Pxl", 4))))));
        Assert.True(pixels.Row(16)[Left].G > 40, $"{pixels.Row(16)[Left]}");
        Assert.True(pixels.Row(16)[Right - 1].R < 200, $"{pixels.Row(16)[Right - 1]}");
        Assert.InRange(pixels.Row(16)[16].R, 240, 255);
        Assert.Equal(0, pixels.Row(16)[16].G);
        Assert.Equal(White, pixels.Row(16)[Left - 1]);
    }

    /// <summary>A gradient overlay runs across the shape's box, not the canvas.</summary>
    [Fact]
    public void GradientOverlayRunsAcrossTheShape()
    {
        Desc gradient = new(
            "Grdn",
            ("Nm  ", "Black, White"),
            ("GrdF", new EnumValue("GrdF", "CstS")),
            ("Intr", 4096.0),
            ("Clrs", new object[] { Stop(0, 0), Stop(4096, 255) }),
            ("Trns", new object[] { Opacity(0), Opacity(4096) }));
        using Raster<Rgba8> pixels = Flatten(Effects(("GrFl", Effect(
            "GrFl",
            ("Grad", gradient),
            ("Angl", new Unit("#Ang", 0)),
            ("Type", new EnumValue("GrdT", "Lnr ")),
            ("Algn", true),
            ("Scl ", new Unit("#Prc", 100))))));
        Assert.True(pixels.Row(16)[Left].R < 32, $"{pixels.Row(16)[Left]}");
        Assert.True(pixels.Row(16)[Right - 1].R > 224, $"{pixels.Row(16)[Right - 1]}");
        Assert.Equal(White, pixels.Row(16)[Left - 1]);
    }

    /// <summary>A pattern overlay tiles the document's pattern from the canvas's origin over the shape.</summary>
    [Fact]
    public void PatternOverlayTilesThePattern()
    {
        byte[] pattern = PsdHandBuiltTests.Pattern("pat-1", 2, 2, [(0, 0, 255), (0, 255, 0), (0, 255, 0), (0, 0, 255)]);
        using Raster<Rgba8> pixels = Flatten(
            Effects(("patternFill", Effect("patternFill", ("Ptrn", new Desc("Ptrn", ("Nm  ", "Checks"), ("Idnt", "pat-1")))))),
            pattern);
        Assert.Equal(new Rgba8(0, 0, 255, 255), pixels.Row(16)[16]);
        Assert.Equal(new Rgba8(0, 255, 0, 255), pixels.Row(16)[17]);
        Assert.Equal(White, pixels.Row(16)[Left - 1]);
    }

    /// <summary>Several strokes of the newer lmfx block draw the first on top.</summary>
    [Fact]
    public void SeveralStrokesDrawTheFirstOnTop()
    {
        static object Stroke(int size, byte blue) => Effect(
            "FrFX",
            ("Styl", new EnumValue("FStl", "OutF")),
            ("PntT", new EnumValue("FrFl", "SClr")),
            ("Sz  ", new Unit("#Pxl", size)),
            ("Clr ", Rgb(0, 0, blue)));
        using Raster<Rgba8> pixels = Flatten(Effects("lmfx", ("frameFXMulti", new object[] { Stroke(2, 255), Stroke(4, 100) })));
        Assert.Equal(new Rgba8(0, 0, 255, 255), pixels.Row(16)[Left - 1]);
        Assert.Equal(new Rgba8(0, 0, 100, 255), pixels.Row(16)[Left - 3]);
    }

    /// <summary>With fill opacity 0 the content is gone and its stroke stays.</summary>
    [Fact]
    public void FillOpacityLeavesTheEffects()
    {
        using Raster<Rgba8> pixels = Flatten(
            Effects(("FrFX", Effect("FrFX", ("Styl", new EnumValue("FStl", "InsF")), ("Sz  ", new Unit("#Pxl", 2)), ("Clr ", Rgb(0, 0, 255))))),
            fill: 0);
        Assert.Equal(new Rgba8(0, 0, 255, 255), pixels.Row(16)[Left]);
        Assert.Equal(White, pixels.Row(16)[16]);
    }

    /// <summary>The master switch off, an effect turned off, or ApplyEffects off: the layer is drawn as it is.</summary>
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void EffectsOffDrawTheLayerAsItIs(bool master, bool enabled, bool apply)
    {
        byte[] block = Versioned(
            new Desc("null", ("masterFXSwitch", master), ("SoFi", new Desc("SoFi", ("enab", enabled), ("Clr ", Rgb(0, 0, 255))))),
            4);
        using Raster<Rgba8> pixels = Flatten([("lfx2", block)], options: new PsdFlattenOptions { ApplyEffects = apply });
        Assert.Equal(Red, pixels.Row(16)[16]);
    }

    /// <summary>The old lrFX block's drop shadow is read when there is no descriptor: its sizes are 16.16 fixed point.</summary>
    [Fact]
    public void OldDropShadowIsRead()
    {
        byte[] shadow = new byte[51];
        BinaryPrimitives.WriteInt32BigEndian(shadow, 2);
        BinaryPrimitives.WriteInt32BigEndian(shadow.AsSpan(4), 0);
        BinaryPrimitives.WriteInt32BigEndian(shadow.AsSpan(8), 0);
        BinaryPrimitives.WriteInt32BigEndian(shadow.AsSpan(12), 90 << 16);
        BinaryPrimitives.WriteInt32BigEndian(shadow.AsSpan(16), 4 << 16);
        "8BIMnorm"u8.CopyTo(shadow.AsSpan(30));
        shadow[38] = 1;
        shadow[40] = 255;
        using MemoryStream block = new();
        block.Write([0, 0, 0, 1]);
        block.Write("8BIMdsdw"u8);
        block.Write([0, 0, 0, (byte)shadow.Length]);
        block.Write(shadow);
        using Raster<Rgba8> pixels = Flatten([("lrFX", block.ToArray())]);
        Assert.Equal(new Rgba8(0, 0, 0, 255), pixels.Row(Right + 2)[16]);
        Assert.Equal(White, pixels.Row(Left - 2)[16]);
    }

    /// <summary>Every effect kind flattens with each of its settings at an extreme without throwing; these are the fuzz seeds too.</summary>
    [Fact]
    public void EveryKindFlattens()
    {
        foreach ((string name, byte[] file) in Seeds())
        {
            using PsdDocument document = PsdImage.ReadLayers(new MemoryStream(file));
            using Raster flat = document.Flatten();
            Assert.True(flat.Width == Size, name);
        }
    }

    /// <summary>
    /// One small file per effect kind (and the old lrFX block), for <see cref="EveryKindFlattens"/> and as the seeds of the
    /// psdlayers fuzz target (tests/images/fuzz/psdeffects-seeds, written from these).
    /// </summary>
    internal static IEnumerable<(string Name, byte[] File)> Seeds()
    {
        static Desc Shadow(string kind) => Effect(
            kind,
            ("Md  ", new EnumValue("BlnM", "Mltp")),
            ("Clr ", Rgb(0, 0, 0)),
            ("Opct", new Unit("#Prc", 75)),
            ("uglg", true),
            ("lagl", new Unit("#Ang", 120)),
            ("Dstn", new Unit("#Pxl", 3)),
            ("Ckmt", new Unit("#Prc", 20)),
            ("blur", new Unit("#Pxl", 3)),
            ("Nose", new Unit("#Prc", 10)));
        Desc gradient = new(
            "Grdn",
            ("Nm  ", "Black, White"),
            ("GrdF", new EnumValue("GrdF", "CstS")),
            ("Intr", 4096.0),
            ("Clrs", new object[] { Stop(0, 0), Stop(4096, 255) }),
            ("Trns", new object[] { Opacity(0), Opacity(4096) }));
        (string, object)[] all =
        [
            ("DrSh", Shadow("DrSh")),
            ("IrSh", Shadow("IrSh")),
            ("OrGl", Glow("OrGl", "SrcE")),
            ("IrGl", Glow("IrGl", "SrcC")),
            ("ebbl", Effect("ebbl", ("bvlT", new EnumValue("bvlT", "SfBL")), ("bvlS", new EnumValue("BESl", "Embs")), ("blur", new Unit("#Pxl", 4)), ("Sftn", new Unit("#Pxl", 1)))),
            ("ChFX", Effect("ChFX", ("Clr ", Rgb(0, 0, 0)), ("lagl", new Unit("#Ang", 19)), ("Dstn", new Unit("#Pxl", 4)), ("blur", new Unit("#Pxl", 3)), ("Invr", true))),
            ("SoFi", Effect("SoFi", ("Md  ", new EnumValue("BlnM", "Scrn")), ("Clr ", Rgb(0, 128, 255)))),
            ("GrFl", Effect("GrFl", ("Grad", gradient), ("Angl", new Unit("#Ang", 45)), ("Type", new EnumValue("GrdT", "Rdl ")), ("Scl ", new Unit("#Prc", 150)))),
            ("patternFill", Effect("patternFill", ("Ptrn", new Desc("Ptrn", ("Nm  ", "Checks"), ("Idnt", "pat-1"))), ("Scl ", new Unit("#Prc", 200)))),
            ("FrFX", Effect("FrFX", ("Styl", new EnumValue("FStl", "CtrF")), ("PntT", new EnumValue("FrFl", "GrFl")), ("Grad", gradient), ("Type", new EnumValue("GrdT", "shapeburst")), ("Sz  ", new Unit("#Pxl", 3)))),
        ];
        byte[] pattern = PsdHandBuiltTests.Pattern("pat-1", 2, 2, [(0, 0, 255), (0, 255, 0), (0, 255, 0), (0, 0, 255)]);
        foreach ((string key, object effect) in all)
        {
            yield return (key, Write(Effects((key, effect)), key == "patternFill" ? pattern : null, 128));
        }

        byte[] old = new byte[51];
        BinaryPrimitives.WriteInt32BigEndian(old, 2);
        BinaryPrimitives.WriteInt32BigEndian(old.AsSpan(4), 3 << 16);
        BinaryPrimitives.WriteInt32BigEndian(old.AsSpan(12), 120 << 16);
        BinaryPrimitives.WriteInt32BigEndian(old.AsSpan(16), 3 << 16);
        "8BIMmul "u8.CopyTo(old.AsSpan(30));
        old[38] = 1;
        old[40] = 191;
        byte[] block = [0, 0, 0, 1, .. "8BIMdsdw"u8, 0, 0, 0, (byte)old.Length, .. old];
        yield return ("lrFX", Write([("lrFX", block)], null, 255));
    }

    private static Desc Glow(string kind, string source) => Effect(
        kind,
        ("Md  ", new EnumValue("BlnM", "Nrml")),
        ("Clr ", Rgb(0, 255, 0)),
        ("Opct", new Unit("#Prc", 100)),
        ("GlwT", new EnumValue("BETE", "SfBL")),
        ("Ckmt", new Unit("#Prc", 0)),
        ("blur", new Unit("#Pxl", 4)),
        ("glwS", new EnumValue("IGSr", source)));

    private static Desc Effect(string kind, params (string Key, object Value)[] items) =>
        new(kind, [("enab", true), .. items]);

    private static Desc Rgb(double r, double g, double b) => new("RGBC", ("Rd  ", r), ("Grn ", g), ("Bl  ", b));

    private static Desc Stop(int at, double v) =>
        new("Clrt", ("Clr ", Rgb(v, v, v)), ("Type", new EnumValue("Clry", "UsrS")), ("Lctn", at), ("Mdpn", 50));

    private static Desc Opacity(int at) => new("TrnS", ("Opct", new Unit("#Prc", 100)), ("Lctn", at), ("Mdpn", 50));

    private static (string Key, byte[] Data)[] Effects(params (string Key, object Value)[] effects) => Effects("lfx2", effects);

    private static (string Key, byte[] Data)[] Effects(string key, params (string Key, object Value)[] effects) =>
        [(key, Versioned(new Desc("null", [("masterFXSwitch", true), .. effects]), 4))];

    private static Raster<Rgba8> Flatten((string Key, byte[] Data)[] blocks, byte[]? pattern = null, int fill = 255, PsdFlattenOptions? options = null)
    {
        using PsdDocument document = PsdImage.ReadLayers(new MemoryStream(Write(blocks, pattern, fill)));
        using Raster flat = document.Flatten(options);
        return flat.As<Rgba8>();
    }

    /// <summary>
    /// Writes the 32-pixel 8-bit RGB file: a white background over the canvas, then the red square with a transparency channel
    /// (opaque), its fill opacity and <paramref name="blocks"/>; the pattern table, if any, after the layer info.
    /// </summary>
    private static byte[] Write((string Key, byte[] Data)[] blocks, byte[]? pattern, int fill)
    {
        using MemoryStream info = new();
        void I16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteInt16BigEndian(b, (short)v); info.Write(b); }
        void I32(long v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, (int)v); info.Write(b); }
        (string Name, int Top, int Left, int Bottom, int Right, (byte, byte, byte) Color, (string Key, byte[] Data)[] Blocks)[] layers =
        [
            ("Background", 0, 0, Size, Size, (255, 255, 255), []),
            ("Square", Left, Left, Right, Right, (255, 0, 0), blocks),
        ];
        I16(layers.Length);
        foreach (var layer in layers)
        {
            int n = (layer.Bottom - layer.Top) * (layer.Right - layer.Left);
            I32(layer.Top);
            I32(layer.Left);
            I32(layer.Bottom);
            I32(layer.Right);
            I16(4);
            foreach (int c in new[] { -1, 0, 1, 2 })
            {
                I16(c);
                I32(2 + n);
            }

            info.Write("8BIMnorm"u8);
            info.Write([255, 0, 0, 0]);
            byte[] name = System.Text.Encoding.ASCII.GetBytes(layer.Name);
            int padded = (name.Length + 1 + 3) & ~3;
            int extra = 12 + 4 + layer.Blocks.Sum(b => 12 + b.Data.Length + (b.Data.Length & 1));
            I32(4 + 4 + padded + extra);
            I32(0);
            I32(0);
            info.WriteByte((byte)name.Length);
            info.Write(name);
            info.Write(new byte[padded - name.Length - 1]);
            info.Write("8BIMiOpa"u8);
            I32(4);
            info.Write([(byte)(layer.Name == "Square" ? fill : 255), 0, 0, 0]);
            foreach ((string key, byte[] data) in layer.Blocks)
            {
                info.Write("8BIM"u8);
                info.Write(System.Text.Encoding.ASCII.GetBytes(key));
                I32(data.Length + (data.Length & 1));
                info.Write(data);
                if ((data.Length & 1) == 1)
                {
                    info.WriteByte(0);
                }
            }
        }

        foreach (var layer in layers)
        {
            int n = (layer.Bottom - layer.Top) * (layer.Right - layer.Left);
            (byte r, byte g, byte b) = layer.Color;
            foreach (byte v in new byte[] { 255, r, g, b })
            {
                I16(0);
                info.Write(Enumerable.Repeat(v, n).ToArray());
            }
        }

        if (info.Length % 2 == 1)
        {
            info.WriteByte(0);
        }

        using MemoryStream file = new();
        void U16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); file.Write(b); }
        void U32(long v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v); file.Write(b); }
        file.Write("8BPS"u8);
        U16(1);
        file.Write(new byte[6]);
        U16(3);
        U32(Size);
        U32(Size);
        U16(8);
        U16(3);
        U32(0);
        U32(0);
        int global = pattern is null ? 0 : 12 + pattern.Length;
        U32(4 + info.Length + 4 + global);
        U32(info.Length);
        info.Position = 0;
        info.CopyTo(file);
        U32(0);
        if (pattern is not null)
        {
            file.Write("8BIMPatt"u8);
            U32(pattern.Length);
            file.Write(pattern);
        }

        U16(0);
        file.Write(Enumerable.Repeat((byte)255, 3 * Size * Size).ToArray());
        return file.ToArray();
    }
}
