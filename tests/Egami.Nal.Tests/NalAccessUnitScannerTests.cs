namespace Egami.Nal.Tests;

public class NalAccessUnitScannerTests
{
    [Fact]
    public void StartsAnAccessUnitAtTheFirstPrefixOrFirstSliceAfterASlice()
    {
        // AUD SPS PPS SEI slice slice suffix | SEI slice | slice slice | AUD slice end-of-stream
        (int Id, NalUnitRole Role)[] stream =
        [
            (1, NalUnitRole.Prefix), (2, NalUnitRole.Prefix), (3, NalUnitRole.Prefix), (4, NalUnitRole.Prefix),
            (5, NalUnitRole.FirstSlice), (6, NalUnitRole.Slice), (7, NalUnitRole.Continues),
            (8, NalUnitRole.Prefix), (9, NalUnitRole.FirstSlice),
            (10, NalUnitRole.FirstSlice), (11, NalUnitRole.Slice),
            (12, NalUnitRole.Prefix), (13, NalUnitRole.FirstSlice), (14, NalUnitRole.Continues),
        ];
        NalAccessUnitScanner scanner = new();
        List<int[]> units = [];
        foreach ((int id, NalUnitRole role) in stream)
        {
            if (scanner.Add(new byte[] { (byte)id }, role) is { } completed)
            {
                units.Add([.. completed.Select(u => (int)u.Span[0])]);
            }
        }

        Assert.Equal(3, scanner.Pending);
        units.Add([.. scanner.Flush()!.Select(u => (int)u.Span[0])]);
        Assert.Null(scanner.Flush());
        Assert.Equal<int[]>([[1, 2, 3, 4, 5, 6, 7], [8, 9], [10, 11], [12, 13, 14]], units);
    }

    [Fact]
    public void KeepsUnitsBeforeAnySliceTogether()
    {
        NalAccessUnitScanner scanner = new();
        Assert.Null(scanner.Add(new byte[1], NalUnitRole.Continues));
        Assert.Null(scanner.Add(new byte[1], NalUnitRole.Prefix));
        Assert.Null(scanner.Add(new byte[1], NalUnitRole.Slice));
        Assert.Null(scanner.Add(new byte[1], NalUnitRole.Slice));
        Assert.Equal(4, scanner.Add(new byte[1], NalUnitRole.FirstSlice)!.Count);
        scanner.Reset();
        Assert.Equal(0, scanner.Pending);
    }

    [Fact]
    public void RefusesAnEndlessAccessUnit()
    {
        NalAccessUnitScanner scanner = new(maxUnits: 3);
        scanner.Add(new byte[1], NalUnitRole.FirstSlice);
        scanner.Add(new byte[1], NalUnitRole.Slice);
        scanner.Add(new byte[1], NalUnitRole.Slice);
        Assert.Throws<CorruptDataException>(() => scanner.Add(new byte[1], NalUnitRole.Slice));
    }
}
