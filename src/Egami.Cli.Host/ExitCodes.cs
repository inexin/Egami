namespace Egami.Cli.Hosting;

/// <summary>The exit codes every tool of the family uses.</summary>
internal static class ExitCodes
{
    /// <summary>Everything worked.</summary>
    public const int Success = 0;

    /// <summary>A file failed (unreadable, corrupt, unwritable), or a check such as <c>compare</c> found a problem.</summary>
    public const int Failed = 1;

    /// <summary>The command line is wrong: an unknown command, option or value.</summary>
    public const int Usage = 2;

    /// <summary>An input exceeded a safety limit.</summary>
    public const int Limit = 3;

    /// <summary>The user stopped the run with Ctrl+C (128 plus SIGINT's 2, as shells report it).</summary>
    public const int Cancelled = 130;
}

/// <summary>A mistake in the command line; the message says what to change. Exits with <see cref="ExitCodes.Usage"/>.</summary>
internal sealed class UsageException(string message) : Exception(message);
