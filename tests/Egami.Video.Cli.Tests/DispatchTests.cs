using System.Diagnostics;

namespace Egami.Video.Cli.Tests;

/// <summary>
/// <c>egami video ...</c> runs egami-video (Build Plan task 2): egami runs any <c>egami-&lt;name&gt;</c> program on the PATH, so
/// with egami-video installed the two tools work as one.
/// </summary>
public sealed class DispatchTests
{
    [Fact]
    public void EgamiVideoRunsEgamiVideo()
    {
        string here = AppContext.BaseDirectory;
        string configuration = new DirectoryInfo(here).Parent!.Name;
        string egami = Path.GetFullPath(Path.Combine(here, "..", "..", "..", "..", "..", "src", "Egami.Cli", "bin", configuration, "net10.0", "Egami.Cli.dll"));
        Assert.SkipUnless(File.Exists(egami), $"egami is not built at {egami}.");
        using Tool tool = new();

        // The program egami looks for: egami-video on the PATH, here a script that starts the tool built next to the tests.
        string video = Path.Combine(here, "Egami.Video.Cli.dll");
        string shim = Path.Combine(tool.Folder, OperatingSystem.IsWindows() ? "egami-video.cmd" : "egami-video");
        File.WriteAllText(shim, OperatingSystem.IsWindows() ? $"@dotnet \"{video}\" %*\r\n" : $"#!/bin/sh\nexec dotnet \"{video}\" \"$@\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        ProcessStartInfo start = new("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string arg in (string[])[egami, "video", "info", Path.Combine(Tool.Corpus, "avi-mjpeg-420-pcm.avi")])
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment["PATH"] = tool.Folder + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        Assert.Contains("AVI, 0:00:02.000", output, StringComparison.Ordinal);
        Assert.Contains("#0 video (v:0): Motion JPEG 160x120 25 fps", output, StringComparison.Ordinal);
    }
}
