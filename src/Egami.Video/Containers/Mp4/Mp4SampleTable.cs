namespace Egami.Video.Containers.Mp4;

/// <summary>One sample: where its bytes are, its decode time, composition offset and duration (in the media timescale), and flags.</summary>
/// <param name="Index">The sample's number in decoding order, from 0.</param>
/// <param name="Offset">Where its bytes start in the file.</param>
/// <param name="Size">Its size in bytes.</param>
/// <param name="DecodeTime">Its decode time.</param>
/// <param name="CompositionOffset">Its presentation time minus its decode time.</param>
/// <param name="Duration">Its duration.</param>
/// <param name="IsSync">Whether a decoder can start at it.</param>
/// <param name="Description">Its sample description, from 1.</param>
internal readonly record struct Mp4Sample(long Index, long Offset, int Size, long DecodeTime, long CompositionOffset, long Duration, bool IsSync, int Description);

/// <summary>
/// A track's sample tables (ISO/IEC 14496-12 8.6 and 8.7) kept as the file stores them: runs of times, offsets and chunks and one
/// size a sample, so a long file costs about four bytes a sample. A <see cref="Cursor"/> walks the samples in decoding order.
/// </summary>
internal sealed class Mp4SampleTable
{
    /// <summary>Gets or sets the stts runs: how many samples, and each one's duration.</summary>
    public (uint Count, uint Delta)[] Times { get; set; } = [];

    /// <summary>Gets or sets the ctts runs: how many samples, and each one's composition offset (signed in version 1).</summary>
    public (uint Count, int Offset)[] Compositions { get; set; } = [];

    /// <summary>Gets or sets the stsc runs: the first chunk (from 1), samples a chunk and the sample description (from 1).</summary>
    public (uint FirstChunk, uint Samples, uint Description)[] Chunks { get; set; } = [];

    /// <summary>Gets or sets where each chunk starts (stco or co64).</summary>
    public long[] ChunkOffsets { get; set; } = [];

    /// <summary>Gets or sets each sample's size, or empty when all have <see cref="ConstantSize"/>.</summary>
    public int[] Sizes { get; set; } = [];

    /// <summary>Gets or sets the size of every sample when stsz gives one, else 0.</summary>
    public int ConstantSize { get; set; }

    /// <summary>Gets or sets the number of samples.</summary>
    public long Count { get; set; }

    /// <summary>Gets or sets the sync samples (stss, from 1, ascending), or <see langword="null"/> when every sample is one.</summary>
    public uint[]? SyncSamples { get; set; }

    /// <summary>
    /// Gets or sets whether a chunk's samples are read as one: uncompressed audio stores one sample a PCM frame, so each chunk
    /// becomes one packet, as FFmpeg reads it.
    /// </summary>
    public bool GroupChunks { get; set; }

    /// <summary>Gets or sets sdtp's byte a sample, or empty.</summary>
    public byte[] Dependencies { get; set; } = [];

    /// <summary>Gets the number of samples the chunks hold, which caps <see cref="Count"/> for a cut-off table.</summary>
    public long ChunkSampleCount()
    {
        long total = 0;
        for (int run = 0; run < Chunks.Length; run++)
        {
            long first = Chunks[run].FirstChunk;
            long last = run + 1 < Chunks.Length ? Chunks[run + 1].FirstChunk - 1 : ChunkOffsets.Length;
            last = Math.Min(last, ChunkOffsets.Length);
            if (last >= first)
            {
                total += (last - first + 1) * Chunks[run].Samples;
            }

            if (total > Count)
            {
                return total;
            }
        }

        return total;
    }

    /// <summary>Gets the sum of every sample's duration.</summary>
    public long TotalDuration()
    {
        long total = 0, left = Count;
        foreach ((uint count, uint delta) in Times)
        {
            long n = Math.Min(count, left);
            total += n * delta;
            left -= n;
        }

        return total;
    }

    /// <summary>Returns the number of the last sample whose decode time is at or before <paramref name="time"/> (0 when every one is later).</summary>
    public long IndexAtDecodeTime(long time)
    {
        long index = 0, at = 0;
        foreach ((uint count, uint delta) in Times)
        {
            long span = (long)count * delta;
            if (delta > 0 && time < at + span)
            {
                return Math.Clamp(index + Math.Max(0, (time - at) / delta), 0, Math.Max(0, Count - 1));
            }

            at += span;
            index += count;
        }

        return Math.Clamp(index - 1, 0, Math.Max(0, Count - 1));
    }

