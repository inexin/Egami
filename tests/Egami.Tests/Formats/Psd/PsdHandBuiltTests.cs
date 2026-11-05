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

    private sealed record Layer(string Name, string Blend, (byte R, byte G, byte B)? Color = null, int Section = 0, bool Clipped = false, double? Feather = null);

    /// <summary>
    /// Writes a 4 by 4 8-bit RGB file of opaque full-canvas layers and group records (lsct), raw channels, a white composite; a
    /// layer with a feather has a white user mask over the canvas with that feather in its mask parameters.
    /// </summary>
    private static byte[] Write(Layer[] layers)
    {
        const int size = 4;
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
            int blocks = layer.Section != 0 ? 12 + 12 : 0;
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
        U32(4 + info.Length + 4);
        U32(info.Length);
        info.Position = 0;
        info.CopyTo(file);
        U32(0);
        U16(0);
        file.Write(Enumerable.Repeat((byte)255, 3 * size * size).ToArray());
        return file.ToArray();
    }
}
