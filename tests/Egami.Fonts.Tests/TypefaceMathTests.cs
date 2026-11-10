using System.Buffers.Binary;

using Egami.Fonts.Substitutes;

namespace Egami.Fonts.Tests;

/// <summary>Typeface.Math: the OpenType MATH table of STIX Two Math, the bundled Cambria Math stand-in.</summary>
public sealed class TypefaceMathTests
{
    private static readonly Typeface Stix = SubstituteFonts.Find("Cambria Math", FontStyle.Normal)!;

    private static TypefaceMath MathTable => Stix.Math!;

    [Fact]
    public void CambriaMathIsStixTwoMathWithAMathTable()
    {
        Assert.Equal("STIXTwoMath-Regular", Stix.PostScriptName);
        Assert.Same(Stix, SubstituteFonts.Find("STIX Two Math", new FontStyle(700, 5, FontSlant.Italic)));
        Assert.NotNull(Stix.Math);
        Assert.Same(Stix.Math, Stix.Math);
    }

    [Fact]
    public void AFaceWithoutAMathTableHasNone()
    {
        Assert.Null(SubstituteFonts.Find("Arial", FontStyle.Normal)!.Math);
    }

    [Fact]
    public void ConstantsAreRead()
    {
        MathConstants c = MathTable.Constants;
        int em = Stix.UnitsPerEm;

        Assert.InRange(c.ScriptPercentScaleDown, 60, 85);
        Assert.InRange(c.ScriptScriptPercentScaleDown, 40, c.ScriptPercentScaleDown);
        Assert.InRange(c.AxisHeight, em * 0.15f, em * 0.35f);
        Assert.InRange(c.FractionRuleThickness, 1, em * 0.1f);
        Assert.True(c.SuperscriptShiftUp > c.SuperscriptShiftUpCramped);
        Assert.True(c.FractionNumeratorDisplayStyleShiftUp > c.FractionNumeratorShiftUp);
        Assert.True(c.DisplayOperatorMinHeight > em);
        Assert.True(c.RadicalKernAfterDegree < 0);
        Assert.InRange(c.RadicalDegreeBottomRaisePercent, 30, 80);
        Assert.True(MathTable.MinConnectorOverlap > 0);
    }

    [Fact]
    public void ParenthesesGrowThroughVariantsThenAnAssembly()
    {
        ushort paren = Stix.GetGlyph('(');

        IReadOnlyList<MathGlyphVariant> variants = MathTable.GetVariants(paren, MathDirection.Vertical);
        Assert.True(variants.Count > 3);
        Assert.Equal(paren, variants[0].Glyph);
        for (int i = 1; i < variants.Count; i++)
        {
            Assert.True(variants[i].Advance > variants[i - 1].Advance);
        }

        MathGlyphAssembly assembly = MathTable.GetAssembly(paren, MathDirection.Vertical)!;
        Assert.InRange(assembly.Parts.Count, 2, 5);
        Assert.Contains(assembly.Parts, p => p.IsExtender);
        Assert.Contains(assembly.Parts, p => !p.IsExtender);
        Assert.All(assembly.Parts, p => Assert.True(p.FullAdvance > 0 && p.StartConnectorLength <= p.FullAdvance && p.EndConnectorLength <= p.FullAdvance));

        Assert.Empty(MathTable.GetVariants(paren, MathDirection.Horizontal));
        Assert.Null(MathTable.GetAssembly(paren, MathDirection.Horizontal));
    }

    [Fact]
    public void OverbracesGrowSideways()
    {
        ushort brace = Stix.GetGlyph(0x23DE);

        Assert.NotEmpty(MathTable.GetVariants(brace, MathDirection.Horizontal));
        MathGlyphAssembly assembly = MathTable.GetAssembly(brace, MathDirection.Horizontal)!;
        Assert.True(assembly.Parts.Count >= 3);
        Assert.Empty(MathTable.GetVariants(Stix.GetGlyph('a'), MathDirection.Horizontal));
    }

    [Fact]
    public void ItalicGlyphsHaveItalicsCorrectionAndAccentAttachments()
    {
        ushort italicF = Stix.GetGlyph(0x1D453);
        ushort italicA = Stix.GetGlyph(0x1D44E);

        Assert.True(MathTable.GetItalicsCorrection(italicF) > 0);
        Assert.Null(MathTable.GetItalicsCorrection(0));
        float accent = MathTable.GetTopAccentAttachment(italicA)!.Value;
        Assert.InRange(accent, 1, Stix.GetAdvance(italicA));
    }

    [Fact]
    public void LargeOperatorsAreExtendedShapes()
    {
        ushort integral = Stix.GetGlyph(0x222B);

        Assert.Contains(MathTable.GetVariants(integral, MathDirection.Vertical), v => MathTable.IsExtendedShape(v.Glyph));
        Assert.False(MathTable.IsExtendedShape(Stix.GetGlyph('a')));
    }

    [Fact]
    public void KerningChangesWithHeight()
    {
        // Some glyph in the face has corner kerning that varies with height; glyph 0 has none.
        bool varies = false;
        for (int g = 1; g < Stix.GlyphCount && !varies; g++)
        {
            foreach (MathKernCorner corner in Enum.GetValues<MathKernCorner>())
            {
                varies |= MathTable.GetKerning((ushort)g, corner, -10000) != MathTable.GetKerning((ushort)g, corner, 10000);
            }
        }

        Assert.True(varies);
        Assert.Equal(0, MathTable.GetKerning(0, MathKernCorner.TopLeft, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MathTable.GetKerning(1, (MathKernCorner)4, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MathTable.GetVariants(1, (MathDirection)2));
    }

    [Fact]
    public void DamagedMathTablesNeverThrow()
    {
        byte[] font = Bytes();
        (int record, int offset, int length) = MathRecord(font);
        Random random = new(17);
        for (int round = 0; round < 24; round++)
        {
            byte[] copy = (byte[])font.Clone();
            if (round < 8)
            {
                BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(record + 12), (uint)(length * round / 8));
            }
            else
            {
                int region = System.Math.Min(length, 64 << (round - 8));
                for (int k = 0; k < 40; k++)
                {
                    copy[offset + random.Next(region)] = (byte)random.Next(256);
                }
            }

            TypefaceMath? math = Typeface.FromMemory(copy).Math;
            if (math is null)
            {
                continue;
            }

            _ = math.Constants.AxisHeight;
            foreach (int codePoint in new[] { '(', '{', 'a', 0x1D453, 0x222B, 0x221A, 0x23DE })
            {
                ushort g = Stix.GetGlyph(codePoint);
                _ = math.GetItalicsCorrection(g);
                _ = math.GetTopAccentAttachment(g);
                _ = math.IsExtendedShape(g);
                _ = math.GetKerning(g, MathKernCorner.BottomLeft, 300);
                foreach (MathDirection direction in Enum.GetValues<MathDirection>())
                {
                    _ = math.GetVariants(g, direction);
                    _ = math.GetAssembly(g, direction);
                }
            }
        }
    }

    private static byte[] Bytes()
    {
        using Stream stream = typeof(SubstituteFonts).Assembly.GetManifestResourceStream("Egami.Fonts.Substitutes.STIXTwoMath-Regular.ttf")!;
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static (int Record, int Offset, int Length) MathRecord(byte[] font)
    {
        int tables = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        for (int i = 0; i < tables; i++)
        {
            int record = 12 + (i * 16);
            if (BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(record)) == 0x4D415448)
            {
                return (record, (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(record + 8)), (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(record + 12)));
            }
        }

        throw new InvalidOperationException("No MATH table.");
    }
}
