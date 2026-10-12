using Egami.Fonts.Parsing;

namespace Egami.Fonts.Tables;

/// <summary>The font header ('head'): units per em, the bounding box and the loca format.</summary>
internal sealed class HeadTable
{
    public const int Size = 54;

    public float Version { get; init; } = 1;

    public float FontRevision { get; init; }

    public uint CheckSumAdjustment { get; init; }

    public uint MagicNumber { get; init; }

    public ushort Flags { get; init; }

    public ushort UnitsPerEm { get; init; } = 1000;

    public long Created { get; init; }

    public long Modified { get; init; }

    public short XMin { get; init; }

    public short YMin { get; init; }

    public short XMax { get; init; }

    public short YMax { get; init; }

    public ushort MacStyle { get; init; }

    public ushort LowestRecPpem { get; init; }

    public short FontDirectionHint { get; init; }

    public short IndexToLocFormat { get; init; }

    public short GlyphDataFormat { get; init; }

    public static HeadTable Read(ReadOnlySpan<byte> data, long offset)
    {
        var r = new FontReader(data, offset);
        return new HeadTable
        {
            Version = r.ReadFixed(),
            FontRevision = r.ReadFixed(),
            CheckSumAdjustment = r.ReadUInt32(),
            MagicNumber = r.ReadUInt32(),
            Flags = r.ReadUInt16(),
            UnitsPerEm = r.ReadUInt16(),
            Created = r.ReadInt64(),
            Modified = r.ReadInt64(),
            XMin = r.ReadInt16(),
            YMin = r.ReadInt16(),
            XMax = r.ReadInt16(),
            YMax = r.ReadInt16(),
            MacStyle = r.ReadUInt16(),
            LowestRecPpem = r.ReadUInt16(),
            FontDirectionHint = r.ReadInt16(),
            IndexToLocFormat = r.ReadInt16(),
            GlyphDataFormat = r.ReadInt16(),
        };
    }
}

/// <summary>The maximum profile ('maxp'): the glyph count, and for TrueType the limits a rasterizer would allocate for.</summary>
internal sealed class MaxpTable
{
    public uint Version { get; init; } = 0x00005000;

    public ushort NumGlyphs { get; init; }

    /// <summary>The version 1.0 fields after numGlyphs, kept as bytes for the subsetter.</summary>
    public byte[] TrueTypeFields { get; init; } = [];

    public static MaxpTable Read(ReadOnlySpan<byte> data, long offset)
    {
        var r = new FontReader(data, offset);
        uint version = r.ReadUInt32();
        ushort count = r.ReadUInt16();
        byte[] fields = version == 0x00010000 && r.Remaining >= 26 ? r.ReadBytes(26).ToArray() : [];
        return new MaxpTable { Version = version, NumGlyphs = count, TrueTypeFields = fields };
    }
}

/// <summary>The horizontal header ('hhea').</summary>
internal sealed class HheaTable
{
    public short Ascender { get; init; }

    public short Descender { get; init; }

    public short LineGap { get; init; }

    public ushort AdvanceWidthMax { get; init; }

    public short MinLeftSideBearing { get; init; }

    public short MinRightSideBearing { get; init; }

    public short XMaxExtent { get; init; }

    public short CaretSlopeRise { get; init; } = 1;

    public short CaretSlopeRun { get; init; }

    public short CaretOffset { get; init; }

    public short MetricDataFormat { get; init; }

    public ushort NumberOfHMetrics { get; init; }

    public static HheaTable Read(ReadOnlySpan<byte> data, long offset)
    {
        var r = new FontReader(data, offset);
        r.Skip(4);
        short ascender = r.ReadInt16(), descender = r.ReadInt16(), lineGap = r.ReadInt16();
        ushort advanceWidthMax = r.ReadUInt16();
        short minLsb = r.ReadInt16(), minRsb = r.ReadInt16(), xMaxExtent = r.ReadInt16();
        short rise = r.ReadInt16(), run = r.ReadInt16(), caretOffset = r.ReadInt16();
        r.Skip(8);
        return new HheaTable
        {
            Ascender = ascender,
            Descender = descender,
            LineGap = lineGap,
            AdvanceWidthMax = advanceWidthMax,
            MinLeftSideBearing = minLsb,
            MinRightSideBearing = minRsb,
            XMaxExtent = xMaxExtent,
            CaretSlopeRise = rise,
            CaretSlopeRun = run,
            CaretOffset = caretOffset,
            MetricDataFormat = r.ReadInt16(),
            NumberOfHMetrics = r.ReadUInt16(),
        };
    }
}

/// <summary>The OS/2 and Windows metrics table. Fields past a short table's end stay zero.</summary>
internal sealed class Os2Table
{
    public ushort Version { get; set; }

    public short XAvgCharWidth { get; set; }

    public ushort WeightClass { get; set; } = 400;

    public ushort WidthClass { get; set; } = 5;

    public ushort FsType { get; set; }

    public short SubscriptXSize { get; set; }

    public short SubscriptYSize { get; set; }

    public short SubscriptXOffset { get; set; }

    public short SubscriptYOffset { get; set; }

    public short SuperscriptXSize { get; set; }

    public short SuperscriptYSize { get; set; }

