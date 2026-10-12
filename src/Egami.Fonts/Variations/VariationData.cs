using Egami.Fonts.Parsing;
using Egami.Fonts.Tables;

namespace Egami.Fonts.Variations;

/// <summary>The variation tables of one variable face, read once and shared by every <see cref="Typeface"/> at a position on it.</summary>
internal sealed class VariationData
{
    private GvarTable? _gvar;
    private bool _gvarRead;
    private HvarTable? _hvar;
    private bool _hvarRead;
    private MvarTable? _mvar;
    private bool _mvarRead;
    private ItemVariationStore? _gdef;
    private bool _gdefRead;
    private readonly Typeface _face;
    private readonly Lock _lock = new();

    public VariationData(Typeface face, VariationSpace space)
    {
        _face = face;
        Space = space;
    }

    public VariationSpace Space { get; }

    public GvarTable? Gvar => Load(ref _gvar, ref _gvarRead, () => _face.TryGetTable(Tags.Gvar, out ReadOnlyMemory<byte> t) && _face.Glyf is not null
        ? GvarTable.Read(t, _face.TableOffset(Tags.Gvar), Space.Axes.Length, _face.GlyphCount) : null, Tags.Gvar, "glyph outlines do not vary");

    public HvarTable? Hvar => Load(ref _hvar, ref _hvarRead, () => _face.TryGetTable(Tags.Hvar, out ReadOnlyMemory<byte> t)
        ? HvarTable.Read(t, _face.TableOffset(Tags.Hvar)) : null, Tags.Hvar, "advances vary through phantom points, or not at all");

    public MvarTable? Mvar => Load(ref _mvar, ref _mvarRead, () => _face.TryGetTable(Tags.Mvar, out ReadOnlyMemory<byte> t)
        ? MvarTable.Read(t, _face.TableOffset(Tags.Mvar)) : null, Tags.Mvar, "the face's extents do not vary");

    /// <summary>GDEF's ItemVariationStore (GDEF 1.3), which GPOS device tables index for varied kerning.</summary>
    public ItemVariationStore? GdefStore => Load(ref _gdef, ref _gdefRead, () =>
    {
        if (!_face.TryGetTable(Tags.Gdef, out ReadOnlyMemory<byte> t) || t.Length < 18)
        {
            return null;
        }

        ReadOnlySpan<byte> gdef = t.Span;
        int major = BigEndian.UInt16(gdef, 0), minor = BigEndian.UInt16(gdef, 2);
        if (major != 1 || minor < 3)
        {
            return null;
        }

        int offset = (int)BigEndian.UInt32(gdef, 14);
        return offset == 0 ? null : ItemVariationStore.Read(t, offset, _face.TableOffset(Tags.Gdef));
    }, Tags.Gdef, "kerning does not vary");

    private T? Load<T>(ref T? field, ref bool read, Func<T?> load, FontTag tag, string defaultText)
        where T : class
    {
        if (Volatile.Read(ref read))
        {
            return field;
        }

        lock (_lock)
        {
            if (!read)
            {
                try
                {
                    field = load();
                }
                catch (CorruptDataException e) when (!_face.Options.Strict)
                {
                    _face.Repair("Font.BadTable", $"The {tag} table is damaged ({e.Message}); {defaultText}.", _face.TableOffset(tag));
                    field = null;
                }

                Volatile.Write(ref read, true);
            }

            return field;
        }
    }
}

/// <summary>One position of a variable face: user and normalized coordinates and the region scalars each store needs there.</summary>
internal sealed class VariationInstance
{
    private float[]? _hvarScalars;
    private float[]? _mvarScalars;
    private float[]? _gdefScalars;

    public VariationInstance(VariationData data, float[] user)
    {
        Data = data;
        User = user;
        Coords = data.Space.Normalize(user);
    }

    public VariationData Data { get; }

    /// <summary>Gets the user coordinates, one per fvar axis.</summary>
    public float[] User { get; }

    /// <summary>Gets the normalized F2Dot14 coordinates, one per fvar axis.</summary>
    public float[] Coords { get; }

    public float[]? HvarScalars => Data.Hvar is HvarTable h ? _hvarScalars ??= h.Store.ComputeScalars(Coords) : null;

    public float[]? MvarScalars => Data.Mvar?.Store is ItemVariationStore s ? _mvarScalars ??= s.ComputeScalars(Coords) : null;

    public float[]? GdefScalars => Data.GdefStore is ItemVariationStore s ? _gdefScalars ??= s.ComputeScalars(Coords) : null;

    /// <summary>Gets an MVAR delta, 0 when MVAR does not vary the value.</summary>
    public float Mvar(string tag) => MvarScalars is float[] scalars ? Data.Mvar!.Delta(tag, scalars) : 0;
}

/// <summary>A glyph's outline at a variation position, kept on its <see cref="Typeface"/> after the first use.</summary>
/// <param name="Verbs">The recorded verbs.</param>
/// <param name="Coordinates">The recorded coordinates.</param>
/// <param name="Bounds">The box: the control points' box for TrueType, as a glyph header holds; the curves' box for CFF2.</param>
/// <param name="Advance">The advance from the phantom points, or NaN when they were not computed.</param>
/// <param name="LeftBearing">The left side bearing from the phantom points, or NaN.</param>
internal sealed record VariedGlyph(OutlineVerb[] Verbs, float[] Coordinates, GlyphExtents Bounds, float Advance, float LeftBearing)
{
    public static readonly VariedGlyph Empty = new([], [], GlyphExtents.Empty, float.NaN, float.NaN);
}
