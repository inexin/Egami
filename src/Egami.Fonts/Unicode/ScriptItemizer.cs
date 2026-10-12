namespace Egami.Fonts.Unicode;

/// <summary>A run of text in one script.</summary>
/// <param name="Start">The index of the run's first UTF-16 unit.</param>
/// <param name="Length">The number of UTF-16 units.</param>
/// <param name="Script">The script.</param>
internal readonly record struct ScriptRun(int Start, int Length, Script Script);

/// <summary>
/// Splits text into script runs (UAX #24). Each run keeps the scripts every character so far is used with: a character's
/// Script_Extensions, or all scripts for Common and Inherited characters without extensions. A character whose scripts share none
/// with the run starts a new one. A run takes the one script left, or Common when several remain (only shared characters, such as a lone danda). A
/// closing bracket takes the script of its opening bracket.
/// </summary>
internal static class ScriptItemizer
{
    private const int MaxBrackets = 64;

    public static void Itemize(ReadOnlySpan<char> text, List<ScriptRun> runs)
    {
        Span<byte> candidates = stackalloc byte[Script.Count];
        Span<byte> scratch = stackalloc byte[Script.Count];
        Span<int> bracketClose = stackalloc int[MaxBrackets];
        Span<int> bracketRun = stackalloc int[MaxBrackets];
        int brackets = 0;
        int candidateCount = -1; // -1: every script (only shared characters so far).
        int runStart = 0;
        int firstRun = runs.Count;
        int pos = 0;
        while (pos < text.Length)
        {
            int cp = TextSegmenter.CodePointAt(text, pos, out int size);
            ReadOnlySpan<byte> scripts = UnicodeData.ScriptExtensions(cp);
            bool universal = scripts.Length == 1 && scripts[0] <= 2;
            Script? forced = null;

            BidiPairedBracketType bracket = UnicodeData.GetBidiPairedBracketType(cp);
            if (bracket == BidiPairedBracketType.Close)
            {
                int canonical = Canonical(cp);
                for (int d = brackets - 1; d >= 0; d--)
                {
                    if (bracketClose[d] == canonical)
                    {
                        // The opening bracket's run is finished when its index is below the runs so far; else it is the current run.
                        if (bracketRun[d] < runs.Count)
                        {
                            forced = runs[bracketRun[d]].Script;
                        }

                        brackets = d;
                        break;
                    }
                }
            }

            if (forced is { IsShared: true })
            {
                forced = null;
                universal = true;
            }

            bool joins;
            if (forced is { } script)
            {
                joins = candidateCount < 0 || candidates[..candidateCount].Contains((byte)script.Index);
            }
            else if (universal || candidateCount < 0)
            {
                joins = true;
            }
            else
            {
                joins = Intersect(candidates[..candidateCount], scripts, scratch) > 0;
            }

            if (!joins)
            {
                runs.Add(new ScriptRun(runStart, pos - runStart, Resolve(candidates, candidateCount)));
                runStart = pos;
                candidateCount = -1;
            }

            if (forced is { } closing)
            {
                candidates[0] = (byte)closing.Index;
                candidateCount = 1;
            }
            else if (!universal)
            {
                if (candidateCount < 0)
                {
                    scripts.CopyTo(candidates);
                    candidateCount = scripts.Length;
                }
                else
                {
                    candidateCount = Intersect(candidates[..candidateCount], scripts, scratch);
                    scratch[..candidateCount].CopyTo(candidates);
                }
            }

            if (bracket == BidiPairedBracketType.Open)
            {
                if (brackets == MaxBrackets)
                {
                    bracketClose[1..].CopyTo(bracketClose);
                    bracketRun[1..].CopyTo(bracketRun);
                    brackets--;
                }

                bracketClose[brackets] = Canonical(UnicodeData.GetBidiPairedBracket(cp));
                bracketRun[brackets] = runs.Count;
                brackets++;
            }

            pos += size;
        }

        if (pos > runStart)
        {
            runs.Add(new ScriptRun(runStart, pos - runStart, Resolve(candidates, candidateCount)));
        }

        // Adjacent runs that resolved to the same script join.
        for (int i = runs.Count - 1; i > firstRun; i--)
        {
            if (runs[i].Script == runs[i - 1].Script)
            {
                runs[i - 1] = runs[i - 1] with { Length = runs[i - 1].Length + runs[i].Length };
                runs.RemoveAt(i);
            }
        }
    }

    private static Script Resolve(ReadOnlySpan<byte> candidates, int count) => count == 1 ? new Script(candidates[0]) : Script.Common;

    private static int Intersect(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> destination)
    {
        int count = 0;
        foreach (byte x in a)
        {
            if (b.Contains(x))
            {
                destination[count++] = x;
            }
        }

        return count;
    }

    private static int Canonical(int codePoint)
    {
        int row = UnicodeData.FindPair(UnicodeTables.Decompositions, 3, codePoint);
        return row >= 0 && UnicodeTables.Decompositions[row + 2] == 0 ? UnicodeTables.Decompositions[row + 1] : codePoint;
    }
}
