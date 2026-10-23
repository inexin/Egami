using System.Text;

using Egami.Video.Cli.Commands;

namespace Egami.Video.Cli.Tests;

/// <summary>
/// ffmpeg and ffprobe mode (Video CLI design Q14, Build Plan task 23): every line of ffmpeg-lines.txt either maps to the native
/// command it names (--explain) or stops with exit code 2 naming the option; and the lines that run give what the native
/// commands give.
/// </summary>
public sealed class FfmpegModeTests
{
    public static TheoryData<string, string> Lines()
    {
        TheoryData<string, string> lines = [];
        foreach (string line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "ffmpeg-lines.txt")))
        {
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            int bar = line.IndexOf(" | ", StringComparison.Ordinal);
            lines.Add(line[..bar].Trim(), line[(bar + 3)..].Trim());
        }

        return lines;
    }

    [Fact]
    public void TheSetHasOverAHundredLines() => Assert.True(Lines().Count >= 100);

    [Theory]
    [MemberData(nameof(Lines))]
    public void LineMapsOrIsRefused(string expected, string command)
    {
        using Tool tool = new();
        File.WriteAllText(tool.PathOf("list.txt"), "file 'a.mkv'\nfile 'b.mkv'\n");
        string[] words = Split(command);
        ToolRun run = tool.Run([.. words, "--explain"]);
        if (expected.StartsWith("=> ", StringComparison.Ordinal))
        {
            Assert.True(run.Code == 0, $"{command}\n{run.Error}");
            Assert.StartsWith("egami-video " + expected[3..], run.Text, StringComparison.Ordinal);

            // The native command parses: every option it names is one the command has.
            string[] native = [.. Split(run.Text.Trim())[1..]];
            if (native[0] != "run")
            {
                Host.VideoSession session = new(new CliConsole(() => Stream.Null, () => Stream.Null, TextWriter.Null, TextWriter.Null), tool.Folder, _ => null);
                // The tool parses with the invariant culture, as VideoCli.Run sets it.
                System.Globalization.CultureInfo culture = System.Globalization.CultureInfo.CurrentCulture;
                System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
                System.CommandLine.ParseResult parsed;
                try
                {
                    parsed = VideoCli.CreateRoot(session).Parse(native);
                }
                finally
                {
                    System.Globalization.CultureInfo.CurrentCulture = culture;
                }

                Assert.True(parsed.Errors.Count == 0, $"{string.Join(' ', native)}: {string.Join("; ", parsed.Errors.Select(e => e.Message))}");

                // An option the command lacks would be taken as an input, such as -o for a command without one.
                IEnumerable<string> arguments = parsed.CommandResult.Children.OfType<System.CommandLine.Parsing.ArgumentResult>().SelectMany(a => a.Tokens.Select(t => t.Value));
                Assert.DoesNotContain(arguments, a => a.Length > 1 && a.StartsWith('-') && !double.TryParse(a, System.Globalization.CultureInfo.InvariantCulture, out _));
            }
        }
        else
        {
            string option = expected[3..];
            Assert.True(run.Code == 2, $"{command}: exit {run.Code}\n{run.Text}");
            Assert.Contains(option, run.Error, StringComparison.Ordinal);
            Assert.Contains("is not supported", run.Error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RemuxLineGivesWhatRemuxGives()
    {
        using Tool tool = new();
        string clip = tool.Copy("avi-mjpeg-420-pcm.avi", "in.avi");
        Assert.Equal(0, tool.Run("ffmpeg", "-i", clip, "-c", "copy", "a.mkv").Code);
        Assert.Equal(0, tool.Run("remux", clip, "-o", "b.mkv").Code);
        Assert.Equal(File.ReadAllBytes(tool.PathOf("b.mkv")), File.ReadAllBytes(tool.PathOf("a.mkv")));
    }

    [Fact]
    public void Framemd5ToAFileAndAudioWithoutVideo()
    {
        using Tool tool = new();
        string clip = tool.Copy("avi-mjpeg-420-pcm.avi", "in.avi");
        Assert.Equal(0, tool.Run("ffmpeg", "-i", clip, "-c", "copy", "-f", "framemd5", "hashes.txt").Code);
        Assert.Equal(tool.Run("hash", clip, "--packets").Text, File.ReadAllText(tool.PathOf("hashes.txt")));

        ToolRun audio = tool.Run("ffmpeg", "-i", clip, "-vn", "-c:a", "flac", "audio.mkv");
        Assert.True(audio.Code == 0, audio.Error);
        Assert.Equal([TrackKind.Audio], MediaFile.Probe(tool.PathOf("audio.mkv")).Tracks.Select(t => t.Kind));
    }

    [Fact]
    public void ExistingOutputNeedsY()
    {
        using Tool tool = new();
        string clip = tool.Copy("avi-mjpeg-420-pcm.avi", "in.avi");
        File.WriteAllText(tool.PathOf("out.mkv"), "x");
        ToolRun run = tool.Run("ffmpeg", "-i", clip, "-c", "copy", "out.mkv");
        Assert.Equal(1, run.Code);
        Assert.Contains("already exists; give -y", run.Error, StringComparison.Ordinal);
        Assert.Equal(0, tool.Run("ffmpeg", "-y", "-i", clip, "-c", "copy", "out.mkv").Code);
    }

    [Fact]
    public void FfprobeJsonEqualsInfo()
    {
        using Tool tool = new();
        string clip = Path.Combine(Tool.Corpus, "mkv-mjpeg-pcm.mkv");
        ToolRun probe = tool.Run("ffprobe", "-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", clip);
        ToolRun info = tool.Run("info", clip, "--format", "ffprobe");
        Assert.Equal(0, probe.Code);
        Assert.Equal(info.Text, probe.Text);
    }

    [Fact]
    public void FfprobeEntriesAndWriters()
    {
        using Tool tool = new();
        string clip = Path.Combine(Tool.Corpus, "avi-mjpeg-420-pcm.avi");
        Assert.Equal("160x120\n", tool.Run("ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=s=x:p=0", clip).Text);
        Assert.Equal("2.000000\n", tool.Run("ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", clip).Text);
        string text = tool.Run("ffprobe", "-show_entries", "stream=codec_name", clip).Text;
        Assert.Equal("[STREAM]\ncodec_name=mjpeg\n[/STREAM]\n[STREAM]\ncodec_name=pcm_s16le\n[/STREAM]\n", text);
        Assert.Equal("streams.stream.0.codec_name=\"mjpeg\"\nstreams.stream.1.codec_name=\"pcm_s16le\"\n", tool.Run("ffprobe", "-show_entries", "stream=codec_name", "-of", "flat", clip).Text);
    }

    [Theory]
    [InlineData("scale=640:-1,hflip", "resize 640 + flip horizontal")]
    [InlineData("select='gt(scene,0.4)'", "")]
    [InlineData("select=gt(scene\\,0.3)", "")]
    public void FilterChains(string chain, string steps)
    {
        List<string> words = FfmpegTranslator.Filters(chain, out _, out string? special);
        Assert.Equal(steps, string.Join(' ', words));
        Assert.Equal(steps.Length == 0, special is not null);
    }

    /// <summary>Splits a command line at spaces, keeping double-quoted words together without their quotes.</summary>
    private static string[] Split(string line)
    {
        List<string> words = [];
        StringBuilder word = new();
        bool quoted = false, any = false;
        foreach (char c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (c == ' ' && !quoted)
            {
                if (any)
                {
                    words.Add(word.ToString());
                    word.Clear();
                    any = false;
                }
            }
            else
            {
                word.Append(c);
                any = true;
            }
        }

        if (any)
        {
            words.Add(word.ToString());
        }

        return [.. words];
    }
}
