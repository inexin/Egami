using Egami.Formats;
using Egami.Formats.Jpeg;

namespace Egami.DocSamples;

[Collection("Samples")]
public class StoredSamples
{
    [Fact]
    public void Load()
    {
        using SampleFiles files = new();

        #region sample-image
        // The values the codestream stores: no color conversion, no widening to 8 or 16 bits, signed samples kept signed.
        SampleImage image = SampleImage.Load("duck.jpg");
        Console.WriteLine($"{image.Color}, {image.Components.Count} components of {image.Components[0].Precision} bits");

        // A YCbCr JPEG stays YCbCr: luma, then the two chroma samples of each pixel, at full resolution.
        Span<int> row = image.Row(0);
        int luma = row[0], cb = row[1], cr = row[2];
        #endregion

        Assert.Equal(SampleColor.YCbCr, image.Color);
        Assert.InRange(luma + cb + cr, 0, 3 * 255);
    }

    [Fact]
    public void Format()
    {
        using SampleFiles files = new();
        ushort[] stored = [.. Enumerable.Range(0, 64 * 64).Select(i => (ushort)(i % 4096))];
        using MemoryStream file = new();
        JpegLosslessWriter.Write(file, stored, components: 1, width: 64, precision: 12, predictor: 1);
        ReadOnlyMemory<byte> fragment = file.ToArray();

        #region sample-image-format
        // A codestream taken out of a container, such as a DICOM pixel data fragment, is read as the format the container names.
        SampleImage image = SampleImage.Load(fragment, JpegFormat.Info);
        SampleComponent gray = image.Components[0];
        int first = image.Samples.Span[0];       // 0 to 4095 for 12 unsigned bits
        #endregion

        Assert.Equal(new SampleComponent(12, false), gray);
        Assert.Equal(0, first);
        Assert.Equal(stored.Select(v => (int)v), image.Samples.ToArray());
    }
}
