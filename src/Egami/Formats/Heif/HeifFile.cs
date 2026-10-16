using System.Buffers.Binary;
using System.Text;

namespace Egami.Formats.Heif;

/// <summary>
/// A HEIF file's structure (ISO/IEC 23008-12 over ISO/IEC 14496-12): the ftyp brands, the top-level boxes and the root meta box's
/// item model (hdlr, pitm, iinf, iloc, idat, iref, iprp with ipco and ipma, grpl), read from the whole file in memory. It knows
/// boxes and items but no codec: the AVIF reader (and Egami.Heic's HEVC reader) look up the items and properties they need and
/// read item data with <see cref="ItemData"/>. Structural errors throw <see cref="CorruptDataException"/>; entries it can skip are
/// skipped with a <see cref="HeifProblem"/>.
/// </summary>
internal sealed class HeifFile
{
    private readonly Dictionary<uint, HeifItem> _byId = [];
    private IsoBox? _idat;

    private HeifFile(ReadOnlyMemory<byte> bytes, string formatName)
    {
        Bytes = bytes;
        FormatName = formatName;
    }

    /// <summary>Gets the whole file.</summary>
    internal ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>Gets the format named in errors, such as AVIF or HEIC.</summary>
    internal string FormatName { get; }

    /// <summary>Gets the ftyp box's major brand.</summary>
    internal uint MajorBrand { get; private set; }

    /// <summary>Gets the ftyp box's minor version.</summary>
    internal uint MinorVersion { get; private set; }

    /// <summary>Gets the ftyp box's compatible brands, in order.</summary>
    internal uint[] CompatibleBrands { get; private set; } = [];

    /// <summary>Gets the top-level boxes, in file order.</summary>
    internal List<IsoBox> Boxes { get; } = [];

    /// <summary>Gets whether the file has a root meta box.</summary>
    internal bool HasMeta { get; private set; }

    /// <summary>Gets the meta box's handler type, such as pict.</summary>
    internal uint Handler { get; private set; }

    /// <summary>Gets the primary item's id from pitm, or 0 when there is none.</summary>
    internal uint PrimaryItemId { get; private set; }

    /// <summary>Gets the items, in iinf order.</summary>
    internal List<HeifItem> Items { get; } = [];

    /// <summary>Gets the ipco box's properties, in order; an association's index counts from 1.</summary>
    internal List<HeifProperty> Properties { get; } = [];

    /// <summary>Gets the grpl box's entity groups, in order.</summary>
    internal List<HeifEntityGroup> Groups { get; } = [];

    /// <summary>Gets the ids that iloc, ipma or iref name but iinf does not list; their entries were skipped.</summary>
    internal HashSet<uint> UnlistedItemIds { get; } = [];

    /// <summary>Gets the problems skipped over.</summary>
    internal List<HeifProblem> Problems { get; } = [];

    /// <summary>Gets the idat box's contents, or an empty span.</summary>
    internal ReadOnlySpan<byte> Idat => _idat is IsoBox idat ? idat.Content(Bytes.Span) : [];

    /// <summary>Gets the primary item, or <see langword="null"/>.</summary>
    internal HeifItem? PrimaryItem => Item(PrimaryItemId);

    /// <summary>Reads the whole input into memory and parses it.</summary>
    internal static HeifFile Read(EgamiInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        using MemoryStream copy = new();
        byte[] block = new byte[81920];
        int n;
        while ((n = input.Read(block)) > 0)
        {
            input.CheckMemory(copy.Length + n);
            copy.Write(block, 0, n);
        }

        return Parse(copy.GetBuffer().AsMemory(0, (int)copy.Length), input.FormatName);
    }

    /// <summary>Parses a whole file.</summary>
    /// <param name="file">The file's bytes.</param>
    /// <param name="formatName">The format named in errors.</param>
    internal static HeifFile Parse(ReadOnlyMemory<byte> file, string formatName)
    {
        HeifFile heif = new(file, formatName);
        heif.ParseTopLevel();
        return heif;
    }

    /// <summary>Gets whether the major brand or a compatible brand is <paramref name="brand"/>.</summary>
    internal bool HasBrand(uint brand) => MajorBrand == brand || Array.IndexOf(CompatibleBrands, brand) >= 0;

