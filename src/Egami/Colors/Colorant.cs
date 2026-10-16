namespace Egami.Colors;

/// <summary>What a colorant is for, as PDF and ICC name inks.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="inks"/>
/// </example>
public enum ColorantKind
{
    /// <summary>A process ink of the output profile, such as Cyan or an n-colour press's Orange: colours convert into these.</summary>
    Process,

    /// <summary>A spot colour, printed with its own ink, such as a Pantone colour.</summary>
    Spot,

    /// <summary>A technical ink, such as varnish, white or a die line: printed, but never shown in proofs.</summary>
    Technical,
}

/// <summary>
/// A named ink: a process ink, a spot colour or a technical ink, with how it looks for backends that cannot print it. Names are
/// matched exactly (ordinal, case-sensitive), as PDF matches them; the names <c>Cyan</c>, <c>Magenta</c>, <c>Yellow</c> and
/// <c>Black</c> always mean the process inks, as in PDF's DeviceN. Two colorants are equal when their names are.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="inks"/>
/// </example>
public sealed class Colorant : IEquatable<Colorant>
{
    /// <summary>The longest name accepted, in characters: PDF's limit on a name.</summary>
    public const int MaxNameLength = 127;

    private Colorant(string name, ColorantKind kind, InkAppearance? appearance)
    {
        (Name, Kind, Appearance) = (name, kind, appearance);
    }

    /// <summary>Gets the process cyan ink.</summary>
    public static Colorant Cyan { get; } = new("Cyan", ColorantKind.Process, InkAppearance.Cmyk(1, 0, 0, 0));

    /// <summary>Gets the process magenta ink.</summary>
    public static Colorant Magenta { get; } = new("Magenta", ColorantKind.Process, InkAppearance.Cmyk(0, 1, 0, 0));

    /// <summary>Gets the process yellow ink.</summary>
    public static Colorant Yellow { get; } = new("Yellow", ColorantKind.Process, InkAppearance.Cmyk(0, 0, 1, 0));

    /// <summary>Gets the process black ink.</summary>
    public static Colorant Black { get; } = new("Black", ColorantKind.Process, InkAppearance.Cmyk(0, 0, 0, 1));

    /// <summary>Gets the ink's name.</summary>
    public string Name { get; }

    /// <summary>Gets what the ink is for.</summary>
    public ColorantKind Kind { get; }

    /// <summary>
    /// Gets how the ink looks at full tint on backends that cannot print it, or <see langword="null"/> when it should not show
    /// there (a technical ink always, other inks when no appearance was given).
    /// </summary>
    public InkAppearance? Appearance { get; }

    /// <summary>Gets a value indicating whether this is one of the four CMYK process inks.</summary>
    public bool IsCmyk => Kind == ColorantKind.Process && IsCmykName(Name);

    /// <summary>Returns a process ink of an n-colour profile, such as Orange; for Cyan, Magenta, Yellow and Black it returns the built-in inks.</summary>
    /// <param name="name">The ink's name.</param>
    /// <param name="appearance">How the ink looks at full tint, or <see langword="null"/> for none.</param>
    /// <returns>The colorant.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty, longer than <see cref="MaxNameLength"/>, or All or None.</exception>
    public static Colorant Process(string name, InkAppearance? appearance)
    {
        Check(name);
        return name switch
        {
            "Cyan" when appearance is null || appearance == Cyan.Appearance => Cyan,
            "Magenta" when appearance is null || appearance == Magenta.Appearance => Magenta,
            "Yellow" when appearance is null || appearance == Yellow.Appearance => Yellow,
            "Black" when appearance is null || appearance == Black.Appearance => Black,
            _ => new Colorant(name, ColorantKind.Process, appearance),
        };
    }

    /// <summary>Returns a spot colour.</summary>
    /// <param name="name">The ink's name, such as <c>PANTONE 186 C</c>.</param>
    /// <param name="appearance">How the ink looks at full tint, or <see langword="null"/> to leave it out where it cannot be printed.</param>
    /// <returns>The colorant.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is empty, longer than <see cref="MaxNameLength"/>, All or None, or names a process ink (Cyan,
    /// Magenta, Yellow, Black).
    /// </exception>
    public static Colorant Spot(string name, InkAppearance? appearance)
    {
        CheckNotProcess(name);
        return new Colorant(name, ColorantKind.Spot, appearance);
    }

    /// <summary>Returns a technical ink, such as varnish: printed on its own plate, never shown in proofs or on other backends.</summary>
    /// <param name="name">The ink's name.</param>
    /// <returns>The colorant.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is empty, longer than <see cref="MaxNameLength"/>, All or None, or names a process ink.
    /// </exception>
    public static Colorant Technical(string name)
    {
        CheckNotProcess(name);
        return new Colorant(name, ColorantKind.Technical, null);
    }

    /// <inheritdoc/>
    public bool Equals(Colorant? other) => other is not null && string.Equals(Name, other.Name, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as Colorant);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Name);

    /// <inheritdoc/>
    public override string ToString() => Name;

    internal static bool IsCmykName(string name) => name is "Cyan" or "Magenta" or "Yellow" or "Black";

    private static void Check(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.Length > MaxNameLength)
        {
            throw new ArgumentException($"A colorant name has at most {MaxNameLength} characters.", nameof(name));
        }

        if (name is "All" or "None")
        {
            throw new ArgumentException($"{name} is not an ink: in PDF it means every ink or no ink.", nameof(name));
        }
    }

    private static void CheckNotProcess(string name)
    {
        Check(name);
        if (IsCmykName(name))
        {
            throw new ArgumentException($"{name} always means the process ink.", nameof(name));
        }
    }
}
