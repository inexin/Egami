using System.Buffers.Binary;

using Egami.Formats.Psd;
using Egami.Pixels;

namespace Egami.Tests.Formats.Psd;

/// <summary>Flattening files written here byte by byte, for layouts the corpus does not have.</summary>
public class PsdHandBuiltTests
{
    /// <summary>
    /// A clipped group whose top child is a pass-through group: the clipping is the parent's, so the pass-through group's
    /// multiply layer still blends with the layer under it in the parent.
    /// </summary>
    [Fact]
    public void PassThroughUnderAClippedParentStaysPassThrough()
    {
        byte[] file = Write(
        [
            new("Background", "norm", Color: (255, 255, 255)),
            new("</Layer group>", "norm", Section: 3),
            new("Under", "norm", Color: (100, 200, 50)),
            new("</Layer group>", "norm", Section: 3),
            new("Multiply", "mul ", Color: (255, 128, 0)),
            new("Pass", "pass", Section: 1),
            new("Clipped parent", "norm", Section: 1, Clipped: true),
        ]);

        using PsdDocument document = PsdImage.ReadLayers(new MemoryStream(file));
        using Raster flat = document.Flatten();
        using Raster<Rgba8> pixels = flat.As<Rgba8>();

        // Multiply over (100, 200, 50): (100, 100, 0). Isolated, the pass-through group would show (255, 128, 0).
        Assert.Equal(new Rgba8(100, 100, 0, 255), pixels.Row(1)[1]);
    }

    /// <summary>A feather radius out of range or not a number reads as no feather or 1000 pixels, so flattening stays quick.</summary>
    [Theory]
    [InlineData(1e9)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    [InlineData(-5.0)]
    public void HugeOrBrokenFeathersFlattenQuickly(double feather)
    {
        byte[] file = Write(
        [
            new("Background", "norm", Color: (255, 255, 255)),
            new("Masked", "norm", Color: (0, 0, 0), Feather: feather),
        ]);

        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        using PsdDocument document = PsdImage.ReadLayers(new MemoryStream(file));
        Assert.InRange(document.Layers[1].Mask!.Feather, 0, 1000);
        using Raster flat = document.Flatten();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"{clock.Elapsed}");

        // The mask is white everywhere, so feathered or not the layer covers the canvas.
        using Raster<Rgba8> pixels = flat.As<Rgba8>();
        Assert.Equal(new Rgba8(0, 0, 0, 255), pixels.Row(2)[2]);
    }

    /// <summary>A linear gradient fill, black to white at 0°, rises left to right through mid gray at the canvas's center.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LinearGradientFillRunsAcrossTheCanvas(bool reverse)
    {
        byte[] file = Write(
        [
            new("Background", "norm", Color: (255, 0, 0)),
            new("Gradient", "norm", Blocks: [("GdFl", GradientFill("Lnr ", 0, reverse))]),
        ], 16);

        using PsdDocument document = PsdImage.ReadLayers(new MemoryStream(file));
        using Raster flat = document.Flatten();
        Assert.DoesNotContain(flat.Metadata.Warnings, w => w.Id == "Psd.FillSkipped");
        using Raster<Rgba8> pixels = flat.As<Rgba8>();
        Span<Rgba8> row = pixels.Row(5);
        for (int x = 1; x < 16; x++)
        {
            Assert.True(reverse ? row[x].R <= row[x - 1].R : row[x].R >= row[x - 1].R, $"x {x}");
            Assert.Equal(row[x].R, row[x].B);
        }

        Assert.Equal(reverse ? 255 : 0, row[0].R);
        Assert.InRange(row[8].R, 126, 130);
        Assert.Equal(pixels.Row(0)[3], pixels.Row(15)[3]);
    }

