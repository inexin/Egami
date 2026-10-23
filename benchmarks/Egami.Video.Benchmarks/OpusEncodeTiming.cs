using System.Diagnostics;
using System.Globalization;

using Egami.Video.Audio;
using Egami.Video.Audio.Codecs;

namespace Egami.Video.Benchmarks;

/// <summary>
/// The V5.7 speed gate's Egami side: decodes an audio file into memory, encodes it to Opus with the options given and prints
/// the encode time (best of --runs), the size and the bitrate, for egami-compare to set against libopus (opusenc or
/// opus_demo). Options: --out=file.opus --bitrate=BITS --effort=0..10 --frame=MS --cbr --cvbr --voice --lowdelay --runs=N.
/// </summary>
internal static class OpusEncodeTiming
{
    internal static void Run(string[] args)
    {
        string input = args.First(a => !a.StartsWith("--", StringComparison.Ordinal));
        string? Value(string name) => args.FirstOrDefault(a => a.StartsWith($"--{name}=", StringComparison.Ordinal))?[(name.Length + 3)..];
        int? Number(string name) => Value(name) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : null;
        bool Flag(string name) => args.Contains($"--{name}");

        List<AudioFrame> frames = [];
        using (AudioReader reader = AudioReader.Open(input))
        {
            foreach (AudioFrame frame in reader.ReadFrames())
            {
                frames.Add(frame);
            }
        }

        OpusEncoderOptions opus = new()
        {
            Bitrate = Number("bitrate"),
            Effort = Number("effort") ?? 10,
            FrameDuration = TimeSpan.FromMilliseconds(double.Parse(Value("frame") ?? "20", CultureInfo.InvariantCulture)),
            RateMode = Flag("cbr") ? OpusRateMode.Cbr : Flag("cvbr") ? OpusRateMode.ConstrainedVbr : OpusRateMode.Vbr,
            Application = Flag("voice") ? OpusApplication.Voice : Flag("lowdelay") ? OpusApplication.LowDelay : OpusApplication.Audio,
        };
        int runs = Number("runs") ?? 3;
        double best = double.MaxValue;
        long bytes = 0;
        for (int run = 0; run < runs; run++)
        {
            using MemoryStream output = new();
            long start = Stopwatch.GetTimestamp();
            using (AudioWriter writer = AudioWriter.Create(output, ".opus", new AudioWriterOptions { Opus = opus }, leaveOpen: true))
            {
                foreach (AudioFrame frame in frames)
                {
                    writer.Write(frame);
                }
            }

            best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            bytes = output.Length;
            if (run == 0 && Value("out") is { } path)
            {
                File.WriteAllBytes(path, output.ToArray());
            }
        }

        double seconds = frames.Sum(f => (double)f.SampleCount) / frames[0].SampleRate;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{{\"seconds\": {seconds:F2}, \"bytes\": {bytes}, \"kbps\": {bytes * 8 / seconds / 1000:F1}, \"ms\": {best:F1}, \"realtime\": {seconds * 1000 / best:F1}}}"));
        frames.ForEach(f => f.Dispose());
    }
}
