using Egami.Pixels;
using Egami.Video.Audio;
using Egami.Video.Codecs;

namespace Egami.Video.Tests.Editing;

/// <summary>
/// Small synthetic clips for the editing tests, written once per run as raw video and PCM in Matroska. Every frame is one
/// flat colour: red counts the frame (8 per frame), green names the clip and blue is 0, so a test can read which source
/// frame landed where.
/// </summary>
internal static class EditClips
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "egami-edit-tests", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static string Make(string name, int width, int height, Rational rate, int frames, byte green, double? tone = 440, int sampleRate = 48000, ChannelLayout? layout = null)
    {
        Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder, $"{name}-{width}x{height}-{rate.Numerator}_{rate.Denominator}-{frames}-{green}-{tone}-{sampleRate}.mkv");
        if (File.Exists(path))
        {
            return path;
        }

        layout ??= ChannelLayout.Stereo;
        VideoWriterOptions options = new()
        {
            Codec = RawVideoCodec.Instance,
            FrameRate = rate,
            AudioCodec = tone is null ? null : AudioCodecs.Pcm,
            AudioSampleRate = sampleRate,
            AudioLayout = layout,
        };
        string temp = path + ".tmp.mkv";
        using (VideoWriter writer = VideoWriter.Create(temp, options))
        {
            long samples = 0;
            for (int i = 0; i < frames; i++)
            {
                using Raster<Rgba8> frame = Raster.Create(width, height, new Rgba8((byte)Math.Min(255, i * 8), green, 0, 255));
                MediaTime time = new(i * rate.Denominator, new Rational(1, rate.Numerator));
                writer.WriteFrame(frame, time);
                if (tone is double frequency)
                {
                    long end = (long)Math.Round((i + 1) * (double)rate.Denominator / rate.Numerator * sampleRate);
                    using AudioFrame audio = new(SampleFormat.F32Planar, layout.Value, sampleRate, (int)(end - samples), new MediaTime(samples, new Rational(1, sampleRate)));
                    for (int c = 0; c < layout.Value.ChannelCount; c++)
                    {
                        Span<float> s = audio.GetSpan<float>(c);
                        for (int k = 0; k < s.Length; k++)
                        {
                            s[k] = (float)(0.25 * Math.Sin(2 * Math.PI * frequency * (samples + k) / sampleRate));
                        }
                    }

                    writer.WriteAudio(audio);
                    samples = end;
                }
            }

            writer.Finish();
        }

        File.Move(temp, path, overwrite: true);
        return path;
    }

    /// <summary>Returns which source frame a rendered pixel came from (red over 8), and the clip's green.</summary>
    public static (int Frame, int Green) Read(Raster<Rgba8> frame, int x = -1, int y = -1)
    {
        Rgba8 p = frame.Row(y < 0 ? frame.Height / 2 : y)[x < 0 ? frame.Width / 2 : x];
        return ((int)Math.Round(p.R / 8.0), p.G);
    }
}
