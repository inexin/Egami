namespace Egami;

/// <summary>The base class of every exception Egami throws for problems with image data or limits.</summary>
/// <remarks>Mistakes in arguments throw the standard <see cref="ArgumentException"/> types instead.</remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/MemoryAndSecurity.cs" region="untrusted"/>
/// </example>
public abstract class EgamiException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="EgamiException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The exception that caused this one, if any.</param>
    protected EgamiException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>Thrown when no registered format matches the data, or the matching format cannot read or write.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/MemoryAndSecurity.cs" region="untrusted"/>
/// </example>
public sealed class UnsupportedFormatException : EgamiException
{
    /// <summary>Initializes a new instance of the <see cref="UnsupportedFormatException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The exception that caused this one, if any.</param>
    public UnsupportedFormatException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when data matches a format but breaks its rules, such as a bad checksum, a truncated stream or an invalid table.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/MemoryAndSecurity.cs" region="untrusted"/>
/// </example>
public sealed class CorruptDataException : EgamiException
{
    /// <summary>Initializes a new instance of the <see cref="CorruptDataException"/> class.</summary>
    /// <param name="formatName">The format being read, such as "PNG".</param>
    /// <param name="offset">The byte offset of the problem in the input, or -1 if unknown.</param>
    /// <param name="problem">What is wrong, as a sentence.</param>
    /// <param name="innerException">The exception that caused this one, if any.</param>
    public CorruptDataException(string formatName, long offset, string problem, Exception? innerException = null)
        : base(offset >= 0 ? $"Corrupt {formatName} data at byte {offset}: {problem}" : $"Corrupt {formatName} data: {problem}", innerException)
    {
        FormatName = formatName;
        Offset = offset;
    }

    /// <summary>Gets the name of the format being read.</summary>
    public string FormatName { get; }

    /// <summary>Gets the byte offset of the problem in the input, or -1 if unknown.</summary>
    public long Offset { get; }

    /// <summary>Gets a value indicating whether the problem is that the data ended early.</summary>
    internal bool IsTruncation { get; init; }
}

/// <summary>Thrown when a <see cref="SafetyLimits"/> limit is exceeded.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/MemoryAndSecurity.cs" region="untrusted"/>
/// </example>
public sealed class LimitExceededException : EgamiException
{
    /// <summary>Initializes a new instance of the <see cref="LimitExceededException"/> class.</summary>
    /// <param name="limitName">The name of the limit, such as <c>MaxPixels</c>.</param>
    /// <param name="allowed">The largest allowed value.</param>
    /// <param name="found">The value found.</param>
    public LimitExceededException(string limitName, long allowed, long found)
        : base($"The image exceeds the {limitName} limit: found {found:N0}, allowed {allowed:N0}.")
    {
        LimitName = limitName;
        Allowed = allowed;
        Found = found;
    }

    /// <summary>Gets the name of the limit, matching a <see cref="SafetyLimits"/> property.</summary>
    public string LimitName { get; }

    /// <summary>Gets the largest allowed value.</summary>
    public long Allowed { get; }

    /// <summary>Gets the value found.</summary>
    public long Found { get; }
}
