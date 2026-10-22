using System.Buffers.Binary;

namespace Egami.Video.Codecs.Vp9;

/// <summary>The frame's tiles and the loop filter after them (vp9_decodeframe.c's decode_tiles).</summary>
internal sealed partial class Vp9Decoder<T>
{
    private int DecodeTiles(Vp9FrameHeader h, int start, int end)
    {
        int tileCols = 1 << h.TileColsLog2, tileRows = 1 << h.TileRowsLog2;
        if (h.LoopFilterLevel != 0)
        {
            _loopFilter.FrameInit(h.LoopFilterLevel, h.LoopFilterSharpness, h.LoopFilterDeltaEnabled, _refDeltas, _modeDeltas,
                SegFeatureActive, SegData, _segAbsDelta, _miRows, _miCols);
        }

        int alignedCols = (_miCols + 7) & ~7;
        for (int p = 0; p < 3; p++)
        {
            Array.Clear(_aboveContext[p], 0, Math.Min(_aboveContext[p].Length, 2 * alignedCols));
        }

        Array.Clear(_abovePartition, 0, Math.Min(_abovePartition.Length, alignedCols));

        // Tile sizes: every tile but the last starts with its size as 4 big-endian bytes.
        Span<int> starts = stackalloc int[4 * 64];
        Span<int> sizes = stackalloc int[4 * 64];
        int at = start;
        for (int r = 0; r < tileRows; r++)
        {
            for (int c = 0; c < tileCols; c++)
            {
                bool last = r == tileRows - 1 && c == tileCols - 1;
                int size;
                if (!last)
                {
                    if (end - at < 4)
                    {
                        throw new CorruptDataException("VP9", at, "A tile size is cut off.");
                    }

                    size = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(at, 4)), int.MaxValue);
                    at += 4;
                    if (size > end - at)
                    {
                        throw new CorruptDataException("VP9", at, "A tile runs past the end of the frame.");
                    }
                }
                else
                {
                    size = end - at;
                }

                if (size == 0)
                {
                    throw new CorruptDataException("VP9", at, "A tile is empty.");
                }

                starts[(r * tileCols) + c] = at;
                sizes[(r * tileCols) + c] = size;
                at += size;
            }
        }

        if (_tiles.Length < tileCols)
        {
            Tile[] tiles = new Tile[tileCols];
            _tiles.CopyTo(tiles, 0);
            for (int i = _tiles.Length; i < tileCols; i++)
            {
                tiles[i] = new Tile(this);
            }

            _tiles = tiles;
        }

        bool counts = !h.ErrorResilientMode && !h.FrameParallelDecodingMode;
        Tile? lastTile = null;
        int workers = Math.Min(MaxParallelism, tileCols);

        // On one thread the loop filter follows the decode a superblock row behind, while the rows are still in the cache: a row
        // is filtered once the row below it is decoded, since the row below predicts from its unfiltered pixels.
        bool lagFilter = h.LoopFilterLevel != 0 && MaxParallelism <= 1;
        int nextFilterRow = 0;
        for (int r = 0; r < tileRows; r++)
        {
            int rowStart = TileOffset(r, _miRows, h.TileRowsLog2), rowEnd = TileOffset(r + 1, _miRows, h.TileRowsLog2);
            for (int c = 0; c < tileCols; c++)
            {
                Tile tile = _tiles[c];
                tile.CountsEnabled = counts;
                if (r > 0 && counts)
                {
                    // Each tile row starts a new boolean decoder; the column's counts carry on.
                    _counts.Add(tile.Counts);
                }

                tile.Start(starts[(r * tileCols) + c], sizes[(r * tileCols) + c], TileOffset(c, _miCols, h.TileColsLog2),
                    TileOffset(c + 1, _miCols, h.TileColsLog2));
            }

            if (workers > 1)
            {
                // Tile columns share nothing a tile reads: each runs down its own rows on its own thread.
                try
                {
                    Parallel.For(0, tileCols, new ParallelOptions { MaxDegreeOfParallelism = workers }, c =>
                    {
                        Tile tile = _tiles[c];
                        for (int miRow = rowStart; miRow < rowEnd && !tile.Corrupted; miRow += 8)
                        {
                            tile.DecodeRow(miRow);
                        }
                    });
                }
                catch (AggregateException e)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(e.InnerExceptions[0]);
                }

                for (int c = 0; c < tileCols; c++)
                {
                    if (_tiles[c].Corrupted)
                    {
                        throw new CorruptDataException("VP9", -1, "A tile's data is cut off or broken.");
                    }
                }

                lastTile = _tiles[tileCols - 1];
                continue;
            }

            for (int miRow = rowStart; miRow < rowEnd; miRow += 8)
            {
                for (int c = 0; c < tileCols; c++)
                {
                    Tile tile = _tiles[c];
                    tile.DecodeRow(miRow);
                    if (tile.Corrupted)
                    {
                        throw new CorruptDataException("VP9", -1, "A tile's data is cut off or broken.");
                    }

                    lastTile = tile;
                }

                if (lagFilter && miRow >= 8)
                {
                    _loopFilter.FilterRow(_current!, _miRows, _miCols, nextFilterRow, _mi, _miGrid);
                    nextFilterRow += 8;
                }
            }
        }

        if (counts)
        {
            for (int c = 0; c < tileCols; c++)
            {
                _counts.Add(_tiles[c].Counts);
            }
        }

        if (h.LoopFilterLevel != 0)
        {
            if (lagFilter)
            {
                for (; nextFilterRow < _miRows; nextFilterRow += 8)
                {
                    _loopFilter.FilterRow(_current!, _miRows, _miCols, nextFilterRow, _mi, _miGrid);
                }
            }
            else
            {
                _loopFilter.FilterFrame(_current!, _miRows, _miCols, _mi, _miGrid, MaxParallelism);
            }
        }

        return lastTile is null ? end : lastTile.End;
    }

    /// <summary>Where tile <paramref name="index"/> starts, in 8x8 units (get_tile_offset).</summary>
    private static int TileOffset(int index, int mis, int log2)
    {
        int sbCols = ((mis + 7) & ~7) >> 3;
        int offset = ((index * sbCols) >> log2) << 3;
        return Math.Min(offset, mis);
    }
}
