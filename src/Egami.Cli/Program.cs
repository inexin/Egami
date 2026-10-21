using Egami.Cli.Host;

namespace Egami.Cli;

/// <summary>The egami executable.</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        return Cli.Run(args, CliConsole.System, Environment.CurrentDirectory, Environment.GetEnvironmentVariable);
    }
}
