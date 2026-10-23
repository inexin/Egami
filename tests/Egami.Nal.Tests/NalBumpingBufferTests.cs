namespace Egami.Nal.Tests;

public class NalBumpingBufferTests
{
    [Fact]
    public void OutputsInPictureOrderWithReordering()
    {
        // Decode order I0 P8 B4 b2 b6 P16 B12 b10 b14, a hierarchical GOP needing two pictures of reordering.
        int[] decodeOrder = [0, 8, 4, 2, 6, 16, 12, 10, 14];
        NalBumpingBuffer<Picture> buffer = new() { Capacity = 8, MaxNumReorder = 2 };
        List<int> output = [];
        foreach (int poc in decodeOrder)
        {
            Picture picture = new(poc) { IsReference = poc % 4 == 0 };
            while (buffer.TryBumpBeforeInsert(out Picture bumped, out int bumpedPoc))
            {
                Assert.Equal(bumped.Poc, bumpedPoc);
                output.Add(bumpedPoc);
            }

            buffer.Insert(picture, poc, needsOutput: true);
            while (buffer.TryBumpAfterInsert(out _, out int bumpedPoc))
            {
                output.Add(bumpedPoc);
            }
        }

        while (buffer.TryFlush(out _, out int poc))
        {
            output.Add(poc);
        }

        Assert.Equal([0, 2, 4, 6, 8, 10, 12, 14, 16], output);
    }

    [Fact]
    public void KeepsReferencesAfterOutputAndDropsTheRest()
    {
        NalBumpingBuffer<Picture> buffer = new() { Capacity = 4, MaxNumReorder = 0 };
        Picture reference = new(0) { IsReference = true };
        Picture other = new(1);
        buffer.Insert(reference, 0, needsOutput: true);
        Assert.True(buffer.TryBumpAfterInsert(out Picture first, out _));
        Assert.Same(reference, first);
        Assert.Equal(1, buffer.Count);
        Assert.Equal(0, buffer.Waiting);

        buffer.Insert(other, 1, needsOutput: true);
        Assert.True(buffer.TryBumpAfterInsert(out Picture second, out _));
        Assert.Same(other, second);
        Assert.Equal([reference], buffer.Pictures());

        reference.IsReference = false;
        buffer.RemoveUnused();
        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void BumpsWhenTheBufferIsFull()
    {
        // No reorder limit: only the capacity makes room.
        NalBumpingBuffer<Picture> buffer = new() { Capacity = 2 };
        buffer.Insert(new Picture(4), 4, needsOutput: true);
        buffer.Insert(new Picture(2), 2, needsOutput: true);
        Assert.False(buffer.TryBumpAfterInsert(out _, out _));
        Assert.True(buffer.TryBumpBeforeInsert(out _, out int poc));
        Assert.Equal(2, poc);
        Assert.False(buffer.TryBumpBeforeInsert(out _, out _));
    }

    [Fact]
    public void CannotBumpAFullBufferOfReferencesThatWereOutput()
    {
        NalBumpingBuffer<Picture> buffer = new() { Capacity = 1 };
        buffer.Insert(new Picture(0) { IsReference = true }, 0, needsOutput: false);
        Assert.False(buffer.TryBumpBeforeInsert(out _, out _));
        Assert.Equal(1, buffer.Count);
    }

    [Fact]
    public void BumpsAPictureThatWaitedTooLong()
    {
        NalBumpingBuffer<Picture> buffer = new() { Capacity = 8, MaxLatencyPictures = 2 };
        buffer.Insert(new Picture(10), 10, needsOutput: true);
        Assert.False(buffer.TryBumpAfterInsert(out _, out _));
        buffer.Insert(new Picture(20), 20, needsOutput: true);
        Assert.False(buffer.TryBumpAfterInsert(out _, out _));
        buffer.Insert(new Picture(30), 30, needsOutput: true);
        Assert.True(buffer.TryBumpAfterInsert(out _, out int poc));
        Assert.Equal(10, poc);
        Assert.False(buffer.TryBumpAfterInsert(out _, out _));
    }

    [Fact]
    public void ClearsWithoutOutput()
    {
        NalBumpingBuffer<Picture> buffer = new();
        buffer.Insert(new Picture(0), 0, needsOutput: true);
        buffer.Clear();
        Assert.False(buffer.TryFlush(out _, out _));
        Assert.Equal(0, buffer.Count);
    }

    private sealed class Picture(int poc) : INalDpbPicture
    {
        public int Poc { get; } = poc;

        public bool IsReference { get; set; }
    }
}
