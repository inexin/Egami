using System.Collections;

namespace Egami.Colors;

/// <summary>
/// The inks a printed page uses, fixed when an ink raster is made: the process inks of an output profile, then named spot and
/// technical inks, at most <see cref="MaxInks"/> in all. Colours naming an ink the set lacks are drawn through their appearance
/// into the process inks, as PDF does for a device without that colorant. Immutable: <see cref="WithSpot(string, InkAppearance?, ColorantKind)"/>
/// and <see cref="WithDisabled"/> return a new set.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="inks"/>
/// </example>
public sealed class InkSet : IReadOnlyList<Colorant>
{
    /// <summary>The most inks a set holds, process inks included.</summary>
    public const int MaxInks = 64;

    private readonly Colorant[] _inks;
    private readonly bool[] _disabled;

    private InkSet(ColorProfile profile, int processCount, Colorant[] inks, bool[] disabled)
    {
        (Profile, ProcessCount, _inks, _disabled) = (profile, processCount, inks, disabled);
    }

    /// <summary>Gets the output profile the process inks belong to, which converts RGB, gray and Lab colours into them.</summary>
    public ColorProfile Profile { get; }

    /// <summary>Gets how many of the inks, the first ones, are the profile's process inks.</summary>
    public int ProcessCount { get; }

    /// <summary>Gets the number of inks.</summary>
    public int Count => _inks.Length;

    /// <summary>Gets the ink at <paramref name="index"/>: process inks first, then spots in the order added.</summary>
    /// <param name="index">The index.</param>
    /// <returns>The ink.</returns>
    public Colorant this[int index] => _inks[index];

    /// <summary>Returns the set of an output profile's process inks: Cyan, Magenta, Yellow and Black for a CMYK profile.</summary>
    /// <param name="profile">The output profile, such as <see cref="ColorProfile.Fogra39"/>.</param>
    /// <returns>The set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The profile is not a CMYK profile: RGB and gray encodings have no inks.</exception>
    public static InkSet FromProfile(ColorProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Colorant[] process = profile.Model switch
        {
            ColorModel.Cmyk => [Colorant.Cyan, Colorant.Magenta, Colorant.Yellow, Colorant.Black],
            _ => throw new ArgumentException($"{profile.Name} is not an output profile with inks.", nameof(profile)),
        };
        return new InkSet(profile, process.Length, process, new bool[process.Length]);
    }

    /// <summary>Returns this set with a spot or technical ink added after the others.</summary>
    /// <param name="name">The ink's name, such as <c>PANTONE 186 C</c>.</param>
    /// <param name="appearance">How a spot looks at full tint, or <see langword="null"/>; a technical ink never shows.</param>
    /// <param name="kind"><see cref="ColorantKind.Spot"/> (the default) or <see cref="ColorantKind.Technical"/>.</param>
    /// <returns>The new set.</returns>
    /// <exception cref="ArgumentException">
    /// The name is not a valid spot name (see <see cref="Colorant.Spot"/>), the set already has an ink of that name, it is full,
    /// or <paramref name="kind"/> is <see cref="ColorantKind.Process"/>.
    /// </exception>
    public InkSet WithSpot(string name, InkAppearance? appearance, ColorantKind kind = ColorantKind.Spot)
    {
        return kind switch
        {
            ColorantKind.Spot => WithSpot(Colorant.Spot(name, appearance)),
            ColorantKind.Technical => WithSpot(Colorant.Technical(name)),
            _ => throw new ArgumentException("Process inks come from the profile; add spot or technical inks.", nameof(kind)),
        };
    }

    /// <summary>Returns this set with a spot or technical ink added after the others.</summary>
    /// <param name="colorant">The ink.</param>
    /// <returns>The new set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="colorant"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The ink is a process ink, the set already has an ink of that name, or it is full.
    /// </exception>
    public InkSet WithSpot(Colorant colorant)
    {
        ArgumentNullException.ThrowIfNull(colorant);
        if (colorant.Kind == ColorantKind.Process)
        {
            throw new ArgumentException("Process inks come from the profile; add spot or technical inks.", nameof(colorant));
        }

        if (Contains(colorant.Name))
        {
            throw new ArgumentException($"The set already has an ink named {colorant.Name}.", nameof(colorant));
        }

        if (_inks.Length == MaxInks)
        {
            throw new ArgumentException($"A set holds at most {MaxInks} inks.", nameof(colorant));
        }

        return new InkSet(Profile, ProcessCount, [.. _inks, colorant], [.. _disabled, false]);
    }

    /// <summary>
    /// Returns this set with a spot disabled: colours naming it are drawn through its appearance into the process inks, as if the
    /// set lacked it, and an ink raster keeps no plate for it. MuPDF calls such a separation disabled.
    /// </summary>
    /// <param name="name">The spot's name.</param>
    /// <returns>The new set.</returns>
    /// <exception cref="ArgumentException">The set has no spot or technical ink of that name.</exception>
    public InkSet WithDisabled(string name)
    {
        int index = IndexOf(name);
        if (index < ProcessCount)
        {
            throw new ArgumentException($"The set has no spot named {name}.", nameof(name));
        }

        bool[] disabled = [.. _disabled];
        disabled[index] = true;
        return new InkSet(Profile, ProcessCount, _inks, disabled);
    }

    /// <summary>Returns whether the ink at <paramref name="index"/> is a disabled spot.</summary>
    /// <param name="index">The index.</param>
    /// <returns><see langword="true"/> when disabled.</returns>
    public bool IsDisabled(int index) => _disabled[index];

    /// <summary>Returns the index of the ink named <paramref name="name"/> (ordinal, case-sensitive), or −1.</summary>
    /// <param name="name">The ink's name.</param>
    /// <returns>The index, or −1.</returns>
    public int IndexOf(string name)
    {
        for (int i = 0; i < _inks.Length; i++)
        {
            if (string.Equals(_inks[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Returns whether the set has an ink named <paramref name="name"/>.</summary>
    /// <param name="name">The ink's name.</param>
    /// <returns><see langword="true"/> when it has.</returns>
    public bool Contains(string name) => IndexOf(name) >= 0;

    /// <inheritdoc/>
    public IEnumerator<Colorant> GetEnumerator() => ((IEnumerable<Colorant>)_inks).GetEnumerator();

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc/>
    public override string ToString() => string.Join(", ", _inks.Select((c, i) => _disabled[i] ? c.Name + " (disabled)" : c.Name));
}
