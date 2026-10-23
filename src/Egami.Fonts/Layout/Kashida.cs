using Egami.Fonts.Unicode;

namespace Egami.Fonts.Layout;

/// <summary>
/// Where kashida justification lengthens a word (2.8.3 design questions 4 and 6): the scripts that join with U+0640 ARABIC TATWEEL,
/// and how good each point between two joined letters is, best first, after the order Microsoft documents for Arabic
/// justification and the W3C Arabic Layout Requirements describe.
/// </summary>
internal static class Kashida
{
    /// <summary>U+0640 ARABIC TATWEEL, the character whose glyph lengthens a joined word.</summary>
    public const int Tatweel = 0x0640;

    /// <summary>The worst priority: a safe point that no rule prefers.</summary>
    public const int Anywhere = 8;

    /// <summary>The scripts whose letters join with U+0640 (its Script_Extensions), whose words kashida lengthens.</summary>
    public static bool Joins(Script script) =>
        script == Script.Arabic || script == Script.Syriac || script == Script.Mandaic || script == Script.Manichaean
        || script == Script.PsalterPahlavi || script == Script.Sogdian || script == Script.HanifiRohingya || script == Script.Adlam
        || script == Script.OldUyghur;

    /// <summary>The most tatweels a word takes at a level before word separators take the rest.</summary>
    public static int Cap(KashidaLevel level) => level switch
    {
        KashidaLevel.Low => 1,
        KashidaLevel.High => 4,
        _ => 2,
    };

    /// <summary>
    /// How good the point between the letter at <paramref name="before"/> and the joined letter at <paramref name="after"/> is for a
    /// tatweel, from 1 (best) to <see cref="Anywhere"/>:
    /// 1 after a tatweel already in the text; 2 after Seen or Sad; 3 before a final Heh, Teh Marbuta or Dal; 4 before a final Alef,
    /// Tah, Lam, Kaf or Gaf; 5 between Beh and a final Reh or Yeh; 6 before a final Waw, Ain, Qaf or Feh; 7 before any other final
    /// letter; 8 anywhere else the shaping allows.
    /// </summary>
    public static int Priority(ReadOnlySpan<char> text, int before, int after)
    {
        int a = TextSegmenter.CodePointAt(text, before, out _);
        int b = TextSegmenter.CodePointAt(text, after, out int size);
        if (a == Tatweel)
        {
            return 1;
        }

        JoiningGroup first = UnicodeData.GetJoiningGroup(a);
        if (first is JoiningGroup.Seen or JoiningGroup.Sad)
        {
            return 2;
        }

        if (!IsFinal(text, b, after + size))
        {
            return Anywhere;
        }

        return UnicodeData.GetJoiningGroup(b) switch
        {
            JoiningGroup.Heh or JoiningGroup.HehGoal or JoiningGroup.KnottedHeh or JoiningGroup.TehMarbuta or JoiningGroup.TehMarbutaGoal
                or JoiningGroup.Dal => 3,
            JoiningGroup.Alef or JoiningGroup.Tah or JoiningGroup.Lam or JoiningGroup.Kaf or JoiningGroup.SwashKaf or JoiningGroup.Gaf => 4,
            JoiningGroup.Reh or JoiningGroup.Yeh or JoiningGroup.FarsiYeh when first == JoiningGroup.Beh => 5,
            JoiningGroup.Waw or JoiningGroup.Ain or JoiningGroup.Qaf or JoiningGroup.AfricanQaf or JoiningGroup.Feh or JoiningGroup.AfricanFeh => 6,
            _ => 7,
        };
    }

    /// <summary>Whether a letter joined on its right ends its word's joining: it cannot join the next letter that is not a mark,
    /// or there is none.</summary>
    private static bool IsFinal(ReadOnlySpan<char> text, int letter, int next)
    {
        if (UnicodeData.GetJoiningType(letter) is not (JoiningType.DualJoining or JoiningType.JoinCausing))
        {
            return true;
        }

        while (next < text.Length)
        {
            int cp = TextSegmenter.CodePointAt(text, next, out int size);
            JoiningType type = UnicodeData.GetJoiningType(cp);
            if (type != JoiningType.Transparent)
            {
                return type is not (JoiningType.DualJoining or JoiningType.RightJoining or JoiningType.JoinCausing);
            }

            next += size;
        }

        return true;
    }
}
