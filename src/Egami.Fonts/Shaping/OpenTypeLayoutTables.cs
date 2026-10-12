using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

using Egami.Fonts.Tables;

namespace Egami.Fonts.Shaping;

/// <summary>A face's GDEF, GSUB, GPOS and kern tables, read once, and the shape plans built for it.</summary>
internal sealed class OpenTypeLayoutTables
{
    private const int MaxPlans = 64;

    private static readonly ConditionalWeakTable<Typeface, OpenTypeLayoutTables> s_tables = new();

    private readonly ConcurrentDictionary<ShapePlanKey, ShapePlan> _plans = new();

    private OpenTypeLayoutTables(Typeface face)
    {
        // As HarfBuzz, a table that fails its structure check is ignored whole.
        Gdef = Read(face, "GDEF", d => LayoutSanitizer.SanitizeGdef(d.Span), d => new GdefTable(d), GdefTable.Empty);
        Gsub = Read(face, "GSUB", d => LayoutSanitizer.SanitizeLayout(d.Span, false), d => new LayoutTable(d, false), LayoutTable.EmptyGsub);
        Gpos = Read(face, "GPOS", d => LayoutSanitizer.SanitizeLayout(d.Span, true), d => new LayoutTable(d, true), LayoutTable.EmptyGpos);
        Kern = face.TryGetTable(new FontTag("kern"), out ReadOnlyMemory<byte> kern) ? new KernTable(kern) : KernTable.Empty;
        Cmap = ReadCmap(face);
    }

    private static CmapTable ReadCmap(Typeface face)
    {
        if (!face.TryGetTable(new FontTag("cmap"), out ReadOnlyMemory<byte> data))
        {
            return face.Cmap;
        }

        try
        {
            int fontPage = face.Os2.Version == 0 ? face.Os2.FsSelection & 0xFF00 : 0;
            return CmapForShaping.Read(data.Span, face.Cmap, fontPage, ArabicTables.SimplifiedArabicPua, ArabicTables.TraditionalArabicPua);
        }
        catch (CorruptDataException)
        {
            return face.Cmap;
        }
    }

    /// <summary>Gets the cmap as HarfBuzz chooses it for shaping (a symbol subtable first).</summary>
    public CmapTable Cmap { get; }

    private static T Read<T>(Typeface face, string tag, Func<ReadOnlyMemory<byte>, bool> sound, Func<ReadOnlyMemory<byte>, T> create, T empty)
    {
        if (!face.TryGetTable(new FontTag(tag), out ReadOnlyMemory<byte> data))
        {
            return empty;
        }

        if (sound(data))
        {
            return create(data);
        }

        face.GetTableSpan(new FontTag(tag), out int offset);
        Typeface.Report(face.Options, "Font.BadTable", $"The {tag} table is damaged; text is shaped without it.", offset);
        return empty;
    }

    public GdefTable Gdef { get; }

    public LayoutTable Gsub { get; }

    public LayoutTable Gpos { get; }

    public KernTable Kern { get; }

    public static OpenTypeLayoutTables For(Typeface face) => s_tables.GetValue(face, f => new OpenTypeLayoutTables(f));

    /// <summary>The plan for a key, built once and kept (up to a bound) for later runs.</summary>
    public ShapePlan GetPlan(ShapePlanKey key, Func<ShapePlanKey, ShapePlan> create)
    {
        if (_plans.TryGetValue(key, out ShapePlan? plan))
        {
            return plan;
        }

        plan = create(key);
        if (_plans.Count < MaxPlans)
        {
            _plans.TryAdd(key, plan);
        }

        return plan;
    }
}
