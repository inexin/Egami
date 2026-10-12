using Egami.Fonts.Parsing;

namespace Egami.Fonts.Variations;

/// <summary>Horizontal metrics variations ('HVAR'): advance and side-bearing deltas through delta-set index maps.</summary>
internal sealed class HvarTable
{
    private HvarTable(ItemVariationStore store, DeltaSetIndexMap? advances, DeltaSetIndexMap? leftBearings)
    {
        Store = store;
        Advances = advances;
        LeftBearings = leftBearings;
    }

    public ItemVariationStore Store { get; }

    public DeltaSetIndexMap? Advances { get; }

    public DeltaSetIndexMap? LeftBearings { get; }

    public static HvarTable Read(ReadOnlyMemory<byte> table, long offset)
    {
        ReadOnlySpan<byte> data = table.Span;
        var r = new FontReader(data, offset);
        int major = r.ReadUInt16();
        r.Skip(2);
        if (major != 1)
        {
            throw FontErrors.Corrupt(offset, $"The HVAR table has version {major}.");
        }

        int storeOffset = (int)r.ReadUInt32();
        int advanceOffset = (int)r.ReadUInt32();
        int lsbOffset = (int)r.ReadUInt32();
        ItemVariationStore store = ItemVariationStore.Read(table, storeOffset, offset);
        DeltaSetIndexMap? advances = advanceOffset != 0 ? DeltaSetIndexMap.Read(data, advanceOffset, offset) : null;
        DeltaSetIndexMap? lsb = lsbOffset != 0 ? DeltaSetIndexMap.Read(data, lsbOffset, offset) : null;
        return new HvarTable(store, advances, lsb);
    }

    /// <summary>The advance delta of a glyph; without a map, the glyph id is the inner index of set 0.</summary>
    public float AdvanceDelta(int glyph, ReadOnlySpan<float> scalars)
        => Store.GetDelta(Advances?.Get(glyph) ?? (uint)glyph, scalars);

    /// <summary>The left side bearing delta, or <see langword="null"/> when the table has no bearing map.</summary>
    public float? LeftBearingDelta(int glyph, ReadOnlySpan<float> scalars)
        => LeftBearings is null ? null : Store.GetDelta(LeftBearings.Get(glyph), scalars);
}

/// <summary>Font-wide metrics variations ('MVAR'): deltas for OS/2, hhea and post values by four-letter value tag.</summary>
internal sealed class MvarTable
{
    private readonly Dictionary<uint, uint> _records;

    private MvarTable(ItemVariationStore? store, Dictionary<uint, uint> records)
    {
        Store = store;
        _records = records;
    }

    public ItemVariationStore? Store { get; }

    public static MvarTable Read(ReadOnlyMemory<byte> table, long offset)
    {
        ReadOnlySpan<byte> data = table.Span;
        var r = new FontReader(data, offset);
        int major = r.ReadUInt16();
        r.Skip(4);
        if (major != 1)
        {
            throw FontErrors.Corrupt(offset, $"The MVAR table has version {major}.");
        }

        int recordSize = r.ReadUInt16();
        int count = r.ReadUInt16();
        int storeOffset = r.ReadUInt16();
        if (recordSize < 8)
        {
            throw FontErrors.Corrupt(offset, "The MVAR value records are too small.");
        }

        var records = new Dictionary<uint, uint>(count);
        for (int i = 0; i < count; i++)
        {
            int at = 12 + (i * recordSize);
            uint tag = BigEndian.UInt32(data, at);
            uint index = ((uint)BigEndian.UInt16(data, at + 4) << 16) | BigEndian.UInt16(data, at + 6);
            records[tag] = index;
        }

        ItemVariationStore? store = storeOffset != 0 ? ItemVariationStore.Read(table, storeOffset, offset) : null;
        return new MvarTable(store, records);
    }

    /// <summary>The delta for a value tag such as <c>hasc</c>, 0 when the table does not vary it.</summary>
    public float Delta(string tag, ReadOnlySpan<float> scalars)
        => Store is not null && _records.TryGetValue(new FontTag(tag).Value, out uint index) ? Store.GetDelta(index, scalars) : 0;
}
