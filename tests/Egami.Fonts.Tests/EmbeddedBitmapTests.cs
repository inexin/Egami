using System.Buffers.Binary;

using Egami.Fonts.Color;

namespace Egami.Fonts.Tests;

/// <summary>
/// 2.7.6 task 22: EBLC and EBDT, the embedded black-and-white and gray strikes Windows draws (Calibri at 12, 13, 15, 16, 17 and
/// 19 pixels per em, for example), read by the CBLC reader. The strikes here are written into DejaVu Sans by
/// <see cref="EmbeddedStrikes.WithStrikes"/>.
/// </summary>
public class EmbeddedBitmapTests
{
    [Fact]
    public void StrikesDecodeEachFormatAndDepth()
    {
        Typeface face = Typeface.FromMemory(EmbeddedStrikes.WithStrikes(TestFonts.Bytes("DejaVuSans.ttf")));
        ushort a = face.GetGlyph('A'), b = face.GetGlyph('B');

        EmbeddedBitmap glyphA = face.GetEmbeddedBitmap(a, 13)!;
        Assert.Equal((3, 4, 1, 9, 7), (glyphA.Width, glyphA.Height, glyphA.BearingX, glyphA.BearingY, glyphA.Advance));
        Assert.Equal(Expand(EmbeddedStrikes.A1, 1), glyphA.Coverage);

        EmbeddedBitmap glyphB = face.GetEmbeddedBitmap(b, 13)!;
        Assert.Equal((5, 2, 0, 8, 6), (glyphB.Width, glyphB.Height, glyphB.BearingX, glyphB.BearingY, glyphB.Advance));
        Assert.Equal(Expand(EmbeddedStrikes.B1, 1), glyphB.Coverage);

        EmbeddedBitmap gray = face.GetEmbeddedBitmap(a, 14)!;
        Assert.Equal([0, 85, 170, 255], gray.Coverage);

        // Only at exactly a strike's size, and only for the glyphs it holds.
        Assert.Null(face.GetEmbeddedBitmap(a, 13.5f));
        Assert.Null(face.GetEmbeddedBitmap(a, 12));
        Assert.Null(face.GetEmbeddedBitmap(face.GetGlyph('C'), 13));
        Assert.Null(face.GetEmbeddedBitmap(b, 14));
        Assert.Null(TestFonts.Load("DejaVuSans.ttf").GetEmbeddedBitmap(a, 13));
    }

    [Fact]
    public void GridFittedAdvancesTakeTheStrikesAdvancesWhenAsked()
    {
        Typeface face = Typeface.FromMemory(EmbeddedStrikes.WithStrikes(TestFonts.Bytes("DejaVuSans.ttf")));
        var font = new Font(face, 13) { Hinting = FontHinting.Full, Metrics = FontMetrics.GridFitted, Kerning = false };
        float hinted = font.MeasureText("AB").Advance;
        Assert.Equal(13, (font with { EmbeddedBitmaps = true }).MeasureText("AB").Advance);
        Assert.NotEqual(13, hinted);

        // Other sizes, and the default, keep the outline's advances.
        Assert.Equal(new Font(face, 12) { Hinting = FontHinting.Full, Metrics = FontMetrics.GridFitted }.MeasureText("AB").Advance, new Font(face, 12) { Hinting = FontHinting.Full, Metrics = FontMetrics.GridFitted, EmbeddedBitmaps = true }.MeasureText("AB").Advance);
        Assert.False(new Font(face, 13).EmbeddedBitmaps);
        Assert.True(new Font(face, 13).WithGdiPlusLook().EmbeddedBitmaps);
    }

    [Fact]
    public void DamagedStrikesNeverThrow()
    {
        byte[] font = EmbeddedStrikes.WithStrikes(TestFonts.Bytes("DejaVuSans.ttf"));
        var random = new Random(22);
        foreach (string tag in new[] { "EBLC", "EBDT" })
        {
            (int offset, int length) = Find(font, tag);
            for (int round = 0; round < 200; round++)
            {
                byte[] copy = (byte[])font.Clone();
                for (int k = 0; k < 4; k++)
                {
                    copy[offset + random.Next(length)] = (byte)random.Next(256);
                }

                Typeface face = Typeface.FromMemory(copy);
                foreach (char c in "AB")
                {
                    for (int ppem = 12; ppem <= 15; ppem++)
                    {
                        EmbeddedBitmap? bitmap = face.GetEmbeddedBitmap(face.GetGlyph(c), ppem);
                        Assert.True(bitmap is null || bitmap.Coverage.Length == bitmap.Width * bitmap.Height);
                    }
                }
            }
        }
    }

    private static (int Offset, int Length) Find(byte[] font, string tag)
    {
        int count = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        for (int i = 0; i < count; i++)
        {
            int record = 12 + (16 * i);
            if (System.Text.Encoding.ASCII.GetString(font, record, 4) == tag)
            {
                return (BinaryPrimitives.ReadInt32BigEndian(font.AsSpan(record + 8)), BinaryPrimitives.ReadInt32BigEndian(font.AsSpan(record + 12)));
            }
        }

        throw new InvalidOperationException($"No {tag} table.");
    }

    // A test bitmap's pixels as 0 to 255 coverage, row by row.
    private static byte[] Expand(byte[,] pixels, int depth)
    {
        int max = (1 << depth) - 1;
        var result = new byte[pixels.Length];
        int i = 0;
        foreach (byte p in pixels)
        {
            result[i++] = (byte)(p * 255 / max);
        }

        return result;
    }
}
