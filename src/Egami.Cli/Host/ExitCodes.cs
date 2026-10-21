namespace Egami.Cli.Host;

/// <summary>The tool's exit codes (CLI design Q13).</summary>
internal static class ExitCodes
{
    /// <summary>Everything worked.</summary>
    public const int Success = 0;

    /// <summary>A file failed (unreadable, corrupt, unwritable), or <c>compare</c> found a difference above the threshold.</summary>
    public const int Failed = 1;

    /// <summary>The command line is wrong: an unknown command, option or value.</summary>
    public const int Usage = 2;

    /// <summary>An input exceeded a safety limit.</summary>
    public const int Limit = 3;
}

/// <summary>A mistake in the command line; the message says what to change. Exits with <see cref="ExitCodes.Usage"/>.</summary>
internal sealed class UsageException(string message) : Exception(message);