    /// <summary>Gets the item with the id, or <see langword="null"/>.</summary>
    internal HeifItem? Item(uint id) => _byId.GetValueOrDefault(id);

    /// <summary>Gets the item's properties in association order, each with whether it is essential.</summary>
    internal IEnumerable<(HeifProperty Property, bool Essential)> PropertiesOf(HeifItem item)
    {
        foreach (HeifAssociation association in item.Associations)
        {
            yield return (Properties[association.Index - 1], association.Essential);
        }
    }

    /// <summary>Gets the item's first property of the type, or <see langword="null"/>.</summary>
    internal HeifProperty? Property(HeifItem item, uint type)
    {
        foreach (HeifAssociation association in item.Associations)
        {
            HeifProperty property = Properties[association.Index - 1];
            if (property.Type == type)
            {
                return property;
            }
        }

        return null;
    }

    /// <summary>Gets a property box's contents.</summary>
    internal ReadOnlySpan<byte> Content(HeifProperty property) => property.Box.Content(Bytes.Span);

    /// <summary>
    /// Marks each item that has an essential property whose type <paramref name="understood"/> rejects (sets
    /// <see cref="HeifItem.UnsupportedEssential"/>), as ISO/IEC 23008-12 requires: a reader must not use such an item.
    /// </summary>
    internal void MarkUnsupportedEssential(Func<uint, bool> understood)
    {
        foreach (HeifItem item in Items)
        {
            item.UnsupportedEssential = 0;
            foreach ((HeifProperty property, bool essential) in PropertiesOf(item))
            {
                if (essential && !understood(property.Type))
                {
                    item.UnsupportedEssential = property.Type;
                    break;
                }
            }
        }
    }

    /// <summary>Gets the item's data, its extents joined: a slice of the file when there is one extent, else a copy.</summary>
    /// <param name="item">The item.</param>
    /// <param name="limits">The limits a copy is checked against (<see cref="SafetyLimits.MaxTotalBytes"/>), or <see langword="null"/>.</param>
    internal ReadOnlyMemory<byte> ItemData(HeifItem item, SafetyLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.Located)
        {
            throw Corrupt(-1, $"The iloc box does not locate {item}.");
        }

        if (item.DataReferenceIndex != 0)
        {
            throw Corrupt(-1, $"{Capital(item)}'s data is in another file (data reference {item.DataReferenceIndex}).");
        }

        if (item.ConstructionMethod > 1)
        {
            throw Corrupt(-1, $"{Capital(item)} is built from other items' data (construction method {item.ConstructionMethod}), which is not supported.");
        }

        ReadOnlyMemory<byte> source = item.ConstructionMethod == 1 ? (_idat is IsoBox idat ? idat.Content(Bytes) : default) : Bytes;
        long origin = item.ConstructionMethod == 1 && _idat is IsoBox box ? box.ContentStart : 0;
        long total = 0;
        foreach (HeifExtent extent in item.Extents)
        {
            long length = extent.Length == 0 ? source.Length - extent.Offset : extent.Length;
            if (extent.Offset < 0 || extent.Offset > source.Length || length < 0 || length > source.Length - extent.Offset)
            {
                string where = item.ConstructionMethod == 1 ? "idat box" : "file";
                throw new CorruptDataException(FormatName, origin + Math.Clamp(extent.Offset, 0, source.Length),
                    $"An extent of {item} ({extent.Offset}, {extent.Length} bytes) runs past the end of the {where}.")
                { IsTruncation = true };
            }

            total += length;
        }

        if (item.Extents.Count == 1)
        {
            HeifExtent only = item.Extents[0];
            return source.Slice((int)only.Offset, (int)total);
        }

