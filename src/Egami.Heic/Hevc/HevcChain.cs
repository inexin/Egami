namespace Egami.Heic.Hevc;

/// <summary>A slice segment of the picture: its NAL unit and header, and the coding tree blocks it covers in tile scan.</summary>
internal sealed class HevcSegment(HevcNal nal, HevcSliceHeader header, HevcSliceHeader slice, int sliceAddrRs, int[] starts, int firstTs)
{
    internal HevcNal Nal { get; } = nal;

    internal HevcSliceHeader Header { get; } = header;

    /// <summary>Gets the header of the independent slice segment the segment belongs to.</summary>
    internal HevcSliceHeader Slice { get; } = slice;

    /// <summary>Gets SliceAddrRs: the address of the independent slice segment's first CTB.</summary>
    internal int SliceAddrRs { get; } = sliceAddrRs;

    /// <summary>Gets the RBSP positions where the substreams start: the slice data, then each entry point.</summary>
    internal int[] Starts { get; } = starts;

    /// <summary>Gets the tile-scan address of the first CTB.</summary>
    internal int FirstTs { get; } = firstTs;

    /// <summary>Gets the tile-scan address after the last CTB: where the next segment starts.</summary>
    internal int EndTs { get; set; }
}

/// <summary>
/// Coding tree blocks that decode one after another on one thread: a tile, or with wavefronts a CTB row of a tile. Chains share
/// nothing but samples and block maps across tile boundaries, which they never read; a wavefront row waits for the row above
/// to be two CTBs ahead, and takes its contexts after that row's second CTB (9.3.2.2).
/// </summary>
internal sealed class HevcChain
{
    private int _progress;

    /// <summary>Gets the parts of slice segments the chain decodes, in order: one substream each.</summary>
    internal List<(HevcSegment Segment, int Substream, int FirstTs, int EndTs)> Pieces { get; } = [];

    /// <summary>Gets or sets the chain of the CTB row above in the same tile (with wavefronts), or null.</summary>
    internal HevcChain? Above { get; set; }

    /// <summary>Gets or sets the width of the chain's tile in CTBs.</summary>
    internal int TileWidth { get; set; }

    /// <summary>Gets the contexts stored after the second CTB of the row, for the row below.</summary>
    internal byte[] WppContexts { get; } = new byte[HevcContexts.Count];

    /// <summary>Gets the StatCoeff values stored with <see cref="WppContexts"/>.</summary>
    internal int[] WppStatCoeff { get; } = new int[4];

    /// <summary>Gets how many CTBs of the chain are decoded.</summary>
    internal int Progress => Volatile.Read(ref _progress);

    /// <summary>Counts one more decoded CTB and wakes the chain below.</summary>
    internal void Advance(bool shared)
    {
        Volatile.Write(ref _progress, _progress + 1);
        if (shared)
        {
            lock (this)
            {
                Monitor.PulseAll(this);
            }
        }
    }

    /// <summary>
    /// Waits until <paramref name="count"/> CTBs of the chain are decoded. Returns false when another chain failed, so this one
    /// stops too.
    /// </summary>
    internal bool WaitFor(int count, HevcChainSet set)
    {
        while (Progress < count)
        {
            set.CancellationToken.ThrowIfCancellationRequested();
            if (set.Failed)
            {
                return false;
            }

            lock (this)
            {
                if (Progress < count && !set.Failed)
                {
                    Monitor.Wait(this, 20);
                }
            }
        }

        return true;
    }

    /// <summary>Wakes the chains waiting for this one (after a failure).</summary>
    internal void Wake()
    {
        lock (this)
        {
            Monitor.PulseAll(this);
        }
    }
}

/// <summary>The chains of a picture, decoded on up to <see cref="Parallelism"/> threads.</summary>
internal sealed class HevcChainSet(List<HevcChain> chains, int parallelism, CancellationToken cancellationToken)
{
    private int _failed;
    private int _next;

    internal List<HevcChain> Chains { get; } = chains;

    internal int Parallelism { get; } = parallelism;

    internal CancellationToken CancellationToken { get; } = cancellationToken;

    /// <summary>Gets whether a chain failed.</summary>
    internal bool Failed => Volatile.Read(ref _failed) != 0;

    /// <summary>Gets whether chains run on several threads, so they wait for each other.</summary>
    internal bool Shared => Parallelism > 1 && Chains.Count > 1;

    /// <summary>
    /// Decodes every chain into the picture. Threads take chains in tile-scan order, so a chain only ever waits for one a running
    /// thread has already taken; the output does not depend on the number of threads.
    /// </summary>
    internal void Decode(HevcPicture picture)
    {
        if (!Shared)
        {
            HevcSliceDecoder decoder = new(picture);
            foreach (HevcChain chain in Chains)
            {
                CancellationToken.ThrowIfCancellationRequested();
                decoder.DecodeChain(chain, this);
            }

            return;
        }

        int threads = Math.Min(Parallelism, Chains.Count);
        HevcThreads.For(threads, threads, _ =>
        {
            HevcSliceDecoder decoder = new(picture);
            while (true)
            {
                int index = Interlocked.Increment(ref _next) - 1;
                if (index >= Chains.Count || Failed)
                {
                    return;
                }

                try
                {
                    if (!decoder.DecodeChain(Chains[index], this))
                    {
                        return;
                    }
                }
                catch
                {
                    Volatile.Write(ref _failed, 1);
                    foreach (HevcChain chain in Chains)
                    {
                        chain.Wake();
                    }

                    throw;
                }
            }
        }, CancellationToken);
    }
}

/// <summary>Runs work on a bounded number of threads, rethrowing the first exception as it would be thrown on one thread.</summary>
internal static class HevcThreads
{
    internal static void For(int count, int parallelism, Action<int> body, CancellationToken cancellationToken)
    {
        if (parallelism > 1 && count > 1)
        {
            try
            {
                Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken }, body);
            }
            catch (AggregateException e) when (e.InnerExceptions.Count > 0)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
            }

            return;
        }

        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            body(i);
        }
    }
}