    /// <summary>A radial gradient fill is darkest at the center and the same at points as far from it.</summary>
    [Fact]
    public void RadialGradientFillIsRoundTheCenter()
    {
        byte[] file = Write([new("Gradient", "norm", Blocks: [("GdFl", GradientFill("Rdl ", 90, false))])], 16);
        using PsdDocument document = PsdImage.ReadLayers(new MemoryStream(file));
        using Raster flat = document.Flatten();
        using Raster<Rgba8> pixels = flat.As<Rgba8>();
        Assert.Equal(0, pixels.Row(8)[8].R);
        Assert.Equal(pixels.Row(8)[4].R, pixels.Row(4)[8].R);
        Assert.Equal(pixels.Row(8)[12].R, pixels.Row(12)[8].R);
        Assert.True(pixels.Row(8)[2].R > pixels.Row(8)[5].R);
    }

    /// <summary>A pattern fill tiles the document's pattern from the canvas's origin, moved by the phase.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    public void PatternFillTilesFromThePhase(int phaseX, int phaseY)
    {
        (byte, byte, byte)[] colors = [(255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 255)];
        byte[] settings = PsdDescriptorWriter.Versioned(new(
            "null",
            ("Ptrn", new PsdDescriptorWriter.Desc("Ptrn", ("Nm  ", "Dots"), ("Idnt", "pat-1"))),
            ("phase", new PsdDescriptorWriter.Desc("Pnt ", ("Hrzn", (double)phaseX), ("Vrtc", (double)phaseY)))));
        byte[] file = Write(
            [new("Pattern", "norm", Blocks: [("PtFl", settings)])],
            4,
            [("Patt", Pattern("pat-1", 2, 2, colors))]);

        using PsdDocument document = PsdImage.ReadLayers(new MemoryStream(file));
        using Raster flat = document.Flatten();
        Assert.DoesNotContain(flat.Metadata.Warnings, w => w.Id == "Psd.FillSkipped");
        using Raster<Rgba8> pixels = flat.As<Rgba8>();
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                (byte r, byte g, byte b) = colors[(((y - phaseY) & 1) * 2) + ((x - phaseX) & 1)];
                Assert.Equal(new Rgba8(r, g, b, 255), pixels.Row(y)[x]);
            }
        }
    }

    /// <summary>A pattern fill whose pattern the document does not hold is left out with a warning.</summary>
    [Fact]
    public void MissingPatternIsLeftOutWithAWarning()
    {
        byte[] settings = PsdDescriptorWriter.Versioned(new("null", ("Ptrn", new PsdDescriptorWriter.Desc("Ptrn", ("Idnt", "absent")))));
        byte[] file = Write([new("Background", "norm", Color: (10, 20, 30)), new("Pattern", "norm", Blocks: [("PtFl", settings)])]);
        using PsdDocument document = PsdImage.ReadLayers(new MemoryStream(file));
        using Raster flat = document.Flatten();
        Assert.Contains(flat.Metadata.Warnings, w => w.Id == "Psd.FillSkipped");
        using Raster<Rgba8> pixels = flat.As<Rgba8>();
        Assert.Equal(new Rgba8(10, 20, 30, 255), pixels.Row(1)[1]);
    }

    /// <summary>
    /// A pattern table whose record ends where its id should start (version, mode, size and an empty name, 16 bytes) loads and
    /// flattens: the pattern fill is left out with its warning.
    /// </summary>
    [Fact]
    public void TruncatedPatternTableLeavesTheFillOut()
    {
        byte[] record = [0, 0, 0, 1, 0, 0, 0, 3, 0, 1, 0, 1, 0, 0, 0, 0];
        byte[] table = new byte[4 + record.Length];
        BinaryPrimitives.WriteUInt32BigEndian(table, (uint)record.Length);
        record.CopyTo(table, 4);
        byte[] settings = PsdDescriptorWriter.Versioned(new("null", ("Ptrn", new PsdDescriptorWriter.Desc("Ptrn", ("Idnt", "pat-1")))));
        byte[] file = Write(
            [new("Background", "norm", Color: (10, 20, 30)), new("Pattern", "norm", Blocks: [("PtFl", settings)])],
            4,
            [("Patt", table)]);

        using Raster loaded = Raster.Load(file, EgamiContext.Default, new PsdReaderOptions { Composite = PsdComposite.Flatten });
        Assert.Contains(loaded.Metadata.Warnings, w => w.Id == "Psd.FillSkipped");
        using Raster<Rgba8> pixels = loaded.As<Rgba8>();
        Assert.Equal(new Rgba8(10, 20, 30, 255), pixels.Row(1)[1]);
    }

    /// <summary>A gradient fill's settings: black to white, Classic, full smoothness, the given style, angle and reverse flag.</summary>
    private static byte[] GradientFill(string style, double angle, bool reverse)
    {
        static PsdDescriptorWriter.Desc Stop(int location, double gray) => new(
            "Clrt",
            ("Clr ", new PsdDescriptorWriter.Desc("RGBC", ("Rd  ", gray), ("Grn ", gray), ("Bl  ", gray))),
            ("Type", new PsdDescriptorWriter.EnumValue("Clry", "UsrS")),
            ("Lctn", location),
            ("Mdpn", 50));
        static PsdDescriptorWriter.Desc Opacity(int location) => new(
            "TrnS", ("Opct", new PsdDescriptorWriter.Unit("#Prc", 100)), ("Lctn", location), ("Mdpn", 50));
        PsdDescriptorWriter.Desc gradient = new(
            "Grdn",
            ("Nm  ", "Black, White"),
            ("GrdF", new PsdDescriptorWriter.EnumValue("GrdF", "CstS")),
            ("Intr", 4096.0),
            ("Clrs", new object[] { Stop(0, 0), Stop(4096, 255) }),
            ("Trns", new object[] { Opacity(0), Opacity(4096) }));
        return PsdDescriptorWriter.Versioned(new(
            "null",
            ("Angl", new PsdDescriptorWriter.Unit("#Ang", angle)),
            ("Type", new PsdDescriptorWriter.EnumValue("GrdT", style)),
            ("Rvrs", reverse),
            ("Scl ", new PsdDescriptorWriter.Unit("#Prc", 100)),
            ("Grad", gradient)));
    }

    /// <summary>
    /// A pattern table holding one 8-bit RGB pattern, its colors row by row, as the specification lays out a pattern: its
    /// header, then the virtual memory array list of three raw channels and two unwritten masks.
    /// </summary>
    private static byte[] Pattern(string id, int width, int height, (byte R, byte G, byte B)[] colors)
    {
        using MemoryStream body = new();
        void U16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); body.Write(b); }
        void U32(long v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v); body.Write(b); }
        U32(1);
        U32(3);
        U16(height);
        U16(width);
        U32(1);
        U16(0);
        body.WriteByte((byte)id.Length);
        body.Write(System.Text.Encoding.ASCII.GetBytes(id));
        int array = 23 + (width * height);
        U32(3);
        U32(4 + 16 + 4 + (3 * (8 + array)) + 8);
        U32(0);
        U32(0);
        U32(height);
        U32(width);
        U32(3);
        for (int c = 0; c < 3; c++)
        {
            U32(1);
            U32(array);
            U32(8);
            U32(0);
            U32(0);
            U32(height);
            U32(width);
            U16(8);
            body.WriteByte(0);
            foreach ((byte r, byte g, byte b) in colors)
            {
                body.WriteByte(c == 0 ? r : c == 1 ? g : b);
            }
        }

        U32(0);
        U32(0);
        while (body.Length % 4 != 0)
        {
            body.WriteByte(0);
        }

        byte[] pattern = body.ToArray();
        byte[] block = new byte[4 + pattern.Length];
        BinaryPrimitives.WriteUInt32BigEndian(block, (uint)pattern.Length);
        pattern.CopyTo(block, 4);
        return block;
    }

    private sealed record Layer(
        string Name, string Blend, (byte R, byte G, byte B)? Color = null, int Section = 0, bool Clipped = false, double? Feather = null, (string Key, byte[] Data)[]? Blocks = null);

    /// <summary>
    /// Writes a <paramref name="size"/>-pixel square 8-bit RGB file of opaque full-canvas layers and group records (lsct), raw
    /// channels, a white composite; a layer with a feather has a white user mask over the canvas with that feather in its mask
    /// parameters; a layer's blocks and the <paramref name="globals"/> (after the layer info) are written as given.
    /// </summary>
    private static byte[] Write(Layer[] layers, int size = 4, (string Key, byte[] Data)[]? globals = null)
    {
        using MemoryStream info = new();
        void I16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteInt16BigEndian(b, (short)v); info.Write(b); }
        void I32(long v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, (int)v); info.Write(b); }
        I16(layers.Length);
        foreach (Layer layer in layers)
        {
            bool pixels = layer.Color is not null;
            I32(0);
            I32(0);
            I32(pixels ? size : 0);
            I32(pixels ? size : 0);
            bool masked = layer.Feather is not null;
            I16((pixels ? 3 : 0) + (masked ? 1 : 0));
            for (int c = 0; pixels && c < 3; c++)
            {
                I16(c);
                I32(2 + (size * size));
            }

            if (masked)
            {
                I16(-2);
                I32(2 + (size * size));
            }

            info.Write("8BIM"u8);
            info.Write(System.Text.Encoding.ASCII.GetBytes(layer.Blend));
            info.Write([255, (byte)(layer.Clipped ? 1 : 0), 0, 0]);
            byte[] name = System.Text.Encoding.ASCII.GetBytes(layer.Name);
            int padded = (name.Length + 1 + 3) & ~3;
            int blocks = (layer.Section != 0 ? 12 + 12 : 0) + (layer.Blocks ?? []).Sum(b => 12 + b.Data.Length);
            // The mask: rectangle, default color 0, flags 0x10 (parameters follow), then parameter bit 1 (user feather), the
            // feather as a double, and one byte of padding.
            int maskBytes = masked ? 16 + 2 + 1 + 8 + 1 : 0;
            I32(4 + maskBytes + 4 + padded + blocks);
            I32(maskBytes);
            if (masked)
            {
                I32(0);
                I32(0);
                I32(size);
                I32(size);
                info.Write([0, 0x10, 0x02]);
                byte[] radius = new byte[8];
                BinaryPrimitives.WriteDoubleBigEndian(radius, layer.Feather!.Value);
                info.Write(radius);
                info.WriteByte(0);
            }

            I32(0);
            info.WriteByte((byte)name.Length);
            info.Write(name);
            info.Write(new byte[padded - name.Length - 1]);
            if (layer.Section != 0)
            {
                info.Write("8BIMlsct"u8);
                I32(12);
                I32(layer.Section);
                info.Write("8BIM"u8);
                info.Write(System.Text.Encoding.ASCII.GetBytes(layer.Blend));
            }

            foreach ((string key, byte[] data) in layer.Blocks ?? [])
            {
                info.Write("8BIM"u8);
                info.Write(System.Text.Encoding.ASCII.GetBytes(key));
                I32(data.Length);
                info.Write(data);
            }
        }

        foreach (Layer layer in layers)
        {
            if (layer.Color is (byte r, byte g, byte b))
            {
                foreach (byte v in new[] { r, g, b })
                {
                    I16(0);
                    info.Write(Enumerable.Repeat(v, size * size).ToArray());
                }
            }

            if (layer.Feather is not null)
            {
                I16(0);
                info.Write(Enumerable.Repeat((byte)255, size * size).ToArray());
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
        U32(size);
        U32(size);
        U16(8);
        U16(3);
        U32(0);
        U32(0);
        int global = (globals ?? []).Sum(b => 12 + b.Data.Length);
        U32(4 + info.Length + 4 + global);
        U32(info.Length);
        info.Position = 0;
        info.CopyTo(file);
        U32(0);
        foreach ((string key, byte[] data) in globals ?? [])
        {
            file.Write("8BIM"u8);
            file.Write(System.Text.Encoding.ASCII.GetBytes(key));
            U32(data.Length);
            file.Write(data);
        }

        U16(0);
        file.Write(Enumerable.Repeat((byte)255, 3 * size * size).ToArray());
        return file.ToArray();
    }
}
