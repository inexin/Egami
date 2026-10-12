using System.Globalization;

namespace Egami.Fonts.Shaping;

/// <summary>
/// An OpenType feature setting: a feature tag, a value and the range of UTF-16 units it applies to, in HarfBuzz's model. A value
/// of 0 turns a feature off (such as default ligatures), 1 turns it on, and higher values pick an alternate.
/// </summary>
/// <param name="Tag">The feature tag, such as <c>liga</c>, <c>kern</c> or <c>ss01</c>.</param>
/// <param name="Value">The value: 0 off, 1 on, 2 and up an alternate.</param>
/// <param name="Start">The first UTF-16 index the setting applies to.</param>
/// <param name="End">The index just past the last one it applies to; <see cref="int.MaxValue"/> for the end of the text.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Shaping.cs" region="features"/>
/// </example>
public readonly record struct FontFeature(FontTag Tag, uint Value = 1, int Start = 0, int End = int.MaxValue)
{
    /// <summary>Gets whether the setting applies to the whole text.</summary>
    public bool IsGlobal => Start == 0 && End == int.MaxValue;

    /// <summary>Parses HarfBuzz's feature syntax: <c>kern</c>, <c>+kern</c>, <c>-liga</c>, <c>aalt=2</c>, <c>ss01=on</c>, <c>kern[3:5]=0</c>.</summary>
    /// <param name="text">The setting.</param>
    /// <returns>The feature setting.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException"><paramref name="text"/> is not a feature setting.</exception>
    public static FontFeature Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TryParse(text, out FontFeature feature) ? feature : throw new FormatException($"\"{text}\" is not a feature setting.");
    }

    /// <summary>Parses HarfBuzz's feature syntax, returning false instead of throwing.</summary>
    /// <param name="text">The setting.</param>
    /// <param name="feature">The feature setting, or the default value.</param>
    /// <returns><see langword="true"/> when <paramref name="text"/> is a feature setting.</returns>
    public static bool TryParse(string? text, out FontFeature feature)
    {
        feature = default;
        if (text is null || !FontFeatureSyntax.TryParse(text, out FeatureSetting setting))
        {
            return false;
        }

        feature = new FontFeature(new FontTag(setting.Tag), setting.Value, setting.Start, setting.End);
        return true;
    }

    /// <summary>Turns standard ligatures (<c>liga</c>) on or off.</summary>
    /// <param name="enabled">Whether ligatures are formed.</param>
    /// <returns>The setting.</returns>
    public static FontFeature Ligatures(bool enabled) => new(new FontTag("liga"), enabled ? 1u : 0u);

    /// <summary>Gets the setting for kerning (<c>kern</c>) off.</summary>
    public static FontFeature NoKerning => new(new FontTag("kern"), 0);

    /// <summary>Gets the setting for tabular figures (<c>tnum</c>): digits of one width, for columns.</summary>
    public static FontFeature TabularFigures => new(new FontTag("tnum"));

    /// <summary>Gets the setting for small capitals (<c>smcp</c>).</summary>
    public static FontFeature SmallCaps => new(new FontTag("smcp"));

    /// <summary>Turns on a stylistic set, <c>ss01</c> to <c>ss20</c>.</summary>
    /// <param name="number">The set, 1 to 20.</param>
    /// <returns>The setting.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="number"/> is outside 1 to 20.</exception>
    public static FontFeature StylisticSet(int number)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, 20);
        return new FontFeature(new FontTag("ss" + number.ToString("00", CultureInfo.InvariantCulture)));
    }

    /// <summary>Returns the setting in HarfBuzz's syntax, such as <c>liga=0</c> or <c>kern[3:5]=0</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString()
    {
        string range = IsGlobal ? "" : End == int.MaxValue ? $"[{Start}:]" : $"[{Start}:{End}]";
        return $"{Tag.ToString().TrimEnd()}{range}={Value}";
    }

    internal FeatureSetting ToSetting() => new(Tag.Value, Value, Start, End);
}
