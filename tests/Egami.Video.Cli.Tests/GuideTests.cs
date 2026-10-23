using System.Text;
using System.Text.RegularExpressions;

namespace Egami.Video.Cli.Tests;

/// <summary>
/// Every egami-video command line in the guide (docs/articles/video-cli) runs and succeeds, in the order the pages show them,
/// on corpus clips named as the guide names them (Build Plan task 20).
/// </summary>
public sealed partial class GuideTests
{
    private static readonly string Docs = Path.Combine(Path.GetDirectoryName(Tool.Images)!, "..", "docs", "articles", "video-cli");

    [Theory]
    [InlineData("index.md")]
    [InlineData("ffmpeg.md")]
    public void EveryCommandLineRuns(string page)
    {
        using Tool tool = new();
        tool.Copy("avi-mjpeg-420-pcm.avi", "in.avi");
        tool.Copy("mkv-mjpeg-pcm.mkv", "in.mkv");
        tool.Copy("webm-vp8-opus.webm", "in.webm");
        tool.Copy("mp4/mp4-vp9-opus.mp4", "in.mp4");
        int count = 0;
        foreach (string line in CommandLines(File.ReadAllText(Path.Combine(Docs, page))))
        {
            List<string> words = Split(line);
            byte[]? stdin = null;
            string? stdout = null;
            int lt = words.IndexOf("<"), gt = words.IndexOf(">");
            if (gt >= 0)
            {
                stdout = words[gt + 1];
                words.RemoveRange(gt, 2);
            }

            lt = words.IndexOf("<");
            if (lt >= 0)
            {
                stdin = File.ReadAllBytes(tool.PathOf(words[lt + 1]));
                words.RemoveRange(lt, 2);
            }

            ToolRun run = tool.Run([.. words.Skip(1)], stdin);
            Assert.True(run.Code == 0, $"{page}: {line}\nexit {run.Code}\n{run.Error}");
            if (stdout is not null)
            {
                File.WriteAllBytes(tool.PathOf(stdout), run.Bytes.Length > 0 ? run.Bytes : Encoding.UTF8.GetBytes(run.Text));
            }

            count++;
        }

        Assert.True(count >= 4, $"{page} has only {count} command lines");
    }

    /// <summary>The egami-video lines of the page's sh blocks.</summary>
    private static IEnumerable<string> CommandLines(string markdown)
    {
        foreach (Match block in ShellBlock().Matches(markdown))
        {
            foreach (string line in block.Groups[1].Value.Split('\n'))
            {
                string text = line.Trim();
                if (text.StartsWith("egami-video ", StringComparison.Ordinal) && !text.Contains(" mcp", StringComparison.Ordinal))
                {
                    yield return text;
                }
            }
        }
    }

    private static List<string> Split(string line)
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

        return words;
    }

    [GeneratedRegex("```sh\\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex ShellBlock();
}
