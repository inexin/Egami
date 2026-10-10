using System.Text.Json;

using Egami.Colors;

namespace Egami.Tests.Colors;

/// <summary>Phase 2.1.2 task 3: the ICC parser against what Little CMS reads from the same profiles, and against damage.</summary>
public class IccParserTests
{
    public static TheoryData<string> Files() => [.. IccReferences.Files];

    [Theory]
    [MemberData(nameof(Files))]
    public void HeaderAndTagsMatchLittleCms(string file)
    {
        JsonElement lcms = IccReferences.Profile(file).GetProperty("lcms");
        IccData icc = IccData.Parse(IccReferences.Bytes(file));
        Assert.False(icc.IsDamaged, string.Join(" ", icc.Warnings));
        Assert.Equal(lcms.GetProperty("version").GetDouble(), icc.Version.Major + (icc.Version.Minor / 10.0), 6);
        Assert.Equal(lcms.GetProperty("class").GetString(), icc.ProfileClass switch
        {
            IccProfileClass.Input => "scnr",
            IccProfileClass.Display => "mntr",
            IccProfileClass.Output => "prtr",
            _ => "?",
        });
        Assert.Equal(lcms.GetProperty("colorSpace").GetString()!.TrimEnd(), icc.DataSpace);
        Assert.Equal(lcms.GetProperty("pcs").GetString()!.TrimEnd(), icc.ConnectionSpace);
        Assert.Equal(lcms.GetProperty("headerIntent").GetInt32(), (int)icc.RenderingIntent);
        Assert.Equal(lcms.GetProperty("description").GetString(), icc.Description);
        Assert.Equal(lcms.GetProperty("tags").EnumerateArray().Select(t => t.GetString()!.TrimEnd()), icc.Tags.Select(t => t.Signature));
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void ModelMatchesLittleCms(string file)
    {
        JsonElement lcms = IccReferences.Profile(file).GetProperty("lcms");
        ColorProfile profile = ColorProfile.FromIcc(IccReferences.Bytes(file));
        IccModel model = profile.IccModel!;
        Assert.Equal(lcms.GetProperty("matrixShaper").GetInt32() == 1, model.Curves is not null);

        // Little CMS's cmsIsCLUT looks up A2B0, A2B1, A2B2, and A2B1 again for absolute colorimetric.
        int[] clut = [.. lcms.GetProperty("clutAsInput").EnumerateArray().Select(e => e.GetInt32())];
        for (int intent = 0; intent < 4; intent++)
        {
            Assert.Equal(clut[intent] == 1, model.DeviceToPcs[intent == 3 ? 1 : intent] is not null);
        }

        double[] white = [.. lcms.GetProperty("mediaWhite").EnumerateArray().Select(e => e.GetDouble())];
        Assert.True(Math.Abs(white[0] - model.MediaWhite.X) < 1e-6 && Math.Abs(white[1] - model.MediaWhite.Y) < 1e-6 && Math.Abs(white[2] - model.MediaWhite.Z) < 1e-6, $"{model.MediaWhite}");
        Assert.Same(profile.Icc, profile.IccModel!.Data);
        Assert.Equal(lcms.GetProperty("description").GetString(), profile.Name);
    }

    // The "micro" profiles approximate their curve with a short table, and AdobeCompat-v4 adds a linear toe, so they are close
    // to the built-ins but not within IsEquivalentTo's 1e-4.
    public static TheoryData<string, string> BuiltIns() => new()
    {
        { "compact/sRGB-v4.icc", "sRGB" },
        { "compact/sRGB-v2-magic.icc", "sRGB" },
        { "compact/DisplayP3-v4.icc", "Display P3" },
        { "compact/ProPhoto-v4.icc", "ProPhoto RGB" },
        { "compact/AdobeCompat-v2.icc", "Adobe RGB (1998)" },
        { "compact/Rec2020-v4.icc", "Rec. 2020" },
        { "compact/scRGB-v2.icc", "Linear sRGB" },
        { "compact/sGrey-v4.icc", "Gray sRGB" },
    };

    [Theory]
    [MemberData(nameof(BuiltIns))]
    public void CompactProfilesAreTheBuiltIns(string file, string builtIn)
    {
        ColorProfile icc = ColorProfile.FromIcc(IccReferences.Bytes(file));
        ColorProfile expected = new[] { ColorProfile.Srgb, ColorProfile.LinearSrgb, ColorProfile.DisplayP3, ColorProfile.AdobeRgb, ColorProfile.ProPhotoRgb, ColorProfile.Rec2020, ColorProfile.GraySrgb }
            .Single(p => p.Name == builtIn);
        Assert.True(icc.IsEquivalentTo(expected), $"{file}: white {icc.White}, primaries {icc.Primaries}, curve {icc.Transfer}");
        Assert.True(expected.IsEquivalentTo(icc));
        Assert.False(icc.IsEquivalentTo(expected == ColorProfile.Srgb ? ColorProfile.DisplayP3 : ColorProfile.Srgb));
    }

    [Fact]
    public void TableProfilesKeepTheirStages()
    {
        IccModel mab = ColorProfile.FromIcc(IccReferences.Bytes("synthetic/rgb-mab-v4.icc")).IccModel!;
        Assert.False(mab.IsMatrixShaper);
        Assert.True(mab.PcsIsLab);
        IccLut a2b0 = mab.DeviceToPcs[0]!;
        Assert.Equal(IccLutType.AToB, a2b0.Type);
        Assert.Equal(["Curves", "Lookup", "Curves", "Matrix"], a2b0.Stages.Select(s => s.GetType().Name));   // B curves are identity
        Assert.Equal([9, 11, 7], ((IccStage.Lookup)a2b0.Stages[1]).Table.Grid);
        Assert.Equal(0.004, ((IccStage.Matrix)a2b0.Stages[3]).Offsets[1], 4);
        Assert.Null(mab.DeviceToPcs[2]);

        IccModel cmyk = ColorProfile.FromIcc(IccReferences.Bytes("synthetic/cmyk-lut16-v2.icc")).IccModel!;
        Assert.Equal(ColorModel.Cmyk, cmyk.Model);
        Assert.Equal(IccLutType.Lut16, cmyk.PcsToDevice[2]!.Type);
        Assert.Equal([9, 9, 9, 9], ((IccStage.Lookup)cmyk.DeviceToPcs[0]!.Stages[1]).Table.Grid);

        IccModel lut8 = ColorProfile.FromIcc(IccReferences.Bytes("synthetic/rgb-lut8-v2.icc")).IccModel!;
        Assert.IsType<IccStage.Matrix>(lut8.PcsToDevice[0]!.Stages[0]);   // the B2A matrix is not identity
        Assert.IsType<IccStage.Curves>(lut8.DeviceToPcs[0]!.Stages[0]);    // the A2B one is, so it is left out

        ColorProfile table = ColorProfile.FromIcc(IccReferences.Bytes("synthetic/rgb-lut16-v2.icc"));
        Assert.Null(table.Primaries);
        Assert.Null(table.Transfer);
        Assert.True(table.IsEquivalentTo(ColorProfile.FromIcc(IccReferences.Bytes("synthetic/rgb-lut16-v2.icc"))));
        Assert.False(table.IsEquivalentTo(ColorProfile.Srgb));
        Assert.Equal(Chromaticity.D65.X, table.White.X, 3);   // a version 2 input profile's media white is its adopted white
    }

    [Fact]
    public void DifferentCurvesLeaveTransferUnset()
    {
        ColorProfile profile = ColorProfile.FromIcc(IccReferences.Bytes("synthetic/matrix-para-v4.icc"));
        Assert.NotNull(profile.Primaries);
        Assert.Null(profile.Transfer);
        Assert.Equal(0.331, profile.White.X, 3);   // the chad tag adapts from about D55
    }

    [Fact]
    public void ParametricCurvesFollowIcc()
    {
        IccCurve type1 = IccCurve.Read(IccBuilder.Para(1, 2.0, 2.0, -1.0), "test").Curve;
        Assert.Equal(0, type1.Evaluate(0.25));                    // below −b/a = 0.5
        Assert.Equal(0.25, type1.Evaluate(0.75), 10);             // (2·0.75 − 1)²
        IccCurve type2 = IccCurve.Read(IccBuilder.Para(2, 1.0, 1.0, -0.5, 0.1), "test").Curve;
        Assert.Equal(0.1, type2.Evaluate(0.25), 4);
        Assert.Equal(0.6, type2.Evaluate(1.0), 4);
        IccCurve table = IccCurve.FromTable([0f, 0.5f, 0.25f]);
        Assert.Equal(0.375, table.Evaluate(0.75), 6);
        Assert.Equal(0.25, table.Evaluate(2), 6);
        Assert.Null(table.ToTransferFunction());                  // it goes down
        // curv stores a gamma as u8Fixed8, so 2.2 is kept as 563/256.
        Assert.Equal(TransferFunction.Gamma(563 / 256.0), IccCurve.Read(IccBuilder.Gamma(2.2), "test").Curve.ToTransferFunction());
        Assert.True(IccCurve.Read(IccBuilder.Para(3, 2.4, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045), "test").Curve.ToTransferFunction()!.IsCloseTo(TransferFunction.Srgb));
    }

    [Fact]
    public void CurvesInvert()
    {
        IccCurve[] curves =
        [
            IccCurve.Read(IccBuilder.Gamma(2.2), "test").Curve,
            IccCurve.Read(IccBuilder.Para(1, 2.0, 1.1, -0.1), "test").Curve,
            IccCurve.Read(IccBuilder.Para(2, 2.0, 1.0, 0.0, 0.05), "test").Curve,
            IccCurve.Read(IccBuilder.Para(3, 2.4, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045), "test").Curve,
            IccCurve.Read(IccBuilder.Para(4, 2.4, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045, 0.01, 0.002), "test").Curve,
            IccCurve.FromTable([.. Enumerable.Range(0, 1024).Select(i => (float)Math.Pow(i / 1023.0, 1.8))]),
        ];
        foreach (IccCurve curve in curves)
        {
            for (int i = 0; i <= 100; i++)
            {
                double x = 0.1 + (i * 0.009);   // where every curve above is rising
                Assert.Equal(x, curve.EvaluateInverse(curve.Evaluate(x)), 5);
            }
        }

        IccCurve flat = IccCurve.FromTable([0f, 0f, 0f, 0.5f, 1f]);
        Assert.Equal(0.5, flat.EvaluateInverse(0), 6);           // the last segment holding 0, as Little CMS reverses it
        Assert.Equal(0.625, flat.EvaluateInverse(0.25), 6);
        Assert.Equal(1, flat.EvaluateInverse(2), 6);
        IccCurve down = IccCurve.FromTable([1f, 0.5f, 0f]);
        Assert.Equal(0.25, down.EvaluateInverse(0.75), 6);
    }

    [Fact]
    public void CicpNamesTheEncoding()
    {
        byte[] bytes = IccBuilder.Build("mntr", "RGB ", "XYZ ", 0x04400000, [.. IccBuilder.SrgbTags(), ("cicp", IccBuilder.Cicp(9, 16))]);
        ColorProfile profile = ColorProfile.FromIcc(bytes);
        Assert.True(profile.IsHdr);
        Assert.True(profile.IsEquivalentTo(ColorProfile.Rec2100Pq));
        Assert.Equal(new ColorCodePoints(9, 16, 0, true), profile.Icc!.CodePoints);
        Assert.Equal(new Version(4, 4, 0), profile.Icc.Version);
    }

    [Fact]
    public void HeaderFieldsAndText()
    {
        byte[] bytes = IccBuilder.Build("mntr", "RGB ", "XYZ ", 0x04300000,
            [.. IccBuilder.SrgbTags(), ("desc", IccBuilder.Mluc(("de", "DE", "Bildschirm"), ("en", "US", "Screen"))), ("cprt", IccBuilder.Mluc(("fr", "FR", "Domaine public")))]);
        IccData icc = IccData.Parse(bytes);
        Assert.Equal("Screen", icc.Description);
        Assert.Equal("Domaine public", icc.Copyright);
        Assert.Equal(new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc), icc.Created);
        Assert.Equal(ColorModel.Rgb, icc.Model);
        Assert.True(icc.TryGetTag("rTRC", out IccTag trc));
        Assert.Equal("para", trc.Type);
        Assert.True(icc.TryGetTag("A2B0 ", out _) == false);
        Assert.Equal(bytes, icc.ToBytes().ToArray());
        Assert.Contains("Screen", icc.ToString(), StringComparison.Ordinal);
        Assert.Equal("Screen", ColorProfile.FromIcc(icc).Name);
    }

