using System.Text;

using Egami.Formats.Eps;
using Egami.Formats.Tiff;
using Egami.Pixels;
using Egami.Tests.Memory;

namespace Egami.Tests.Formats.Eps;

/// <summary>
/// EPS allocation budgets: a Load of Photoshop's image (binary and hex, interleaved and by rows) and of an EPSI preview allocate
/// the same for 10 rows as for 3,000, since the reader keeps one row of buffers and the pixels come from the memory source; a
/// TIFF preview adds the same over the TIFF reader's own allocations for both.
/// </summary>
[Trait("Category", AllocationMeter.Category)]
public class EpsAllocationTests
{
    public static TheoryData<string> Kinds() => AllocationMeter.Rows(new TheoryData<string> { "photoshop-binary", "photoshop-hex", "photoshop-lines", "photoshop-cmyk", "epsi", "tiff" });

    [Theory]
    [MemberData(nameof(Kinds))]
    public void LoadsAllocateTheSameForTenRowsAndThousands(string kind)
    {
        AllocationMeter.SkipUnlessOnDemand();
        byte[] small = File(kind, 300, 10), large = File(kind, 300, 3000);
        long few = AllocationMeter.Fewest(() => Raster.Load(small).Dispose());
        long many = AllocationMeter.Fewest(() => Raster.Load(large).Dispose());
        if (kind == "tiff")
        {
            // The TIFF reader keeps a table of its strips, which grows with the rows; what EPS adds on top must not.
            byte[] smallTiff = EpsFile.Load(small).TiffPreview.ToArray(), largeTiff = EpsFile.Load(large).TiffPreview.ToArray();
            few -= AllocationMeter.Fewest(() => Raster.Load(smallTiff).Dispose());
            many -= AllocationMeter.Fewest(() => Raster.Load(largeTiff).Dispose());
        }

        Assert.True(many - few < 1024, $"{kind}: 3,000 rows allocated {many:N0} bytes against {few:N0} for 10 rows.");
    }

    private static byte[] File(string kind, int width, int height)
    {
        byte[] Bytes(int count) => [.. Enumerable.Range(0, count).Select(i => (byte)((i * 31) + (i / width)))];
        string Hex(byte[] data)
        {
            StringBuilder text = new();
            for (int i = 0; i < data.Length; i += 32)
            {
                text.Append(Convert.ToHexString(data, i, Math.Min(32, data.Length - i))).Append('\n');
            }

            return text.ToString();
        }

        switch (kind)
        {
            case "photoshop-binary":
                return EpsPhotoshopTests.Build($"{width} {height} 8 3 0 1 1", Bytes(width * height * 3));
            case "photoshop-hex":
                return EpsPhotoshopTests.Build($"{width} {height} 8 3 0 1 2", Encoding.ASCII.GetBytes(Hex(Bytes(width * height * 3))));
            case "photoshop-lines":
                return EpsPhotoshopTests.Build($"{width} {height} 8 3 0 {width} 1", Bytes(width * height * 3));
            case "photoshop-cmyk":
                return EpsPhotoshopTests.Build($"{width} {height} 8 4 0 1 1", Bytes(width * height * 4));
            case "epsi":
                {
                    string preview = string.Concat(Hex(Bytes(width * height)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => "%" + line + "\n"));
                    return Encoding.ASCII.GetBytes($"%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 0 0 {width} {height}\n%%EndComments\n%%BeginPreview: {width} {height} 8 {height}\n{preview}%%EndPreview\nshowpage\n%%EOF\n");
                }

            default:
                {
                    using Raster<Rgb8> picture = Raster.Create(width, height, new Rgb8(10, 20, 30));
                    using MemoryStream tiff = new();
                    picture.Save(tiff, TiffFormat.Info);
                    return EpsTests.Binary(Encoding.ASCII.GetBytes($"%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 0 0 {width} {height}\n%%EndComments\nshowpage\n%%EOF\n"), tiff: tiff.ToArray());
                }
        }
    }
}