    /// <summary>Returns the number of the first sample of the first chunk at or after <paramref name="offset"/>, or <see cref="Count"/>.</summary>
    public long IndexAtOffset(long offset)
    {
        int chunk = Array.BinarySearch(ChunkOffsets, offset);
        chunk = chunk >= 0 ? chunk : ~chunk;
        if (chunk >= ChunkOffsets.Length)
        {
            return Count;
        }

        long index = 0;
        for (int run = 0; run < Chunks.Length; run++)
        {
            long first = Chunks[run].FirstChunk - 1;
            long next = run + 1 < Chunks.Length ? Chunks[run + 1].FirstChunk - 1 : ChunkOffsets.Length;
            if (chunk < next)
            {
                return Math.Min(Count, index + (Math.Max(0, chunk - first) * Chunks[run].Samples));
            }

            index += Math.Max(0, Math.Min(next, ChunkOffsets.Length) - first) * Chunks[run].Samples;
        }

        return Count;
    }

    /// <summary>Returns a cursor at the first sample.</summary>
    public Cursor Start() => new(this);

    /// <summary>Returns a cursor at <paramref name="index"/>, or past the end.</summary>
    public Cursor At(long index)
    {
        Cursor cursor = new(this);
        cursor.MoveTo(index);
        return cursor;
    }

    /// <summary>Returns the number (from 0) of the last sync sample at or before <paramref name="index"/>, or of the first one after it when there is none.</summary>
    public long SyncAtOrBefore(long index)
    {
        if (SyncSamples is null)
        {
            return index;
        }

        if (SyncSamples.Length == 0)
        {
            return 0;
        }

        int at = Array.BinarySearch(SyncSamples, (uint)Math.Min(index + 1, uint.MaxValue));
        at = at >= 0 ? at : ~at - 1;

        // A sync sample past the table's end (a broken stss) is not one.
        return Math.Clamp(at >= 0 ? SyncSamples[at] - 1L : SyncSamples[0] - 1L, 0, Math.Max(0, Count - 1));
    }

    /// <summary>Walks the samples in decoding order, keeping its place in each table's runs.</summary>
    internal struct Cursor
    {
        private readonly Mp4SampleTable _table;
        private long _index;
        private int _timeRun;
        private long _timeLeft;
        private long _decodeTime;
        private int _compositionRun;
        private long _compositionLeft;
        private int _chunkRun;
        private long _chunk;
        private long _inChunk;
        private long _offset;
        private int _syncAt;

        public Cursor(Mp4SampleTable table)
        {
            _table = table;
            _chunk = -1;
            _inChunk = 0;
            _timeLeft = table.Times.Length > 0 ? table.Times[0].Count : 0;
            _compositionLeft = table.Compositions.Length > 0 ? table.Compositions[0].Count : 0;
            NextChunk();
        }

        /// <summary>Gets the number of the sample <see cref="Peek"/> describes.</summary>
        public readonly long Index => _index;

        /// <summary>Gets whether every sample has been read.</summary>
        public readonly bool AtEnd => _index >= _table.Count || _chunk >= _table.ChunkOffsets.Length;

        /// <summary>Describes the current sample, or with <see cref="GroupChunks"/> the rest of its chunk.</summary>
        public readonly Mp4Sample Peek()
        {
            Mp4SampleTable t = _table;
            if (t.GroupChunks && t.Sizes.Length == 0 && _chunkRun < t.Chunks.Length)
            {
                long left = Math.Min(t.Chunks[_chunkRun].Samples - _inChunk, t.Count - _index);
                long delta = _timeRun < t.Times.Length ? t.Times[_timeRun].Delta : 0;
                int entry = (int)Math.Min(t.Chunks[_chunkRun].Description, int.MaxValue);
                return new Mp4Sample(_index, _offset, (int)Math.Min(left * t.ConstantSize, int.MaxValue), _decodeTime, 0, left * delta, true, entry);
            }

            int size = t.Sizes.Length > 0 ? (_index < t.Sizes.Length ? t.Sizes[_index] : 0) : t.ConstantSize;
            long duration = _timeRun < t.Times.Length ? t.Times[_timeRun].Delta : 0;
            long composition = _compositionRun < t.Compositions.Length ? t.Compositions[_compositionRun].Offset : 0;
            bool sync = t.SyncSamples is null
                ? t.Dependencies.Length <= _index || (t.Dependencies[_index] >> 4 & 3) != 1
                : _syncAt < t.SyncSamples.Length && t.SyncSamples[_syncAt] == _index + 1;
            int description = _chunkRun < t.Chunks.Length ? (int)Math.Min(t.Chunks[_chunkRun].Description, int.MaxValue) : 1;
            return new Mp4Sample(_index, _offset, size, _decodeTime, composition, duration, sync, description);
        }

        /// <summary>Moves to the next sample, or with <see cref="GroupChunks"/> to the next chunk.</summary>
        public void Advance()
        {
            Mp4SampleTable t = _table;
            if (t.GroupChunks && t.Sizes.Length == 0 && _chunkRun < t.Chunks.Length)
            {
                long chunk = _chunk;
                while (!AtEnd && _chunk == chunk)
                {
                    AdvanceOne();
                }

                return;
            }

            AdvanceOne();
        }

