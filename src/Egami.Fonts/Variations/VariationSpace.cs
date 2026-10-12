using Egami.Fonts.Parsing;
using Egami.Fonts.Tables;

namespace Egami.Fonts.Variations;

/// <summary>An fvar axis with its name ID, as read.</summary>
internal readonly record struct FvarAxis(FontTag Tag, float Min, float Default, float Max, int Flags, int NameId);

/// <summary>An fvar instance with its name IDs and user coordinates, as read.</summary>
internal readonly record struct FvarInstance(int SubfamilyNameId, int PostScriptNameId, float[] Coordinates);

/// <summary>
/// The design space of a variable font: fvar's axes and instances, avar's segment maps (version 1) and its ItemVariationStore
/// (version 2), and the normalization from user coordinates to the F2Dot14 coordinates every variation table works in.
/// </summary>
internal sealed class VariationSpace
{
    private readonly float[][]? _segments;
    private readonly ItemVariationStore? _avarStore;
    private readonly DeltaSetIndexMap? _avarMap;

    private VariationSpace(FvarAxis[] axes, FvarInstance[] instances, float[][]? segments, ItemVariationStore? avarStore, DeltaSetIndexMap? avarMap)
    {
        Axes = axes;
        Instances = instances;
        _segments = segments;
        _avarStore = avarStore;
        _avarMap = avarMap;
    }

    public FvarAxis[] Axes { get; }

    public FvarInstance[] Instances { get; }

    public int AxisIndex(FontTag tag)
    {
        for (int i = 0; i < Axes.Length; i++)
        {
            if (Axes[i].Tag == tag)
            {
                return i;
            }
        }

        return -1;
    }

    public static VariationSpace Read(ReadOnlySpan<byte> fvar, long fvarOffset, ReadOnlyMemory<byte> avar, long avarOffset, Action<string, string, long> repair)
    {
        var r = new FontReader(fvar, fvarOffset);
        int major = r.ReadUInt16();
        r.Skip(2);
        if (major != 1)
        {
            throw FontErrors.Corrupt(fvarOffset, $"The fvar table has version {major}.");
        }

        int axesOffset = r.ReadUInt16();
        r.Skip(2);
        int axisCount = r.ReadUInt16();
        int axisSize = r.ReadUInt16();
        int instanceCount = r.ReadUInt16();
        int instanceSize = r.ReadUInt16();
        if (axisSize < 20 || axisCount == 0)
        {
            throw FontErrors.Corrupt(fvarOffset, "The fvar table has no usable axes.");
        }

        var axes = new FvarAxis[axisCount];
        for (int i = 0; i < axisCount; i++)
        {
            int at = axesOffset + (i * axisSize);
            var a = new FontReader(BigEndian.Slice(fvar, at, 20), fvarOffset + at);
            var tag = a.ReadTag();
            float min = a.ReadFixed(), def = a.ReadFixed(), max = a.ReadFixed();
            int flags = a.ReadUInt16(), nameId = a.ReadUInt16();
            if (!(min <= def && def <= max))
            {
                repair("Font.BadTable", $"The fvar axis {tag} has minimum {min}, default {def} and maximum {max}; the default is used at every position.", fvarOffset + at);
                min = max = def;
            }

            axes[i] = new FvarAxis(tag, min, def, max, flags, nameId);
        }

        var instances = new List<FvarInstance>(instanceCount);
        int instancesAt = axesOffset + (axisCount * axisSize);
        bool hasPostScript = instanceSize >= (axisCount * 4) + 6;
        if (instanceSize >= (axisCount * 4) + 4)
        {
            for (int i = 0; i < instanceCount; i++)
            {
                int at = instancesAt + (i * instanceSize);
                if ((long)at + instanceSize > fvar.Length)
                {
                    repair("Font.BadTable", $"The fvar table holds {i} of its {instanceCount} instances.", fvarOffset + at);
                    break;
                }

                var a = new FontReader(fvar.Slice(at, instanceSize), fvarOffset + at);
                int subfamily = a.ReadUInt16();
                a.Skip(2);
                float[] coords = new float[axisCount];
                for (int k = 0; k < axisCount; k++)
                {
                    coords[k] = a.ReadFixed();
                }

                int postScript = hasPostScript ? a.ReadUInt16() : 0xFFFF;
                instances.Add(new FvarInstance(subfamily, postScript, coords));
            }
        }

        float[][]? segments = null;
        ItemVariationStore? store = null;
        DeltaSetIndexMap? map = null;
        if (!avar.IsEmpty)
        {
            try
            {
                (segments, store, map) = ReadAvar(avar, avarOffset, axisCount);
            }
            catch (CorruptDataException e)
            {
                repair("Font.BadTable", $"The avar table is damaged ({e.Message}); axes normalize linearly.", avarOffset);
            }
        }

        return new VariationSpace(axes, [.. instances], segments, store, map);
    }

