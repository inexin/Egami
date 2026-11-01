using System.Security.Cryptography;

using Egami.Colors;
using Egami.Formats.Av1;
using Egami.Formats.Avif;
using Egami.Formats.Heif;
using Egami.Metadata;

namespace Egami.Tests.Formats.Avif;

/// <summary>
/// The AVIF item layout, held byte for byte to the files it wrote before HEIC came to share it (HEIC encoder design, task 10):
/// one fixed AV1 stream laid out whole, with alpha, as a grid, with every kind of metadata, a gain map and layers. The stream comes
/// from a committed file, so changes to the AV1 encoder leave these files alone.
/// </summary>
public class AvifLayoutTests
{
    /// <summary>The SHA-256 of each layout's file, as the AVIF writer laid it out before the HEIF layout was shared.</summary>
    private static readonly Dictionary<string, string> Expected = new()
    {
        ["plain"] = "DC7BA1F72510A877CFCD9229ACBB8FABE035F24F1CD5A5AF7925F477F3215E7D",
        ["alpha"] = "85A6D9D06B4FE5EA145831E1A456CB062FC25489A9A3C14EE56B4E0160EA9D79",
        ["grid"] = "AC53E45E4C50C27D4FF23C80170DF915898056047D2C8B141090B37C6BE48F7A",
        ["metadata"] = "92BC45F5DD3CD87EB9D55BE944663BE8F6C39D637C5E6EB922E7F027B96E6E04",
        ["gainmap"] = "A8D8706D266D460BA20570A415038DCA4C89AD316F36D84B7964E9F6603683C4",
        ["layers"] = "165CFF518A588D7235E4FA4FA77DE41A5AD011793BFDE81B38BC0A68BD6CDEC6",
    };

    [Theory]
    [InlineData("plain")]
    [InlineData("alpha")]
    [InlineData("grid")]
    [InlineData("metadata")]
    [InlineData("gainmap")]
    [InlineData("layers")]
    public void LayoutIsUnchanged(string layout)
    {
        Assert.Equal(Expected[layout], Hash(Write(layout)));
    }

    internal static byte[] Write(string layout)
    {
        Av1Encoder cell = Cell();
        AvifWriter.CodedImage one = new([cell], 1, 1, 64, 48);
        AvifWriter.CodedImage grid = new([cell, cell, cell, cell], 2, 2, 64, 48);
        HeifImageMetadata metadata = new(new byte[] { 1, 2, 3, 4, 5 }, new byte[] { 0x4D, 0x4D, 0, 42, 0, 0, 0, 8 }, new byte[] { 0x3C, 0x78, 0x3E },
            ImageOrientation.Rotate90, new HdrMetadata { MaxContentLightLevel = 1000, MaxFrameAverageLightLevel = 400, MasteringPrimaries = ColorPrimaries.Rec2020, MasteringMaxLuminance = 1000, MasteringMinLuminance = 0.005 });
        using MemoryStream output = new();
        switch (layout)
        {
            case "plain":
                AvifWriter.WriteFile(output, 64, 48, one, null, 1, 13, 6);
                break;
            case "alpha":
                AvifWriter.WriteFile(output, 64, 48, one, one, 1, 13, 6);
                break;
            case "grid":
                AvifWriter.WriteFile(output, 120, 90, grid, grid, 9, 16, 9, metadata with { Orientation = ImageOrientation.Transpose });
                break;
            case "metadata":
                AvifWriter.WriteFile(output, 64, 48, one, null, 2, 2, 6, metadata);
                break;
            case "gainmap":
                AvifWriter.WriteFile(output, 64, 48, one, null, 1, 13, 6, metadata, new AvifWriter.GainMapItem(cell, 64, 48, new GainMapInfo { HdrCapacityMax = 2 }, 1, 16));
                break;
            default:
                AvifWriter.WriteFile(output, 64, 48, one with { LayerSizes = [10, 20] }, null, 1, 13, 6);
                break;
        }

        return output.ToArray();
    }

    /// <summary>An AV1 stream and its sequence header from a committed AVIF file's color item.</summary>
    private static Av1Encoder Cell()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "images", "avif", "duck-420-q60.avif"));
        HeifFile heif = HeifFile.Parse(file, "AVIF");
        HeifItem item = heif.Items.First(i => i.Id == heif.PrimaryItemId);
        byte[] data = heif.ItemData(item, SafetyLimits.Default).ToArray();

        // The sequence header OBU: each OBU's header byte (and an extension byte), then a leb128 size and its payload.
        int at = 0, size;
        while (true)
        {
            int type = (data[at] >> 3) & 15;
            at += (data[at] & 4) != 0 ? 2 : 1;
            size = 0;
            for (int shift = 0; ; shift += 7)
            {
                byte b = data[at++];
                size |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    break;
                }
            }

            if (type == 1)
            {
                break;
            }

            at += size;
        }

        Av1SequenceHeader sequence = Av1SequenceHeader.Parse(data.AsSpan(at, size), "AVIF");
        return Av1Encoder.FromStream(data, sequence, null!);
    }

    private static string Hash(byte[] file) => Convert.ToHexString(SHA256.HashData(file));
}
