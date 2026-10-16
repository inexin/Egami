namespace Egami.Tests;

/// <summary>
/// Which part of a split test class a subclass runs. xUnit runs the rows of one class one after another, so a slow theory class
/// is written as an abstract <c>Name&lt;TPart&gt;</c> with one sealed subclass per part, each running every n-th row; the parts
/// run in parallel like any other classes.
/// </summary>
public interface IPart
{
    /// <summary>Gets the part's index, from 0.</summary>
    static abstract int Index { get; }
}

/// <summary>Part 1 of a split test class.</summary>
public sealed class Part0 : IPart
{
    /// <inheritdoc/>
    public static int Index => 0;
}

/// <summary>Part 2 of a split test class.</summary>
public sealed class Part1 : IPart
{
    /// <inheritdoc/>
    public static int Index => 1;
}

/// <summary>Part 3 of a split test class.</summary>
public sealed class Part2 : IPart
{
    /// <inheritdoc/>
    public static int Index => 2;
}

/// <summary>Part 4 of a split test class.</summary>
public sealed class Part3 : IPart
{
    /// <inheritdoc/>
    public static int Index => 3;
}

/// <summary>Part 5 of a split test class.</summary>
public sealed class Part4 : IPart
{
    /// <inheritdoc/>
    public static int Index => 4;
}

/// <summary>Part 6 of a split test class.</summary>
public sealed class Part5 : IPart
{
    /// <inheritdoc/>
    public static int Index => 5;
}

/// <summary>Picks a part's rows from a theory's data.</summary>
internal static class Parts
{
    /// <summary>Gets every <paramref name="count"/>-th row, starting at row <paramref name="index"/>.</summary>
    internal static TheoryData<T> Take<T>(IEnumerable<T> rows, int index, int count) => [.. rows.Where((_, i) => i % count == index)];

    /// <summary>Gets every <paramref name="count"/>-th row, starting at row <paramref name="index"/>.</summary>
    internal static TheoryData<T1, T2> Take<T1, T2>(TheoryData<T1, T2> rows, int index, int count)
    {
        TheoryData<T1, T2> part = [];
        int i = 0;
        foreach (TheoryDataRow<T1, T2> row in rows)
        {
            if (i++ % count == index)
            {
                part.Add(row.Data.Item1, row.Data.Item2);
            }
        }

        return part;
    }
}
