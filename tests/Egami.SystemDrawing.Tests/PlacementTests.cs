using System.Buffers.Binary;

using Egami.SystemDrawing.Internal;
using Egami.SystemDrawing.Text;

namespace Egami.SystemDrawing.Tests;

/// <summary>
/// Where DrawString puts lines in a layout box, as measured from GDI+ (2.7.6 task 28, egami-compare GdiPlacementProbeTests and the
/// sysdrawing Placement probe): one line is a cell tall, the default format adds 1/8 em, and grid-fitted ClearType puts the first
/// baseline a whole-pixel ascent below the rounded line top.
/// </summary>
public class PlacementTests
{
    private static readonly Lazy<PrivateFontCollection> s_fonts = new(() =>
    {
        PrivateFontCollection fonts = new();
        fonts.AddFontFile(TestFiles.Font("DejaVuSans.ttf"));
        return fonts;
    });

    private static FontFamily DejaVu => new("DejaVu Sans", s_fonts.Value);

    [Theory]
    [InlineData(StringAlignment.Near, false)]
    [InlineData(StringAlignment.Center, false)]
    [InlineData(StringAlignment.Far, false)]
    [InlineData(StringAlignment.Center, true)]
    [InlineData(StringAlignment.Far, true)]
    public void OneLineIsACellTall(StringAlignment align, bool typographic)
    {
        using Font font = new(DejaVu, 16, FontStyle.Regular, GraphicsUnit.Pixel);
        using StringFormat format = new(typographic ? StringFormat.GenericTypographic : StringFormat.GenericDefault) { LineAlignment = align };
        GdiTextLayout layout = GdiTextLayout.Create("H", font, 16, new SizeF(120, 40), format);
        float block = layout.Ascent + layout.Descent + (typographic ? 0 : 2);
        float expected = align switch { StringAlignment.Center => (40 - block) / 2, StringAlignment.Far => 40 - block, _ => 0 };
        Assert.Equal(expected, layout.Top, 4);
    }

    [Fact]
    public void MoreLinesAreALineSpacingEach()
    {
        using Font font = new(DejaVu, 16, FontStyle.Regular, GraphicsUnit.Pixel);
        using StringFormat format = new() { LineAlignment = StringAlignment.Center };
        GdiTextLayout layout = GdiTextLayout.Create("H\nH", font, 16, new SizeF(120, 80), format);
        Assert.Equal((80 - (2 * layout.LineSpacing) - 2) / 2, layout.Top, 4);
    }

    /// <summary>With LineLimit, text of one line is drawn in a box shorter than a line; text of more lines is not.</summary>
    [Fact]
    public void LineLimitKeepsASingleLine()
    {
        using Font font = new(DejaVu, 20, FontStyle.Regular, GraphicsUnit.Pixel);
        Assert.Single(GdiTextLayout.Create("H", font, 20, new SizeF(120, 5), StringFormat.GenericTypographic).Lines);
        Assert.Empty(GdiTextLayout.Create("H\nH", font, 20, new SizeF(120, 5), StringFormat.GenericTypographic).Lines);
        Assert.Single(GdiTextLayout.Create("H\nH", font, 20, new SizeF(120, 30), StringFormat.GenericTypographic).Lines);
    }

