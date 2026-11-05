using System.Buffers.Binary;

namespace Egami.Formats.Psd;

/// <summary>The kinds of layer effect, in the order they are drawn, bottom to top.</summary>
internal enum PsdEffectKind
{
    DropShadow,
    OuterGlow,
    PatternOverlay,
    GradientOverlay,
    ColorOverlay,
    Satin,
    InnerGlow,
    InnerShadow,
    Bevel,
    Stroke,
}

/// <summary>One layer effect that is turned on: its kind and its settings, with the keys of the lfx2 descriptor.</summary>
internal sealed record PsdEffect(PsdEffectKind Kind, PsdDescriptor Settings)
{
    /// <summary>Gets whether the effect is drawn under the layer's content (drop shadow and outer glow).</summary>
    public bool IsUnder => Kind is PsdEffectKind.DropShadow or PsdEffectKind.OuterGlow;

    /// <summary>Gets whether the effect is drawn inside the layer's shape over its content (the overlays, satin, inner glow and shadow, bevel).</summary>
    public bool IsInterior => !IsUnder && Kind != PsdEffectKind.Stroke;

    /// <summary>Gets the effect's opacity, 0 to 1 (the bevel's are its highlight's and shadow's own).</summary>
    public float Opacity => (float)Math.Clamp((Settings.Number("Opct") ?? 100) / 100, 0, 1);

    /// <summary>Gets the blend key of the effect's mode (a record key such as "mul "), from <paramref name="key"/> (Md by default).</summary>
    public string ModeKey(string key = "Md  ") => PsdEffects.RecordKeyOf(Settings.Items.GetValueOrDefault(key) as string);

    /// <summary>
    /// Gets a length setting in pixels. The effects' scale (Scale Effects) is not applied: the sizes are stored already scaled,
    /// and the scale is only what the dialog shows.
    /// </summary>
    public double Pixels(string key) => Math.Max(0, Settings.Number(key) ?? 0);
}

/// <summary>
/// A layer's effects (layer style), read from its lfx2 or lmfx descriptor, or from the older lrFX block when it has neither:
/// the effects turned on (several of a kind in newer files), in drawing order, the global light,
/// and the layer's flags for how they combine (blend interior effects as group, transparency shapes layer, layer mask and
/// vector mask hide effects).
/// </summary>
internal sealed class PsdEffects
{
    /// <summary>Gets the key under which a record keeps its effect flags (infx, tsly, lmgm, vmgm), only when it has effects.</summary>
    public const string FlagsKey = "fxfl";

    /// <summary>Gets the effects turned on, bottom first.</summary>
    public required List<PsdEffect> Items { get; init; }


    /// <summary>Gets whether the interior effects blend with the layer's content before its mode and fill opacity (infx).</summary>
    public bool InteriorAsGroup { get; init; }

    /// <summary>Gets whether the layer's transparency shapes its effects (tsly, on unless the file says off).</summary>
    public bool TransparencyShapes { get; init; } = true;

    /// <summary>Gets whether the user mask cuts the effects instead of shaping them (lmgm).</summary>
    public bool UserMaskHides { get; init; }

    /// <summary>Gets whether the vector mask cuts the effects instead of shaping them (vmgm).</summary>
    public bool VectorMaskHides { get; init; }

    /// <summary>Gets the document's global light angle in degrees (resource 1037; 120 when absent).</summary>
    public double GlobalAngle { get; init; } = 120;

    /// <summary>Gets the document's global light altitude in degrees (resource 1049; 30 when absent).</summary>
    public double GlobalAltitude { get; init; } = 30;

