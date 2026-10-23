namespace Egami.Video.Cli;

/// <summary>The egami-video executable.</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        // Ctrl+C stops the job between packets through the cancellation token: its output is deleted, and the exit code is 130.
        // A second Ctrl+C ends the process at once.
        using CancellationTokenSource cancel = new();
        Console.CancelKeyPress += (_, e) =>
        {
            if (!cancel.IsCancellationRequested)
            {
                e.Cancel = true;
                cancel.Cancel();
            }
        };
        return VideoCli.Run(args, CliConsole.System, Environment.CurrentDirectory, Environment.GetEnvironmentVariable, session => session.Cancellation = cancel.Token);
    }
}
