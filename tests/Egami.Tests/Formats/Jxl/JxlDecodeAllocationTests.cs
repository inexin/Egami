using Egami.Formats.Jxl;
using Egami.Pixels;
using Egami.Tests.Memory;

namespace Egami.Tests.Formats.Jxl;

/// <summary>
/// The JPEG XL decoder holds its Modular channels, and for frames of several passes its coefficients, in managed arrays, and a
/// single-pass lossy frame keeps the strips beside its group seams, so what it allocates grows with the image;
/// these budgets cap the bytes per added pixel (roadmap 2.4.11) so that the growth cannot creep back up unnoticed. One thread,
/// so every allocation is on the calling thread where the meter counts it.
/// </summary>
[Trait("Category", AllocationMeter.Category)]
public class JxlDecodeAllocationTests
{
    private static readonly EgamiContext OneThread = EgamiContext.Default with { MaxParallelism = 1 };

    /// <summary>The settings the files are written with and their budgets in bytes per pixel, about a fifth above what each allocated when set.</summary>
    public static TheoryData<string, int> Budgets => AllocationMeter.Rows(new TheoryData<string, int>
    {
        { "d1e3", 3 }, // 2.0 when set, in the group pipeline (37.7 before it)
        { "d1e7", 3 }, // 2.4 (41.9)
        { "ll-e1", 69 }, // 57.5
        { "ll-e3", 31 }, // 26.1
        { "ll-e7", 32 }, // 27
    });

    [Theory]
    [MemberData(nameof(Budgets))]
    public void DecodingAllocatesWithinItsBudgetPerPixel(string setting, int budget)
    {
        AllocationMeter.SkipUnlessOnDemand();
        JxlWriterOptions options = setting.StartsWith("ll-e", StringComparison.Ordinal)
            ? new() { Lossless = true, Effort = int.Parse(setting[4..], System.Globalization.CultureInfo.InvariantCulture) }
            : new() { Lossless = false, Distance = 1f, Effort = int.Parse(setting[3..], System.Globalization.CultureInfo.InvariantCulture) };
        byte[] small = Encode(128, options);
        byte[] large = Encode(512, options);
        long smallBytes = AllocationMeter.Fewest(() => Raster.Load(small, OneThread).Dispose(), warmUps: 2, tries: 3);
        long largeBytes = AllocationMeter.Fewest(() => Raster.Load(large, OneThread).Dispose(), warmUps: 2, tries: 3);
        double perPixel = (largeBytes - smallBytes) / (double)((512 * 512) - (128 * 128));
        Assert.True(perPixel <= budget, $"{setting}: {perPixel:F1} bytes per added pixel ({largeBytes:N0} for 512×512, {smallBytes:N0} for 128×128), budget {budget}.");
    }

    private static byte[] Encode(int size, JxlWriterOptions options)
    {
        using Raster<Rgba8> raster = Photo(size, size);
        using MemoryStream stream = new();
        raster.Save(stream, JxlFormat.Info, options);
        return stream.ToArray();
    }

    /// <summary>A smooth gradient with a fixed pseudo-random texture: compresses like a photo, the same on every run.</summary>
    private static Raster<Rgba8> Photo(int width, int height)
    {
        Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height, OneThread);
        uint seed = 12345;
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                seed = (seed * 1664525) + 1013904223;
                int noise = (int)(seed >> 27) - 16;
                row[x] = new Rgba8((byte)Math.Clamp((x * 255 / width) + noise, 0, 255), (byte)Math.Clamp((y * 255 / height) + noise, 0, 255), (byte)Math.Clamp(128 + noise, 0, 255), 255);
            }
        }

        return raster;
    }
}
