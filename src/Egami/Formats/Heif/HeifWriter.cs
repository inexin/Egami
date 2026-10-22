using System.Buffers.Binary;
using System.Text;

namespace Egami.Formats.Heif;

/// <summary>
/// Writes a HEIF file (ISO/IEC 23008-12 over ISO/IEC 14496-12) from an item model: ftyp, one meta box (hdlr, pitm, iloc, iinf,
/// iref, iprp with ipco and ipma, idat) and the items' data in one mdat after it, so a reader can parse the meta box before any
/// coded data. It knows boxes and items but no codec: the AVIF writer adds av01 items with their av1C property. Field sizes
/// follow the content: 16-bit item ids while they fit, 32-bit offsets unless the file passes 4 GiB, one-byte property indexes
/// unless there are more than 127 properties.
/// </summary>
internal sealed class HeifWriter
{
    private readonly uint _majorBrand;
    private readonly uint[] _compatibleBrands;
    private readonly List<Entry> _items = [];
    private readonly List<byte[]> _properties = [];
    private readonly List<(uint Type, uint From, uint[] To)> _references = [];
    private readonly List<(uint Type, uint[] Entities)> _groups = [];

    /// <summary>Starts a file with the ftyp box's brands.</summary>
    /// <param name="majorBrand">The major brand, such as avif.</param>
    /// <param name="compatibleBrands">The compatible brands, such as avif, mif1, miaf and MA1B.</param>
    internal HeifWriter(string majorBrand, params string[] compatibleBrands)
    {
        _majorBrand = IsoTypes.Of(majorBrand);
        _compatibleBrands = [.. compatibleBrands.Select(b => IsoTypes.Of(b))];
    }

    /// <summary>Gets or sets the primary item's id; 0 writes no pitm box.</summary>
    internal uint PrimaryItemId { get; set; }

    /// <summary>Gets the number of items added.</summary>
    internal int ItemCount => _items.Count;

    /// <summary>Adds an item and returns its id, numbered from 1 in the order added.</summary>
    /// <param name="type">The item type, such as av01, grid, tmap, Exif or mime.</param>
    /// <param name="data">The item's data.</param>
    /// <param name="name">The item's name.</param>
    /// <param name="hidden">Whether the item is hidden (infe flag 1), as an alpha or gain map item is.</param>
    /// <param name="contentType">A mime item's content type, such as application/rdf+xml.</param>
    /// <param name="inIdat">Whether the data goes in the meta box's idat (construction method 1) rather than the mdat.</param>
    internal uint AddItem(string type, ReadOnlyMemory<byte> data, string name = "", bool hidden = false, string? contentType = null, bool inIdat = false)
    {
        uint id = (uint)_items.Count + 1;
        _items.Add(new Entry(id, IsoTypes.Of(type), data, name, hidden, contentType, inIdat));
        return id;
    }

    /// <summary>
    /// Adds a property box (a whole box, header included) to ipco and returns its index, counted from 1; a box equal to one
    /// already added shares that one's index.
    /// </summary>
    internal int AddProperty(byte[] box)
    {
        for (int i = 0; i < _properties.Count; i++)
        {
            if (_properties[i].AsSpan().SequenceEqual(box))
            {
                return i + 1;
            }
        }

        _properties.Add(box);
        return _properties.Count;
    }

    /// <summary>Associates a property with an item, in the order the item's associations are written.</summary>
    internal void Associate(uint item, int property, bool essential = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(property, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(property, _properties.Count);
        _items[(int)item - 1].Associations.Add((property, essential));
    }

    /// <summary>Adds a property box and associates it with an item.</summary>
    internal void AddProperty(uint item, byte[] box, bool essential = false) => Associate(item, AddProperty(box), essential);

    /// <summary>Adds an item reference of a type (such as dimg, auxl, cdsc or prem) from one item to others, in order.</summary>
    internal void Reference(string type, uint from, params uint[] to) => _references.Add((IsoTypes.Of(type), from, to));

    /// <summary>
    /// Adds an entity group of a type (such as altr, alternatives in order of preference) to the grpl box, with an id numbered
    /// after the items'.
    /// </summary>
    internal void Group(string type, params uint[] entities) => _groups.Add((IsoTypes.Of(type), entities));

    /// <summary>Writes the file.</summary>
    internal void Write(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        long mdatData = 0;
        foreach (Entry item in _items)
        {
            if (!item.InIdat)
            {
                mdatData += item.Data.Length;
            }
        }

        // The meta box's size does not depend on the offsets, so it is built once with them at 0 to place the mdat.
        byte[] ftyp = FileType();
        bool wide = ftyp.Length + 16 + mdatData > uint.MaxValue - 1024 * 1024;
        byte[] meta = Meta(0, wide);
        long mdatHeader = mdatData + 8 > uint.MaxValue ? 16 : 8;
        meta = Meta(ftyp.Length + meta.Length + mdatHeader, wide);

        output.Write(ftyp);
        output.Write(meta);
        Span<byte> header = stackalloc byte[16];
        if (mdatHeader == 16)
        {
            BinaryPrimitives.WriteUInt32BigEndian(header, 1);
            BinaryPrimitives.WriteUInt32BigEndian(header[4..], IsoTypes.Of("mdat"));
            BinaryPrimitives.WriteUInt64BigEndian(header[8..], (ulong)(mdatData + 16));
            output.Write(header);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(mdatData + 8));
            BinaryPrimitives.WriteUInt32BigEndian(header[4..], IsoTypes.Of("mdat"));
            output.Write(header[..8]);
        }

        foreach (Entry item in _items)
        {
            if (!item.InIdat)
            {
                output.Write(item.Data.Span);
            }
        }
    }

