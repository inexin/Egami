using System.Buffers.Binary;

using Egami.Pixels;

namespace Egami.Metafiles.Tests;

/// <summary>Inputs the fuzzers found (task 17), run through the fuzz target: none may throw anything but what it allows.</summary>
public class FuzzFindingsTests
{
    public static TheoryData<string> Findings =>
        [.. Directory.EnumerateFiles(System.IO.Path.Combine(TestFiles.Root, "tests", "images", "fuzz", "metafile-findings")).Select(f => System.IO.Path.GetFileName(f)).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Findings))]
    public async Task FindingsDoNotCrashOrHang(string name)
    {
        byte[] data = File.ReadAllBytes(System.IO.Path.Combine(TestFiles.Root, "tests", "images", "fuzz", "metafile-findings", name));
        Task run = Task.Run(() =>
        {
            if (name.EndsWith(".emfplus", StringComparison.Ordinal))
            {
                Egami.Fuzz.MetafileFuzzTarget.RunPlus(data);
            }
            else
            {
                Egami.Fuzz.MetafileFuzzTarget.Run(data);
            }
        }, TestContext.Current.CancellationToken);
        await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken); // a TimeoutException fails the test
    }

    [Fact]
    public void ADashPatternShorterThanAPixelDrawsSolid()
    {
        // Fuzz finding (step 2.13 task 40): a user-styled pen whose dashes are zero long cut each line into tens of thousands of
        // dashes; a pattern under a device pixel now draws as the solid pen it looks like. The same file with that pen made solid
        // gives the same pixels.
        byte[] dashed = File.ReadAllBytes(System.IO.Path.Combine(TestFiles.Root, "tests", "images", "fuzz", "metafile-findings", "subpixel-dash-a.emf"));
        byte[] solid = (byte[])dashed.Clone();
        int pen = BinaryPrimitives.ReadInt32LittleEndian(dashed.AsSpan(4)); // the header's size: the pen record follows it
        Assert.Equal(95, BinaryPrimitives.ReadInt32LittleEndian(dashed.AsSpan(pen))); // EMR_EXTCREATEPEN
        uint style = BinaryPrimitives.ReadUInt32LittleEndian(dashed.AsSpan(pen + 28));
        Assert.Equal(7u, style & 0xF); // PS_USERSTYLE
        BinaryPrimitives.WriteUInt32LittleEndian(solid.AsSpan(pen + 28), style & ~0xFu);

        MetafileRenderOptions options = new() { Width = 128, Height = 96 };
        using Raster<Rgba8> a = Metafile.Load(new MemoryStream(dashed)).Rasterize<Rgba8>(options);
        using Raster<Rgba8> b = Metafile.Load(new MemoryStream(solid)).Rasterize<Rgba8>(options);
        for (int y = 0; y < a.Height; y++)
        {
            Assert.True(a.Row(y).SequenceEqual(b.Row(y)), $"row {y}");
        }
    }
}
