using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Egami.Fonts.Cff;
using Egami.Fonts.Layout;
using Egami.Fonts.Parsing;
using Egami.Fonts.Tables;
using Egami.Fonts.Variations;

namespace Egami.Fonts;

public sealed partial class Typeface
{
    private const int MaxInstances = 256;

    private readonly Typeface? _base;
    private readonly VariationInstance? _instance;
    private Box<VariationData?>? _variations;
    private VariedGlyph?[]? _varied;
    private float[]? _cff2Scalars;
    private Dictionary<string, Typeface>? _instances;
    private IReadOnlyList<VariationAxis>? _axes;
    private IReadOnlyList<NamedInstance>? _namedInstances;

    // A face at a variation position: the default face's memory, tables and parsed structures, with the position's own outlines,
    // metrics, kerning and names.
    private Typeface(Typeface face, VariationInstance instance)
    {
        _base = face;
        _instance = instance;
        _data = face._data;
        _tables = face._tables;
        _options = face._options;
        _head = face._head;
        _cffOptions = face._cffOptions;
        _web = face._web;
        _maxp = face._maxp;
        _glyf = face._glyf;
        _cff = face._cff;
        FaceIndex = face.FaceIndex;
        FaceOffset = face.FaceOffset;
        SfntVersion = face.SfntVersion;
        IsBareCff = face.IsBareCff;
        Outlines = face.Outlines;
        GlyphCount = face.GlyphCount;
        UnitsPerEm = face.UnitsPerEm;
        IsCff2 = face.IsCff2;
    }

    /// <summary>Gets whether the face is a variable font (it has an fvar table with at least one axis).</summary>
    public bool IsVariable => VariationData is not null;

    /// <summary>Gets the axes of a variable font, in fvar order; empty for a static font.</summary>
    public IReadOnlyList<VariationAxis> VariationAxes => Root._axes ??= BuildAxes();

    /// <summary>Gets the named instances of a variable font, such as "Bold"; empty for a static font.</summary>
    public IReadOnlyList<NamedInstance> NamedInstances => Root._namedInstances ??= BuildInstances();

    /// <summary>
    /// Gets this face's position: one value per axis that is not at its default, in fvar order; empty for the default position and
    /// for a static font.
    /// </summary>
    public IReadOnlyList<FontVariation> Variations
    {
        get
        {
            if (_instance is null)
            {
                return [];
            }

            var list = new List<FontVariation>();
            FvarAxis[] axes = _instance.Data.Space.Axes;
            for (int i = 0; i < axes.Length; i++)
            {
                if (_instance.User[i] != axes[i].Default)
                {
                    list.Add(new FontVariation(axes[i].Tag, _instance.User[i]));
                }
            }

            return list;
        }
    }

    internal bool IsCff2 { get; }

    /// <summary>Gets the default face this one is a position of, or this face.</summary>
    internal Typeface Root => _base ?? this;

    internal VariationInstance? Instance => _instance;

    /// <summary>Gets a CFF2 face's region scalars at this position, or null at the default.</summary>
    private float[]? Cff2Scalars => _instance is null || _cff?.VariationStore is not ItemVariationStore store ? null : _cff2Scalars ??= store.ComputeScalars(_instance.Coords);

    internal VariationData? VariationData
    {
        get
        {
            Typeface root = Root;
            Box<VariationData?>? box = Volatile.Read(ref root._variations);
            return (box ?? Publish(ref root._variations, new Box<VariationData?>(root.LoadVariations()))).Value;
        }
    }

    /// <summary>
    /// Returns this face at another position on its axes, sharing the file's memory and parsed tables: outlines, advances, extents,
    /// kerning, names and subsets all answer for that position. Axes not named keep this face's values; values clamp to each
    /// axis's range; axes the font does not have are ignored.
    /// </summary>
    /// <param name="variations">The axis values, such as <c>FontVariation.Weight(650)</c>.</param>
    /// <returns>The face at that position: the default face when every axis is at its default, this face for a static font.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="variable"/>
    /// </example>
    public Typeface WithVariations(params ReadOnlySpan<FontVariation> variations)
    {
        VariationData? data = VariationData;
        if (data is null)
        {
            return this;
        }

        FvarAxis[] axes = data.Space.Axes;
        float[] user = _instance is null ? [.. axes.Select(a => a.Default)] : (float[])_instance.User.Clone();
        foreach (FontVariation v in variations)
        {
            int i = data.Space.AxisIndex(v.Axis);
            if (i >= 0 && float.IsFinite(v.Value))
            {
                user[i] = Math.Clamp(v.Value, axes[i].Min, axes[i].Max);
            }
        }

        return Root.AtPosition(data, user);
    }

    /// <summary>Returns the face at one of its named instances.</summary>
    /// <param name="index">The instance's index in <see cref="NamedInstances"/>.</param>
    /// <returns>The face at the instance's position.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not an instance of the face.</exception>
    public Typeface WithNamedInstance(int index)
    {
        VariationData? data = VariationData;
        if (data is null || (uint)index >= (uint)data.Space.Instances.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"The face has {data?.Space.Instances.Length ?? 0} named instances.");
        }

