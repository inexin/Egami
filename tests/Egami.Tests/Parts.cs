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

/// <summary>Part 7 of a split test class.</summary>
public sealed class Part6 : IPart
{
    /// <inheritdoc/>
    public static int Index => 6;
}

/// <summary>Part 8 of a split test class.</summary>
public sealed class Part7 : IPart
{
    /// <inheritdoc/>
    public static int Index => 7;
}

/// <summary>Part 9 of a split test class.</summary>
public sealed class Part8 : IPart
{
    /// <inheritdoc/>
    public static int Index => 8;
}

/// <summary>Part 10 of a split test class.</summary>
public sealed class Part9 : IPart
{
    /// <inheritdoc/>
    public static int Index => 9;
}

/// <summary>Part 11 of a split test class.</summary>
public sealed class Part10 : IPart
{
    /// <inheritdoc/>
    public static int Index => 10;
}

/// <summary>Part 12 of a split test class.</summary>
public sealed class Part11 : IPart
{
    /// <inheritdoc/>
    public static int Index => 11;
}

/// <summary>Picks a part's rows from a theory's data.</summary>
internal static class Parts
{
    /// <summary>
    /// Gets every <paramref name="count"/>-th row, starting at row <paramref name="index"/>; with none, one row skipped with
    /// <paramref name="skip"/>, or none.
    /// </summary>
    internal static TheoryData<T> Take<T>(IEnumerable<T> rows, int index, int count, string? skip = null)
    {
        TheoryData<T> part = [.. rows.Where((_, i) => i % count == index)];
        if (part.Count == 0 && skip is not null)
        {
            part.Add(new TheoryDataRow<T>(default!) { Skip = skip });
        }

        return part;
    }

    /// <summary>
    /// Gets every <paramref name="count"/>-th row, starting at row <paramref name="index"/>; with none, one row skipped with
    /// <paramref name="skip"/>, or none.
    /// </summary>
    internal static TheoryData<T1, T2> Take<T1, T2>(TheoryData<T1, T2> rows, int index, int count, string? skip = null)
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

        if (part.Count == 0 && skip is not null)
        {
            part.Add(new TheoryDataRow<T1, T2>(default!, default!) { Skip = skip });
        }

        return part;
    }
}
