using System.Text;

namespace Egami.Fonts.Unicode;

/// <summary>
/// A Unicode script, such as Latin, Arabic or Devanagari, with its ISO 15924 code and the OpenType script tags fonts use for it.
/// </summary>
/// <remarks>The default value is <see cref="Unknown"/>. There is one static member per script of the Unicode version Egami's
/// tables follow (<see cref="UnicodeData.UnicodeVersion"/>).</remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Unicode.cs" region="properties"/>
/// </example>
public readonly partial record struct Script
{
    private static readonly byte[] s_identity = CreateIdentity();

    private readonly byte _index;

    internal Script(int index) => _index = (byte)index;

    /// <summary>Gets the four-letter ISO 15924 code, such as <c>"Latn"</c>, <c>"Arab"</c> or <c>"Zyyy"</c> for Common.</summary>
    public string Code => Encoding.ASCII.GetString(CodeBytes.Slice(_index * 4, 4));

    /// <summary>Gets the Unicode name of the script, such as <c>"Latin"</c> or <c>"Old_Italic"</c>.</summary>
    public string Name => s_names[_index];

    /// <summary>
    /// Gets the OpenType script tags fonts use for the script, newest first: <c>dev2</c> then <c>deva</c> for Devanagari, <c>latn</c>
    /// for Latin, and <c>DFLT</c> for Common, Inherited and Unknown.
    /// </summary>
    public ReadOnlySpan<FontTag> OpenTypeTags => s_tags.AsSpan(TagStarts[_index], TagStarts[_index + 1] - TagStarts[_index]);

    /// <summary>Gets whether the script is Common, Inherited or Unknown: shared characters that take the script around them.</summary>
    public bool IsShared => _index <= 2;

    /// <summary>Gets the index of the script in Egami's tables.</summary>
    internal int Index => _index;

    /// <summary>Finds a script by its ISO 15924 code, such as <c>"Arab"</c>; case does not matter.</summary>
    /// <param name="code">The four-letter code.</param>
    /// <param name="script">The script, or <see cref="Unknown"/> when no script has the code.</param>
    /// <returns><see langword="true"/> when a script has the code.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="code"/> is <see langword="null"/>.</exception>
    public static bool TryFromCode(string code, out Script script)
    {
        ArgumentNullException.ThrowIfNull(code);
        script = Unknown;
        if (code.Length != 4)
        {
            return false;
        }

        ReadOnlySpan<byte> codes = CodeBytes;
        for (int i = 0; i < Count; i++)
        {
            bool match = true;
            for (int j = 0; j < 4 && match; j++)
            {
                match = char.ToLowerInvariant(code[j]) == char.ToLowerInvariant((char)codes[(i * 4) + j]);
            }

            if (match)
            {
                script = new Script(i);
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns the ISO 15924 code.</summary>
    /// <returns>The code, such as <c>"Latn"</c>.</returns>
    public override string ToString() => Code;

    /// <summary>A one-script span for a script index, so a code point without extensions has a span like one with them.</summary>
    internal static ReadOnlySpan<byte> IndexSpan(int index) => s_identity.AsSpan(index, 1);

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Code = ").Append(Code);
        return true;
    }

    private static byte[] CreateIdentity()
    {
        byte[] identity = new byte[Count];
        for (int i = 0; i < identity.Length; i++)
        {
            identity[i] = (byte)i;
        }

        return identity;
    }
}
