namespace Egami.Colors;

/// <summary>One ink of an <see cref="InkColor"/> and its tint.</summary>
/// <param name="Colorant">The ink.</param>
/// <param name="Tint">How much of it, 0 (none) to 1 (full).</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="inks"/>
/// </example>
public readonly record struct InkTint(Colorant Colorant, float Tint);

/// <summary>
/// A colour given as inks: named colorants with tints, as PDF's Separation and DeviceN colours, plus the appearance other
/// backends draw. An ink canvas paints each named ink's plate; an RGB canvas, SVG or a raster without the ink draws
/// <see cref="Appearance"/>. Immutable.
/// </summary>
/// <remarks>
/// A colour names its inks; under overprint only those plates are painted. <see cref="Cmyk"/> names all four process inks, even
/// at 0, and is marked <see cref="IsCmyk"/>, so overprint's NonZero mode can leave its zero components alone, as PDF's overprint
/// mode 1 does for DeviceCMYK. A colour mixing the same inks by <see cref="Mix"/> is not marked: its zeros are painted.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="inks"/>
/// </example>
public sealed class InkColor : IEquatable<InkColor>
{
    private readonly InkTint[] _inks;

    private InkColor(InkTint[] inks, bool isCmyk)
    {
        _inks = inks;
        IsCmyk = isCmyk;
        Appearance = inks.Length == 0 ? InkAppearance.Cmyk(0, 0, 0, 0)
            : isCmyk ? InkAppearance.Cmyk(inks[0].Tint, inks[1].Tint, inks[2].Tint, inks[3].Tint)
            : InkAppearance.Combine(inks);
    }

    /// <summary>Gets paper: no ink at all. Drawn without overprint, it clears every plate; its appearance is white.</summary>
    public static InkColor None { get; } = new([], isCmyk: false);

    /// <summary>Gets the inks and their tints, in the order given.</summary>
    public IReadOnlyList<InkTint> Inks => _inks;

    /// <summary>Gets a value indicating whether this is a process CMYK colour made by <see cref="Cmyk"/>.</summary>
    public bool IsCmyk { get; }

    /// <summary>
    /// Gets how the colour looks where its inks cannot be printed: the inks' appearances, each scaled by its tint, printed over
    /// one another (see <see cref="InkAppearance"/>); <see langword="null"/> when no ink has one, so nothing shows there.
    /// </summary>
    public InkAppearance? Appearance { get; }

    /// <summary>Returns a process CMYK colour: the four process inks, each named even at 0.</summary>
    /// <param name="c">Cyan, 0 to 1.</param>
    /// <param name="m">Magenta, 0 to 1.</param>
    /// <param name="y">Yellow, 0 to 1.</param>
    /// <param name="k">Black, 0 to 1.</param>
    /// <returns>The colour.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A tint is outside 0 to 1.</exception>
    public static InkColor Cmyk(float c, float m, float y, float k) =>
        new([new(Colorant.Cyan, Tint(c, nameof(c))), new(Colorant.Magenta, Tint(m, nameof(m))), new(Colorant.Yellow, Tint(y, nameof(y))), new(Colorant.Black, Tint(k, nameof(k)))], isCmyk: true);

    /// <summary>Returns one ink at a tint, as PDF's Separation colour.</summary>
    /// <param name="colorant">The ink.</param>
    /// <param name="tint">The tint, 0 to 1.</param>
    /// <returns>The colour.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="colorant"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tint"/> is outside 0 to 1.</exception>
    public static InkColor Spot(Colorant colorant, float tint)
    {
        ArgumentNullException.ThrowIfNull(colorant);
        return new([new(colorant, Tint(tint, nameof(tint)))], isCmyk: false);
    }

    /// <summary>Returns a spot colour at a tint.</summary>
    /// <param name="name">The spot's name, such as <c>PANTONE 186 C</c>.</param>
    /// <param name="tint">The tint, 0 to 1.</param>
    /// <param name="appearance">How the spot looks at full tint, or <see langword="null"/> to draw nothing where it cannot be printed.</param>
    /// <returns>The colour.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a valid spot name (see <see cref="Colorant.Spot"/>).</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tint"/> is outside 0 to 1.</exception>
    public static InkColor Spot(string name, float tint, InkAppearance? appearance) => Spot(Colorant.Spot(name, appearance), tint);

    /// <summary>Returns several inks at their tints, as PDF's DeviceN colour.</summary>
    /// <param name="inks">The inks, each named once; at most <see cref="InkSet.MaxInks"/>.</param>
    /// <returns>The colour.</returns>
    /// <exception cref="ArgumentException">An ink is named twice, or there are more than <see cref="InkSet.MaxInks"/>.</exception>
    /// <exception cref="ArgumentNullException">A colorant is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A tint is outside 0 to 1.</exception>
    public static InkColor Mix(params ReadOnlySpan<InkTint> inks)
    {
        if (inks.Length > InkSet.MaxInks)
        {
            throw new ArgumentException($"A colour has at most {InkSet.MaxInks} inks.", nameof(inks));
        }

        InkTint[] copy = inks.ToArray();
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (InkTint ink in copy)
        {
            ArgumentNullException.ThrowIfNull(ink.Colorant, nameof(inks));
            Tint(ink.Tint, nameof(inks));
            if (!names.Add(ink.Colorant.Name))
            {
                throw new ArgumentException($"{ink.Colorant.Name} is named twice.", nameof(inks));
            }
        }

        return copy.Length == 0 ? None : new InkColor(copy, isCmyk: false);
    }

    /// <summary>Returns the index in <see cref="Inks"/> of the ink named <paramref name="name"/> (ordinal), or −1.</summary>
    /// <param name="name">The ink's name.</param>
    /// <returns>The index, or −1.</returns>
    public int IndexOf(string name)
    {
        for (int i = 0; i < _inks.Length; i++)
        {
            if (string.Equals(_inks[i].Colorant.Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Returns the tint of the ink named <paramref name="name"/>, or <see langword="null"/> when the colour does not name it.</summary>
    /// <param name="name">The ink's name.</param>
    /// <returns>The tint, or <see langword="null"/>.</returns>
    public float? TintOf(string name) => IndexOf(name) is int i and >= 0 ? _inks[i].Tint : null;

    /// <inheritdoc/>
    public bool Equals(InkColor? other) => other is not null && IsCmyk == other.IsCmyk && _inks.AsSpan().SequenceEqual(other._inks);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as InkColor);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.Add(IsCmyk);
        foreach (InkTint ink in _inks)
        {
            hash.Add(ink);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public override string ToString() => _inks.Length == 0 ? "None" : string.Join(", ", _inks.Select(i => $"{i.Colorant.Name} {i.Tint:0.###}"));

    private static float Tint(float value, string name) =>
        value is >= 0 and <= 1 ? value : throw new ArgumentOutOfRangeException(name, value, "A tint must be from 0 to 1.");
}