    private static (float[][] Segments, ItemVariationStore? Store, DeltaSetIndexMap? Map) ReadAvar(ReadOnlyMemory<byte> table, long offset, int axisCount)
    {
        ReadOnlySpan<byte> avar = table.Span;
        var r = new FontReader(avar, offset);
        int major = r.ReadUInt16();
        r.Skip(2);
        if (major is not (1 or 2))
        {
            throw FontErrors.Corrupt(offset, $"The avar table has version {major}.");
        }

        r.Skip(2);
        int count = r.ReadUInt16();
        var segments = new float[axisCount][];
        for (int i = 0; i < count; i++)
        {
            int pairs = r.ReadUInt16();
            float[] map = new float[pairs * 2];
            for (int k = 0; k < map.Length; k++)
            {
                map[k] = r.ReadF2Dot14();
            }

            if (i < axisCount)
            {
                segments[i] = map;
            }
        }

        for (int i = 0; i < axisCount; i++)
        {
            segments[i] ??= [];
        }

        if (major < 2)
        {
            return (segments, null, null);
        }

        int mapOffset = (int)r.ReadUInt32();
        int storeOffset = (int)r.ReadUInt32();
        DeltaSetIndexMap? indexMap = mapOffset != 0 ? DeltaSetIndexMap.Read(avar, mapOffset, offset) : null;
        ItemVariationStore? store = storeOffset != 0 ? ItemVariationStore.Read(table, storeOffset, offset) : null;
        return (segments, store, indexMap);
    }

    /// <summary>
    /// Normalizes a position: user values clamp to each axis and map to -1..1 through the default, then through avar's segment maps,
    /// then avar version 2's deltas; each result is rounded to F2Dot14, as OpenType 1.9.1 defines. Axes not given stay at default.
    /// </summary>
    public float[] Normalize(ReadOnlySpan<float> user)
    {
        int n = Axes.Length;
        var coords = new float[n];
        var mapped = new float[n];
        for (int i = 0; i < n; i++)
        {
            FvarAxis a = Axes[i];
            float v = Math.Clamp(user[i], a.Min, a.Max);
            float c = v == a.Default ? 0
                : v < a.Default ? (a.Default == a.Min ? 0 : (v - a.Default) / (a.Default - a.Min))
                : (a.Max == a.Default ? 0 : (v - a.Default) / (a.Max - a.Default));
            if (_segments is not null && _segments[i].Length >= 2)
            {
                c = PiecewiseLinear(c, _segments[i]);
            }

            mapped[i] = c;
            coords[i] = F2Dot14(c);
        }

        if (_avarStore is not null)
        {
            // avar 2's store is evaluated at version 1's mapped coordinates, before they are rounded, as fontTools does.
            float[] scalars = _avarStore.ComputeScalars(mapped);
            var varied = new float[n];
            for (int i = 0; i < n; i++)
            {
                uint index = _avarMap?.Get(i) ?? (uint)i;
                float delta = MathF.Floor(_avarStore.GetDelta(index, scalars) + 0.5f);
                varied[i] = Math.Clamp((MathF.Floor((coords[i] * 16384) + 0.5f) + delta) / 16384f, -1, 1);
            }

            coords = varied;
        }

        return coords;
    }

    public static float F2Dot14(float value) => MathF.Floor((value * 16384) + 0.5f) / 16384f;

    // avar's map: pairs of (from, to) in ascending from order; values outside the first and last pair shift by their offset.
    private static float PiecewiseLinear(float value, float[] map)
    {
        int pairs = map.Length / 2;
        if (value <= map[0])
        {
            return value + map[1] - map[0];
        }

        for (int k = 1; k < pairs; k++)
        {
            float from = map[k * 2], to = map[(k * 2) + 1];
            if (value == from)
            {
                return to;
            }

            if (value < from)
            {
                float from0 = map[(k - 1) * 2], to0 = map[((k - 1) * 2) + 1];
                return from == from0 ? to : to0 + ((to - to0) * (value - from0) / (from - from0));
            }
        }

        return value + map[^1] - map[^2];
    }

    /// <summary>Gets the English name of a name ID, or <paramref name="fallback"/>.</summary>
    public static string NameOf(NameTable name, int id, string fallback) => id is > 0 and < 0xFFFF ? name.GetEnglish(id) ?? fallback : fallback;
}
