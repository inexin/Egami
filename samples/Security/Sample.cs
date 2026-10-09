using Egami.Formats;

namespace Egami.Samples.Security;

/// <summary>Loads untrusted bytes safely: a valid image, a truncated one, a decompression bomb and something that is not an image.</summary>
public static class Sample
{
    /// <summary>Runs the sample.</summary>
    /// <param name="output">Where to write.</param>
    public static void Run(string output)
    {
        EgamiContext strict = EgamiContext.Default with
        {
            Limits = SafetyLimits.Default with { MaxPixels = 10_000_000, MaxTotalBytes = 128L * 1024 * 1024 },
        };

        byte[] photo = File.ReadAllBytes(SampleRunner.Image("duck.png"));
        byte[] bomb = [.. "P5\n100000 100000\n255\n"u8];
        (string Name, byte[] Data)[] uploads =
        [
            ("valid", photo),
            ("truncated", photo[..(photo.Length / 2)]),
            ("bomb", bomb),
            ("text", "hello, world"u8.ToArray()),
        ];

        foreach ((string name, byte[] data) in uploads)
        {
            try
            {
                RasterInfo info = Raster.Probe(data, strict);
                using Raster image = Raster.Load(data, strict);
                string warnings = image.Metadata.Warnings.Count == 0 ? "no warnings" : string.Join("; ", image.Metadata.Warnings);
                Console.WriteLine($"{name}: {info.Format.Name} {image.Width} × {image.Height}, {warnings}");
                image.Save(Path.Combine(output, name + ".png"));
            }
            catch (LimitExceededException e)
            {
                Console.WriteLine($"{name}: rejected before allocating, over {e.LimitName}");
            }
            catch (EgamiException e)
            {
                Console.WriteLine($"{name}: rejected, {e.GetType().Name}: {e.Message}");
            }
        }
    }
}