    /// <summary>Writes the file to a new array.</summary>
    internal byte[] ToArray()
    {
        using MemoryStream stream = new();
        Write(stream);
        return stream.ToArray();
    }

    /// <summary>Builds a box from its type and contents.</summary>
    internal static byte[] Box(string type, params ReadOnlySpan<byte[]> parts)
    {
        int length = 8;
        foreach (byte[] part in parts)
        {
            length += part.Length;
        }

        byte[] box = new byte[length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)length);
        BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(4), IsoTypes.Of(type));
        int at = 8;
        foreach (byte[] part in parts)
        {
            part.CopyTo(box, at);
            at += part.Length;
        }

        return box;
    }

    /// <summary>Builds a full box: its version and flags, then its contents.</summary>
    internal static byte[] FullBox(string type, int version, int flags, params ReadOnlySpan<byte[]> parts)
    {
        byte[] head = [(byte)version, (byte)(flags >> 16), (byte)(flags >> 8), (byte)flags];
        return Box(type, [head, .. parts]);
    }

    /// <summary>The ispe property: the image's width and height.</summary>
    internal static byte[] SpatialExtents(int width, int height) => FullBox("ispe", 0, 0, U32((uint)width), U32((uint)height));

    /// <summary>The pixi property: the bits of each channel.</summary>
    internal static byte[] PixelInformation(int bitDepth, int channels) =>
        FullBox("pixi", 0, 0, [(byte)channels, .. Enumerable.Repeat((byte)bitDepth, channels)]);

    /// <summary>An nclx colr property: CICP code points (ITU-T H.273) and the range flag.</summary>
    internal static byte[] ColorNclx(int primaries, int transfer, int matrix, bool fullRange) =>
        Box("colr", U32(IsoTypes.Of("nclx")), U16(primaries), U16(transfer), U16(matrix), [(byte)(fullRange ? 0x80 : 0)]);

    /// <summary>An ICC colr property (prof).</summary>
    internal static byte[] ColorIcc(ReadOnlySpan<byte> profile) => Box("colr", U32(IsoTypes.Of("prof")), profile.ToArray());

    /// <summary>The auxC property: an auxiliary image's type URN, such as the alpha URN.</summary>
    internal static byte[] AuxiliaryType(string urn) => FullBox("auxC", 0, 0, [.. Encoding.UTF8.GetBytes(urn), 0]);

    /// <summary>The clli property: the maximum content light level and maximum frame-average light level, in cd/m².</summary>
    internal static byte[] ContentLightLevel(int maxCll, int maxFall) => Box("clli", U16(maxCll), U16(maxFall));

    /// <summary>
    /// The mdcv property: the mastering display's primaries (green, blue, red x and y, as SMPTE ST 2086 orders them) and white
    /// point in 0.00002 units, and its luminance in 0.0001 cd/m².
    /// </summary>
    internal static byte[] MasteringDisplay(ReadOnlySpan<ushort> primariesXY, ushort whiteX, ushort whiteY, uint maxLuminance, uint minLuminance)
    {
        byte[] content = new byte[24];
        for (int i = 0; i < 6; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(content.AsSpan(2 * i), primariesXY[i]);
        }

        BinaryPrimitives.WriteUInt16BigEndian(content.AsSpan(12), whiteX);
        BinaryPrimitives.WriteUInt16BigEndian(content.AsSpan(14), whiteY);
        BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(16), maxLuminance);
        BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(20), minLuminance);
        return Box("mdcv", content);
    }

    /// <summary>The irot property: the anticlockwise rotation in quarter turns.</summary>
    internal static byte[] Rotation(int quarterTurns) => Box("irot", [(byte)(quarterTurns & 3)]);

    /// <summary>The imir property: 0 mirrors top to bottom, 1 left to right.</summary>
    internal static byte[] Mirror(int axis) => Box("imir", [(byte)(axis & 1)]);

    /// <summary>A big-endian 16-bit field.</summary>
    internal static byte[] U16(int value) => [(byte)(value >> 8), (byte)value];

    /// <summary>A big-endian 32-bit field.</summary>
    internal static byte[] U32(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private byte[] FileType()
    {
        List<byte[]> parts = [U32(_majorBrand), U32(0)];
        parts.AddRange(_compatibleBrands.Select(U32));
        return Box("ftyp", [.. parts]);
    }

    private byte[] Meta(long mdatStart, bool wide)
    {
        bool longIds = _items.Count > ushort.MaxValue;
        List<byte[]> children = [FullBox("hdlr", 0, 0, U32(0), U32(IsoTypes.Pict), new byte[12], [0])];
        if (PrimaryItemId != 0)
        {
            children.Add(FullBox("pitm", longIds ? 1 : 0, 0, Id(PrimaryItemId, longIds)));
        }

        children.Add(Location(mdatStart, wide, longIds));
        children.Add(Information(longIds));
        if (_references.Count > 0)
        {
            List<byte[]> references = [];
            foreach ((uint type, uint from, uint[] to) in _references)
            {
                List<byte[]> fields = [Id(from, longIds), U16(to.Length)];
                fields.AddRange(to.Select(t => Id(t, longIds)));
                references.Add(Box(IsoTypes.ToString(type), [.. fields]));
            }

            children.Add(FullBox("iref", longIds ? 1 : 0, 0, [.. references]));
        }

        if (_properties.Count > 0)
        {
            children.Add(Box("iprp", Box("ipco", [.. _properties]), Associations(longIds)));
        }

        if (_groups.Count > 0)
        {
            // Group ids follow the item ids, as libavif numbers them, so no id names both.
            List<byte[]> groups = [];
            uint groupId = (uint)_items.Count;
            foreach ((uint type, uint[] entities) in _groups)
            {
                groups.Add(FullBox(IsoTypes.ToString(type), 0, 0, [U32(++groupId), U32((uint)entities.Length), .. entities.Select(e => U32(e))]));
            }

            children.Add(Box("grpl", [.. groups]));
        }

        List<byte[]> idat = [.. _items.Where(i => i.InIdat).Select(i => i.Data.ToArray())];
        if (idat.Count > 0)
        {
            children.Add(Box("idat", [.. idat]));
        }

        return FullBox("meta", 0, 0, [.. children]);
    }

    private byte[] Location(long mdatStart, bool wide, bool longIds)
    {
        bool idat = _items.Any(i => i.InIdat);
        int version = longIds ? 2 : idat ? 1 : 0;
        int offsetSize = wide ? 8 : 4;
        int lengthSize = _items.Any(i => (long)i.Data.Length > uint.MaxValue) ? 8 : 4;
        List<byte[]> fields = [U16((offsetSize << 12) | (lengthSize << 8)), version < 2 ? U16(_items.Count) : U32((uint)_items.Count)];
        long mdatAt = mdatStart, idatAt = 0;
        foreach (Entry item in _items)
        {
            fields.Add(version < 2 ? U16((int)item.Id) : U32(item.Id));
            if (version > 0)
            {
                fields.Add(U16(item.InIdat ? 1 : 0));
            }

            // data_reference_index 0 (this file), no base offset, one extent.
            fields.Add(U16(0));
            fields.Add(U16(1));
            long offset = item.InIdat ? idatAt : mdatAt;
            fields.Add(offsetSize == 8 ? U64((ulong)offset) : U32((uint)offset));
            fields.Add(lengthSize == 8 ? U64((ulong)item.Data.Length) : U32((uint)item.Data.Length));
            if (item.InIdat)
            {
                idatAt += item.Data.Length;
            }
            else
            {
                mdatAt += item.Data.Length;
            }
        }

        return FullBox("iloc", version, 0, [.. fields]);
    }

    private byte[] Information(bool longIds)
    {
        List<byte[]> entries = [longIds ? U32((uint)_items.Count) : U16(_items.Count)];
        foreach (Entry item in _items)
        {
            List<byte[]> fields = [Id(item.Id, longIds), U16(0), U32(item.Type), [.. Encoding.UTF8.GetBytes(item.Name), 0]];
            if (item.ContentType is string contentType)
            {
                fields.Add([.. Encoding.UTF8.GetBytes(contentType), 0]);
            }

            entries.Add(FullBox("infe", longIds ? 3 : 2, item.Hidden ? 1 : 0, [.. fields]));
        }

        return FullBox("iinf", longIds ? 1 : 0, 0, [.. entries]);
    }

    private byte[] Associations(bool longIds)
    {
        bool wideIndex = _properties.Count > 127;
        List<Entry> associated = [.. _items.Where(i => i.Associations.Count > 0)];
        List<byte[]> fields = [U32((uint)associated.Count)];
        foreach (Entry item in associated)
        {
            fields.Add(Id(item.Id, longIds));
            fields.Add([(byte)item.Associations.Count]);
            foreach ((int index, bool essential) in item.Associations)
            {
                fields.Add(wideIndex ? U16(index | (essential ? 0x8000 : 0)) : [(byte)(index | (essential ? 0x80 : 0))]);
            }
        }

        return FullBox("ipma", longIds ? 1 : 0, wideIndex ? 1 : 0, [.. fields]);
    }

    private static byte[] Id(uint id, bool longIds) => longIds ? U32(id) : U16((int)id);

    private static byte[] U64(ulong value) => [.. U32((uint)(value >> 32)), .. U32((uint)value)];

    private sealed record Entry(uint Id, uint Type, ReadOnlyMemory<byte> Data, string Name, bool Hidden, string? ContentType, bool InIdat)
    {
        internal List<(int Index, bool Essential)> Associations { get; } = [];
    }
}
