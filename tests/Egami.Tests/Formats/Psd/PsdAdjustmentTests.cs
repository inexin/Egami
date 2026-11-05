using System.Buffers.Binary;

using Egami.Formats.Psd;

namespace Egami.Tests.Formats.Psd;

/// <summary>Adjustment layers' color changes on single colors.</summary>
public class PsdAdjustmentTests
{
    private static PsdAdjustment Of(string key, byte[] data, bool gray = false)
    {
        PsdLayerRecord record = new() { Info = new PsdLayer("Adjustment", default, 1, "norm", null, true, false, PsdLayerKind.Layer, 0) };
        record.Keys.Add(key);
        record.Blocks[key] = data;
        return PsdAdjustment.Of(record, gray, EgamiContext.Default)!;
    }

    /// <summary>A hue/saturation block: version 2, colorize flag, colorization, master, and six neutral ranges.</summary>
    private static byte[] HueSaturation(bool colorize, short[] colorization, short[] master)
    {
        byte[] data = new byte[4 + 12 + (6 * 14)];
        BinaryPrimitives.WriteInt16BigEndian(data, 2);
        data[2] = colorize ? (byte)1 : (byte)0;
        for (int i = 0; i < 3; i++)
        {
            BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(4 + (2 * i)), colorization[i]);
            BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(10 + (2 * i)), master[i]);
        }

        return data;
    }

    [Fact]
    public void HueTurnsAndLightnessMixesTowardWhiteOrBlack()
    {
        (float r, float g, float b) = (1, 0, 0);
        Of("hue2", HueSaturation(false, [0, 0, 0], [180, 0, 0])).Apply(ref r, ref g, ref b);
        Assert.Equal((0f, 1f, 1f), (r, g, b), Near);

        (r, g, b) = (0.2f, 0.4f, 0.6f);
        Of("hue2", HueSaturation(false, [0, 0, 0], [0, 0, 50])).Apply(ref r, ref g, ref b);
        Assert.Equal((0.6f, 0.7f, 0.8f), (r, g, b), Near);

        (r, g, b) = (0.5f, 0.5f, 0.5f);
        Of("hue2", HueSaturation(true, [240, 100, 0], [0, 0, 0])).Apply(ref r, ref g, ref b);
        Assert.Equal((0f, 0f, 1f), (r, g, b), Near);
    }

    private static bool Near((float, float, float) x, (float, float, float) y) =>
        Math.Abs(x.Item1 - y.Item1) + Math.Abs(x.Item2 - y.Item2) + Math.Abs(x.Item3 - y.Item3) < 1e-5;

    [Fact]
    public void LegacyBrightnessShiftsAndContrastScalesAboutTheMean()
    {
        byte[] brit = new byte[8];
        BinaryPrimitives.WriteInt16BigEndian(brit, 51);
        BinaryPrimitives.WriteInt16BigEndian(brit.AsSpan(2), 50);
        BinaryPrimitives.WriteInt16BigEndian(brit.AsSpan(4), 127);
        (float r, float g, float b) = (0.4f, 0.5f, 0.6f);
        Of("brit", brit).Apply(ref r, ref g, ref b);

        // (v + 0.2 − m)·2 + m with m = 127/255.
        float m = 127 / 255f;
        Assert.Equal(((0.6f - m) * 2) + m, r, 1e-5f);
        Assert.Equal(1f, b);
    }

    [Fact]
    public void InvertAndUnknownBlocks()
    {
        (float r, float g, float b) = (0.25f, 0.5f, 1);
        Of("nvrt", []).Apply(ref r, ref g, ref b);
        Assert.Equal((0.75f, 0.5f, 0f), (r, g, b));
        PsdLayerRecord record = new() { Info = new PsdLayer("Mixer", default, 1, "norm", null, true, false, PsdLayerKind.Layer, 0) };
        record.Keys.Add("mixr");
        Assert.Null(PsdAdjustment.Of(record, false, EgamiContext.Default));
    }

    private static (float, float, float) Applied(PsdAdjustment adjustment, float r, float g, float b)
    {
        adjustment.Apply(ref r, ref g, ref b);
        return (r, g, b);
    }

    private static byte[] Shorts(params int[] values)
    {
        byte[] data = new byte[2 * values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(2 * i), (short)values[i]);
        }

        return data;
    }

    [Fact]
    public void PosterizeAndThreshold()
    {
        // 77 of 255 falls in the second of four levels: 1/3.
        Assert.Equal((1 / 3f, 0f, 1f), Applied(Of("post", Shorts(4, 0)), 77 / 255f, 0, 1), Near);
        Assert.Equal((1f, 1f, 1f), Applied(Of("thrs", Shorts(128, 0)), 0.6f, 0.6f, 0.6f));
        Assert.Equal((0f, 0f, 0f), Applied(Of("thrs", Shorts(128, 0)), 0.4f, 0.6f, 0.2f));
    }

    [Fact]
    public void ExposureDoublesLinearLight()
    {
        byte[] data = new byte[16];
        BinaryPrimitives.WriteInt16BigEndian(data, 1);
        BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(2), 1);
        BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(10), 1);
        (float r, float g, float b) = Applied(Of("expA", data), 0.5f, 0, 1);
        double linear = Math.Pow((0.5 + 0.055) / 1.055, 2.4) * 2;
        Assert.Equal((1.055 * Math.Pow(linear, 1 / 2.4)) - 0.055, r, 2e-3);
        Assert.Equal((0f, 1f), (g, b));
    }

    [Fact]
    public void ChannelMixerSwapsChannelsAndAddsTheConstant()
    {
        // Red from green, green from red, blue from blue plus 10 %.
        byte[] data = Shorts(1, 0, 0, 100, 0, 0, 0, 100, 0, 0, 0, 0, 0, 0, 100, 0, 10, 0, 0, 0, 0, 0);
        Assert.Equal((0.5f, 0.2f, 0.4f), Applied(Of("mixr", data), 0.2f, 0.5f, 0.3f), Near);
    }

    [Fact]
    public void ColorBalanceAndPhotoFilterAtRestChangeNothing()
    {
        byte[] balance = [.. Shorts(0, 0, 0, 0, 0, 0, 0, 0, 0), 1, 0];
        Assert.Equal((0.2f, 0.5f, 0.7f), Applied(Of("blnc", balance), 0.2f, 0.5f, 0.7f), Near);

        // Version 2, an RGB orange, density 0 %.
        byte[] filter = [.. Shorts(2, 0, -1, 32768, 0, 0), 0, 0, 0, 0, 1, 0];
        Assert.Equal((0.2f, 0.5f, 0.7f), Applied(Of("phfl", filter), 0.2f, 0.5f, 0.7f), Near);
    }

    [Fact]
    public void PhotoFilterKeepsLuminosityWhenAsked()
    {
        byte[] filter = [.. Shorts(2, 0, -1, 32768, 0, 0), 0, 0, 0, 50, 1, 0];
        (float r, float g, float b) = Applied(Of("phfl", filter), 0.4f, 0.5f, 0.6f);
        Assert.Equal((0.3f * 0.4f) + (0.59f * 0.5f) + (0.11f * 0.6f), (0.3f * r) + (0.59f * g) + (0.11f * b), 1e-4f);
        Assert.True(r > g && g > b);
    }

    [Fact]
    public void BlackAndWhiteWeighsPrimariesAndSecondaries()
    {
        byte[] data = PsdDescriptorWriter.Versioned(new(
            "null", ("Rd  ", 40), ("Yllw", 60), ("Grn ", 40), ("Cyn ", 60), ("Bl  ", 20), ("Mgnt", 80), ("useTint", false)));
        PsdAdjustment adjustment = Of("blwh", data);
        Assert.Equal((0.4f, 0.4f, 0.4f), Applied(adjustment, 1, 0, 0), Near);
        Assert.Equal((0.6f, 0.6f, 0.6f), Applied(adjustment, 1, 1, 0), Near);
        Assert.Equal((0.5f, 0.5f, 0.5f), Applied(adjustment, 0.5f, 0.5f, 0.5f), Near);
    }

    [Fact]
    public void SelectiveColorAddsInkToItsClass()
    {
        // Absolute; reds get 100 % more cyan; the other classes nothing.
        int[] values = new int[2 + 40];
        values[0] = 1;
        values[1] = 1;
        values[2 + 4] = 100;
        PsdAdjustment adjustment = Of("selc", Shorts(values));
        Assert.Equal((0f, 0f, 0f), Applied(adjustment, 1, 0, 0), Near);
        Assert.Equal((0f, 1f, 0f), Applied(adjustment, 0, 1, 0), Near);
    }

    [Fact]
    public void GradientMapMapsLuminosityAlongTheGradient()
    {
        PsdAdjustment map = Of("grdm", GradientMap(false));
        Assert.Equal((0f, 0f, 0f), Applied(map, 0, 0, 0), Near);
        Assert.Equal((1f, 0f, 0f), Applied(map, 1, 1, 1), Near);
        (float r, _, _) = Applied(map, 0.5f, 0.5f, 0.5f);
        Assert.InRange(r, 0.45f, 0.55f);
        Assert.Equal((1f, 0f, 0f), Applied(Of("grdm", GradientMap(true)), 0, 0, 0), Near);
    }

    /// <summary>A version 1 gradient map from black to red, full smoothness.</summary>
    private static byte[] GradientMap(bool reverse)
    {
        using MemoryStream stream = new();
        void U16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); stream.Write(b); }
        void U32(long v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v); stream.Write(b); }
        U16(1);
        stream.Write([reverse ? (byte)1 : (byte)0, 0]);
        U32(0);
        U16(2);
        foreach ((int location, int red) in new[] { (0, 0), (4096, 65535) })
        {
            U32(location);
            U32(50);
            U16(0);
            U16(red);
            U16(0);
            U16(0);
            U16(0);
            U16(0);
        }

        U16(2);
        foreach (int location in new[] { 0, 4096 })
        {
            U32(location);
            U32(50);
            U16(255);
        }

        U16(2);
        U16(4096);
        U16(32);
        U16(0);
        stream.Write(new byte[16]);
        return stream.ToArray();
    }

    [Fact]
    public void ColorLookupReadsItsCubeTable()
    {
        // A 2-point table that swaps red and blue: red runs fastest.
        System.Text.StringBuilder cube = new("LUT_3D_SIZE 2\n");
        for (int b = 0; b < 2; b++)
        {
            for (int g = 0; g < 2; g++)
            {
                for (int r = 0; r < 2; r++)
                {
                    cube.Append(System.Globalization.CultureInfo.InvariantCulture, $"{b} {g} {r}\n");
                }
            }
        }

        byte[] data = PsdDescriptorWriter.Versioned(
            new(
                "null",
                ("lookupType", new PsdDescriptorWriter.EnumValue("colorLookupType", "3DLUT")),
                ("LUTFormat", new PsdDescriptorWriter.EnumValue("LUTFormatType", "LUTFormatCUBE")),
                ("LUT3DFileData", System.Text.Encoding.ASCII.GetBytes(cube.ToString()))),
            2);
        Assert.Equal((0.25f, 0.5f, 1f), Applied(Of("clrL", data), 1, 0.5f, 0.25f), Near);

        // No table changes nothing; an abstract profile is not applied.
        Assert.Equal((1f, 0.5f, 0.25f), Applied(Of("clrL", PsdDescriptorWriter.Versioned(new("null"), 2)), 1, 0.5f, 0.25f));
        byte[] profile = PsdDescriptorWriter.Versioned(new("null", ("lookupType", new PsdDescriptorWriter.EnumValue("colorLookupType", "abstractProfile"))), 2);
        Assert.Null(Of("clrL", profile));
    }

    [Fact]
    public void VibranceLeavesGraysAndSaturatesColors()
    {
        byte[] data = PsdDescriptorWriter.Versioned(new("null", ("vibrance", 50), ("Strt", 0)));
        PsdAdjustment adjustment = Of("vibA", data);
        Assert.Equal((0.5f, 0.5f, 0.5f), Applied(adjustment, 0.5f, 0.5f, 0.5f), Near);
        (float r, float g, _) = Applied(adjustment, 0.6f, 0.5f, 0.5f);
        Assert.True(r - g > 0.1f);
    }
}