        private void AdvanceOne()
        {
            Mp4SampleTable t = _table;
            _offset += t.Sizes.Length > 0 ? (_index < t.Sizes.Length ? t.Sizes[_index] : 0) : t.ConstantSize;
            if (_timeRun < t.Times.Length)
            {
                _decodeTime += t.Times[_timeRun].Delta;
                _timeLeft--;
                while (_timeLeft <= 0 && _timeRun + 1 < t.Times.Length)
                {
                    _timeRun++;
                    _timeLeft = t.Times[_timeRun].Count;
                }
            }

            if (_compositionRun < t.Compositions.Length)
            {
                _compositionLeft--;
                while (_compositionLeft <= 0 && _compositionRun + 1 < t.Compositions.Length)
                {
                    _compositionRun++;
                    _compositionLeft = t.Compositions[_compositionRun].Count;
                }
            }

            if (t.SyncSamples is { } sync)
            {
                while (_syncAt < sync.Length && sync[_syncAt] <= _index + 1)
                {
                    _syncAt++;
                }
            }

            _index++;
            _inChunk++;
            if (_chunkRun < t.Chunks.Length && _inChunk >= t.Chunks[_chunkRun].Samples)
            {
                NextChunk();
            }
        }

        /// <summary>Moves to sample <paramref name="index"/>, skipping whole runs and chunks.</summary>
        public void MoveTo(long index)
        {
            Mp4SampleTable t = _table;
            this = new Cursor(t);
            index = Math.Clamp(index, 0, t.Count);

            // Times: whole runs, then within the run.
            long left = index;
            _decodeTime = 0;
            int run = 0;
            while (run < t.Times.Length && left >= t.Times[run].Count && run + 1 < t.Times.Length)
            {
                _decodeTime += (long)t.Times[run].Count * t.Times[run].Delta;
                left -= t.Times[run].Count;
                run++;
            }

            if (run < t.Times.Length)
            {
                _decodeTime += left * t.Times[run].Delta;
                _timeRun = run;
                _timeLeft = t.Times[run].Count - left;
                while (_timeLeft <= 0 && _timeRun + 1 < t.Times.Length)
                {
                    _timeRun++;
                    _timeLeft = t.Times[_timeRun].Count;
                }
            }

            left = index;
            run = 0;
            while (run < t.Compositions.Length && left >= t.Compositions[run].Count && run + 1 < t.Compositions.Length)
            {
                left -= t.Compositions[run].Count;
                run++;
            }

            if (run < t.Compositions.Length)
            {
                _compositionRun = run;
                _compositionLeft = t.Compositions[run].Count - left;
            }

            if (t.SyncSamples is { } sync)
            {
                int at = Array.BinarySearch(sync, (uint)Math.Min(index + 1, uint.MaxValue));
                _syncAt = at >= 0 ? at : ~at;
            }

            // Chunks: whole stsc runs, then whole chunks, then samples inside the chunk.
            left = index;
            _chunkRun = 0;
            _chunk = 0;
            _index = 0;
            while (_chunkRun < t.Chunks.Length)
            {
                long first = t.Chunks[_chunkRun].FirstChunk - 1;
                long next = _chunkRun + 1 < t.Chunks.Length ? t.Chunks[_chunkRun + 1].FirstChunk - 1 : long.MaxValue;
                long perChunk = t.Chunks[_chunkRun].Samples;
                long chunks = Math.Min(next, t.ChunkOffsets.Length) - first;
                if (perChunk > 0 && chunks > 0 && left < chunks * perChunk)
                {
                    _chunk = first + (left / perChunk);
                    _inChunk = left % perChunk;
                    break;
                }

                left -= Math.Max(0, chunks) * perChunk;
                _chunkRun++;
                if (_chunkRun >= t.Chunks.Length || next >= t.ChunkOffsets.Length)
                {
                    _chunk = t.ChunkOffsets.Length;
                    _index = index;
                    return;
                }
            }

            if (_chunkRun >= t.Chunks.Length)
            {
                _chunk = t.ChunkOffsets.Length;
                _index = index;
                return;
            }

            _index = index;
            _offset = t.ChunkOffsets[_chunk];
            long firstInChunk = index - _inChunk;
            for (long i = firstInChunk; i < index; i++)
            {
                _offset += t.Sizes.Length > 0 ? t.Sizes[i] : t.ConstantSize;
            }
        }

        private void NextChunk()
        {
            Mp4SampleTable t = _table;
            do
            {
                _chunk++;
                while (_chunkRun + 1 < t.Chunks.Length && t.Chunks[_chunkRun + 1].FirstChunk <= _chunk + 1)
                {
                    _chunkRun++;
                }
            }
            while (_chunk < t.ChunkOffsets.Length && _chunkRun < t.Chunks.Length && t.Chunks[_chunkRun].Samples == 0);

            _inChunk = 0;
            if (_chunk < t.ChunkOffsets.Length)
            {
                _offset = t.ChunkOffsets[_chunk];
            }
        }
    }
}
