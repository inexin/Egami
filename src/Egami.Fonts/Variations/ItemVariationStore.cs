using Egami.Fonts.Parsing;

namespace Egami.Fonts.Variations;

/// <summary>
/// An OpenType ItemVariationStore (OpenType 1.9.1, "Item variation stores"), shared by HVAR, VVAR, MVAR, GDEF, avar version 2,
/// CFF2 and COLR: variation regions over the axes and delta sets per item. Deltas are read on demand from the table's bytes.
/// </summary>
internal sealed class ItemVariationStore
{
    /// <summary>The value a delta-set index map or device table uses for "no variation".</summary>
    public const uint NoVariation = 0xFFFFFFFF;

    private readonly ReadOnlyMemory<byte> _data;
    private readonly int _axisCount;

    // Per region, per axis: start, peak and end, as F2Dot14 floats.
    private readonly float[] _regions;
    private readonly int _regionCount;
    private readonly DataSet[] _sets;

    private ItemVariationStore(ReadOnlyMemory<byte> data, int axisCount, float[] regions, int regionCount, DataSet[] sets)
    {
        _data = data;
        _axisCount = axisCount;
        _regions = regions;
        _regionCount = regionCount;
        _sets = sets;
    }

    public int SetCount => _sets.Length;

    public int RegionCount => _regionCount;

    /// <summary>Gets the number of regions item variation data <paramref name="outer"/> uses (CFF2's blend operand count).</summary>
    public int RegionCountOf(int outer) => (uint)outer < (uint)_sets.Length ? _sets[outer].RegionIndices.Length : 0;

    /// <summary>Reads a store at <paramref name="offset"/> of <paramref name="table"/>.</summary>
    public static ItemVariationStore Read(ReadOnlyMemory<byte> table, int offset, long fileOffset)
    {
        ReadOnlySpan<byte> data = table.Span;
        var r = new FontReader(BigEndian.Slice(data, offset, data.Length - (long)offset), fileOffset + offset);
        int format = r.ReadUInt16();
        if (format != 1)
        {
            throw FontErrors.Corrupt(fileOffset + offset, $"An ItemVariationStore has format {format}.");
        }

        int regionListOffset = (int)r.ReadUInt32();
        int setCount = r.ReadUInt16();
        var setOffsets = new int[setCount];
        for (int i = 0; i < setCount; i++)
        {
            setOffsets[i] = (int)r.ReadUInt32();
        }

        // Region list.
        var regions = new FontReader(BigEndian.Slice(data, offset + (long)regionListOffset, data.Length - offset - (long)regionListOffset), fileOffset + offset + regionListOffset);
        int axisCount = regions.ReadUInt16();
        int regionCount = regions.ReadUInt16();
        if ((long)regionCount * axisCount * 6 > regions.Remaining)
        {
            throw FontErrors.Corrupt(fileOffset + offset + regionListOffset, "An ItemVariationStore's region list is larger than its table.");
        }

        float[] coords = new float[regionCount * axisCount * 3];
        for (int i = 0; i < coords.Length; i++)
        {
            coords[i] = regions.ReadF2Dot14();
        }

        var sets = new DataSet[setCount];
        for (int s = 0; s < setCount; s++)
        {
            if (setOffsets[s] == 0)
            {
                sets[s] = new DataSet(0, 0, 0, false, []);
                continue;
            }

            int at = offset + setOffsets[s];
            var d = new FontReader(BigEndian.Slice(data, at, data.Length - (long)at), fileOffset + at);
            int itemCount = d.ReadUInt16();
            int wordDeltaCount = d.ReadUInt16();
            int indexCount = d.ReadUInt16();
            var indices = new ushort[indexCount];
            for (int i = 0; i < indexCount; i++)
            {
                indices[i] = d.ReadUInt16();
                if (indices[i] >= regionCount)
                {
                    throw FontErrors.Corrupt(fileOffset + at, $"An item variation data set names region {indices[i]} of {regionCount}.");
                }
            }

            bool longWords = (wordDeltaCount & 0x8000) != 0;
            int words = wordDeltaCount & 0x7FFF;
            if (words > indexCount)
            {
                throw FontErrors.Corrupt(fileOffset + at, "An item variation data set has more word deltas than regions.");
            }

            int rowSize = longWords ? (4 * words) + (2 * (indexCount - words)) : (2 * words) + (indexCount - words);
            if ((long)rowSize * itemCount > d.Remaining)
            {
                throw FontErrors.Corrupt(fileOffset + at, "An item variation data set's rows run past the end of its table.");
            }

            sets[s] = new DataSet(at + d.Position, itemCount, words, longWords, indices);
        }

        return new ItemVariationStore(table, axisCount, coords, regionCount, sets);
    }

    /// <summary>
    /// Computes every region's scalar at normalized coordinates (OpenType's region scalar: the product over axes of the tent each
    /// axis's start, peak and end describe).
    /// </summary>
    public float[] ComputeScalars(ReadOnlySpan<float> coords)
    {
        var scalars = new float[_regionCount];
        for (int r = 0; r < _regionCount; r++)
        {
            float scalar = 1;
            for (int a = 0; a < _axisCount && scalar != 0; a++)
            {
                int at = ((r * _axisCount) + a) * 3;
                float coord = a < coords.Length ? coords[a] : 0;
                scalar *= AxisScalar(coord, _regions[at], _regions[at + 1], _regions[at + 2]);
            }

            scalars[r] = scalar;
        }

        return scalars;
    }

