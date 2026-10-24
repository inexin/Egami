using System.Text.Json;

using Egami.Pixels;
using Egami.Video.Audio.Codecs;

namespace Egami.Video.Tests;

/// <summary>
/// Writes smart cuts for tools/video/check-smart-cut.py to decode with vpxdec, dav1d and FFmpeg: each source (Egami's own
/// encodes with hidden frames and audio, and libvpx and libaom clips from the corpus) cut on a frame between keyframes into each
/// container that holds its codec, the IVF cut next to the frames Egami's decoder makes of it as packed planes (16-bit
/// little-endian samples above 8 bits), and manifest.json with each file's frame count. Runs only when EGAMI_INTEROP_DIR is set.
/// </summary>
public class SmartCutInteropFiles
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [Fact]
    public void WriteCuts()
    {
        string? root = Environment.GetEnvironmentVariable("EGAMI_INTEROP_DIR");
        Assert.SkipWhen(root is null, "EGAMI_INTEROP_DIR is not set (python tools/video/check-smart-cut.py sets it).");
        string directory = Path.Combine(root, "smartcut");
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        Directory.CreateDirectory(directory);
        Dictionary<string, object> manifest = [];
        foreach ((string name, string codec, byte[] source, _) in Sources())
        {
            List<TrimTests.DecodedFrame> all = TrimTests.Decode(source);
            int first = Math.Min(13, all.Count / 3);
            int last = Math.Min(all.Count, first + 14);
            MediaTime start = all[first].Time;
            MediaTime? end = last < all.Count ? all[last].Time : null;
            foreach (string container in Containers(codec))
            {
                using MemoryStream output = new();
                TrimResult result = MediaFile.Trim(new MemoryStream(source), output, container, start, end, TrimMode.Smart, container == "ivf" ? t => t.Kind == TrackKind.Video : null, leaveOpen: true, cancellationToken: TestContext.Current.CancellationToken);
                string file = $"{name}.{container}";
                File.WriteAllBytes(Path.Combine(directory, file), output.ToArray());
                manifest[file] = new { codec, frames = result.Frames, reencoded = result.ReencodedFrames, mode = result.Mode.ToString() };
                if (container == "ivf")
                {
                    WritePlanes(Path.Combine(directory, name + ".yuv"), output.ToArray());
                }
            }
        }

        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest, Indented));
    }

    private static IEnumerable<(string Name, string Codec, byte[] Source, string Container)> Sources()
    {
        foreach (string codec in (string[])["vp8", "vp9", "av1"])
        {
            string container = codec == "av1" ? "mkv" : "webm";
            yield return ($"egami-{codec}", codec, TrimTests.MakeClip(TrimTests.Codec(codec), container, hidden: true, audio: OpusCodec.Instance), container);
        }

        yield return ("egami-mjpeg", "mjpeg", TrimTests.MakeClip(TrimTests.Codec("mjpeg"), "avi", audio: PcmCodec.Instance), "avi");
        foreach (string clip in (string[])["vp8/v0-keyframes.webm", "vp8/alpha-ramp.webm", "vp9/p0-keyframes.webm", "vp9/p2-10bit.webm", "vp9/p1-444.webm", "av1/forward-keyframes.webm", "av1/tools-10bit.mkv", "av1/yuv444.mkv"])
        {
            string path = Path.Combine(Corpus.Directory, clip);
            yield return ($"{clip.Split('/')[0]}-{Path.GetFileNameWithoutExtension(clip)}", clip.Split('/')[0], File.ReadAllBytes(path), Path.GetExtension(clip)[1..]);
        }
    }

    private static string[] Containers(string codec) => codec switch
    {
        "vp8" => ["webm", "mkv", "ivf"],
        "vp9" => ["webm", "mp4", "ivf"],
        "av1" => ["mkv", "mp4", "webm", "ivf"],
        _ => ["avi", "mkv"],
    };

    /// <summary>The shown frames of a file as packed planes, cropped, as vpxdec and dav1d write them.</summary>
    private static void WritePlanes(string path, byte[] file)
    {
        using FileStream yuv = File.Create(path);
        using VideoReader reader = VideoReader.Open(new MemoryStream(file));
        foreach (YuvVideoFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                if (frame.Raster is YuvRaster<byte> b)
                {
                    foreach (YuvPlane<byte>? plane in (YuvPlane<byte>?[])[b.Y, b.U, b.V])
                    {
                        for (int y = 0; plane is not null && y < plane.Height; y++)
                        {
                            yuv.Write(plane.Row(y));
                        }
                    }
                }
                else if (frame.Raster is YuvRaster<ushort> w)
                {
                    foreach (YuvPlane<ushort>? plane in (YuvPlane<ushort>?[])[w.Y, w.U, w.V])
                    {
                        for (int y = 0; plane is not null && y < plane.Height; y++)
                        {
                            yuv.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(plane.Row(y)));
                        }
                    }
                }
            }
        }
    }
}
