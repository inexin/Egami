using System.CommandLine;

using Egami.Cli.Host;

namespace Egami.Cli.Tests;

/// <summary>
/// Snapshots of every command's --help and of info --json for one file per format, so any change to the command line shows up
/// in review. Set EGAMI_UPDATE_SNAPSHOTS=1 to write them again after a deliberate change.
/// </summary>
public sealed class SnapshotTests : IDisposable
{
    private static readonly string Folder = Path.Combine(Path.GetDirectoryName(Tool.Images)!, "Egami.Cli.Tests", "Snapshots");

    private static readonly bool Update = Environment.GetEnvironmentVariable("EGAMI_UPDATE_SNAPSHOTS") == "1";

    private readonly Tool _tool = new();

    public void Dispose() => _tool.Dispose();

    /// <summary>Every command and subcommand, as the words that run it.</summary>
    public static TheoryData<string> Commands()
    {
        CliSession session = new(new CliConsole(() => Stream.Null, () => Stream.Null, TextWriter.Null, TextWriter.Null), ".", _ => null);
        Command root = Cli.CreateRoot(session);
        TheoryData<string> names = [string.Empty];
        void Walk(Command command, string prefix)
        {
            foreach (Command child in command.Subcommands)
            {
                string name = (prefix + " " + child.Name).Trim();
                names.Add(name);
                Walk(child, name);
            }
        }

        Walk(root, string.Empty);
        return names;
    }

    /// <summary>One test file per format egami reads.</summary>
    public static TheoryData<string> InfoFiles() =>
    [
        "jpeg/duck-420.jpg", "pngsuite/basn6a16.png", "gif/loop-3.gif", "apng/" + FirstFile("apng", "*.png"), "webp/" + FirstFile("webp", "*.webp"),
        "bmp/" + FirstFile("bmp", "*.bmp"), "qoi/" + FirstFile("qoi", "*.qoi"), "tiff/" + FirstFile("tiff", "*.tif"), "tga/" + FirstFile("tga", "*.tga"),
        "exr/" + FirstFile("exr", "*.exr"), "radiance/" + FirstFile("radiance", "*.hdr"), "ico/" + FirstFile("ico", "*.ico"), "psd/" + FirstFile("psd", "*.psd"),
        "jxl/" + FirstFile("jxl", "*.jxl"), "avif/" + FirstFile("avif", "*.avif"), "jpeg2000/" + FirstFile("jpeg2000", "*.jp2"), "pnm/" + FirstFile("pnm", "*.ppm"),
        "jbig2/" + FirstFile("jbig2", "*.jb2"), "metafiles/synthetic/" + FirstFile(Path.Combine("metafiles", "synthetic"), "*.emf"),
        "metadata/synthetic/phone-orientation6.jpg",
    ];

    [Theory]
    [MemberData(nameof(Commands))]
    public void HelpMatchesItsSnapshot(string command)
    {
        string[] args = [.. command.Split(' ', StringSplitOptions.RemoveEmptyEntries), "--help"];
        ToolRun run = _tool.Run(args);
        Assert.Equal(0, run.Code);
        Check(Path.Combine("help", (command.Length == 0 ? "egami" : command.Replace(' ', '-')) + ".txt"), run.Text.Lines());
    }

    [Theory]
    [MemberData(nameof(InfoFiles))]
    public void InfoJsonMatchesItsSnapshot(string image)
    {
        string name = _tool.Copy(image, Path.GetFileName(image));
        ToolRun run = _tool.Run("info", name, "--json", "--all");
        Assert.True(run.Code == 0, run.Error);
        Check(Path.Combine("info", Path.GetFileName(image) + ".json"), run.Text.Lines());
    }

    private static void Check(string relative, string actual)
    {
        string path = Path.Combine(Folder, relative);
        if (Update || !File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            Assert.SkipWhen(!Update, $"Wrote the new snapshot {relative}; run again.");
            return;
        }

        Assert.Equal(File.ReadAllText(path).Lines(), actual);
    }

    private static string FirstFile(string folder, string pattern)
    {
        string[] files = Directory.GetFiles(Path.Combine(Tool.Images, folder), pattern);
        Array.Sort(files, StringComparer.Ordinal);
        return files.Length > 0 ? Path.GetFileName(files[0]) : "missing";
    }
}
