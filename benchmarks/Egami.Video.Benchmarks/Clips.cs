using Egami;
using Egami.Pixels;
using Egami.Video;
using Egami.Video.Codecs;
using Egami.Video.Containers;

namespace Egami.Video.Benchmarks;

/// <summary>Benchmark inputs, written once into the temp folder: long clips made by repeating real frames.</summary>
internal static class Clips
{
    public static string Directory { get; } = System.IO.Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "egami-video-bench")).FullName;

    /// <summary>A 720p Motion JPEG clip of <paramref name="frames"/> frames with PCM audio, in the given container.</summary>
    public static string MotionJpeg720p(string extension, int frames) => MotionJpeg(1280, 720, extension, frames);

    /// <summary>A Motion JPEG clip of <paramref name="frames"/> frames with PCM audio, in the given container.</summary>
    public static string MotionJpeg(int width, int height, string extension, int frames)
    {
        string path = Path.Combine(Directory, width == 1280 && height == 720 ? $"mjpeg-720p-{frames}{extension}" : $"mjpeg-{width}x{height}-{frames}{extension}");
        if (File.Exists(path))
        {
            return path;
        }

        // Ten distinct frames, encoded once at quality 85, repeated.
        List<byte[]> encoded = [];
        using (IVideoEncoder encoder = VideoCodecs.MotionJpeg.CreateEncoder(new VideoEncoderOptions { Width = width, Height = height, FrameRate = new Rational(25, 1), Quality = 85 }, MediaContext.Default))
        {
            for (int i = 0; i < 10; i++)
            {
                using VideoFrame<Rgb8> frame = new(Picture(width, height, i), new MediaTime(i, new Rational(1, 25)));
                encoder.Send(frame);
                while (encoder.TryReceive(out EncodedPacket? packet))
                {
                    using (packet)
                    {
                        encoded.Add(packet.Data.ToArray());
                    }
                }
            }
        }

        VideoTrackInfo video = new() { Number = 1, Kind = TrackKind.Video, CodecId = CodecIds.MotionJpeg, TimeBase = new Rational(1, 25), Width = width, Height = height, FrameRate = new Rational(25, 1) };
        AudioTrackInfo audio = new() { Number = 2, Kind = TrackKind.Audio, CodecId = CodecIds.PcmLittleEndian, TimeBase = new Rational(1, 48000), SampleRate = 48000, Channels = 2, BitsPerSample = 16 };
        byte[] sound = new byte[1920 * 4];
        string partial = Path.Combine(Directory, "partial-" + Path.GetFileName(path));
        using (IMuxer muxer = MediaFile.CreateMuxer(partial, [video, audio]))
        {
            for (int i = 0; i < frames; i++)
            {
                using EncodedPacket picture = new(1, encoded[i % encoded.Count], new MediaTime(i, new Rational(1, 25))) { IsKeyframe = true };
                muxer.WritePacket(picture);
                using EncodedPacket samples = new(2, sound, new MediaTime(i * 1920L, new Rational(1, 48000))) { IsKeyframe = true };
                muxer.WritePacket(samples);
            }
        }

        File.Move(partial, path, overwrite: true);
        return path;
    }

    private static Raster<Rgb8> Picture(int width, int height, int seed)
    {
        // Smooth gradients with some texture, roughly like camera footage after JPEG.
        Raster<Rgb8> raster = Raster.Create<Rgb8>(width, height);
        Random random = new(seed);
        for (int y = 0; y < height; y++)
        {
            Span<Rgb8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                int noise = random.Next(16);
                row[x] = new Rgb8((byte)(((x + (seed * 40)) / 5) + noise), (byte)((y / 3) + noise), (byte)(((x + y) / 8) + noise));
            }
        }

        return raster;
    }
}
