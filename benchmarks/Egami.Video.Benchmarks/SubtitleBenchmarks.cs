using System.Globalization;
using System.Text;

using BenchmarkDotNet.Attributes;

using Egami.Video.Subtitles;

namespace Egami.Video.Benchmarks;

/// <summary>
/// V1.2 subtitles: reading, saving and converting a file of 10,000 cues with italics, positions and line breaks. FFmpeg's times
/// for the same files come from tools/video/time-subtitles.py, which runs <c>--time-subtitles</c>.
/// </summary>
[MemoryDiagnoser]
public class SubtitleBenchmarks
{
    private byte[] _data = [];
    private SubtitleDocument _document = new();

    [Params(SubtitleFormat.Srt, SubtitleFormat.WebVtt, SubtitleFormat.Ass)]
    public SubtitleFormat Format { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _data = Encoding.UTF8.GetBytes(SubtitleTiming.Script(Format, 10_000));
        _document = SubtitleDocument.Load(_data);
    }

    [Benchmark(Description = "Read")]
    public int Read() => SubtitleDocument.Load(_data).Cues.Count;

    [Benchmark(Description = "Save unchanged")]
    public long Save()
    {
        using MemoryStream output = new(_data.Length);
        _document.Save(output);
        return output.Length;
    }

    [Benchmark(Description = "Convert to SRT")]
    public int Convert() => _document.ToText(SubtitleFormat.Srt).Length;
}

/// <summary>
/// Times reading a subtitle file and writing it as SRT, the best of five runs after a warm-up, as <c>ffmpeg -i file -f srt</c>
/// does; tools/video/time-subtitles.py compares the times with FFmpeg's. <c>--write dir</c> writes the test files.
/// </summary>
internal static class SubtitleTiming
{
    internal static void Run(string[] args)
    {
        if (args is ["--write", string directory])
        {
            System.IO.Directory.CreateDirectory(directory);
            foreach ((SubtitleFormat format, string extension) in (ReadOnlySpan<(SubtitleFormat, string)>)[(SubtitleFormat.Srt, "srt"), (SubtitleFormat.WebVtt, "vtt"), (SubtitleFormat.Ass, "ass")])
            {
                File.WriteAllText(Path.Combine(directory, $"cues-20000.{extension}"), Script(format, 20_000), new UTF8Encoding(false));
            }

            return;
        }

        foreach (string file in args)
        {
            byte[] data = File.ReadAllBytes(file);
            SubtitleDocument.Load(data).ToText(SubtitleFormat.Srt);
            double best = double.MaxValue;
            for (int run = 0; run < 5; run++)
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                SubtitleDocument document = SubtitleDocument.Load(File.ReadAllBytes(file));
                File.WriteAllText(Path.ChangeExtension(file, ".egami.srt"), document.ToText(SubtitleFormat.Srt));
                best = Math.Min(best, System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileName(file)}\t{best:F2} ms"));
        }
    }

    /// <summary>A file of <paramref name="count"/> two-line cues, every third in italics and every tenth placed at the top.</summary>
    internal static string Script(SubtitleFormat format, int count)
    {
        StringBuilder text = new();
        static string Time(long ms, char separator, bool ass) => ass
            ? string.Create(CultureInfo.InvariantCulture, $"{ms / 3_600_000}:{ms / 60_000 % 60:00}:{ms / 1000 % 60:00}.{ms % 1000 / 10:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{ms / 3_600_000:00}:{ms / 60_000 % 60:00}:{ms / 1000 % 60:00}{separator}{ms % 1000:000}");
        if (format == SubtitleFormat.WebVtt)
        {
            text.Append("WEBVTT\n\n");
        }
        else if (format == SubtitleFormat.Ass)
        {
            text.Append("[Script Info]\nScriptType: v4.00+\nPlayResX: 1920\nPlayResY: 1080\n\n[V4+ Styles]\n")
                .Append("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n")
                .Append("Style: Default,Arial,48,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,2,2,10,10,10,1\n\n[Events]\n")
                .Append("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");
        }

        for (int i = 0; i < count; i++)
        {
            long start = i * 2500L, end = start + 2000;
            string first = i % 3 == 0 ? "<i>Line one of cue</i>" : "Line one of cue";
            string number = i.ToString(CultureInfo.InvariantCulture);
            switch (format)
            {
                case SubtitleFormat.Srt:
                    text.Append(i + 1).Append('\n').Append(Time(start, ',', false)).Append(" --> ").Append(Time(end, ',', false)).Append('\n')
                        .Append(i % 10 == 0 ? "{\\an8}" : "").Append(first).Append(' ').Append(number).Append("\nand the second line.\n\n");
                    break;
                case SubtitleFormat.WebVtt:
                    text.Append(Time(start, '.', false)).Append(" --> ").Append(Time(end, '.', false)).Append(i % 10 == 0 ? " line:0" : "").Append('\n')
                        .Append(first).Append(' ').Append(number).Append("\nand the second line.\n\n");
                    break;
                default:
                    text.Append("Dialogue: 0,").Append(Time(start, '.', true)).Append(',').Append(Time(end, '.', true)).Append(",Default,,0,0,0,,")
                        .Append(i % 10 == 0 ? "{\\an8}" : "").Append(i % 3 == 0 ? "{\\i1}Line one of cue{\\i0}" : "Line one of cue").Append(' ').Append(number)
                        .Append("\\Nand the second line.\n");
                    break;
            }
        }

        return text.ToString();
    }
}