        if (limits is not null && total > limits.MaxTotalBytes)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), limits.MaxTotalBytes, total);
        }

        if (total > Array.MaxLength)
        {
            throw Corrupt(-1, $"{Capital(item)} is {total} bytes, too large to read.");
        }

        byte[] joined = new byte[total];
        int at = 0;
        foreach (HeifExtent extent in item.Extents)
        {
            int length = (int)(extent.Length == 0 ? source.Length - extent.Offset : extent.Length);
            source.Span.Slice((int)extent.Offset, length).CopyTo(joined.AsSpan(at));
            at += length;
        }

        return joined;
    }

    private static string Capital(HeifItem item) => "I" + item.ToString()[1..];

    private void ParseTopLevel()
    {
        ReadOnlySpan<byte> bytes = Bytes.Span;
        IsoBoxReader top = new(bytes, 0, bytes.Length, FormatName);
        bool first = true;
        while (true)
        {
            IsoBox box;
            try
            {
                if (!top.Next(out box))
                {
                    break;
                }
            }
            catch (CorruptDataException e) when (HasMeta)
            {
                // Bytes after the boxes that do not form one, such as data appended to the file, are left alone.
                Problem("Heif.TrailingData", e.Offset, "The data after the last whole top-level box does not form a box; it was ignored.");
                break;
            }

            Boxes.Add(box);
            if (first)
            {
                if (box.Type != FourCC.Ftyp)
                {
                    throw Corrupt(0, $"The file starts with a \"{FourCC.ToString(box.Type)}\" box, not ftyp.");
                }

                ParseFileType(box);
                first = false;
            }
            else if (box.Type == FourCC.Meta)
            {
                if (HasMeta)
                {
                    Problem("Heif.SecondMeta", box.Start, "A second meta box at the top level was ignored.");
                    continue;
                }

                HasMeta = true;
                ParseMeta(box);
            }
        }

        if (first)
        {
            throw Corrupt(0, "The file is empty.");
        }
    }

    private void ParseFileType(IsoBox box)
    {
        ReadOnlySpan<byte> content = box.Content(Bytes.Span);
        if (content.Length < 8 || content.Length % 4 != 0)
        {
            throw Corrupt(box.Start, $"The ftyp box's contents are {content.Length} bytes, not 8 plus a multiple of 4.");
        }

        MajorBrand = BinaryPrimitives.ReadUInt32BigEndian(content);
        MinorVersion = BinaryPrimitives.ReadUInt32BigEndian(content[4..]);
        uint[] brands = new uint[(content.Length - 8) / 4];
        for (int i = 0; i < brands.Length; i++)
        {
            brands[i] = BinaryPrimitives.ReadUInt32BigEndian(content[(8 + (4 * i))..]);
        }

        CompatibleBrands = brands;
    }

    private void ParseMeta(IsoBox meta)
    {
        ReadOnlySpan<byte> bytes = Bytes.Span;
        FullBox(meta, out int metaVersion, out _);
        if (metaVersion != 0)
        {
            throw Corrupt(meta.Start, $"The meta box's version is {metaVersion}, not 0.");
        }

        IsoBox? hdlr = null, pitm = null, iinf = null, iloc = null, iref = null, iprp = null, grpl = null;
        IsoBoxReader children = new(bytes, meta.ContentStart + 4, meta.End, FormatName);
        while (true)
        {
            IsoBox box;
            try
            {
                if (!children.Next(out box))
                {
                    break;
                }
            }
            catch (CorruptDataException e) when (meta.End == bytes.Length && iinf is not null && iloc is not null)
            {
                // A meta box sized to the end of the file (size 0) takes in whatever follows it; data that does not form a box is left alone.
                Problem("Heif.TrailingData", e.Offset, "The data after the last whole top-level box does not form a box; it was ignored.");
                break;
            }

            ref IsoBox? slot = ref hdlr;
            switch (box.Type)
            {
                case FourCC.Hdlr: slot = ref hdlr; break;
                case FourCC.Pitm: slot = ref pitm; break;
                case FourCC.Iinf: slot = ref iinf; break;
                case FourCC.Iloc: slot = ref iloc; break;
                case FourCC.Iref: slot = ref iref; break;
                case FourCC.Iprp: slot = ref iprp; break;
                case FourCC.Grpl: slot = ref grpl; break;
                case FourCC.Idat:
                    if (_idat is not null)
                    {
                        throw Corrupt(box.Start, "The meta box has two idat boxes.");
                    }

                    _idat = box;
                    continue;
                default:
                    continue;
            }

            if (slot is not null)
            {
                throw Corrupt(box.Start, $"The meta box has two {FourCC.ToString(box.Type)} boxes.");
            }

            slot = box;
        }

        if (hdlr is not IsoBox handler)
        {
            throw Corrupt(meta.Start, "The meta box has no hdlr box.");
        }

        ParseHandler(handler);
        if (iinf is IsoBox info)
        {
            ParseItemInfo(info);
        }

        if (pitm is IsoBox primary)
        {
            ParsePrimaryItem(primary);
        }

        if (iloc is IsoBox location)
        {
            ParseItemLocation(location);
        }

        if (iref is IsoBox references)
        {
            ParseItemReferences(references);
        }

        if (iprp is IsoBox properties)
        {
            ParseItemProperties(properties);
        }

        if (grpl is IsoBox groups)
        {
            ParseGroups(groups);
        }
    }

    private void ParseHandler(IsoBox box)
    {
        ReadOnlySpan<byte> content = FullBox(box, out _, out _);
        if (content.Length < 8)
        {
            throw Corrupt(box.Start, "The hdlr box is cut off.");
        }

        Handler = BinaryPrimitives.ReadUInt32BigEndian(content[4..]);
    }

    private void ParsePrimaryItem(IsoBox box)
    {
        ReadOnlySpan<byte> content = FullBox(box, out int version, out _);
        int size = version == 0 ? 2 : 4;
        if (content.Length < size)
        {
            throw Corrupt(box.Start, "The pitm box is cut off.");
        }

        PrimaryItemId = Read(content, size);
        if (!_byId.ContainsKey(PrimaryItemId))
        {
            Problem("Heif.PrimaryItemMissing", box.Start, $"The primary item, {PrimaryItemId}, has no iinf entry.");
        }
    }

    private void ParseItemInfo(IsoBox box)
    {
        ReadOnlySpan<byte> bytes = Bytes.Span;
        ReadOnlySpan<byte> content = FullBox(box, out int version, out _);
        int countSize = version == 0 ? 2 : 4;
        if (content.Length < countSize)
        {
            throw Corrupt(box.Start, "The iinf box is cut off.");
        }

        uint count = Read(content, countSize);
        IsoBoxReader entries = new(bytes, box.ContentStart + 4 + countSize, box.End, FormatName);
        uint seen = 0;
        while (entries.Next(out IsoBox entry))
        {
            if (entry.Type != FourCC.Infe)
            {
                continue;
            }

            seen++;
            ParseItemEntry(entry);
        }

        if (seen != count)
        {
            Problem("Heif.ItemCount", box.Start, $"The iinf box says {count} items but holds {seen}.");
        }
    }

    private void ParseItemEntry(IsoBox box)
    {
        ReadOnlySpan<byte> content = FullBox(box, out int version, out int flags);
        if (version < 2)
        {
            Problem("Heif.OldItemEntry", box.Start, $"An infe box of version {version} (no item type) was skipped.");
            return;
        }

        if (version > 3)
        {
            Problem("Heif.ItemEntryVersion", box.Start, $"An infe box of unknown version {version} was skipped.");
            return;
        }

        int idSize = version == 2 ? 2 : 4;
        if (content.Length < idSize + 6)
        {
            throw Corrupt(box.Start, "An infe box is cut off.");
        }

        uint id = Read(content, idSize);
        int protection = BinaryPrimitives.ReadUInt16BigEndian(content[idSize..]);
        uint type = BinaryPrimitives.ReadUInt32BigEndian(content[(idSize + 2)..]);
        ReadOnlySpan<byte> rest = content[(idSize + 6)..];
        string name = String(ref rest);
        string? contentType = null, encoding = null, uriType = null;
        if (type == FourCC.Mime)
        {
            contentType = String(ref rest);
            encoding = rest.IsEmpty ? null : String(ref rest);
        }
        else if (type == FourCC.Uri)
        {
            uriType = String(ref rest);
        }

        if (_byId.ContainsKey(id))
        {
            throw Corrupt(box.Start, $"Two infe boxes have item id {id}.");
        }

        HeifItem item = new(id, type)
        {
            Name = name,
            ContentType = contentType,
            ContentEncoding = string.IsNullOrEmpty(encoding) ? null : encoding,
            UriType = uriType,
            Hidden = (flags & 1) != 0,
            ProtectionIndex = protection,
        };
        _byId.Add(id, item);
        Items.Add(item);
    }

    private void ParseItemLocation(IsoBox box)
    {
        ReadOnlySpan<byte> content = FullBox(box, out int version, out _);
        if (version > 2)
        {
            throw Corrupt(box.Start, $"The iloc box's version is {version}, not 0, 1 or 2.");
        }

        Cursor at = new(this, box, content);
        int sizes = at.U16();
        int offsetSize = sizes >> 12, lengthSize = (sizes >> 8) & 15, baseSize = (sizes >> 4) & 15;
        int indexSize = version > 0 ? sizes & 15 : 0;
        foreach (int size in (ReadOnlySpan<int>)[offsetSize, lengthSize, baseSize, indexSize])
        {
            if (size is not (0 or 4 or 8))
            {
                throw Corrupt(box.Start, $"The iloc box has a field size of {size} bytes, not 0, 4 or 8.");
            }
        }

        uint count = version < 2 ? (uint)at.U16() : at.U32();
        for (uint i = 0; i < count; i++)
        {
            uint id = version < 2 ? (uint)at.U16() : at.U32();
            int method = version > 0 ? at.U16() & 15 : 0;
            int reference = at.U16();
            ulong baseOffset = at.Sized(baseSize);
            int extentCount = at.U16();
            HeifItem? item = Item(id);
            if (item is not null && item.Located)
            {
                throw Corrupt(box.Start, $"The iloc box locates item {id} twice.");
            }

            List<HeifExtent>? extents = item?.Extents;
            for (int e = 0; e < extentCount; e++)
            {
                ulong index = at.Sized(indexSize);
                ulong offset = at.Sized(offsetSize);
                ulong length = at.Sized(lengthSize);
                ulong start = baseOffset + offset;
                if (start < baseOffset || start > long.MaxValue || length > long.MaxValue)
                {
                    throw Corrupt(box.Start, $"An extent of item {id} lies beyond any file.");
                }

                extents?.Add(new HeifExtent((long)start, (long)length, index));
            }

            if (item is null)
            {
                UnlistedItemIds.Add(id);
                Problem("Heif.UnknownLocatedItem", box.Start, $"The iloc box locates item {id}, which has no iinf entry; it was ignored.");
                continue;
            }

            item.ConstructionMethod = method;
            item.DataReferenceIndex = reference;
            item.Located = true;
        }
    }

    private void ParseItemReferences(IsoBox box)
    {
        FullBox(box, out int version, out _);
        if (version > 1)
        {
            throw Corrupt(box.Start, $"The iref box's version is {version}, not 0 or 1.");
        }

        int idSize = version == 0 ? 2 : 4;
        IsoBoxReader references = new(Bytes.Span, box.ContentStart + 4, box.End, FormatName);
        while (references.Next(out IsoBox reference))
        {
            Cursor at = new(this, reference, reference.Content(Bytes.Span));
            uint from = at.Id(idSize);
            int count = at.U16();
            uint[] to = new uint[count];
            for (int i = 0; i < count; i++)
            {
                to[i] = at.Id(idSize);
            }

            if (Item(from) is not HeifItem item)
            {
                UnlistedItemIds.Add(from);
                Problem("Heif.ReferenceFromUnknownItem", reference.Start,
                    $"A \"{FourCC.ToString(reference.Type)}\" reference from item {from}, which has no iinf entry, was ignored.");
                continue;
            }

            item.References.Add(new HeifReference(reference.Type, to));
        }
    }

    private void ParseItemProperties(IsoBox box)
    {
        ReadOnlySpan<byte> bytes = Bytes.Span;
        IsoBoxReader children = new(bytes, box.ContentStart, box.End, FormatName);
        List<IsoBox> associations = [];
        bool sawContainer = false;
        while (children.Next(out IsoBox child))
        {
            if (child.Type == FourCC.Ipco)
            {
                if (sawContainer)
                {
                    throw Corrupt(child.Start, "The iprp box has two ipco boxes.");
                }

                sawContainer = true;
                IsoBoxReader properties = new(bytes, child.ContentStart, child.End, FormatName);
                while (properties.Next(out IsoBox property))
                {
                    Properties.Add(new HeifProperty(property.Type, property));
                }
            }
            else if (child.Type == FourCC.Ipma)
            {
                associations.Add(child);
            }
        }

        HashSet<uint> associated = [];
        foreach (IsoBox ipma in associations)
        {
            ReadOnlySpan<byte> content = FullBox(ipma, out int version, out int flags);
            Cursor at = new(this, ipma, content);
            uint count = at.U32();
            for (uint i = 0; i < count; i++)
            {
                uint id = version < 1 ? (uint)at.U16() : at.U32();
                int n = at.U8();
                HeifItem? item = Item(id);
                if (item is not null && !associated.Add(id))
                {
                    throw Corrupt(ipma.Start, $"Item {id} has two sets of property associations.");
                }

                for (int a = 0; a < n; a++)
                {
                    int value = (flags & 1) != 0 ? at.U16() : at.U8();
                    int bits = (flags & 1) != 0 ? 15 : 7;
                    int index = value & ((1 << bits) - 1);
                    bool essential = (value >> bits) != 0;
                    if (index == 0)
                    {
                        continue;
                    }

                    if (index > Properties.Count)
                    {
                        throw Corrupt(ipma.Start, $"Item {id} is associated with property {index}, but the ipco box holds {Properties.Count}.");
                    }

                    item?.Associations.Add(new HeifAssociation(index, essential));
                }

                if (item is null)
                {
                    UnlistedItemIds.Add(id);
                    Problem("Heif.PropertiesOfUnknownItem", ipma.Start, $"Properties of item {id}, which has no iinf entry, were ignored.");
                }
            }
        }
    }

    private void ParseGroups(IsoBox box)
    {
        IsoBoxReader groups = new(Bytes.Span, box.ContentStart, box.End, FormatName);
        while (groups.Next(out IsoBox group))
        {
            ReadOnlySpan<byte> content = FullBox(group, out _, out _);
            Cursor at = new(this, group, content);
            uint id = at.U32();
            uint count = at.U32();
            if (count > (uint)content.Length / 4)
            {
                throw Corrupt(group.Start, $"A \"{FourCC.ToString(group.Type)}\" group says {count} entities, more than it holds.");
            }

            uint[] entities = new uint[count];
            for (int i = 0; i < entities.Length; i++)
            {
                entities[i] = at.U32();
            }

            Groups.Add(new HeifEntityGroup(group.Type, id, entities));
        }
    }

    private ReadOnlySpan<byte> FullBox(IsoBox box, out int version, out int flags)
    {
        ReadOnlySpan<byte> content = box.Content(Bytes.Span);
        if (content.Length < 4)
        {
            throw Corrupt(box.Start, $"The \"{FourCC.ToString(box.Type)}\" box is too short for its version and flags.");
        }

        version = content[0];
        flags = (content[1] << 16) | (content[2] << 8) | content[3];
        return content[4..];
    }

    private static uint Read(ReadOnlySpan<byte> data, int size) =>
        size == 2 ? BinaryPrimitives.ReadUInt16BigEndian(data) : BinaryPrimitives.ReadUInt32BigEndian(data);

    /// <summary>Reads a null-terminated UTF-8 string; a missing terminator ends it at the end of the box.</summary>
    private static string String(ref ReadOnlySpan<byte> data)
    {
        int end = data.IndexOf((byte)0);
        string text = Encoding.UTF8.GetString(end < 0 ? data : data[..end]);
        data = end < 0 ? [] : data[(end + 1)..];
        return text;
    }

    private void Problem(string id, long offset, string message) => Problems.Add(new HeifProblem(id, message, offset));

    private CorruptDataException Corrupt(long offset, string problem) => new(FormatName, offset, problem);

    /// <summary>Reads big-endian fields from a box's contents, failing as corrupt when they run out.</summary>
    private ref struct Cursor(HeifFile file, IsoBox box, ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _at;

        internal int U8() => (int)Sized(1);

        internal int U16() => (int)Sized(2);

        internal uint U32() => (uint)Sized(4);

        internal uint Id(int size) => (uint)Sized(size);

        internal ulong Sized(int size)
        {
            if (_data.Length - _at < size)
            {
                throw new CorruptDataException(file.FormatName, box.Start, $"The \"{FourCC.ToString(box.Type)}\" box is cut off.") { IsTruncation = true };
            }

            ulong value = 0;
            for (int i = 0; i < size; i++)
            {
                value = (value << 8) | _data[_at + i];
            }

            _at += size;
            return value;
        }
    }
}
