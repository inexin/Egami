using Egami.Formats;
using Egami.Formats.Jpeg;

namespace Egami.Samples.Jpeg;

/// <summary>Decodes a JPEG at reduced scale, writes the same image at several settings, and rotates one losslessly.</summary>
public static class Sample
{
    /// <summary>Runs the sample.</summary>
    /// <param name="output">Where to write.</param>
    public static void Run(string output)
    {
        foreach (int side in new[] { 320, 160, 80, 40 })
        {
            using Raster scaled = Raster.Load(SampleRunner.Image("duck.jpg"), options: new ReaderOptions { MinimumSize = new Size(side, side * 3 / 4) });
            Console.WriteLine($"Needed {side} wide: decoded {scaled.Width} × {scaled.Height}");
        }

        using Raster photo = Raster.Load(SampleRunner.Image("duck.png"));
        foreach (int quality in new[] { 50, 75, 90 })
        {
            string path = Path.Combine(output, $"q{quality}.jpg");
            using (FileStream file = File.Create(path))
            {
                photo.Save(file, JpegFormat.Info, new JpegWriterOptions { Quality = quality, OptimizeHuffman = true });
            }

            using Raster back = Raster.Load(path);
            JpegDetails details = (JpegDetails)back.Metadata.FormatDetails!;
            Console.WriteLine($"Quality {quality}: {new FileInfo(path).Length:N0} bytes, estimated {details.EstimatedQuality}");
        }

        using (FileStream sharp = File.Create(Path.Combine(output, "q85-444.jpg")))
        {
            photo.Save(sharp, JpegFormat.Info, new JpegWriterOptions { Quality = 85, Subsampling = ChromaSubsampling.Subsample444 });
        }

        // Turned on the DCT coefficients, so the rotated file keeps exactly the quality it had.
        JpegTransformResult turned = JpegTransform.Apply(
            SampleRunner.Image("duck.jpg"), Path.Combine(output, "duck-rotated.jpg"), new JpegTransformOptions { Operation = JpegTransformOperation.Rotate90 });
        Console.WriteLine($"Rotated losslessly: {turned.Width} × {turned.Height}");
    }
}
