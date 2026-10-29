namespace Egami.SystemDrawing.Imaging;

/// <summary>A copy of a bitmap ready to draw at device pixels with <see cref="Graphics.DrawCachedBitmap(CachedBitmap, int, int)"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="extras"/>
/// </example>
public sealed class CachedBitmap : IDisposable
{
    /// <summary>Initializes a new instance of the <see cref="CachedBitmap"/> class for drawing on a graphics.</summary>
    /// <param name="bitmap">The bitmap, copied.</param>
    /// <param name="graphics">The graphics it is for.</param>
    public CachedBitmap(Bitmap bitmap, Graphics graphics)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(graphics);
        Bitmap = (Bitmap)bitmap.Clone();
    }

    /// <summary>Gets the copy.</summary>
    internal Bitmap Bitmap { get; }

    /// <summary>Releases the copy.</summary>
    public void Dispose() => Bitmap.Dispose();
}
