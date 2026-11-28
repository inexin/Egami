using Egami.Fonts.Cff;

namespace Egami.Fonts.Tests;

/// <summary>
/// The charstring-level CFF writer (2.7.10): CFF2 instances keep their hints and subroutines and draw the position within half a
/// unit, <see cref="SubsetOptions.DropHinting"/> drops CFF hints with the outlines unchanged, and the written charstrings use the
/// short operators.
/// </summary>
public class CffCharStringWriterTests
{
    [Theory]
    [InlineData("derived/SourceSerif4-VF-subset.otf", 800f)]
    [InlineData("derived/SourceSans3-VF-subset.otf", 300f)]
    public void Cff2InstancesKeepEveryGlyphsHints(string file, float weight)
    {
        Typeface face = TestFonts.Load(file).WithVariations(FontVariation.Weight(weight));
        FontSubset subset = FontSubsetter.Subset(face, Enumerable.Range(0, face.GlyphCount).Select(g => (ushort)g));
        CffFont written = subset.ToTypeface().Cff!;
        CffFont source = face.Cff!;
        int hinted = 0;
        for (int g = 0; g < face.GlyphCount; g++)
        {
            Ops before = Count(source, g);
            Ops after = Count(written, subset.GlyphMap[(ushort)g]);
            Assert.True(before.Stems == after.Stems, $"glyph {g}: {after.Stems} stems, the source has {before.Stems}");
            Assert.True(before.Masks == after.Masks, $"glyph {g}: {after.Masks} masks, the source has {before.Masks}");
            hinted += before.Stems > 0 ? 1 : 0;
        }

        if (file.Contains("Serif", StringComparison.Ordinal))
        {
            Assert.True(hinted > 100, $"{hinted} hinted glyphs");
        }
    }

    [Theory]
    [InlineData("derived/SourceSerif4-VF-subset.otf", 800f)]
    [InlineData("SourceSans3-Regular.otf", 0f)]
    [InlineData("SourceSerif4-Regular.otf", 0f)]
    public void DropHintingDropsCffHintsAndKeepsTheOutlines(string file, float weight)
    {
        Typeface face = TestFonts.Load(file);
        if (weight != 0)
        {
            face = face.WithVariations(FontVariation.Weight(weight));
        }

        ushort[] glyphs = [.. Enumerable.Range(0, Math.Min(face.GlyphCount, 400)).Select(g => (ushort)g)];
        FontSubset hinted = FontSubsetter.Subset(face, glyphs);
        FontSubset plain = FontSubsetter.Subset(face, glyphs, new SubsetOptions { DropHinting = true });
        Assert.True(plain.Data.Length < hinted.Data.Length, $"{plain.Data.Length} bytes without hints, {hinted.Data.Length} with");
        Typeface a = hinted.ToTypeface(), b = plain.ToTypeface();
        CffFont cff = b.Cff!;
        Assert.DoesNotContain(cff.Privates, p => p.Dict.Contains(6) || p.Dict.Contains(10) || p.Dict.Contains(1212));
        for (int g = 0; g < b.GlyphCount; g++)
        {
            Ops ops = Count(cff, g);
            Assert.True(ops.Stems == 0 && ops.Masks == 0, $"glyph {g} keeps {ops.Stems} stems and {ops.Masks} masks");
            var expected = new TextSink();
            var actual = new TextSink();
            a.GetGlyphOutline((ushort)g, expected);
            b.GetGlyphOutline((ushort)g, actual);
            AssertSameOutline(expected, actual, 1e-3, $"{file} glyph {g}");
            Assert.Equal(a.GetAdvance((ushort)g), b.GetAdvance((ushort)g));
        }
    }

    [Fact]
    public void Cff2InstancesKeepSharedSubroutines()
    {
        // The Source Sans 3 cut calls its subroutines from several glyphs; a subset of them all keeps those shared.
        Typeface face = TestFonts.Load("derived/SourceSans3-VF-subset.otf").WithVariations(FontVariation.Weight(650));
        Assert.True(face.Cff!.Privates[0].LocalSubrs?.Count > 0 || face.Cff.GlobalSubrs.Count > 0);
        FontSubset subset = FontSubsetter.Subset(face, Enumerable.Range(0, face.GlyphCount).Select(g => (ushort)g));
        CffFont written = subset.ToTypeface().Cff!;
        int kept = written.GlobalSubrs.Count + written.Privates.Sum(p => p.LocalSubrs?.Count ?? 0);
        Assert.True(kept > 0, "no subroutine kept");
        int calls = 0;
        for (int g = 0; g < written.GlyphCount; g++)
        {
            calls += Count(written, g).Calls;
        }

        Assert.True(calls >= kept * 2, $"{calls} calls of {kept} kept subroutines");
    }

