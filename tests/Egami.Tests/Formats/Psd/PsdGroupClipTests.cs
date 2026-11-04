using System.Buffers.Binary;

using Egami.Formats.Psd;
using Egami.Pixels;

namespace Egami.Tests.Formats.Psd;

/// <summary>Which groups a clipping isolates: only a pass-through group with layers clipped to it, not one inside a clipped group.</summary>
public class PsdGroupClipTests
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

    private sealed record Layer(string Name, string Blend, (byte R, byte G, byte B)? Color = null, int Section = 0, bool Clipped = false);

    /// <summary>Writes a 4 by 4 8-bit RGB file of opaque full-canvas layers and group records (lsct), raw channels, a white composite.</summary>
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
            I16(pixels ? 3 : 0);
            for (int c = 0; pixels && c < 3; c++)
            {
                I16(c);
                I32(2 + (size * size));
            }

            info.Write("8BIM"u8);
            info.Write(System.Text.Encoding.ASCII.GetBytes(layer.Blend));
            info.Write([255, (byte)(layer.Clipped ? 1 : 0), 0, 0]);
            byte[] name = System.Text.Encoding.ASCII.GetBytes(layer.Name);
            int padded = (name.Length + 1 + 3) & ~3;
            int blocks = layer.Section != 0 ? 12 + 12 : 0;
            I32(4 + 4 + padded + blocks);
            I32(0);
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
