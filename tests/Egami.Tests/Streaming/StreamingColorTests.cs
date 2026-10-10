using Egami.Colors;
using Egami.Formats;
using Egami.Formats.Png;
using Egami.Pixels;

namespace Egami.Tests.Streaming;

/// <summary>
/// Streaming reads honour <see cref="ReaderOptions.ConvertColorTo"/>: the decoder's rows, type, metadata and Transcode equal a
/// load with the same options, which loads and then converts.
/// </summary>
public class StreamingColorTests
{
    private static readonly Dictionary<string, ColorProfile> Targets = new()
    {
        ["p3"] = ColorProfile.DisplayP3,
        ["linear"] = ColorProfile.LinearSrgb,
        ["gray"] = ColorProfile.GraySrgb,
    };

    public static TheoryData<string, string> FilesAndTargets()
    {
        TheoryData<string, string> data = [];
        foreach (string file in StreamingCorpus.Files)
        {
            foreach (string target in Targets.Keys)
            {
                data.Add(file, target);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FilesAndTargets))]
    public void RowsTypeAndMetadataEqualALoad(string file, string target)
    {
        byte[] data = StreamingCorpus.Read(file);
        RasterInfo stored = Raster.Probe(data);
        foreach (ReaderOptions shape in (ReaderOptions[])[new(), new() { Region = new Rect(stored.Width / 4, stored.Height / 4, Math.Max(1, stored.Width / 2), Math.Max(1, stored.Height / 2)), MinimumSize = new Size(Math.Max(1, stored.Width / 5), 1) }])
        {
            ReaderOptions options = shape with { ConvertColorTo = Targets[target] };
            using Raster<Rgba16> expected = Raster.Load<Rgba16>(data, options: options);
            using RasterDecoder decoder = RasterDecoder.Open(data, options: options);
            using Raster natural = Raster.Load(data, options: options);
            Assert.Equal(natural.Layout, decoder.Info.Layout);
            Assert.Equal(expected.Metadata.GetColorProfile().Name, decoder.Metadata.GetColorProfile().Name);
            using Raster<Rgba16> frame = Raster.Create<Rgba16>(decoder.Info.Width, decoder.Info.Height);
            for (int f = 0; decoder.ReadFrame(frame) is not null; f++)
            {
                StreamingCorpus.Equal(expected, f, frame, $"{file} to {target}, {shape}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(FilesAndTargets))]
    public void TranscodeEqualsALoadThenSave(string file, string target)
    {
        byte[] data = StreamingCorpus.Read(file);
        ReaderOptions options = new() { ConvertColorTo = Targets[target], Frames = 0..1 };
        using Raster natural = Raster.Load(data, options: options);
        using MemoryStream saved = new();
        natural.Save(saved, PngFormat.Info);
        using MemoryStream transcoded = new();
        Raster.Transcode(new MemoryStream(data), transcoded, PngFormat.Info, readerOptions: options);
        using Raster<Rgba16> want = Raster.Load<Rgba16>(saved.ToArray());
        using Raster<Rgba16> got = Raster.Load<Rgba16>(transcoded.ToArray());
        StreamingCorpus.Equal(want, 0, got, $"{file} to {target}");
    }
}