        FvarAxis[] axes = data.Space.Axes;
        float[] user = (float[])data.Space.Instances[index].Coordinates.Clone();
        for (int i = 0; i < user.Length; i++)
        {
            user[i] = Math.Clamp(user[i], axes[i].Min, axes[i].Max);
        }

        return Root.AtPosition(data, user);
    }

    /// <summary>Returns the face at opsz = <paramref name="size"/> when it has an opsz axis it leaves at the default, else itself.</summary>
    internal Typeface AtOpticalSize(float size)
    {
        if (VariationData is not VariationData data || data.Space.AxisIndex(FontVariation.OpticalSizeAxis) is not (int axis and >= 0))
        {
            return this;
        }

        float current = _instance?.User[axis] ?? data.Space.Axes[axis].Default;
        return current != data.Space.Axes[axis].Default ? this : WithVariations(FontVariation.OpticalSize(size));
    }

    internal int TableOffset(FontTag tag)
    {
        GetTableSpan(tag, out int offset);
        return offset;
    }

    // Faces at positions are cached on the default face by their user coordinates, so drawing at one position reuses one face.
    private Typeface AtPosition(VariationData data, float[] user)
    {
        bool isDefault = true;
        for (int i = 0; i < user.Length && isDefault; i++)
        {
            isDefault = user[i] == data.Space.Axes[i].Default;
        }

        if (isDefault)
        {
            return this;
        }

        string key = string.Join(',', user.Select(v => BitConverter.SingleToInt32Bits(v).ToString(CultureInfo.InvariantCulture)));
        lock (_lock)
        {
            _instances ??= [];
            if (_instances.TryGetValue(key, out Typeface? face))
            {
                return face;
            }

            if (_instances.Count >= MaxInstances)
            {
                _instances.Clear();
            }

            face = new Typeface(this, new VariationInstance(data, user));
            _instances[key] = face;
            return face;
        }
    }

    private VariationData? LoadVariations()
    {
        ReadOnlySpan<byte> fvar = GetTableSpan(Tags.Fvar, out int fvarOffset);
        if (fvarOffset < 0)
        {
            return null;
        }

        try
        {
            TryGetTable(Tags.Avar, out ReadOnlyMemory<byte> avar);
            VariationSpace space = VariationSpace.Read(fvar, fvarOffset, avar, TableOffset(Tags.Avar), Repair);
            return new VariationData(this, space);
        }
        catch (CorruptDataException e) when (!_options.Strict)
        {
            Repair("Font.BadTable", $"The fvar table is damaged ({e.Message}); the face is static.", fvarOffset);
            return null;
        }
    }

    private VariationAxis[] BuildAxes()
    {
        if (VariationData is not VariationData data)
        {
            return [];
        }

        return [.. data.Space.Axes.Select(a => new VariationAxis(a.Tag, VariationSpace.NameOf(Name, a.NameId, a.Tag.ToString().TrimEnd()), a.Min, a.Default, a.Max, (a.Flags & 1) != 0))];
    }

    private NamedInstance[] BuildInstances()
    {
        if (VariationData is not VariationData data)
        {
            return [];
        }

        FvarAxis[] axes = data.Space.Axes;
        return [.. data.Space.Instances.Select(i => new NamedInstance(
            VariationSpace.NameOf(Name, i.SubfamilyNameId, "Instance"),
            i.PostScriptNameId is > 0 and < 0xFFFF ? Name.GetEnglish(i.PostScriptNameId) : null,
            [.. axes.Select((a, k) => new FontVariation(a.Tag, i.Coordinates[k]))]))];
    }

    // The named instance at this face's position (the default face's included), or -1.
    private int NamedInstanceIndex()
    {
        if (VariationData is not VariationData data)
        {
            return -1;
        }

        float[] user = _instance?.User ?? [.. data.Space.Axes.Select(a => a.Default)];
        FvarInstance[] instances = data.Space.Instances;
        for (int i = 0; i < instances.Length; i++)
        {
            if (instances[i].Coordinates.AsSpan().SequenceEqual(user))
            {
                return i;
            }
        }

        return -1;
    }

    private VariedGlyph Varied(int glyph)
    {
        VariedGlyph?[] cache = Volatile.Read(ref _varied) ?? Publish(ref _varied, new VariedGlyph?[GlyphCount]);
        VariedGlyph? varied = Volatile.Read(ref cache[glyph]);
        if (varied is null)
        {
            varied = BuildVaried(glyph);
            Volatile.Write(ref cache[glyph], varied);
        }

        return varied;
    }

    private VariedGlyph BuildVaried(int glyph)
    {
        VariationInstance instance = _instance!;
        try
        {
            if (_glyf is not null)
            {
                return GlyfVariations.Build(_glyf, instance.Data.Gvar, Root.Hmtx, instance.Coords, glyph);
            }

            OutlineRecorder recorder = OutlineRecorder.Take();
            try
            {
                CharStringInterpreter interpreter = CharStringInterpreter.For(_cff!);
                interpreter.Draw(glyph, recorder, null, Cff2Scalars);
                return new VariedGlyph(recorder.Verbs.ToArray(), recorder.Coordinates.ToArray(), recorder.ComputeBounds(), float.NaN, float.NaN);
            }
            finally
            {
                recorder.Release();
            }
        }
        catch (CorruptDataException e)
        {
            if (_options.Strict)
            {
                throw;
            }

            Report(_options, "Font.BadGlyph", $"Glyph {glyph} cannot be varied ({e.Message}); it draws empty.", FaceOffset);
            return VariedGlyph.Empty;
        }
    }

    private float VariedAdvance(int glyph)
    {
        VariationInstance instance = _instance!;
        float advance = Root.Hmtx is HmtxTable hmtx ? hmtx.GetAdvance(glyph) : UnitsPerEm / 2f;
        if (instance.Data.Hvar is HvarTable hvar)
        {
            return advance + hvar.AdvanceDelta(glyph, instance.HvarScalars!);
        }

        if (_glyf is not null && instance.Data.Gvar is not null)
        {
            float varied = Varied(glyph).Advance;
            return float.IsNaN(varied) ? advance : varied;
        }

        return advance;
    }

    private float VariedLeftSideBearing(int glyph)
    {
        VariationInstance instance = _instance!;
        float lsb = Root.Hmtx is HmtxTable hmtx ? hmtx.GetLeftSideBearing(glyph) : 0;
        if (instance.Data.Hvar is HvarTable hvar && hvar.LeftBearingDelta(glyph, instance.HvarScalars!) is float delta)
        {
            return lsb + delta;
        }

        if (_glyf is not null)
        {
            float varied = Varied(glyph).LeftBearing;
            return float.IsNaN(varied) ? lsb : varied;
        }

        GlyphExtents box = Varied(glyph).Bounds;
        return box.IsEmpty ? lsb : box.XMin;
    }

    private Kerning LoadVariedKerning()
    {
        VariationInstance instance = _instance!;
        if (instance.Data.GdefStore is not Variations.ItemVariationStore store)
        {
            return Root.Kerning;
        }

        ReadOnlySpan<byte> gpos = GetTableSpan(Tags.Gpos, out int gposOffset);
        ReadOnlySpan<byte> kern = GetTableSpan(Tags.Kern, out int kernOffset);
        float[] scalars = instance.GdefScalars!;
        try
        {
            return Kerning.Read(gpos, gposOffset, kern, kernOffset, (data, device) => DeviceDelta(data, device, store, scalars));
        }
        catch (CorruptDataException e) when (!_options.Strict)
        {
            Repair("Font.BadTable", $"The kerning data is damaged ({e.Message}); text is not kerned.", gposOffset >= 0 ? gposOffset : kernOffset);
            return Kerning.None;
        }
    }

    // A VariationIndex device table: outer and inner indices, then the format 0x8000. Other device tables are ppem hints and add nothing.
    private static int DeviceDelta(ReadOnlySpan<byte> data, int offset, Variations.ItemVariationStore store, float[] scalars)
    {
        if (offset + 6 > data.Length || BigEndian.UInt16(data, offset + 4) != 0x8000)
        {
            return 0;
        }

        float delta = store.GetDelta(BigEndian.UInt16(data, offset), BigEndian.UInt16(data, offset + 2), scalars);
        return (int)MathF.Floor(delta + 0.5f);
    }

    private FontStyle VariedStyle(FontStyle style) => Matching.VariableRange.StyleAt(_instance!.Data.Space, _instance.User, style);

    // Adobe Technical Note #5902: the instance's own PostScript name, else the prefix (name ID 25, else the family without
    // spaces and punctuation) and, per axis away from its default, "_" + value + tag; a name over 63 characters is hashed.
    private string VariedPostScriptName(string family, int named)
    {
        VariationInstance instance = _instance!;
        VariationSpace space = instance.Data.Space;
        if (named >= 0 && space.Instances[named].PostScriptNameId is > 0 and < 0xFFFF && Name.GetEnglish(space.Instances[named].PostScriptNameId) is string own)
        {
            return own;
        }

        string prefix = Name.GetEnglish(25) ?? new string([.. family.Where(char.IsAsciiLetterOrDigit)]);
        var name = new StringBuilder(prefix);
        for (int i = 0; i < space.Axes.Length; i++)
        {
            if (instance.User[i] != space.Axes[i].Default)
            {
                name.Append('_').Append(Math.Round(instance.User[i], 3).ToString("0.###", CultureInfo.InvariantCulture)).Append(space.Axes[i].Tag.ToString().TrimEnd(' ', '_'));
            }
        }

        if (name.Length <= 63)
        {
            return name.ToString();
        }

        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name.ToString())))[..16];
        return $"{prefix}-{hash}...";
    }
}
