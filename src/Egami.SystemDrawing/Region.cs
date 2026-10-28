using System.Buffers.Binary;
using System.ComponentModel;
using System.Numerics;

using Egami.Drawing;
using Egami.GdiPlus;
using Egami.SystemDrawing.Drawing2D;
using Egami.SystemDrawing.Internal;

namespace Egami.SystemDrawing;

/// <summary>
/// An area made of rectangles and shapes combined with <see cref="Union(Region)"/>, <see cref="Intersect(Region)"/>,
/// <see cref="Xor(Region)"/>, <see cref="Exclude(Region)"/> and <see cref="Complement(Region)"/>, as System.Drawing's Region:
/// infinite when made with no arguments. Graphics clips to one.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="clipping"/>
/// </example>
public sealed class Region : MarshalByRefObject, IDisposable
{
    /// <summary>The bounds GDI+ reports for the infinite region.</summary>
    private static readonly RectangleF InfiniteBounds = new(-4194304, -4194304, 8388608, 8388608);

    /// <summary>Initializes a new instance of the <see cref="Region"/> class: the infinite region.</summary>
    public Region()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Region"/> class: a rectangle.</summary>
    /// <param name="rect">The rectangle.</param>
    public Region(RectangleF rect)
    {
        Shape = Rect(rect);
    }