    /// <summary>The tent of one axis of a region, with the spec's rule that a malformed axis (start above peak, or a region crossing zero) counts as 1.</summary>
    public static float AxisScalar(float coord, float start, float peak, float end)
    {
        if (start > peak || peak > end || (start < 0 && end > 0 && peak != 0))
        {
            return 1;
        }

        if (peak == 0 || coord == peak)
        {
            return 1;
        }

        if (coord <= start || coord >= end)
        {
            return 0;
        }

        return coord < peak ? (coord - start) / (peak - start) : (end - coord) / (end - peak);
    }

    /// <summary>Gets the delta of item <paramref name="inner"/> of set <paramref name="outer"/> with region scalars from <see cref="ComputeScalars"/>.</summary>
    public float GetDelta(int outer, int inner, ReadOnlySpan<float> scalars)
    {
        if ((uint)outer >= (uint)_sets.Length)
        {
            return 0;
        }

        DataSet set = _sets[outer];
        if ((uint)inner >= (uint)set.ItemCount)
        {
            return 0;
        }

        ReadOnlySpan<byte> data = _data.Span;
        int indexCount = set.RegionIndices.Length;
        int rowSize = set.LongWords ? (4 * set.Words) + (2 * (indexCount - set.Words)) : (2 * set.Words) + (indexCount - set.Words);
        int at = set.RowsStart + (inner * rowSize);
        float sum = 0;
        for (int i = 0; i < indexCount; i++)
        {
            int delta;
            if (i < set.Words)
            {
                delta = set.LongWords ? (int)BigEndian.UInt32(data, at) : BigEndian.Int16(data, at);
                at += set.LongWords ? 4 : 2;
            }
            else
            {
                delta = set.LongWords ? BigEndian.Int16(data, at) : (sbyte)data[at];
                at += set.LongWords ? 2 : 1;
            }

            float scalar = scalars[set.RegionIndices[i]];
            if (scalar != 0)
            {
                sum += scalar * delta;
            }
        }

        return sum;
    }

    /// <summary>Gets the delta for a packed outer/inner index, as delta-set index maps and device tables give it.</summary>
    public float GetDelta(uint index, ReadOnlySpan<float> scalars) => index == NoVariation ? 0 : GetDelta((int)(index >> 16), (int)(index & 0xFFFF), scalars);

    /// <summary>Gets the raw deltas of one item, one per region of its set, with the regions they apply to (for CFF2 blends and instancing).</summary>
    public ReadOnlySpan<ushort> RegionIndicesOf(int outer) => (uint)outer < (uint)_sets.Length ? _sets[outer].RegionIndices : default;

    private readonly record struct DataSet(int RowsStart, int ItemCount, int Words, bool LongWords, ushort[] RegionIndices);
}

/// <summary>A DeltaSetIndexMap (formats 0 and 1): maps an item, such as a glyph id, to an outer/inner index into a store.</summary>
internal sealed class DeltaSetIndexMap
{
    private readonly uint[] _map;

    private DeltaSetIndexMap(uint[] map)
    {
        _map = map;
    }

    public static DeltaSetIndexMap Read(ReadOnlySpan<byte> table, int offset, long fileOffset)
    {
        var r = new FontReader(BigEndian.Slice(table, offset, table.Length - (long)offset), fileOffset + offset);
        int format = r.ReadByte();
        int entryFormat = r.ReadByte();
        uint count = format switch
        {
            0 => r.ReadUInt16(),
            1 => r.ReadUInt32(),
            _ => throw FontErrors.Corrupt(fileOffset + offset, $"A DeltaSetIndexMap has format {format}."),
        };
        int entrySize = ((entryFormat & 0x30) >> 4) + 1;
        int innerBits = (entryFormat & 0x0F) + 1;
        if ((long)count * entrySize > r.Remaining)
        {
            throw FontErrors.Corrupt(fileOffset + offset, "A DeltaSetIndexMap runs past the end of its table.");
        }

        var map = new uint[count];
        for (int i = 0; i < count; i++)
        {
            uint entry = 0;
            for (int b = 0; b < entrySize; b++)
            {
                entry = (entry << 8) | r.ReadByte();
            }

            uint outer = entry >> innerBits, inner = entry & ((1u << innerBits) - 1);
            map[i] = (outer << 16) | inner;
        }

        return new DeltaSetIndexMap(map);
    }

    /// <summary>Gets the packed outer/inner index of an item; items past the end use the last entry, as the spec says.</summary>
    public uint Get(int item)
    {
        if (_map.Length == 0)
        {
            return ItemVariationStore.NoVariation;
        }

        uint packed = _map[Math.Min((uint)item, (uint)_map.Length - 1)];

        // An inner index of 0xFFFF with outer 0xFFFF is "no variation".
        return packed == 0xFFFFFFFF ? ItemVariationStore.NoVariation : packed;
    }
}