    /// <summary>Gets how far, in pixels, the effects reach beyond the layer's shape.</summary>
    public int Reach
    {
        get
        {
            double reach = 0;
            foreach (PsdEffect e in Items)
            {
                double size = e.Pixels(e.Kind == PsdEffectKind.Stroke ? "Sz  " : "blur");
                reach = Math.Max(reach, e.Kind switch
                {
                    PsdEffectKind.DropShadow => e.Pixels("Dstn") + (2 * size),
                    PsdEffectKind.OuterGlow => 2 * size,
                    PsdEffectKind.Stroke => size,
                    PsdEffectKind.Bevel => size + e.Pixels("Sftn"),
                    _ => 0,
                });
            }

            return (int)Math.Min(Math.Ceiling(reach) + 2, 1 << 16);
        }
    }

    /// <summary>The blocks that hold a layer's effects descriptor, the first present winning: lmfx (several of a kind), lfx2, and lfxs (a group's).</summary>
    public static readonly string[] DescriptorKeys = ["lmfx", "lfx2", "lfxs"];

    /// <summary>Returns whether the record has any effects block: a descriptor or the old lrFX.</summary>
    public static bool Has(PsdLayerRecord record) => Has(record.Blocks);

    /// <summary>Returns whether the blocks hold any effects block: a descriptor or the old lrFX.</summary>
    public static bool Has(IReadOnlyDictionary<string, byte[]> blocks) =>
        blocks.ContainsKey("lmfx") || blocks.ContainsKey("lfx2") || blocks.ContainsKey("lfxs") || blocks.ContainsKey("lrFX");

    /// <summary>Returns whether the layer's effects descriptor cannot be read.</summary>
    public static bool IsDamaged(PsdLayerRecord record) =>
        Descriptor(record) is byte[] data && PsdDescriptor.ReadVersioned(data, 4) is null;

    /// <summary>Returns the layer's effects descriptor block, or <see langword="null"/>.</summary>
    private static byte[]? Descriptor(PsdLayerRecord record)
    {
        foreach (string key in DescriptorKeys)
        {
            if (record.Blocks.TryGetValue(key, out byte[]? data))
            {
                return data;
            }
        }

        return null;
    }

    /// <summary>Reads a layer's effects; <see langword="null"/> when it has none turned on.</summary>
    public static PsdEffects? Of(PsdLayerRecord record, PsdFile file)
    {
        if (!Has(record))
        {
            return null;
        }

        List<PsdEffect> items = [];
        if (Descriptor(record) is byte[] data)
        {
            if (PsdDescriptor.ReadVersioned(data, 4) is not PsdDescriptor d || d.Flag("masterFXSwitch") == false)
            {
                return null;
            }

            foreach ((string key, object value) in d.Items)
            {
                if (KindOf(key) is not PsdEffectKind kind)
                {
                    continue;
                }

                if (value is PsdDescriptor one)
                {
                    Add(items, kind, one);
                }
                else if (value is List<object> list)
                {
                    // Several of a kind: the first is drawn on top.
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        if (list[i] is PsdDescriptor each)
                        {
                            Add(items, kind, each);
                        }
                    }
                }
            }
        }
        else if (record.Blocks.TryGetValue("lrFX", out byte[]? old))
        {
            ReadOld(old, items);
        }

        if (items.Count == 0)
        {
            return null;
        }

        // Stable: instances of a kind keep their order.
        List<PsdEffect> sorted = [.. items.OrderBy(e => e.Kind)];
        int flags = record.Blocks.TryGetValue(FlagsKey, out byte[]? f) && f.Length > 0 ? f[0] : 0;
        (double angle, double altitude) = GlobalLight(file);
        return new PsdEffects
        {
            Items = sorted,
            InteriorAsGroup = (flags & 1) != 0,
            TransparencyShapes = (flags & 2) == 0,
            UserMaskHides = (flags & 4) != 0,
            VectorMaskHides = (flags & 8) != 0,
            GlobalAngle = angle,
            GlobalAltitude = altitude,
        };

