using Egami.Formats.Extended.Dicom;
using Egami.Formats.Extended.Tests.JpegLs;

namespace Egami.Formats.Extended.Tests.Dicom;

/// <summary>
/// The pixel-data seeds of the dicom fuzz target (tests/images/fuzz/dicom-seeds/pixels-*.dcm): each decodes every way. With
/// EGAMI_WRITE_DICOM_SEEDS set to the seed folder, the test writes them again from <see cref="DicomImageBuilder"/> first.
/// </summary>
public class DicomSeedTests
{
    private static readonly EgamiContext Context = new EgamiContext().WithDicom();

    public static TheoryData<string> Seeds => ["pixels-window.dcm", "pixels-bits.dcm", "pixels-rle.dcm", "pixels-ybr.dcm", "pixels-palette.dcm", "pixels-jpegls.dcm"];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void DecodesEveryWay(string name)
    {
        Dictionary<string, byte[]> seeds = Write();
        if (Environment.GetEnvironmentVariable("EGAMI_WRITE_DICOM_SEEDS") is string folder)
        {
            File.WriteAllBytes(Path.Combine(folder, name), seeds[name]);
        }

        byte[] data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "images", "dicom", name));
        Assert.Equal(seeds[name], data);
        foreach (DicomValues values in Enum.GetValues<DicomValues>())
        {
            using RasterList all = Raster.LoadAll(data, Context, new DicomReaderOptions { Values = values, Recovery = RecoveryMode.Strict });
            Assert.NotEmpty(all);
        }
    }

    private static Dictionary<string, byte[]> Write()
    {
        ushort[] ramp = [.. Enumerable.Range(0, 64).Select(i => (ushort)(i * 61))];
        uint[] rgb = [.. Enumerable.Range(0, 48).Select(i => (uint)(i * 5 % 256))];
        return new()
        {
            ["pixels-window.dcm"] = new DicomImageBuilder(8, 8, 16, 12, 13, signed: true)
                .Text(0x0028, 0x1050, "DS", "40\\400").Text(0x0028, 0x1051, "DS", "80\\2000")
                .Text(0x0028, 0x1052, "DS", "-1024").Text(0x0028, 0x1053, "DS", "1").Text(0x2050, 0x0020, "CS", "INVERSE")
                .Native16([.. ramp.Select(v => (ushort)(v << 2))]).File("1.2.840.10008.1.2.1"),
            ["pixels-bits.dcm"] = new DicomImageBuilder(5, 5, 1, frames: 3, photometric: "MONOCHROME1")
                .Native([0x55, 0xAA, 0x0F, 0xF0, 0x33, 0xCC, 0x99, 0x66, 0x01, 0x80], "OB").File("1.2.840.10008.1.2"),
            ["pixels-rle.dcm"] = new DicomImageBuilder(4, 4, 8, samples: 3, photometric: "RGB", frames: 2)
                .Encapsulated([], DicomImageBuilder.Rle(rgb, 3, 1), DicomImageBuilder.Rle([.. rgb.Reverse()], 3, 1)).File("1.2.840.10008.1.2.5"),
            ["pixels-ybr.dcm"] = new DicomImageBuilder(2, 4, 8, samples: 3, photometric: "YBR_FULL_422")
                .Native([16, 200, 128, 128, 90, 91, 40, 220, 235, 16, 128, 128, 1, 2, 255, 0], "OB").File("1.2.840.10008.1.2.2"),
            ["pixels-palette.dcm"] = new DicomImageBuilder(2, 4, 8, photometric: "PALETTE COLOR")
                .UInt16Lut(0x1101, [8, 0, 16]).UInt16Lut(0x1102, [8, 0, 16]).UInt16Lut(0x1103, [8, 0, 16])
                .Add(0x0028, 0x1201, b => b.UInt16(0x0028, 0x1201, "OW", 0, 9000, 18000, 27000, 36000, 45000, 54000, 65535))
                .Add(0x0028, 0x1222, b => b.UInt16(0x0028, 0x1222, "OW", 0, 1, 0, 1, 7, 65535))
                .Add(0x0028, 0x1223, b => b.UInt16(0x0028, 0x1223, "OW", 0, 2, 5, 6, 2, 1, 0, 0))
                .Native([0, 1, 2, 3, 4, 5, 6, 7], "OB").File("1.2.840.10008.1.2.1"),
            ["pixels-jpegls.dcm"] = new DicomImageBuilder(6, 6, 16, 12, frames: 2)
                .Encapsulated([], JpegLsTesting.Encode([.. ramp.Take(36)], 1, 6, 12), JpegLsTesting.Encode([.. ramp.Skip(20).Take(36)], 1, 6, 12))
                .File("1.2.840.10008.1.2.4.80"),
        };
    }
}
