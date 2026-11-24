using System.Text;
using System.Text.RegularExpressions;

namespace Egami.Cli.Tests;

/// <summary>
/// Every egami command line and workflow file in the agent guide (docs/agents/Egami.Cli.md, which egami mcp also serves as
/// egami://guide) runs and succeeds, in the order the guide shows them, on test images named as the guide names them.
/// </summary>
public sealed partial class GuideTests
{
    private static readonly string Guide = Path.Combine(Path.GetDirectoryName(Tool.Images)!, "..", "docs", "agents", "Egami.Cli.md");

    [Fact]
    public void EveryCommandLineRuns()
    {
        using Tool tool = new();
        tool.Copy("jpeg/duck-420.jpg", "photo.jpg");
        tool.Copy("jpeg/duck-420.jpg", "photos/a.jpg");
        tool.Copy("jpeg/duck-420.jpg", "photos/b.jpg");
        tool.Copy("pngsuite/basn6a08.png", "logo.png");
        string markdown = File.ReadAllText(Guide).ReplaceLineEndings("\n");
        int count = 0;
        foreach (string line in CommandLines(markdown))
        {
            ToolRun run = tool.Run([.. Split(line).Skip(1)]);
            Assert.True(run.Code == 0, $"{line}\nexit {run.Code}\n{run.Error}");
            count++;
        }

        Assert.True(count >= 10, $"the guide has only {count} command lines");

        // Each workflow file the guide shows runs as it is.
        int workflows = 0;
        foreach (Match file in WorkflowFile().Matches(markdown))
        {
            File.WriteAllText(tool.PathOf("guide-" + file.Groups[1].Value), file.Groups[2].Value);
            ToolRun run = tool.Run("run", "photo.jpg", "--workflow", "guide-" + file.Groups[1].Value, "-o", "guide/{name}.webp");
            Assert.True(run.Code == 0, $"{file.Groups[1].Value}\nexit {run.Code}\n{run.Error}");
            workflows++;
        }

        Assert.True(workflows >= 1, "the guide shows no workflow file");
    }

    /// <summary>The egami lines of the guide's sh blocks.</summary>
    private static IEnumerable<string> CommandLines(string markdown)
    {
        foreach (Match block in ShellBlock().Matches(markdown))
        {
            foreach (string line in block.Groups[1].Value.Split('\n'))
            {
                string text = line.Trim();
                if (text.StartsWith("egami ", StringComparison.Ordinal) && !text.Contains(" mcp", StringComparison.Ordinal))
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

    [GeneratedRegex("`([\\w.-]+\\.json)`:\\n\\n```json\\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex WorkflowFile();
}
