using System.Diagnostics.CodeAnalysis;

using Egami.Fonts.Unicode;

namespace Egami.Fonts.Layout;

/// <summary>
/// Word dictionaries by script, for breaking text written without spaces between words. Add the dictionaries before laying
/// text out; reading from several threads is safe while nothing is added.
/// </summary>
/// <remarks>
/// <see cref="BuiltIn"/> holds the word lists Egami.Fonts ships, each loaded the first time a lookup finds it. To add scripts
/// to them or replace one, copy them with <see cref="WordDictionaries(WordDictionaries)"/>; <see cref="None"/> breaks no
/// words by dictionary.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="word-dictionaries"/>
/// </example>
public sealed class WordDictionaries
{
    private const string ResourcePrefix = "Egami.Fonts.WordBreaking.";
    private const string ResourceSuffix = ".edict";

    private static readonly Lazy<WordDictionaries> s_builtIn = new(CreateBuiltIn);
    private static readonly WordDictionaries s_none = new([], readOnly: true);

    private readonly Dictionary<Script, Lazy<WordDictionary>> _byScript;
    private readonly bool _readOnly;

    /// <summary>Makes an empty set of dictionaries.</summary>
    public WordDictionaries()
    {
        _byScript = [];
    }

    /// <summary>Makes a set that starts with <paramref name="source"/>'s dictionaries (sharing them, loaded or not), to add
    /// to.</summary>
    /// <param name="source">The dictionaries to start with, such as <see cref="BuiltIn"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public WordDictionaries(WordDictionaries source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _byScript = new(source._byScript);
    }

    private WordDictionaries(Dictionary<Script, Lazy<WordDictionary>> byScript, bool readOnly)
    {
        _byScript = byScript;
        _readOnly = readOnly;
    }

    /// <summary>
    /// Gets the dictionaries Egami.Fonts ships: ICU's word lists for Thai, Lao, Khmer and Myanmar, the ones browsers break
    /// these scripts with (their licences are in THIRD-PARTY-NOTICES.md). Each loads the first time a lookup needs it. The set
    /// is shared and read-only.
    /// </summary>
    public static WordDictionaries BuiltIn => s_builtIn.Value;

    /// <summary>Gets an empty, read-only set: no text breaks between words by dictionary.</summary>
    public static WordDictionaries None => s_none;

    /// <summary>Gets the scripts dictionaries were added for.</summary>
    public IReadOnlyCollection<Script> Scripts => _byScript.Keys;

    /// <summary>Gets whether <see cref="Add"/> throws: true for <see cref="BuiltIn"/> and <see cref="None"/>, which are
    /// shared.</summary>
    public bool IsReadOnly => _readOnly;

    /// <summary>Adds or replaces the dictionary for a script.</summary>
    /// <param name="script">The script, such as <see cref="Script.Thai"/>.</param>
    /// <param name="dictionary">The dictionary.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dictionary"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The set is <see cref="IsReadOnly">read-only</see>.</exception>
    public void Add(Script script, WordDictionary dictionary)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        if (_readOnly)
        {
            throw new InvalidOperationException(
                "The built-in word dictionaries are shared; add to a copy: new WordDictionaries(WordDictionaries.BuiltIn).");
        }

        _byScript[script] = new Lazy<WordDictionary>(dictionary);
    }

    /// <summary>Finds the dictionary for a script, loading a built-in one on first use.</summary>
    /// <param name="script">The script.</param>
    /// <param name="dictionary">The dictionary found.</param>
    /// <returns>Whether the set has one for the script.</returns>
    public bool TryGet(Script script, [NotNullWhen(true)] out WordDictionary? dictionary)
    {
        if (_byScript.Count != 0 && _byScript.TryGetValue(script, out Lazy<WordDictionary>? found))
        {
            dictionary = found.Value;
            return true;
        }

        dictionary = null;
        return false;
    }

    private static WordDictionaries CreateBuiltIn()
    {
        Dictionary<Script, Lazy<WordDictionary>> byScript = new()
        {
            [Script.Thai] = new(() => LoadBuiltIn("th")),
            [Script.Lao] = new(() => LoadBuiltIn("lo")),
            [Script.Khmer] = new(() => LoadBuiltIn("km")),
            [Script.Myanmar] = new(() => LoadBuiltIn("my")),
        };
        return new WordDictionaries(byScript, readOnly: true);
    }

    /// <summary>Reads the built-in dictionary named <paramref name="name"/> (th, lo, km or my).</summary>
    internal static WordDictionary LoadBuiltIn(string name)
    {
        using Stream stream = typeof(WordDictionaries).Assembly.GetManifestResourceStream(ResourcePrefix + name + ResourceSuffix)
            ?? throw new ArgumentException($"No built-in word dictionary '{name}'.", nameof(name));
        return WordDictionary.LoadTrusted(stream);
    }
}
