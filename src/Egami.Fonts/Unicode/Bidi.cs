using System.Buffers;

namespace Egami.Fonts.Unicode;

/// <summary>The direction of a paragraph or a run of text.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Unicode.cs" region="bidi"/>
/// </example>
public enum TextDirection
{
    /// <summary>The paragraph direction follows the first strong character (UAX #9 rules P2 and P3), left to right when there is none.</summary>
    Auto = 0,

    /// <summary>Left to right.</summary>
    LeftToRight = 1,

    /// <summary>Right to left.</summary>
    RightToLeft = 2,

    /// <summary>Top to bottom, for vertical runs; bidi treats it as left to right.</summary>
    TopToBottom = 3,
}

/// <summary>
/// The Unicode Bidirectional Algorithm (UAX #9): embedding levels for mixed left-to-right and right-to-left text, and the visual
/// order of a line.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Unicode.cs" region="bidi"/>
/// </example>
public static class Bidi
{
    [ThreadStatic]
    private static BidiResolver? t_resolver;

    /// <summary>Resolves the embedding level of every UTF-16 unit of a text.</summary>
    /// <param name="text">The text. Each paragraph separator (U+2029, CR, LF and the other Bidi_Class B characters) ends a paragraph,
    /// and each paragraph is resolved on its own (rule P1).</param>
    /// <param name="direction">The paragraph direction; <see cref="TextDirection.Auto"/> takes it from the first strong character of
    /// each paragraph; <see cref="TextDirection.TopToBottom"/> resolves as left to right.</param>
    /// <returns>The levels and runs; dispose it to return its arrays to the pool.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="direction"/> is not a <see cref="TextDirection"/>.</exception>
    public static BidiResult Resolve(ReadOnlySpan<char> text, TextDirection direction = TextDirection.Auto)
    {
        if ((uint)direction > (uint)TextDirection.TopToBottom)
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        int n = text.Length;
        int[] cps = ArrayPool<int>.Shared.Rent(Math.Max(n, 1));
        int[] firstUnit = ArrayPool<int>.Shared.Rent(Math.Max(n + 1, 1));
        BidiClass[] cpClasses = ArrayPool<BidiClass>.Shared.Rent(Math.Max(n, 1));
        byte[] cpLevels = ArrayPool<byte>.Shared.Rent(Math.Max(n, 1));
        try
        {
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                int cp = text[i];
                firstUnit[count] = i;
                if (char.IsHighSurrogate(text[i]) && i + 1 < n && char.IsLowSurrogate(text[i + 1]))
                {
                    cp = char.ConvertToUtf32(text[i], text[i + 1]);
                    i++;
                }

                cps[count] = cp;
                cpClasses[count] = UnicodeData.BidiClassOf(cp);
                count++;
            }

            firstUnit[count] = n;

            BidiResult result = new(n);
            BidiResolver resolver = t_resolver ??= new BidiResolver();
            int start = 0;
            ReadOnlyMemory<int> memory = cps.AsMemory(0, count);
            while (start < count)
            {
                int end = start;
                while (end < count && cpClasses[end] != BidiClass.ParagraphSeparator)
                {
                    end++;
                }

                end = Math.Min(end + 1, count);
                resolver.Resolve(cpClasses.AsSpan(start, end - start), memory[start..end], direction, cpLevels.AsSpan(start, end - start));
                result.AddParagraph(firstUnit[start], firstUnit[end], resolver.ParagraphLevel);
                start = end;
            }

            // Expand code point levels and classes to UTF-16 units.
            for (int c = 0; c < count; c++)
            {
                for (int u = firstUnit[c]; u < firstUnit[c + 1]; u++)
                {
                    result.SetUnit(u, cpLevels[c], cpClasses[c]);
                }
            }

            result.Complete(count == 0 && direction == TextDirection.RightToLeft ? 1 : 0);
            return result;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(cps);
            ArrayPool<int>.Shared.Return(firstUnit);
            ArrayPool<BidiClass>.Shared.Return(cpClasses);
            ArrayPool<byte>.Shared.Return(cpLevels);
        }
    }
}

/// <summary>A run of UTF-16 units at one embedding level.</summary>
/// <param name="Start">The index of the run's first UTF-16 unit.</param>
/// <param name="Length">The number of UTF-16 units.</param>
/// <param name="Level">The embedding level: even for left to right, odd for right to left.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Unicode.cs" region="bidi"/>
/// </example>
public readonly record struct BidiRun(int Start, int Length, int Level)
{
    /// <summary>Gets whether the run is drawn right to left (its level is odd).</summary>
    public bool IsRightToLeft => (Level & 1) != 0;
}

/// <summary>
/// The result of <see cref="Bidi.Resolve(ReadOnlySpan{char}, TextDirection)"/>: a level per UTF-16 unit, the runs in logical order,
/// and the visual order of any line. Dispose it to return its arrays to the pool.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Unicode.cs" region="bidi"/>
/// </example>
public sealed class BidiResult : IDisposable
{
    private readonly List<(int Start, int End, int Level)> _paragraphs = [];
    private byte[]? _resolved;
    private byte[]? _levels;
    private BidiClass[]? _classes;
    private BidiRun[] _runs = [];
    private int _runCount;
    private int _paragraphLevel;

    internal BidiResult(int length)
    {
        Length = length;
        _resolved = ArrayPool<byte>.Shared.Rent(Math.Max(length, 1));
        _levels = ArrayPool<byte>.Shared.Rent(Math.Max(length, 1));
        _classes = ArrayPool<BidiClass>.Shared.Rent(Math.Max(length, 1));
    }

