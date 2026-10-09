using Egami.Formats;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class MemoryAndSecurity
{
    [Fact]
    public void Context()
    {
        using SampleFiles files = new();

        #region context
        // Everything that is not specific to one call lives in an immutable EgamiContext. Change it with `with`.
        EgamiContext context = EgamiContext.Default with
        {
            MaxParallelism = 4,                                         // threads one operation may use; 1 is sequential
            Limits = SafetyLimits.Default with { MaxPixels = 50_000_000 },
        };

        using Raster image = Raster.Load("duck.png", context);
        image.Apply(ops => ops.Blur(2));                                // uses the raster's context: 4 threads at most
        #endregion

        Assert.Equal(4, context.MaxParallelism);
    }

    [Fact]
    public void Usage()
    {
        using SampleFiles files = new();

        #region usage
        PixelMemoryUsage usage = PooledPixelMemorySource.Shared.Usage;
        Console.WriteLine($"{usage.LiveBlocks} blocks, {usage.LiveBytes:N0} bytes in use");

        // Looking for a raster someone forgot to dispose: track leaks for a while, with stack traces.
        usage.CaptureStackTraces = true;
        usage.LeakDetected += (_, leak) => Console.WriteLine($"{leak.Bytes:N0} bytes never disposed, allocated at\n{leak.StackTrace}");
        usage.TrackLeaks = true;
        #endregion

        usage.TrackLeaks = false;
        usage.CaptureStackTraces = false;
        Assert.True(usage.LiveBytes >= 0);
    }

    [Fact]
    public void Untrusted()
    {
        using SampleFiles files = new();
        byte[] upload = File.ReadAllBytes("duck.png");

        #region untrusted
        EgamiContext strict = EgamiContext.Default with
        {
            Limits = SafetyLimits.Default with { MaxPixels = 25_000_000, MaxTotalBytes = 256L * 1024 * 1024 },
        };

        try
        {
            // Read the header first: cheap, and it rejects what you do not want before any pixel memory is used.
            RasterInfo info = Raster.Probe(upload, strict);
            if (info.Width > 8000 || info.Height > 8000)
            {
                throw new InvalidDataException("Too large.");
            }

            using Raster image = Raster.Load(upload, strict, new ReaderOptions { Recovery = RecoveryMode.Strict });
            Console.WriteLine($"{info.Format.Name} {image.Width} × {image.Height}");
        }
        catch (UnsupportedFormatException)
        {
            Console.WriteLine("Not an image format we accept.");
        }
        catch (LimitExceededException e)
        {
            Console.WriteLine($"Over the {e.LimitName} limit.");
        }
        catch (CorruptDataException e)
        {
            Console.WriteLine($"Damaged at byte {e.Offset}.");
        }
        #endregion
    }
}
