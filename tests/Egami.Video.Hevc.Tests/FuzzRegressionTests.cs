using Egami.Pixels;

namespace Egami.Video.Hevc.Tests;

/// <summary>
/// Inputs the fuzz harness found failing (tests/video/fuzz-regressions/hevc). Each must now be read or rejected with an
/// EgamiException; eleven-bit.h265, a broken 11-bit stream, comes out as 12-bit frames when errors are concealed.
/// </summary>
public sealed class FuzzRegressionTests
{
    public static TheoryData<string> Inputs => [.. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fuzz-regressions")).Select(f => Path.GetFileName(f))];

    [Theory]
    [MemberData(nameof(Inputs))]
    public void ReadsOrRejectsCleanly(string input)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fuzz-regressions", input));
        MediaContext context = MediaContext.Default.WithHevc() with { Egami = EgamiContext.Default with { MaxParallelism = 2, Diagnostics = _ => { } } };
        int frames = 0;
        foreach (DecodeErrorMode mode in (DecodeErrorMode[])[DecodeErrorMode.Skip, DecodeErrorMode.Conceal])
        {
            try
            {
                using VideoReader reader = VideoReader.Open(new MemoryStream(data), new VideoReaderOptions { Errors = mode }, context);
                while (reader.ReadFrame() is { } frame)
                {
                    using (frame)
                    {
                        if (input == "eleven-bit.h265")
                        {
                            Assert.Equal(12, Assert.IsType<YuvRaster<ushort>>(frame.Planes).BitDepth);
                        }

                        frames++;
                    }
                }
            }
            catch (EgamiException)
            {
            }
        }

        if (input == "eleven-bit.h265")
        {
            Assert.True(frames > 0);
        }
    }
}
