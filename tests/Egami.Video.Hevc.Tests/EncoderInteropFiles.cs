using Egami.Video.Codecs;

namespace Egami.Video.Hevc.Tests;

/// <summary>
/// Writes Annex B streams from the HEVC encoder, each with the frames Egami's decoder makes of it, for
/// tools/video/check-hevc-encoder.py to decode with FFmpeg: into EGAMI_INTEROP_DIR/hevcvideo, each stream as &lt;name&gt;.h265,
/// the shown frames as &lt;name&gt;.yuv (planar 4:2:0, 16-bit little-endian samples for Main 10) and its size, frame count and bit
/// depth as &lt;name&gt;.txt. Each stream is also encoded at one thread and must give the same bytes. Runs only when
/// EGAMI_INTEROP_DIR is set.
/// </summary>
public class EncoderInteropFiles
{
    /// <summary>The streams: a size, a frame count, the source's bit depth and the encoder's options.</summary>
    internal static IEnumerable<(string Name, int Width, int Height, int Frames, int Depth, VideoEncoderOptions Options)> Streams()
    {
        foreach (int effort in new[] { 0, 1, 2, 4, 6, 8, 10 })
        {
            yield return ($"effort{effort}", 96, 64, 6, 8, Options(96, 64, effort, new HevcEncoderOptions()));
        }

        yield return ("main10", 96, 64, 6, 10, Options(96, 64, 4, new HevcEncoderOptions()));
        yield return ("main10-from8", 96, 64, 6, 8, Options(96, 64, 4, new HevcEncoderOptions { Profile = HevcProfile.Main10 }));
        yield return ("main-from10", 96, 64, 6, 10, Options(96, 64, 4, new HevcEncoderOptions { Profile = HevcProfile.Main }));
        yield return ("no-aq", 96, 64, 6, 8, Options(96, 64, 4, new HevcEncoderOptions { AdaptiveQuantization = false }));
        yield return ("keyint", 64, 48, 12, 8, Options(64, 48, 2, new HevcEncoderOptions()) with { KeyframeInterval = 5 });
        yield return ("cbr", 96, 64, 12, 8, Options(96, 64, 2, new HevcEncoderOptions()) with { RateControl = RateControlMode.ConstantBitrate, Bitrate = 400_000 });
        yield return ("qp0", 48, 32, 4, 8, Options(48, 32, 2, new HevcEncoderOptions()) with { RateControl = RateControlMode.Quantizer, Quantizer = 0 });
        yield return ("qp51", 64, 48, 4, 8, Options(64, 48, 2, new HevcEncoderOptions()) with { RateControl = RateControlMode.Quantizer, Quantizer = 51 });
        yield return ("qp0-main10", 48, 32, 4, 10, Options(48, 32, 2, new HevcEncoderOptions()) with { RateControl = RateControlMode.Quantizer, Quantizer = 0 });
        yield return ("crop-98x62", 98, 62, 4, 8, Options(98, 62, 5, new HevcEncoderOptions()));
        yield return ("crop-18x10", 18, 10, 4, 8, Options(18, 10, 7, new HevcEncoderOptions()));
        yield return ("crop-2x2", 2, 2, 3, 8, Options(2, 2, 4, new HevcEncoderOptions()));
        yield return ("rows", 200, 300, 3, 8, Options(200, 300, 4, new HevcEncoderOptions()));
        yield return ("wide", 1280, 48, 3, 8, Options(1280, 48, 1, new HevcEncoderOptions()));
    }

    [Fact]
    public void WriteFiles()
    {
        string? directory = Environment.GetEnvironmentVariable("EGAMI_INTEROP_DIR");
        Assert.SkipUnless(directory is not null, "EGAMI_INTEROP_DIR is not set.");
        directory = Path.Combine(directory!, "hevcvideo");
        Directory.CreateDirectory(directory);
        string? only = Environment.GetEnvironmentVariable("EGAMI_INTEROP_ONLY");
        foreach ((string name, int w, int h, int count, int depth, VideoEncoderOptions options) in Streams())
        {
            if (only is not null && !only.Split(',').Contains(name))
            {
                continue;
            }

            (List<EncodedPacket> packets, _, VideoTrackInfo track) = EncoderTests.Encode(options, count, i => EncoderTests.Picture(w, h, i, depth));
            (List<EncodedPacket> single, _, _) = EncoderTests.Encode(options, count, i => EncoderTests.Picture(w, h, i, depth),
                new MediaContext { Egami = EgamiContext.Default with { MaxParallelism = 1 } });
            byte[] file = [.. packets.SelectMany(p => p.Data.ToArray())];
            Assert.True(file.AsSpan().SequenceEqual([.. single.SelectMany(p => p.Data.ToArray())]), $"{name}: the stream differs at one thread.");
            string basePath = Path.Combine(directory, name);
            File.WriteAllBytes(basePath + ".h265", file);
            List<ushort[]> decoded = EncoderTests.Decode(track, packets);
            Assert.Equal(count, decoded.Count);
            using (FileStream yuv = File.Create(basePath + ".yuv"))
            {
                foreach (ushort[] frame in decoded)
                {
                    if (track.BitDepth == 8)
                    {
                        yuv.Write([.. frame.Select(s => (byte)s)]);
                    }
                    else
                    {
                        yuv.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(frame.AsSpan()));
                    }
                }
            }

            EncoderTests.Dispose(packets);
            EncoderTests.Dispose(single);
            File.WriteAllText(basePath + ".txt", $"{w} {h} {count} {track.BitDepth}");
        }
    }

    private static VideoEncoderOptions Options(int width, int height, int effort, HevcEncoderOptions hevc) => new()
    {
        Width = width,
        Height = height,
        Effort = effort,
        Hevc = hevc with { AnnexB = true },
    };
}
