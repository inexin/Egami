using Egami.Fonts.Parsing;

namespace Egami.Fonts.Hinting;

/// <summary>
/// What a TrueType face gives its instructions, read once per face from the tables the OpenType specification defines for them:
/// the maxp limits, the control values ('cvt '), the font and control value programs ('fpgm', 'prep'), the grid-fitting and
/// scan-conversion procedure ('gasp'), the hinted widths ('hdmx'), the linear threshold ('LTSH') and the head flags.
/// </summary>
internal sealed class HintingData
{
    /// <summary>The empty data of a face with no instructions.</summary>
    public static readonly HintingData None = new();

    /// <summary>Gets maxp's maxZones: 1 without a twilight zone, 2 with one.</summary>
    public int MaxZones { get; init; } = 2;

    /// <summary>Gets maxp's maxTwilightPoints.</summary>
    public int MaxTwilightPoints { get; init; }

    /// <summary>Gets maxp's maxStorage.</summary>
    public int MaxStorage { get; init; }

    /// <summary>Gets maxp's maxFunctionDefs.</summary>
    public int MaxFunctionDefs { get; init; }

    /// <summary>Gets maxp's maxInstructionDefs.</summary>
    public int MaxInstructionDefs { get; init; }

    /// <summary>Gets maxp's maxStackElements.</summary>
    public int MaxStackElements { get; init; }

    /// <summary>Gets maxp's maxSizeOfInstructions: the longest glyph program.</summary>
    public int MaxSizeOfInstructions { get; init; }

    /// <summary>Gets the control values in font units.</summary>
    public short[] Cvt { get; init; } = [];

    /// <summary>Gets the font program.</summary>
    public byte[] Fpgm { get; init; } = [];

    /// <summary>Gets the control value program.</summary>
    public byte[] Prep { get; init; } = [];

    /// <summary>Gets the gasp ranges in order: the largest ppem each covers and its flags.</summary>
    public (int MaxPpem, GaspFlags Flags)[] Gasp { get; init; } = [];

    /// <summary>Gets the gasp table's version (0 or 1), or -1 when there is none.</summary>
    public int GaspVersion { get; init; } = -1;

    /// <summary>Gets the hdmx widths by ppem, each a byte per glyph; empty when the table is missing or not usable.</summary>
    public IReadOnlyDictionary<int, byte[]> Hdmx { get; init; } = new Dictionary<int, byte[]>();

    /// <summary>Gets the LTSH table's yPels per glyph: the ppem from which the glyph's advance scales linearly; empty when missing.</summary>
    public byte[] Ltsh { get; init; } = [];

    /// <summary>Gets head.flags.</summary>
    public int HeadFlags { get; init; }

    /// <summary>Gets a value indicating whether the font has instructions to run: a non-zero maxSizeOfInstructions, a font program or a control value program.</summary>
    public bool HasHinting => MaxSizeOfInstructions > 0 || Fpgm.Length > 0 || Prep.Length > 0;

    /// <summary>Gets a value indicating whether outlines scale from a whole ppem (head.flags bit 3).</summary>
    public bool IntegerPpem => (HeadFlags & 0x08) != 0;

    /// <summary>The gasp flags of the first range that covers <paramref name="ppem"/>, or null when none does.</summary>
    public GaspFlags? GaspFor(int ppem)
    {
        foreach ((int max, GaspFlags flags) in Gasp)
        {
            if (ppem <= max)
            {
                return flags;
            }
        }

        return null;
    }

