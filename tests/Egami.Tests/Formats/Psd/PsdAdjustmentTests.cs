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
        return PsdAdjustment.Of(record, gray)!;
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
        Assert.Null(PsdAdjustment.Of(record, false));
    }
}