    /// <summary>Initializes a new instance of the <see cref="Region"/> class: a rectangle.</summary>
    /// <param name="rect">The rectangle.</param>
    public Region(Rectangle rect)
        : this((RectangleF)rect)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Region"/> class from the data <see cref="GetRegionData"/> returned.</summary>
    /// <param name="rgnData">The data, in the EMF+ region format.</param>
    /// <exception cref="ArgumentException">The data is not a region.</exception>
    public Region(RegionData rgnData)
    {
        ArgumentNullException.ThrowIfNull(rgnData);
        byte[] data = rgnData.Data ?? throw GdiErrors.InvalidParameter();
        try
        {
            // Skip the size and checksum GDI+ puts before the EMF+ region record.
            int at = data.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)) == RegionData.Version ? 8 : 0;
            Shape = GdiPlusData.Region(data, ref at);
        }
        catch (Exception e) when (e is ArgumentException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            throw GdiErrors.InvalidParameter();
        }
    }

    internal Region(Path? shape)
    {
        Shape = shape;
    }

    /// <summary>Gets the region as a path in its own coordinates, or null when infinite.</summary>
    internal Path? Shape { get; private set; }

    /// <summary>Not supported: Windows region handles. Always throws.</summary>
    /// <param name="hrgn">The handle.</param>
    /// <returns>Never returns.</returns>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    public static Region FromHrgn(IntPtr hrgn) => throw GdiErrors.WindowsOnly("Region.FromHrgn", "Build the region from rectangles or a GraphicsPath.");

    /// <summary>A copy.</summary>
    /// <returns>The copy.</returns>
    public Region Clone() => new(Shape);

    /// <summary>Does nothing; the region holds no unmanaged memory.</summary>
    public void Dispose()
    {
    }

    /// <summary>Makes the region empty.</summary>
    public void MakeEmpty() => Shape = Path.Empty;

    /// <summary>Makes the region infinite.</summary>
    public void MakeInfinite() => Shape = null;

    /// <summary>Intersects with a rectangle.</summary>
    /// <param name="rect">The rectangle.</param>
    public void Intersect(RectangleF rect) => Combine(Rect(rect), GdiPlusRegions.Intersect);

    /// <summary>Intersects with a rectangle.</summary>
    /// <param name="rect">The rectangle.</param>
    public void Intersect(Rectangle rect) => Intersect((RectangleF)rect);

    /// <summary>Intersects with another region.</summary>
    /// <param name="region">The region.</param>
    public void Intersect(Region region) => Combine(Of(region), GdiPlusRegions.Intersect);

    /// <summary>Adds a rectangle.</summary>
    /// <param name="rect">The rectangle.</param>
    public void Union(RectangleF rect) => Combine(Rect(rect), GdiPlusRegions.Union);

    /// <summary>Adds a rectangle.</summary>
    /// <param name="rect">The rectangle.</param>
    public void Union(Rectangle rect) => Union((RectangleF)rect);

    /// <summary>Adds another region.</summary>
    /// <param name="region">The region.</param>
    public void Union(Region region) => Combine(Of(region), GdiPlusRegions.Union);

    /// <summary>Keeps what is in exactly one of the region and a rectangle.</summary>
    /// <param name="rect">The rectangle.</param>
    public void Xor(RectangleF rect) => Combine(Rect(rect), GdiPlusRegions.Xor);

    /// <summary>Keeps what is in exactly one of the region and a rectangle.</summary>
    /// <param name="rect">The rectangle.</param>
    public void Xor(Rectangle rect) => Xor((RectangleF)rect);

    /// <summary>Keeps what is in exactly one of the two regions.</summary>
    /// <param name="region">The region.</param>
    public void Xor(Region region) => Combine(Of(region), GdiPlusRegions.Xor);

    /// <summary>Removes a rectangle.</summary>
    /// <param name="rect">The rectangle.</param>
    public void Exclude(RectangleF rect) => Combine(Rect(rect), GdiPlusRegions.Exclude);

    /// <summary>Removes a rectangle.</summary>
    /// <param name="rect">The rectangle.</param>
    public void Exclude(Rectangle rect) => Exclude((RectangleF)rect);

    /// <summary>Removes another region.</summary>
    /// <param name="region">The region.</param>
    public void Exclude(Region region) => Combine(Of(region), GdiPlusRegions.Exclude);

    /// <summary>Becomes the part of a rectangle outside the region.</summary>
    /// <param name="rect">The rectangle.</param>
    public void Complement(RectangleF rect) => Combine(Rect(rect), GdiPlusRegions.Complement);

    /// <summary>Becomes the part of a rectangle outside the region.</summary>
    /// <param name="rect">The rectangle.</param>
    public void Complement(Rectangle rect) => Complement((RectangleF)rect);

    /// <summary>Becomes the part of another region outside this one.</summary>
    /// <param name="region">The region.</param>
    public void Complement(Region region) => Combine(Of(region), GdiPlusRegions.Complement);

    /// <summary>Moves the region.</summary>
    /// <param name="dx">The horizontal distance.</param>
    /// <param name="dy">The vertical distance.</param>
    public void Translate(float dx, float dy) => Shape = Shape?.Transform(Matrix3x2.CreateTranslation(dx, dy));

    /// <summary>Moves the region.</summary>
    /// <param name="dx">The horizontal distance.</param>
    /// <param name="dy">The vertical distance.</param>
    public void Translate(int dx, int dy) => Translate((float)dx, dy);

    /// <summary>Transforms the region.</summary>
    /// <param name="matrix">The transform.</param>
    public void Transform(Matrix matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        Shape = Shape?.Transform(matrix.MatrixElements);
    }

    /// <summary>Gets the bounds; the infinite region's are GDI+'s, about 4 million each way.</summary>
    /// <param name="g">The graphics whose units the bounds are in; the region's own coordinates are used.</param>
    /// <returns>The bounds.</returns>
    public RectangleF GetBounds(Graphics g)
    {
        ArgumentNullException.ThrowIfNull(g);
        if (Shape is null)
        {
            return InfiniteBounds;
        }

        if (Shape.IsEmpty)
        {
            return RectangleF.Empty;
        }

        // A single rectangle keeps its own edges; anything else is GDI+'s pixels.
        if (RegionData.IsRect(Shape, out RectF r))
        {
            return new RectangleF(r.X, r.Y, r.Width, r.Height);
        }

        List<RectangleF> scans = Scans(Matrix3x2.Identity);
        return scans.Count == 0 ? RectangleF.Empty : scans.Aggregate(RectangleF.Union);
    }

    /// <summary>Gets whether the region is empty.</summary>
    /// <param name="g">A graphics, which GDI+ uses for precision.</param>
    /// <returns><see langword="true"/> when empty.</returns>
    public bool IsEmpty(Graphics g)
    {
        ArgumentNullException.ThrowIfNull(g);
        return Shape is not null && (Shape.IsEmpty || Shape.Bounds.Width <= 0 || Shape.Bounds.Height <= 0 || Scans(Matrix3x2.Identity).Count == 0);
    }

    /// <summary>Gets whether the region is infinite.</summary>
    /// <param name="g">A graphics, which GDI+ uses for precision.</param>
    /// <returns><see langword="true"/> when infinite.</returns>
    public bool IsInfinite(Graphics g)
    {
        ArgumentNullException.ThrowIfNull(g);
        return Shape is null;
    }

    /// <summary>Gets whether two regions cover the same pixels.</summary>
    /// <param name="region">The other region.</param>
    /// <param name="g">A graphics, which GDI+ uses for precision.</param>
    /// <returns><see langword="true"/> when equal.</returns>
    public bool Equals(Region region, Graphics g)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(g);
        if (Shape is null || region.Shape is null)
        {
            return Shape is null && region.Shape is null;
        }

        List<RectangleF> a = Scans(Matrix3x2.Identity), b = region.Scans(Matrix3x2.Identity);
        return a.SequenceEqual(b);
    }

    /// <summary>Gets whether a point is in the region.</summary>
    /// <param name="x">The horizontal coordinate.</param>
    /// <param name="y">The vertical coordinate.</param>
    /// <returns><see langword="true"/> when inside.</returns>
    public bool IsVisible(float x, float y) => Contains(x, y);

    /// <summary>Gets whether a point is in the region.</summary>
    /// <param name="point">The point.</param>
    /// <returns><see langword="true"/> when inside.</returns>
    public bool IsVisible(PointF point) => Contains(point.X, point.Y);

    /// <summary>Gets whether a point is in the region.</summary>
    /// <param name="x">The horizontal coordinate.</param>
    /// <param name="y">The vertical coordinate.</param>
    /// <param name="g">A graphics, or null.</param>
    /// <returns><see langword="true"/> when inside.</returns>
    public bool IsVisible(float x, float y, Graphics? g) => Contains(x, y);

    /// <summary>Gets whether a point is in the region.</summary>
    /// <param name="point">The point.</param>
    /// <param name="g">A graphics, or null.</param>
    /// <returns><see langword="true"/> when inside.</returns>
    public bool IsVisible(PointF point, Graphics? g) => Contains(point.X, point.Y);

    /// <summary>Gets whether any of a rectangle is in the region.</summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <returns><see langword="true"/> when they overlap.</returns>
    public bool IsVisible(float x, float y, float width, float height) => Overlaps(new RectangleF(x, y, width, height));

    /// <summary>Gets whether any of a rectangle is in the region.</summary>
    /// <param name="rect">The rectangle.</param>
    /// <returns><see langword="true"/> when they overlap.</returns>
    public bool IsVisible(RectangleF rect) => Overlaps(rect);

    /// <summary>Gets whether any of a rectangle is in the region.</summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="g">A graphics, or null.</param>
    /// <returns><see langword="true"/> when they overlap.</returns>
    public bool IsVisible(float x, float y, float width, float height, Graphics? g) => Overlaps(new RectangleF(x, y, width, height));

    /// <summary>Gets whether any of a rectangle is in the region.</summary>
    /// <param name="rect">The rectangle.</param>
    /// <param name="g">A graphics, or null.</param>
    /// <returns><see langword="true"/> when they overlap.</returns>
    public bool IsVisible(RectangleF rect, Graphics? g) => Overlaps(rect);

    /// <summary>Gets whether a point is in the region.</summary>
    /// <param name="x">The horizontal coordinate.</param>
    /// <param name="y">The vertical coordinate.</param>
    /// <param name="g">A graphics, or null.</param>
    /// <returns><see langword="true"/> when inside.</returns>
    public bool IsVisible(int x, int y, Graphics? g) => Contains(x, y);

    /// <summary>Gets whether a point is in the region.</summary>
    /// <param name="point">The point.</param>
    /// <returns><see langword="true"/> when inside.</returns>
    public bool IsVisible(Point point) => Contains(point.X, point.Y);

    /// <summary>Gets whether a point is in the region.</summary>
    /// <param name="point">The point.</param>
    /// <param name="g">A graphics, or null.</param>
    /// <returns><see langword="true"/> when inside.</returns>
    public bool IsVisible(Point point, Graphics? g) => Contains(point.X, point.Y);

    /// <summary>Gets whether any of a rectangle is in the region.</summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <returns><see langword="true"/> when they overlap.</returns>
    public bool IsVisible(int x, int y, int width, int height) => Overlaps(new RectangleF(x, y, width, height));

    /// <summary>Gets whether any of a rectangle is in the region.</summary>
    /// <param name="rect">The rectangle.</param>
    /// <returns><see langword="true"/> when they overlap.</returns>
    public bool IsVisible(Rectangle rect) => Overlaps(rect);

    /// <summary>Gets whether any of a rectangle is in the region.</summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="g">A graphics, or null.</param>
    /// <returns><see langword="true"/> when they overlap.</returns>
    public bool IsVisible(int x, int y, int width, int height, Graphics? g) => Overlaps(new RectangleF(x, y, width, height));

    /// <summary>Gets whether any of a rectangle is in the region.</summary>
    /// <param name="rect">The rectangle.</param>
    /// <param name="g">A graphics, or null.</param>
    /// <returns><see langword="true"/> when they overlap.</returns>
    public bool IsVisible(Rectangle rect, Graphics? g) => Overlaps(rect);

    /// <summary>The region as rectangles of whole pixels after a transform, one run of rows at a time.</summary>
    /// <param name="matrix">The transform.</param>
    /// <returns>The rectangles, top to bottom and left to right.</returns>
    public RectangleF[] GetRegionScans(Matrix matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        return Shape is null ? [InfiniteBounds] : [.. Scans(matrix.MatrixElements)];
    }

    /// <summary>The region in the EMF+ region format, which <see cref="Region(RegionData)"/> reads back.</summary>
    /// <returns>The data.</returns>
    public RegionData GetRegionData() => new(RegionData.Write(Shape));

    /// <summary>Not supported: Windows region handles. Always throws.</summary>
    /// <param name="g">The graphics.</param>
    /// <returns>Never returns.</returns>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public IntPtr GetHrgn(Graphics g) => throw GdiErrors.WindowsOnly("Region.GetHrgn", "Use GetRegionScans for the rectangles.");

    /// <summary>Not supported: Windows region handles. Always throws.</summary>
    /// <param name="regionHandle">The handle.</param>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    public void ReleaseHrgn(IntPtr regionHandle) => throw GdiErrors.WindowsOnly("Region.ReleaseHrgn", "Use GetRegionScans for the rectangles.");

    internal static Path Rect(RectangleF rect) =>
        rect.Width <= 0 || rect.Height <= 0 ? Path.Empty : Path.Rect(new RectF(rect.X, rect.Y, rect.Width, rect.Height));

    internal void Combine(Path? other, int mode) => Shape = GdiPlusRegions.Combine(Shape, other, mode);

    private static Path? Of(Region region)
    {
        ArgumentNullException.ThrowIfNull(region);
        return region.Shape;
    }

    private bool Contains(float x, float y) => Shape is null || Shape.WithFillRule(Shape.FillRule).Contains(new Vector2(x, y));

    private bool Overlaps(RectangleF rect)
    {
        if (Shape is null)
        {
            return true;
        }

        return !Shape.Intersect(Rect(rect)).IsEmpty;
    }

    /// <summary>The pixels whose centers the transformed region holds, as rectangles merged across rows that match.</summary>
    /// <summary>The pixels of the transformed region, as GDI+ rasterizes it (see <see cref="PixelScans"/>).</summary>
    private List<RectangleF> Scans(Matrix3x2 m)
    {
        Path shape = Shape!.Transform(m);
        if (PixelScans.Rectangles(shape) is List<RectangleF> rects)
        {
            return rects;
        }

        RectF b = shape.Bounds;
        return [new RectangleF(b.X, b.Y, b.Width, b.Height)];
    }
}
