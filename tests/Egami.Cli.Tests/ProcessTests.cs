using System.Diagnostics;

using Egami.Cli.Host;
using Egami.Formats.Png;
using Egami.Formats.Qoi;

namespace Egami.Cli.Tests;

/// <summary>End-to-end: the built tool started as a process, with real standard streams, a batch, a limit and an external command.</summary>
public sealed class ProcessTests : IDisposable
{
    private const string Png = "pngsuite/basn6a08.png";

    private readonly Tool _tool = new();

    public void Dispose() => _tool.Dispose();

    [Fact]
    public void StandardInputToStandardOutput()
    {
        byte[] png = File.ReadAllBytes(Path.Combine(Tool.Images, Png));
        (int code, byte[] output, string error) = Start(["convert", "-", "-o", "-", "--format", "qoi"], png);
        Assert.True(code == 0, error);
        using Raster image = Raster.Load(png);
        using MemoryStream expected = new();
        image.Save(expected, QoiFormat.Info);
        Assert.Equal(expected.ToArray(), output);
    }

    [Fact]
    public void BatchWithJobs()
    {
        for (int i = 0; i < 8; i++)
        {
            _tool.Copy(Png, $"in/{i}.png");
        }

        (int code, _, string error) = Start(["thumbnail", "in/*.png", "-o", "out/{name}.webp", "--size", "16", "--jobs", "4"]);
        Assert.True(code == 0, error);
        Assert.Equal(8, Directory.GetFiles(_tool.PathOf("out"), "*.webp").Length);
    }

    [Fact]
    public void ExitCodesReachTheShell()
    {
        string png = _tool.Copy(Png);
        Assert.Equal(ExitCodes.Usage, Start(["resize", png, "-o", "x.png"]).Code);
        Assert.Equal(ExitCodes.Failed, Start(["convert", "nothing.png", "-o", "x.png"]).Code);
        (int code, _, string error) = Start(["convert", png, "-o", "x.png", "--limit", "pixels=10"]);
        Assert.Equal(ExitCodes.Limit, code);
        Assert.Contains("safety limit", error, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Limit, Start(["convert", png, "-o", "y.png"], environment: new() { ["EGAMI_LIMITS"] = "width=8" }).Code);
    }

    [Fact]
    public void ExternalCommandsGetTheArgumentsAndStreams()
    {
        string bin = _tool.PathOf("bin");
        Directory.CreateDirectory(bin);
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path.Combine(bin, "egami-test.cmd"), "@echo external %1 %2\r\n@exit /b 5\r\n");
        }
        else
        {
            string script = Path.Combine(bin, "egami-test");
            File.WriteAllText(script, "#!/bin/sh\necho external \"$1\" \"$2\"\nexit 5\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        string path = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        (int code, byte[] output, _) = Start(["test", "a", "b"], environment: new() { ["PATH"] = path });
        Assert.Equal(5, code);
        Assert.Contains("external a b", output.Utf8(), StringComparison.Ordinal);
    }

    [Fact]
    public void VersionAndHelp()
    {
        (int code, byte[] output, _) = Start(["--version"]);
        Assert.Equal(0, code);
        Assert.Matches(@"^\d+\.\d+\.\d+", output.Utf8());
        Assert.Contains("Usage:", Start(["--help"]).Output.Utf8(), StringComparison.Ordinal);
        _ = PngFormat.Info;
    }

    private (int Code, byte[] Output, string Error) Start(string[] args, byte[]? stdin = null, Dictionary<string, string>? environment = null)
    {
        string tool = Path.Combine(AppContext.BaseDirectory, "Egami.Cli.dll");
        ProcessStartInfo start = new("dotnet")
        {
            WorkingDirectory = _tool.Folder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(tool);
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        foreach ((string key, string value) in environment ?? [])
        {
            start.Environment[key] = value;
        }

        using Process process = Process.Start(start)!;
        Task<string> error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using MemoryStream output = new();
        Task copy = process.StandardOutput.BaseStream.CopyToAsync(output, TestContext.Current.CancellationToken);
        if (stdin is not null)
        {
            process.StandardInput.BaseStream.Write(stdin);
        }

        process.StandardInput.Close();
        copy.Wait(TestContext.Current.CancellationToken);
        process.WaitForExit();
        return (process.ExitCode, output.ToArray(), error.Result);
    }
}
