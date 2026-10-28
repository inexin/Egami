namespace Egami.SystemDrawing.Imaging;

/// <summary>
/// One metadata tag of an image, as EXIF and TIFF store it: its id, type and raw bytes. <see cref="Image.PropertyItems"/> and
/// <see cref="Image.GetPropertyItem(int)"/> read them from the image's EXIF data, and <see cref="Image.SetPropertyItem(PropertyItem)"/>
/// writes one back. There is no public constructor, as in System.Drawing: change an item read from an image.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="frames"/>
/// </example>
public sealed class PropertyItem
{
    internal PropertyItem()
    {
    }

    /// <summary>Gets or sets the tag id, such as 0x010F for the camera maker.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the length of <see cref="Value"/> in bytes.</summary>
    public int Len { get; set; }

    /// <summary>Gets or sets the type of the value: 1 byte, 2 ASCII, 3 short, 4 long, 5 rational, 7 undefined, 9 signed long, 10 signed rational.</summary>
    public short Type { get; set; }

    /// <summary>Gets or sets the raw value, little-endian.</summary>
    public byte[]? Value { get; set; }

    internal PropertyItem Copy() => new() { Id = Id, Len = Len, Type = Type, Value = (byte[]?)Value?.Clone() };
}