    [Theory]
    [InlineData("link", "RGB ", "Lab ")]
    [InlineData("abst", "Lab ", "Lab ")]
    [InlineData("mntr", "Lab ", "XYZ ")]
    [InlineData("nmcl", "RGB ", "Lab ")]
    public void UnsupportedProfilesSaySo(string profileClass, string dataSpace, string pcs)
    {
        byte[] bytes = IccBuilder.Build(profileClass, dataSpace, pcs, 0x04300000, IccBuilder.SrgbTags());
        Assert.False(IccData.Parse(bytes).IsDamaged);
        Assert.Throws<UnsupportedFormatException>(() => ColorProfile.FromIcc(bytes));
    }

    [Fact]
    public void BrokenTagsSaySo()
    {
        (string, byte[])[] tags = IccBuilder.SrgbTags();
        tags[4] = ("rTRC", IccBuilder.Para(7, 2.2));
        CorruptDataException e = Assert.Throws<CorruptDataException>(() => ColorProfile.FromIcc(IccBuilder.Build("mntr", "RGB ", "XYZ ", 0x04300000, tags)));
        Assert.Contains("rTRC", e.Message, StringComparison.Ordinal);
        Assert.Throws<CorruptDataException>(() => ColorProfile.FromIcc(IccBuilder.Build("mntr", "RGB ", "XYZ ", 0x04300000, tags[..3])));
        Assert.Throws<CorruptDataException>(() => ColorProfile.FromIcc(IccBuilder.Build("prtr", "CMYK", "Lab ", 0x04300000, tags)));
    }