    public short SuperscriptXOffset { get; set; }

    public short SuperscriptYOffset { get; set; }

    public short StrikeoutSize { get; set; }

    public short StrikeoutPosition { get; set; }

    public short FamilyClass { get; set; }

    public byte[] Panose { get; set; } = new byte[10];

    public uint UnicodeRange1 { get; set; }

    public uint UnicodeRange2 { get; set; }

    public uint UnicodeRange3 { get; set; }

    public uint UnicodeRange4 { get; set; }

    public FontTag VendorId { get; set; }

    public ushort FsSelection { get; set; }

    public ushort FirstCharIndex { get; set; }

    public ushort LastCharIndex { get; set; }

    public short TypoAscender { get; set; }

    public short TypoDescender { get; set; }

    public short TypoLineGap { get; set; }

    public ushort WinAscent { get; set; }

    public ushort WinDescent { get; set; }

    public uint CodePageRange1 { get; set; }

    public uint CodePageRange2 { get; set; }

    public short XHeight { get; set; }

    public short CapHeight { get; set; }

    public ushort DefaultChar { get; set; }

    public ushort BreakChar { get; set; }

    public ushort MaxContext { get; set; }

    public ushort LowerOpticalPointSize { get; set; }

    public ushort UpperOpticalPointSize { get; set; }

    /// <summary>Gets whether the typo metrics are the ones to use (fsSelection bit 7, USE_TYPO_METRICS).</summary>
    public bool UseTypoMetrics => (FsSelection & 0x80) != 0;

    /// <summary>Reads as many fields as the table holds; a table shorter than version 0's 68 bytes still yields its leading fields.</summary>
    public static Os2Table Read(ReadOnlySpan<byte> data)
    {
        var t = new Os2Table();
        var r = new FontReader(data);
        if (r.Remaining < 2)
        {
            return t;
        }

        t.Version = r.ReadUInt16();
        if (r.Remaining < 66)
        {
            // Too short for version 0: keep the defaults rather than read a partial set.
            return t;
        }

        t.XAvgCharWidth = r.ReadInt16();
        t.WeightClass = r.ReadUInt16();
        t.WidthClass = r.ReadUInt16();
        t.FsType = r.ReadUInt16();
        t.SubscriptXSize = r.ReadInt16();
        t.SubscriptYSize = r.ReadInt16();
        t.SubscriptXOffset = r.ReadInt16();
        t.SubscriptYOffset = r.ReadInt16();
        t.SuperscriptXSize = r.ReadInt16();
        t.SuperscriptYSize = r.ReadInt16();
        t.SuperscriptXOffset = r.ReadInt16();
        t.SuperscriptYOffset = r.ReadInt16();
        t.StrikeoutSize = r.ReadInt16();
        t.StrikeoutPosition = r.ReadInt16();
        t.FamilyClass = r.ReadInt16();
        t.Panose = r.ReadBytes(10).ToArray();
        t.UnicodeRange1 = r.ReadUInt32();
        t.UnicodeRange2 = r.ReadUInt32();
        t.UnicodeRange3 = r.ReadUInt32();
        t.UnicodeRange4 = r.ReadUInt32();
        t.VendorId = r.ReadTag();
        t.FsSelection = r.ReadUInt16();
        t.FirstCharIndex = r.ReadUInt16();
        t.LastCharIndex = r.ReadUInt16();
        if (r.Remaining < 10)
        {
            return t;
        }

        t.TypoAscender = r.ReadInt16();
        t.TypoDescender = r.ReadInt16();
        t.TypoLineGap = r.ReadInt16();
        t.WinAscent = r.ReadUInt16();
        t.WinDescent = r.ReadUInt16();
        if (t.Version < 1 || r.Remaining < 8)
        {
            return t;
        }

        t.CodePageRange1 = r.ReadUInt32();
        t.CodePageRange2 = r.ReadUInt32();
        if (t.Version < 2 || r.Remaining < 10)
        {
            return t;
        }

        t.XHeight = r.ReadInt16();
        t.CapHeight = r.ReadInt16();
        t.DefaultChar = r.ReadUInt16();
        t.BreakChar = r.ReadUInt16();
        t.MaxContext = r.ReadUInt16();
        if (t.Version < 5 || r.Remaining < 4)
        {
            return t;
        }

        t.LowerOpticalPointSize = r.ReadUInt16();
        t.UpperOpticalPointSize = r.ReadUInt16();
        return t;
    }
}

/// <summary>The PostScript table ('post'): italic angle, underline and fixed pitch. Glyph names are not read in 2.7.1.</summary>
internal sealed class PostTable
{
    public float Version { get; init; } = 3;

    public float ItalicAngle { get; init; }

    public short UnderlinePosition { get; init; }

    public short UnderlineThickness { get; init; }

    public uint IsFixedPitch { get; init; }

    public static PostTable Read(ReadOnlySpan<byte> data, long offset)
    {
        var r = new FontReader(data, offset);
        return new PostTable
        {
            Version = r.ReadFixed(),
            ItalicAngle = r.ReadFixed(),
            UnderlinePosition = r.ReadInt16(),
            UnderlineThickness = r.ReadInt16(),
            IsFixedPitch = r.ReadUInt32(),
        };
    }
}
