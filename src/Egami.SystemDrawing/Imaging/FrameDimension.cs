namespace Egami.SystemDrawing.Imaging;

/// <summary>How an image's frames are counted: as pages (TIFF), as moments in time (an animated GIF) or as resolutions.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="frames"/>
/// </example>
public sealed class FrameDimension
{
    private static readonly FrameDimension TimeDimension = new(new Guid("{6aedbd6d-3fb5-418a-83a6-7f45229dc872}"));
    private static readonly FrameDimension ResolutionDimension = new(new Guid("{84236f7b-3bd3-428f-8dab-4ea1439ca315}"));
    private static readonly FrameDimension PageDimension = new(new Guid("{7462dc86-6180-4c7e-8e3f-ee7333a7a483}"));

    /// <summary>Initializes a new instance of the <see cref="FrameDimension"/> class from its GUID.</summary>
    /// <param name="guid">The GUID.</param>
    public FrameDimension(Guid guid)
    {
        Guid = guid;
    }

    /// <summary>Gets the GUID.</summary>
    public Guid Guid { get; }

    /// <summary>Gets the dimension of an animation's frames.</summary>
    public static FrameDimension Time => TimeDimension;

    /// <summary>Gets the dimension of an image's resolutions.</summary>
    public static FrameDimension Resolution => ResolutionDimension;

    /// <summary>Gets the dimension of a document's pages.</summary>
    public static FrameDimension Page => PageDimension;

    /// <inheritdoc/>
    public override bool Equals(object? o) => o is FrameDimension other && other.Guid == Guid;

    /// <inheritdoc/>
    public override int GetHashCode() => Guid.GetHashCode();

    /// <summary>Returns the dimension's name: Time, Resolution or Page, or its GUID in brackets.</summary>
    /// <returns>The name.</returns>
    public override string ToString() =>
        Equals(Time) ? "Time" : Equals(Resolution) ? "Resolution" : Equals(Page) ? "Page" : "[FrameDimension: " + Guid + "]";
}