    /// <summary>Gets the number of UTF-16 units resolved.</summary>
    public int Length { get; }

    /// <summary>Gets the embedding level of the first paragraph: 0 for left to right, 1 for right to left.</summary>
    public int ParagraphLevel => _paragraphLevel;

    /// <summary>
    /// Gets the level of every UTF-16 unit, with each paragraph laid out as one line (rule L1 applied: separators and trailing
    /// whitespace take the paragraph level). Units that UAX #9 removes (embedding controls and boundary neutrals) take the level of
    /// the unit before them.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The result was disposed.</exception>
    public ReadOnlySpan<byte> Levels => Alive(_levels).AsSpan(0, Length);

    /// <summary>Gets the runs of equal <see cref="Levels"/>, in logical order.</summary>
    /// <exception cref="ObjectDisposedException">The result was disposed.</exception>
    public ReadOnlySpan<BidiRun> Runs
    {
        get
        {
            Alive(_levels);
            return _runs.AsSpan(0, _runCount);
        }
    }

    /// <summary>Gets the original Bidi_Class of every UTF-16 unit.</summary>
    internal ReadOnlySpan<BidiClass> Classes => Alive(_classes).AsSpan(0, Length);

    /// <summary>
    /// Orders one line for display (rules L1 and L2): splits it into runs of equal level, with the line's trailing whitespace at
    /// the paragraph level, and returns the runs from left to right. The units of a right-to-left run are drawn last to first.
    /// </summary>
    /// <param name="start">The index of the line's first UTF-16 unit.</param>
    /// <param name="length">The number of UTF-16 units in the line.</param>
    /// <returns>The runs in visual order.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The line is not inside the text.</exception>
    /// <exception cref="ObjectDisposedException">The result was disposed.</exception>
    public BidiRun[] Reorder(int start, int length)
    {
        byte[] resolved = Alive(_resolved);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, Length - start);
        if (length == 0)
        {
            return [];
        }

        byte[] line = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            Span<byte> levels = line.AsSpan(0, length);
            resolved.AsSpan(start, length).CopyTo(levels);
            foreach ((int pStart, int pEnd, int pLevel) in _paragraphs)
            {
                int s = Math.Max(pStart, start);
                int e = Math.Min(pEnd, start + length);
                if (s < e)
                {
                    BidiResolver.ApplyLineRule(_classes.AsSpan(s, e - s), levels.Slice(s - start, e - s), pLevel);
                }
            }

            List<BidiRun> runs = [];
            int highest = 0;
            int lowestOdd = int.MaxValue;
            int runStart = 0;
            for (int i = 1; i <= length; i++)
            {
                if (i == length || levels[i] != levels[runStart])
                {
                    int level = levels[runStart];
                    runs.Add(new BidiRun(start + runStart, i - runStart, level));
                    highest = Math.Max(highest, level);
                    if ((level & 1) != 0)
                    {
                        lowestOdd = Math.Min(lowestOdd, level);
                    }

                    runStart = i;
                }
            }

            // L2: from the highest level down to the lowest odd level, reverse every sequence of runs at that level or higher.
            for (int level = highest; level >= lowestOdd && level > 0; level--)
            {
                for (int i = 0; i < runs.Count; i++)
                {
                    if (runs[i].Level < level)
                    {
                        continue;
                    }

                    int end = i;
                    while (end < runs.Count && runs[end].Level >= level)
                    {
                        end++;
                    }

                    runs.Reverse(i, end - i);
                    i = end;
                }
            }

            return [.. runs];
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(line);
        }
    }

    /// <summary>Returns the arrays to the pool.</summary>
    public void Dispose()
    {
        if (_resolved is not null)
        {
            ArrayPool<byte>.Shared.Return(_resolved);
            ArrayPool<byte>.Shared.Return(_levels!);
            ArrayPool<BidiClass>.Shared.Return(_classes!);
            _resolved = null;
            _levels = null;
            _classes = null;
        }
    }

    internal void AddParagraph(int start, int end, int level)
    {
        if (_paragraphs.Count == 0)
        {
            _paragraphLevel = level;
        }

        _paragraphs.Add((start, end, level));
    }

    internal void SetUnit(int unit, byte level, BidiClass bidiClass)
    {
        _resolved![unit] = level;
        _classes![unit] = bidiClass;
    }

    internal void Complete(int emptyParagraphLevel)
    {
        if (_paragraphs.Count == 0)
        {
            _paragraphLevel = emptyParagraphLevel;
        }

        Span<byte> levels = _levels.AsSpan(0, Length);
        _resolved.AsSpan(0, Length).CopyTo(levels);
        foreach ((int start, int end, int level) in _paragraphs)
        {
            BidiResolver.ApplyLineRule(_classes.AsSpan(start, end - start), levels[start..end], level);
        }

        List<BidiRun> runs = [];
        int runStart = 0;
        for (int i = 1; i <= Length; i++)
        {
            if (i == Length || levels[i] != levels[runStart])
            {
                runs.Add(new BidiRun(runStart, i - runStart, levels[runStart]));
                runStart = i;
            }
        }

        _runs = [.. runs];
        _runCount = _runs.Length;
    }

    private static T[] Alive<T>(T[]? array) => array ?? throw new ObjectDisposedException(nameof(BidiResult));
}
