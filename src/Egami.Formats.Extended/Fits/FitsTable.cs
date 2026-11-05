using System.Globalization;

namespace Egami.Formats.Extended.Fits;

/// <summary>A binary-table column's place in a row and its type, from <c>TFORMn</c>.</summary>
/// <param name="Name">The column's name (<c>TTYPEn</c>), upper case.</param>
/// <param name="Offset">Where the column starts in a row.</param>
/// <param name="Type">The type letter: B, I, J, K, E, D, P or Q among those used here.</param>
/// <param name="Repeat">The repeat count.</param>
/// <param name="Element">For a variable-length array (P or Q), the type of its elements.</param>
internal sealed record FitsColumn(string Name, int Offset, char Type, long Repeat, char Element);

/// <summary>The binary table that holds a tile-compressed image: its rows, heap and the columns the convention defines.</summary>
internal sealed class FitsTable
{
    public required int RowLength { get; init; }

    public required int Rows { get; init; }

    public required long Heap { get; init; }

    public required long HeapLength { get; init; }

    public required IReadOnlyDictionary<string, FitsColumn> Columns { get; init; }

    /// <summary>Reads the table structure from the header, checking every column fits its row.</summary>
    public static FitsTable Read(FitsHeader header, long[] axes, long pcount, long at, EgamiInput input)
    {
        if (axes.Length != 2 || axes[0] > int.MaxValue || axes[1] > int.MaxValue)
        {
            throw input.Corrupt(at, "The tile-compressed image's table does not have two axes.");
        }

        long fields = header.GetInteger("TFIELDS") ?? 0;
        if (fields is < 0 or > 999)
        {
            throw input.Corrupt(at, $"TFIELDS is {fields}.");
        }

        Dictionary<string, FitsColumn> columns = new(StringComparer.Ordinal);
        long offset = 0;
        for (int i = 1; i <= fields; i++)
        {
            string n = i.ToString(CultureInfo.InvariantCulture);
            string form = header.GetString("TFORM" + n)?.Trim() ?? throw input.Corrupt(at, $"TFORM{n} is missing.");
            string name = (header.GetString("TTYPE" + n) ?? "").Trim().ToUpperInvariant();
            (long repeat, char type, char element) = ParseForm(form, at, input);
            long width = type == 'X' ? (repeat + 7) / 8 : repeat * Width(type, element);
            if (name.Length > 0)
            {
                columns.TryAdd(name, new FitsColumn(name, (int)Math.Min(offset, int.MaxValue), type, repeat, element));
            }

            offset += width;
        }

        if (offset > axes[0])
        {
            throw input.Corrupt(at, $"The columns take {offset} bytes, more than the row's {axes[0]}.");
        }

        long heap = header.GetInteger("THEAP") ?? (axes[0] * axes[1]);
        if (heap < axes[0] * axes[1] || heap > (axes[0] * axes[1]) + pcount)
        {
            throw input.Corrupt(at, $"THEAP {heap} is outside the data.");
        }

        return new FitsTable
        {
            RowLength = (int)axes[0],
            Rows = (int)axes[1],
            Heap = heap,
            HeapLength = (axes[0] * axes[1]) + pcount - heap,
            Columns = columns,
        };
    }

    /// <summary>Parses a <c>TFORMn</c> value such as <c>1J</c>, <c>8A</c> or <c>1PB(2048)</c>.</summary>
    private static (long Repeat, char Type, char Element) ParseForm(string form, long at, EgamiInput input)
    {
        int i = 0;
        while (i < form.Length && char.IsAsciiDigit(form[i]))
        {
            i++;
        }

        long repeat = i == 0 ? 1 : long.TryParse(form.AsSpan(0, i), NumberStyles.None, CultureInfo.InvariantCulture, out long r) && r <= int.MaxValue ? r : -1;
        if (repeat < 0 || i >= form.Length)
        {
            throw input.Corrupt(at, $"TFORM '{form}' is not valid.");
        }

        char type = char.ToUpperInvariant(form[i]);
        char element = type is 'P' or 'Q' && i + 1 < form.Length ? char.ToUpperInvariant(form[i + 1]) : ' ';
        if (Width(type, element) < 0)
        {
            throw input.Corrupt(at, $"TFORM '{form}' has an unknown type.");
        }

        return (repeat, type, element);
    }

    /// <summary>Returns the bytes one element of a column type takes in a row, or -1 for an unknown type.</summary>
    internal static long Width(char type, char element) => type switch
    {
        'L' or 'B' or 'A' => 1,
        'X' => 1,
        'I' => 2,
        'J' or 'E' => 4,
        'K' or 'D' or 'C' => 8,
        'M' => 16,
        'P' => element == ' ' ? -1 : 8,
        'Q' => element == ' ' ? -1 : 16,
        _ => -1,
    };
}
