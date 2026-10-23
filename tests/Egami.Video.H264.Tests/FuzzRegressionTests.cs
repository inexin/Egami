namespace Egami.Video.H264.Tests;

/// <summary>
/// Inputs the fuzz harness found failing (tests/video/fuzz-regressions/h264): broken parameter sets in a raw stream or an avcC
/// record. Each must now be read or rejected with an EgamiException, and a broken stream never throws the codec's
/// CorruptDataException outside Throw mode.
/// </summary>
public sealed class FuzzRegressionTests
{
    public static TheoryData<string> Inputs => [.. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fuzz-regressions")).Select(f => Path.GetFileName(f))];

    [Theory]
    [MemberData(nameof(Inputs))]
    public void ReadsOrRejectsCleanly(string input)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fuzz-regressions", input));
        MediaContext context = MediaContext.Default.WithH264() with { Egami = EgamiContext.Default with { MaxParallelism = 2, Diagnostics = _ => { } } };
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
            catch (EgamiException e) when (e is not CorruptDataException { FormatName: "H.264" })
            {
            }
        }
    }
}
