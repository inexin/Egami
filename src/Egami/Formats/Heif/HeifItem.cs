using Egami.Formats.Iso;

namespace Egami.Formats.Heif;

/// <summary>One extent of an item's data, from the iloc box.</summary>
/// <param name="Offset">Where it starts: in the file (construction method 0) or in the idat box (method 1), base offset added.</param>
/// <param name="Length">Its length; 0 means to the end of the file or idat box.</param>
/// <param name="Index">The extent index of construction method 2, or 0.</param>
internal readonly record struct HeifExtent(long Offset, long Length, ulong Index);

/// <summary>A property associated with an item in an ipma box.</summary>
/// <param name="Index">The property's index in the ipco box, from 1.</param>
/// <param name="Essential">Whether a reader that does not understand the property must not use the item.</param>
internal readonly record struct HeifAssociation(int Index, bool Essential);

/// <summary>A property box in the ipco box.</summary>
/// <param name="Type">Its four-character type.</param>
/// <param name="Box">Where it lies in <see cref="HeifFile.Bytes"/>.</param>
internal sealed record HeifProperty(uint Type, IsoBox Box);

/// <summary>An item reference of an iref box: from one item to others, in order.</summary>
/// <param name="Type">The reference type, such as dimg, auxl, thmb, cdsc or prem.</param>
/// <param name="To">The items referred to, in order.</param>
internal sealed record HeifReference(uint Type, uint[] To);

/// <summary>An entity group of the grpl box, such as altr.</summary>
/// <param name="Type">The grouping type.</param>
/// <param name="Id">The group's id.</param>
/// <param name="Entities">The ids of the items (or tracks) in the group, in order.</param>
internal sealed record HeifEntityGroup(uint Type, uint Id, uint[] Entities);

/// <summary>A problem a lenient read recovered from, such as an item entry it skipped.</summary>
internal readonly record struct HeifProblem(string Id, string Message, long Offset);

/// <summary>
/// An item of the HEIF item model (ISO/IEC 23008-12): its entry in iinf, its location in iloc, its properties in ipma and its
/// references in iref. Codec-neutral: an AV1 (av01), HEVC (hvc1), JPEG, grid, overlay, tmap, Exif or mime item alike.
/// </summary>
internal sealed class HeifItem
{
    internal HeifItem(uint id, uint type)
    {
        Id = id;
        Type = type;
    }

    /// <summary>Gets the item's id.</summary>
    internal uint Id { get; }

    /// <summary>Gets the item type, such as av01, hvc1, grid, iovl, tmap, Exif or mime.</summary>
    internal uint Type { get; }

    /// <summary>Gets the item's name, often empty.</summary>
    internal string Name { get; init; } = "";

    /// <summary>Gets a mime item's content type, such as application/rdf+xml for XMP.</summary>
    internal string? ContentType { get; init; }

    /// <summary>Gets a mime item's content encoding, such as deflate, or <see langword="null"/>.</summary>
    internal string? ContentEncoding { get; init; }

    /// <summary>Gets a uri item's URI type.</summary>
    internal string? UriType { get; init; }

    /// <summary>Gets whether the item is hidden (infe flags bit 0): not meant to be shown on its own.</summary>
    internal bool Hidden { get; init; }

    /// <summary>Gets the item's protection scheme index; 0 when unprotected.</summary>
    internal int ProtectionIndex { get; init; }

    /// <summary>Gets the iloc construction method: 0 file offsets, 1 idat offsets, 2 offsets into other items.</summary>
    internal int ConstructionMethod { get; set; }

    /// <summary>Gets the iloc data reference index; 0 means this file.</summary>
    internal int DataReferenceIndex { get; set; }

    /// <summary>Gets whether the iloc box located the item.</summary>
    internal bool Located { get; set; }

    /// <summary>Gets the item's extents, in order; their data joined is the item's data.</summary>
    internal List<HeifExtent> Extents { get; } = [];

    /// <summary>Gets the item's property associations, in ipma order.</summary>
    internal List<HeifAssociation> Associations { get; } = [];

    /// <summary>Gets the item's references to other items, in iref order.</summary>
    internal List<HeifReference> References { get; } = [];

    /// <summary>
    /// Gets the type of the first essential property the reader does not understand, or 0. Such an item must not be used
    /// (ISO/IEC 23008-12 section 9.3); see <see cref="HeifFile.MarkUnsupportedEssential"/>.
    /// </summary>
    internal uint UnsupportedEssential { get; set; }

    /// <summary>Gets the items this one refers to with a reference of the type, in order, or an empty array.</summary>
    internal uint[] ReferencesOf(uint type)
    {
        foreach (HeifReference reference in References)
        {
            if (reference.Type == type)
            {
                return reference.To;
            }
        }

        return [];
    }

    /// <inheritdoc/>
    public override string ToString() => $"item {Id} ({IsoTypes.ToString(Type)})";
}