    [Fact]
    public void DamageIsReportedNotThrown()
    {
        byte[] good = IccReferences.Bytes("compact/sRGB-v4.icc");
        byte[] noMagic = (byte[])good.Clone();
        noMagic[36] = (byte)'x';
        Assert.Contains(IccData.Parse(noMagic).Warnings, w => w.Contains("acsp", StringComparison.Ordinal));
        byte[] badOffset = (byte[])good.Clone();
        badOffset[128 + 4 + 4] = 0x7F;
        IccData skipped = IccData.Parse(badOffset);
        Assert.True(skipped.IsDamaged);
        Assert.Equal(IccData.Parse(good).Tags.Count - 1, skipped.Tags.Count);
        Assert.True(IccData.Parse(good, SafetyLimits.Default with { MaxMetadataBytes = 100 }).IsDamaged);
        Assert.Empty(IccData.Parse(good, SafetyLimits.Default with { MaxMetadataBytes = 100 }).Tags);
        byte[] duplicate = IccBuilder.Build("mntr", "RGB ", "XYZ ", 0x04300000, [.. IccBuilder.SrgbTags(), ("rXYZ", IccBuilder.Xyz(1, 0, 0))]);
        Assert.Equal(0.4360747, ColorProfile.FromIcc(duplicate).IccModel!.Colorants!.Value[0, 0], 4);   // the first one counts
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void EveryTruncationIsHandled(string file)
    {
        byte[] bytes = IccReferences.Bytes(file);
        for (int length = 0; length < bytes.Length; length += Math.Max(1, bytes.Length / 400))
        {
            Survive(bytes.AsSpan(0, length).ToArray());
        }
    }

    [Fact]
    public void RandomDamageIsHandled()
    {
        Random random = new(2102);
        string[] files = [.. IccReferences.Files];
        for (int i = 0; i < 3000; i++)
        {
            byte[] bytes = IccReferences.Bytes(files[i % files.Length]);
            for (int edits = random.Next(1, 8); edits > 0; edits--)
            {
                int at = random.Next(bytes.Length);
                bytes[at] = random.Next(3) switch
                {
                    0 => (byte)random.Next(256),
                    1 => 0xFF,
                    _ => 0,
                };
            }

            Survive(bytes);
        }
    }

    private static void Survive(byte[] bytes)
    {
        IccData icc = IccData.Parse(bytes);
        try
        {
            _ = ColorProfile.FromIcc(icc);
        }
        catch (EgamiException)
        {
        }
    }
}