    [Fact]
    public void ACff2InstanceUsesTheShortOperatorsAndWholeNumbers()
    {
        Typeface face = TestFonts.Load("derived/SourceSerif4-VF-subset.otf").WithVariations(FontVariation.Weight(800));
        ushort[] glyphs = [.. "The quick brown fox jumps over the lazy dog.".Select(c => face.GetGlyph(c))];
        FontSubset subset = FontSubsetter.Subset(face, glyphs, new SubsetOptions { LayoutClosure = false });
        CffFont written = subset.ToTypeface().Cff!;
        var used = new HashSet<int>();
        int fractions = 0;
        for (int g = 0; g < written.GlyphCount; g++)
        {
            Ops ops = Count(written, g);
            used.UnionWith(ops.Operators);
            fractions += ops.Fractions;
        }

        Assert.Equal(0, fractions);
        Assert.Contains(6, used); // hlineto
        Assert.Contains(7, used); // vlineto
        Assert.True(used.Contains(30) || used.Contains(31), "no hvcurveto or vhcurveto");
        Assert.True(subset.Data.Length < 8500, $"{subset.Data.Length} bytes");
    }

    internal static void AssertSameOutline(TextSink expected, TextSink actual, double tolerance, string where)
    {
        Assert.True(expected.Verbs.SequenceEqual(actual.Verbs), $"{where}: verbs {new string([.. actual.Verbs])}, expected {new string([.. expected.Verbs])}");
        for (int k = 0; k < expected.Ops.Count; k++)
        {
            for (int c = 0; c < expected.Ops[k].Length; c++)
            {
                Assert.True(Math.Abs(expected.Ops[k][c] - actual.Ops[k][c]) <= tolerance, $"{where} verb {k}: {actual.Ops[k][c]}, expected {expected.Ops[k][c]}");
            }
        }
    }

    internal readonly record struct Ops(int Stems, int Masks, int Calls, int Fractions, HashSet<int> Operators);

    // Counts a glyph's hint operators, subroutine calls, 16.16 operands and operators, following its subroutines.
    internal static Ops Count(CffFont cff, int glyph)
    {
        int stems = 0, masks = 0, calls = 0, fractions = 0, stemCount = 0;
        var operators = new HashSet<int>();
        var stack = new List<double>();
        int fd = cff.FontDictOf(glyph);
        int vsindex = cff.Privates[fd].VsIndex;
        bool ended = false;
        void Run(ReadOnlySpan<byte> code, int depth)
        {
            int i = 0;
            while (i < code.Length && !ended)
            {
                int b0 = code[i];
                if (b0 == 28)
                {
                    stack.Add((short)((code[i + 1] << 8) | code[i + 2]));
                    i += 3;
                    continue;
                }

                if (b0 >= 32)
                {
                    if (b0 <= 246)
                    {
                        stack.Add(b0 - 139);
                        i++;
                    }
                    else if (b0 <= 250)
                    {
                        stack.Add(((b0 - 247) * 256) + code[i + 1] + 108);
                        i += 2;
                    }
                    else if (b0 <= 254)
                    {
                        stack.Add((-(b0 - 251) * 256) - code[i + 1] - 108);
                        i += 2;
                    }
                    else
                    {
                        fractions++;
                        stack.Add(((code[i + 1] << 24) | (code[i + 2] << 16) | (code[i + 3] << 8) | code[i + 4]) / 65536.0);
                        i += 5;
                    }

                    continue;
                }

                int op = b0;
                i++;
                if (b0 == 12)
                {
                    op = 1200 + code[i++];
                }

                operators.Add(op);
                switch (op)
                {
                    case 1 or 3 or 18 or 23:
                        stems++;
                        stemCount += stack.Count / 2;
                        stack.Clear();
                        break;
                    case 19 or 20:
                        masks++;
                        stemCount += stack.Count / 2;
                        stack.Clear();
                        i += (stemCount + 7) / 8;
                        break;
                    case 10 or 29:
                        {
                            calls++;
                            bool global = op == 29;
                            CffIndex subrs = global ? cff.GlobalSubrs : cff.Privates[fd].LocalSubrs!.Value;
                            int index = (int)stack[^1] + CffFont.Bias(subrs.Count);
                            stack.RemoveAt(stack.Count - 1);
                            Run(subrs[index], depth + 1);
                            break;
                        }

                    case 11:
                        return;
                    case 14 when !cff.IsCff2:
                        ended = true;
                        return;
                    case 15 when cff.IsCff2:
                        vsindex = (int)stack[^1];
                        stack.RemoveAt(stack.Count - 1);
                        break;
                    case 16 when cff.IsCff2:
                        {
                            int n = (int)stack[^1];
                            int k = cff.VariationStore!.RegionCountOf(vsindex);
                            stack.RemoveRange(stack.Count - 1 - (n * k), (n * k) + 1);
                            break;
                        }

                    default:
                        stack.Clear();
                        break;
                }
            }
        }

        Run(cff.CharStrings[glyph], 0);
        return new Ops(stems, masks, calls, fractions, operators);
    }
}