    /// <summary>ClearTypeGridFit: the first baseline is the line top rounded, then the cell ascent rounded, below.</summary>
    [Theory]
    [InlineData(30, 0f)]
    [InlineData(31, 0.25f)]
    [InlineData(36, 0.5f)]
    [InlineData(37, 0.75f)]
    public void ClearTypePutsTheFirstBaselineAWholeAscentBelowTheRoundedTop(int height, float y)
    {
        using Font font = new(DejaVu, 16, FontStyle.Regular, GraphicsUnit.Pixel);
        using Bitmap bitmap = new(120, 60, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        using (StringFormat format = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            g.Clear(Color.Black);
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.DrawString("H", font, Brushes.White, new RectangleF(0, y, 120, height), format);
        }

        FontFamily f = DejaVu;
        float em = f.GetEmHeight(FontStyle.Regular);
        float ascent = f.GetCellAscent(FontStyle.Regular) * 16 / em, descent = f.GetCellDescent(FontStyle.Regular) * 16 / em;
        float top = y + ((height - (ascent + descent) - 2) / 2);
        int baseline = (int)MathF.Floor(top + 0.5f) + (int)MathF.Floor(ascent + 0.5f);
        Assert.Equal(baseline, LowestInk(bitmap) + 1);
    }

    /// <summary>A grid-fitted font's GDI+ ascent is the cell ascent rounded, or VDMX's yMax at the size when larger.</summary>
    [Fact]
    public void VdmxRaisesTheAscentAtItsSizes()
    {
        Fonts.Typeface face = Fonts.Typeface.FromMemory(WithVdmx(File.ReadAllBytes(TestFiles.Font("DejaVuSans.ttf")), charSet: 1));
        float cell = Cell(face, 16);
        Assert.Equal(20, new Fonts.Font(face, 16).WithGdiPlusLook().GdiPlusAscent);
        Assert.Equal(MathF.Floor(Cell(face, 17) + 0.5f), new Fonts.Font(face, 17).WithGdiPlusLook().GdiPlusAscent);
        Assert.Equal(cell, new Fonts.Font(face, 16).GdiPlusAscent, 4);
    }

    [Fact]
    public void VdmxNeedsTheAnsiRecord()
    {
        Fonts.Typeface face = Fonts.Typeface.FromMemory(WithVdmx(File.ReadAllBytes(TestFiles.Font("DejaVuSans.ttf")), charSet: 0));
        Assert.Equal(MathF.Floor(Cell(face, 16) + 0.5f), new Fonts.Font(face, 16).WithGdiPlusLook().GdiPlusAscent);
    }

    private static float Cell(Fonts.Typeface face, float em) => face.WinMetrics.Ascent * em / face.UnitsPerEm;

    [Fact]
    public void TypoMetricsCountWhereTheFontAsksForThem()
    {
        Assert.False(GdiAscent.UsesTypoMetrics(Fonts.Typeface.FromFile(TestFiles.Font("DejaVuSans.ttf"))));
        Assert.True(GdiAscent.UsesTypoMetrics(Fonts.Typeface.FromFile(TestFiles.Font("NotoSans-Regular.ttf"))));
    }

    private static int LowestInk(Bitmap bitmap)
    {
        for (int y = bitmap.Height - 1; y >= 0; y--)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).G > 100)
                {
                    return y;
                }
            }
        }

        return -1;
    }

    // The font with a VDMX table added: one ratio record (the charset given, 1:1) and one group with yMax 20 at 16 ppem.
    private static byte[] WithVdmx(byte[] font, byte charSet)
    {
        byte[] vdmx = new byte[6 + 4 + 2 + 4 + 6];
        BinaryPrimitives.WriteUInt16BigEndian(vdmx.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(vdmx.AsSpan(4), 1);
        vdmx[6] = charSet;
        vdmx[7] = vdmx[8] = vdmx[9] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(vdmx.AsSpan(10), 12);
        BinaryPrimitives.WriteUInt16BigEndian(vdmx.AsSpan(12), 1);
        vdmx[14] = vdmx[15] = 16;
        BinaryPrimitives.WriteUInt16BigEndian(vdmx.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16BigEndian(vdmx.AsSpan(18), 20);
        BinaryPrimitives.WriteInt16BigEndian(vdmx.AsSpan(20), -4);

        // A new table directory one record longer, every table moved past it, then the VDMX table at the end.
        int count = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        int oldHeader = 12 + (16 * count), newHeader = oldHeader + 16;
        using MemoryStream output = new();
        byte[] header = new byte[newHeader];
        font.AsSpan(0, 12).CopyTo(header);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), (ushort)(count + 1));
        int shift = newHeader - oldHeader;
        for (int i = 0; i < count; i++)
        {
            Span<byte> record = header.AsSpan(12 + (16 * i), 16);
            font.AsSpan(12 + (16 * i), 16).CopyTo(record);
            BinaryPrimitives.WriteUInt32BigEndian(record[8..], BinaryPrimitives.ReadUInt32BigEndian(record[8..]) + (uint)shift);
        }

        int end = (font.Length - oldHeader + 3) & ~3;
        Span<byte> added = header.AsSpan(12 + (16 * count), 16);
        "VDMX"u8.CopyTo(added);
        BinaryPrimitives.WriteUInt32BigEndian(added[8..], (uint)(newHeader + end));
        BinaryPrimitives.WriteUInt32BigEndian(added[12..], (uint)vdmx.Length);
        output.Write(header);
        output.Write(font.AsSpan(oldHeader));
        output.Write(new byte[end - (font.Length - oldHeader)]);
        output.Write(vdmx);
        return output.ToArray();
    }
}