        static void Add(List<PsdEffect> items, PsdEffectKind kind, PsdDescriptor settings)
        {
            if (settings.Flag("enab") ?? true)
            {
                items.Add(new PsdEffect(kind, settings));
            }
        }
    }

    /// <summary>Returns the flags byte a record keeps under <see cref="FlagsKey"/>: bit 0 infx, bit 1 tsly off, bit 2 lmgm, bit 3 vmgm.</summary>
    public static byte Flags(bool interiorAsGroup, bool transparencyShapes, bool userMaskHides, bool vectorMaskHides) =>
        (byte)((interiorAsGroup ? 1 : 0) | (transparencyShapes ? 0 : 2) | (userMaskHides ? 4 : 0) | (vectorMaskHides ? 8 : 0));

    /// <summary>Returns the document's global light: the angle (resource 1037) and altitude (1049) in degrees.</summary>
    public static (double Angle, double Altitude) GlobalLight(PsdFile file)
    {
        double angle = 120, altitude = 30;
        foreach ((int id, _, byte[] data) in file.Resources)
        {
            if (data.Length >= 4 && id == 1037)
            {
                angle = BinaryPrimitives.ReadInt32BigEndian(data);
            }
            else if (data.Length >= 4 && id == 1049)
            {
                altitude = BinaryPrimitives.ReadInt32BigEndian(data);
            }
        }

        return (angle, altitude);
    }

    /// <summary>
    /// Tabulates a contour (a ShpC descriptor's Crv points, input and output 0 to 255) at 1024 inputs from 0 to 1, outputs 0 to
    /// 1: a natural spline through each run of smooth points, broken at corner points (Cnty false). No contour is the line.
    /// </summary>
    public static float[] Contour(PsdDescriptor? contour)
    {
        List<(double In, double Out, bool Corner)> points = [];
        if (contour?.Get<List<object>>("Crv ") is List<object> list)
        {
            foreach (object o in list)
            {
                if (o is PsdDescriptor p && p.Number("Hrzn") is double x && p.Number("Vrtc") is double y && double.IsFinite(x) && double.IsFinite(y))
                {
                    points.Add((Math.Clamp(x, 0, 255), Math.Clamp(y, 0, 255), p.Flag("Cnty") == false));
                }
            }
        }

        points = [.. points.OrderBy(p => p.In).DistinctBy(p => p.In)];
        if (points.Count < 2)
        {
            return PsdAdjustment.Spline([(0, 0), (255, 255)]);
        }

        // Each run between corners gets its own spline; a point is the end of one run and the start of the next.
        float[] table = new float[1024];
        int start = 0;
        for (int i = 1; i < points.Count; i++)
        {
            if (!points[i].Corner && i < points.Count - 1)
            {
                continue;
            }

            List<(double In, double Out)> run = [.. points.Skip(start).Take(i - start + 1).Select(p => (p.In, p.Out))];
            float[] part = PsdAdjustment.Spline(run);
            int from = start == 0 ? 0 : (int)Math.Ceiling(points[start].In * 1023 / 255);
            int to = i == points.Count - 1 ? 1023 : (int)Math.Floor(points[i].In * 1023 / 255);
            for (int t = from; t <= to; t++)
            {
                table[t] = part[t];
            }

            start = i;
        }

        return table;
    }

    /// <summary>Looks up a value from 0 to 1 in a 1024-step table, between its steps.</summary>
    public static float Look(float[] table, float v)
    {
        float x = Math.Clamp(v, 0, 1) * 1023;
        int i = Math.Min((int)x, 1022);
        return table[i] + ((table[i + 1] - table[i]) * (x - i));
    }

    /// <summary>
    /// Returns the record blend key of an effect's mode as the descriptor names it (the short ids such as Mltp, or the longer
    /// names newer files use such as multiply), or the key itself when it is one already; "norm" when unknown.
    /// </summary>
    public static string RecordKeyOf(string? mode) => mode switch
    {
        null => "norm",
        "Nrml" or "normal" => "norm",
        "Dslv" or "dissolve" => "diss",
        "Drkn" or "darken" => "dark",
        "Mltp" or "multiply" => "mul ",
        "CBrn" or "colorBurn" => "idiv",
        "linearBurn" => "lbrn",
        "darkerColor" => "dkCl",
        "Lghn" or "lighten" => "lite",
        "Scrn" or "screen" => "scrn",
        "CDdg" or "colorDodge" => "div ",
        "linearDodge" => "lddg",
        "lighterColor" => "lgCl",
        "Ovrl" or "overlay" => "over",
        "SftL" or "softLight" => "sLit",
        "HrdL" or "hardLight" => "hLit",
        "vividLight" => "vLit",
        "linearLight" => "lLit",
        "pinLight" => "pLit",
        "hardMix" => "hMix",
        "Dfrn" or "difference" => "diff",
        "Xclu" or "exclusion" => "smud",
        "blendSubtraction" => "fsub",
        "blendDivide" => "fdiv",
        "H   " or "hue" => "hue ",
        "Strt" or "saturation" => "sat ",
        "Clr " or "color" => "colr",
        "Lmns" or "luminosity" => "lum ",
        _ when mode.Length == 4 && PsdFile.BlendModeOf(mode) is not null => mode,
        _ => mode,
    };

    private static PsdEffectKind? KindOf(string key) => key switch
    {
        "DrSh" or "dropShadowMulti" => PsdEffectKind.DropShadow,
        "IrSh" or "innerShadowMulti" => PsdEffectKind.InnerShadow,
        "OrGl" => PsdEffectKind.OuterGlow,
        "IrGl" => PsdEffectKind.InnerGlow,
        "ebbl" => PsdEffectKind.Bevel,
        "ChFX" => PsdEffectKind.Satin,
        "SoFi" or "solidFillMulti" => PsdEffectKind.ColorOverlay,
        "GrFl" or "gradientFillMulti" => PsdEffectKind.GradientOverlay,
        "patternFill" => PsdEffectKind.PatternOverlay,
        "FrFX" or "frameFXMulti" => PsdEffectKind.Stroke,
        _ => null,
    };

    /// <summary>
    /// Reads the effects of an old lrFX block (the specification's "Effects Layer"), turned on, into descriptors with the
    /// lfx2 keys: drop and inner shadow (dsdw, isdw), outer and inner glow (oglw, iglw), bevel (bevl) and solid fill (sofi).
    /// </summary>
    private static void ReadOld(ReadOnlySpan<byte> data, List<PsdEffect> items)
    {
        if (data.Length < 4)
        {
            return;
        }

        int count = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        int at = 4;
        for (int i = 0; i < count && at + 12 <= data.Length; i++)
        {
            if (!data.Slice(at, 4).SequenceEqual("8BIM"u8))
            {
                return;
            }

            string key = System.Text.Encoding.Latin1.GetString(data.Slice(at + 4, 4));
            long length = BinaryPrimitives.ReadUInt32BigEndian(data[(at + 8)..]);
            at += 12;
            if (length > data.Length - at)
            {
                return;
            }

            ReadOnlySpan<byte> e = data.Slice(at, (int)length);
            at += (int)length;
            if (OldEffect(key, e) is PsdEffect effect)
            {
                items.Add(effect);
            }
        }
    }

    private static PsdEffect? OldEffect(string key, ReadOnlySpan<byte> e)
    {
        // Sizes, angles, distances and percents are 16.16 fixed point.
        static double Fixed(ReadOnlySpan<byte> e, int at) => BinaryPrimitives.ReadInt32BigEndian(e[at..]) / 65536.0;

        static string Mode(ReadOnlySpan<byte> e, int at) => System.Text.Encoding.Latin1.GetString(e.Slice(at + 4, 4));

        static PsdDescriptor Color(ReadOnlySpan<byte> e, int at)
        {
            // Color space 0 (RGB), or anything else read as RGB too; components 0 to 65535.
            double r = BinaryPrimitives.ReadUInt16BigEndian(e[(at + 2)..]) / 257.0;
            double g = BinaryPrimitives.ReadUInt16BigEndian(e[(at + 4)..]) / 257.0;
            double b = BinaryPrimitives.ReadUInt16BigEndian(e[(at + 6)..]) / 257.0;
            return PsdDescriptor.Of("RGBC", ("Rd  ", r), ("Grn ", g), ("Bl  ", b));
        }

        static PsdUnitFloat Unit(string unit, double value) => new(unit, value);

        switch (key)
        {
            case "dsdw" or "isdw" when e.Length >= 41:
                if (e[38] == 0)
                {
                    return null;
                }

                return new PsdEffect(key == "dsdw" ? PsdEffectKind.DropShadow : PsdEffectKind.InnerShadow, PsdDescriptor.Of(
                    key == "dsdw" ? "DrSh" : "IrSh",
                    ("blur", Unit("#Pxl", Fixed(e, 4))),
                    ("Ckmt", Unit("#Prc", Fixed(e, 8))),
                    ("lagl", Unit("#Ang", Fixed(e, 12))),
                    ("Dstn", Unit("#Pxl", Fixed(e, 16))),
                    ("Clr ", Color(e, 20)),
                    ("Md  ", Mode(e, 30)),
                    ("uglg", e[39] != 0),
                    ("Opct", Unit("#Prc", e[40] * 100.0 / 255)),
                    ("layerConceals", true)));
            case "oglw" or "iglw" when e.Length >= 32:
                if (e[30] == 0)
                {
                    return null;
                }

                return new PsdEffect(key == "oglw" ? PsdEffectKind.OuterGlow : PsdEffectKind.InnerGlow, PsdDescriptor.Of(
                    key == "oglw" ? "OrGl" : "IrGl",
                    ("blur", Unit("#Pxl", Fixed(e, 4))),
                    ("Ckmt", Unit("#Prc", Fixed(e, 8))),
                    ("Clr ", Color(e, 12)),
                    ("Md  ", Mode(e, 22)),
                    ("Opct", Unit("#Prc", e[31] * 100.0 / 255)),
                    ("glwS", key == "iglw" && e.Length >= 33 && e[32] == 0 ? "SrcC" : "SrcE")));
            case "bevl" when e.Length >= 58:
                if (e[55] == 0)
                {
                    return null;
                }

                string style = e[52] switch
                {
                    1 => "OtrB",
                    3 => "Embs",
                    4 => "PlEb",
                    5 => "strokeEmboss",
                    _ => "InrB",
                };
                return new PsdEffect(PsdEffectKind.Bevel, PsdDescriptor.Of(
                    "ebbl",
                    ("lagl", Unit("#Ang", Fixed(e, 4))),
                    ("srgR", Unit("#Prc", Fixed(e, 8))),
                    ("blur", Unit("#Pxl", Fixed(e, 12))),
                    ("hglM", Mode(e, 16)),
                    ("sdwM", Mode(e, 24)),
                    ("hglC", Color(e, 32)),
                    ("sdwC", Color(e, 42)),
                    ("bvlS", style),
                    ("hglO", Unit("#Prc", e[53] * 100.0 / 255)),
                    ("sdwO", Unit("#Prc", e[54] * 100.0 / 255)),
                    ("uglg", e[56] != 0),
                    ("bvlD", e[57] == 0 ? "In  " : "Out ")));
            case "sofi" when e.Length >= 24:
                if (e[23] == 0)
                {
                    return null;
                }

                return new PsdEffect(PsdEffectKind.ColorOverlay, PsdDescriptor.Of(
                    "SoFi",
                    ("Md  ", Mode(e, 4)),
                    ("Clr ", Color(e, 12)),
                    ("Opct", Unit("#Prc", e[22] * 100.0 / 255))));
            default:
                return null;
        }
    }
}
