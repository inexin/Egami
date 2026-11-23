using Egami.Video.Codecs;

namespace Egami.Video.Hardware.Tests;

/// <summary>
/// Inputs the fuzz harness found failing on the GPU (tests/video/fuzz-regressions/hardware): each must now be read or rejected
/// with an EgamiException, in good time. hevc-gpu-timeout.h265 made the driver give up on a picture without writing its status,
/// and the decoder waited for that status forever.
/// </summary>
[Trait("Category", "Gpu")]
public sealed class FuzzRegressionTests
{
    public static TheoryData<string> Inputs => [.. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fuzz-regressions")).Select(f => Path.GetFileName(f))];

    [Theory]
    [MemberData(nameof(Inputs))]
    public async Task ReadsOrRejectsCleanly(string input)
    {
        if (input.StartsWith("hevc", StringComparison.Ordinal))
        {
            HevcDecoderTests.RequireDecoder();
        }
        else
        {
            H264DecoderTests.RequireDecoder();
        }

        byte[] data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fuzz-regressions", input));
        MediaContext context = (MediaContext.Default.WithH264().WithHevc() with { Egami = EgamiContext.Default with { Diagnostics = _ => { } } })
            .WithCodec(HardwareVideoCodecs.H264).WithCodec(HardwareVideoCodecs.Hevc);
        Task read = Task.Run(
            () =>
            {
                foreach (DecodeErrorMode mode in (DecodeErrorMode[])[DecodeErrorMode.Skip, DecodeErrorMode.Conceal])
                {
                    try
                    {
                        using VideoReader reader = VideoReader.Open(new MemoryStream(data), new VideoReaderOptions { Errors = mode }, context);
                        while (reader.ReadFrame() is { } frame)
                        {
                            frame.Dispose();
                        }
                    }
                    catch (EgamiException)
                    {
                    }
                }
            },
            TestContext.Current.CancellationToken);
        await read.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
    }
}