    /// <summary>Reads the hinting tables of a face; damaged optional tables are left out rather than failing the face.</summary>
    public static HintingData Read(Typeface face)
    {
        ReadOnlySpan<byte> maxp = face.GetTableSpan(Tags.Maxp, out _);
        if (maxp.Length < 32 || BigEndian.UInt32(maxp, 0) != 0x00010000)
        {
            return None;
        }

        int glyphs = BigEndian.UInt16(maxp, 4);
        return new HintingData
        {
            MaxZones = BigEndian.UInt16(maxp, 14),
            MaxTwilightPoints = BigEndian.UInt16(maxp, 16),
            MaxStorage = BigEndian.UInt16(maxp, 18),
            MaxFunctionDefs = BigEndian.UInt16(maxp, 20),
            MaxInstructionDefs = BigEndian.UInt16(maxp, 22),
            MaxStackElements = BigEndian.UInt16(maxp, 24),
            MaxSizeOfInstructions = BigEndian.UInt16(maxp, 26),
            Cvt = ReadCvt(face.GetTableSpan(Tags.Cvt, out _)),
            Fpgm = face.GetTableSpan(Tags.Fpgm, out _).ToArray(),
            Prep = face.GetTableSpan(Tags.Prep, out _).ToArray(),
            Gasp = ReadGasp(face.GetTableSpan(Tags.Gasp, out _), out int gaspVersion),
            GaspVersion = gaspVersion,
            Hdmx = ReadHdmx(face.GetTableSpan(HdmxTag, out _), glyphs),
            Ltsh = ReadLtsh(face.GetTableSpan(LtshTag, out _), glyphs),
            HeadFlags = face.Head.Flags,
        };
    }

    private static readonly FontTag HdmxTag = new("hdmx");
    private static readonly FontTag LtshTag = new("LTSH");

    private static short[] ReadCvt(ReadOnlySpan<byte> data)
    {
        var cvt = new short[data.Length / 2];
        for (int i = 0; i < cvt.Length; i++)
        {
            cvt[i] = BigEndian.Int16(data, i * 2);
        }

        return cvt;
    }

    private static (int, GaspFlags)[] ReadGasp(ReadOnlySpan<byte> data, out int version)
    {
        version = -1;
        if (data.Length < 4)
        {
            return [];
        }

        version = BigEndian.UInt16(data, 0);
        int count = Math.Min(BigEndian.UInt16(data, 2), (data.Length - 4) / 4);
        var ranges = new (int, GaspFlags)[count];
        for (int i = 0; i < count; i++)
        {
            ranges[i] = (BigEndian.UInt16(data, 4 + (i * 4)), (GaspFlags)BigEndian.UInt16(data, 6 + (i * 4)));
        }

        return ranges;
    }

    // hdmx: version, record count, record size; each record a ppem, a maximum width and a width per glyph, padded to 4 bytes. A
    // table whose record size does not fit the glyph count is not used, except that an upper word of 0xFFFF (written by some
    // tools) is ignored.
    private static Dictionary<int, byte[]> ReadHdmx(ReadOnlySpan<byte> data, int glyphs)
    {
        var records = new Dictionary<int, byte[]>();
        if (data.Length < 8 || BigEndian.UInt16(data, 0) != 0)
        {
            return records;
        }

        int count = BigEndian.Int16(data, 2);
        uint size = BigEndian.UInt32(data, 4);
        if ((size >> 16) == 0xFFFF)
        {
            size &= 0xFFFF;
        }

        if (count is < 0 or > 255 || size < glyphs + 2 || size > ((glyphs + 2 + 3) & ~3) || 8 + ((long)count * size) > data.Length)
        {
            return records;
        }

        for (int r = 0; r < count; r++)
        {
            int at = 8 + (r * (int)size);
            records.TryAdd(data[at], data.Slice(at + 2, glyphs).ToArray());
        }

        return records;
    }

    private static byte[] ReadLtsh(ReadOnlySpan<byte> data, int glyphs)
    {
        if (data.Length < 4 || BigEndian.UInt16(data, 0) != 0 || BigEndian.UInt16(data, 2) != glyphs || data.Length < 4 + glyphs)
        {
            return [];
        }

        return data.Slice(4, glyphs).ToArray();
    }
}

/// <summary>A gasp range's flags.</summary>
[Flags]
internal enum GaspFlags
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>Use instructions.</summary>
    GridFit = 1,

    /// <summary>Use grayscale rendering.</summary>
    DoGray = 2,

    /// <summary>Use instructions in ClearType's symmetric mode (version 1).</summary>
    SymmetricGridFit = 4,

    /// <summary>Smooth in y as well as x under ClearType (version 1).</summary>
    SymmetricSmoothing = 8,
}
