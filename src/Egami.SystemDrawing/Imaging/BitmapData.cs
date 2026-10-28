namespace Egami.SystemDrawing.Imaging;

/// <summary>
/// A locked area of a bitmap's pixels, from <see cref="Bitmap.LockBits(System.Drawing.Rectangle, ImageLockMode, PixelFormat)"/>:
/// where the first row starts, the bytes from one row to the next, and the size and format of the pixels.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="lockbits"/>
/// </example>
public sealed class BitmapData
{
    /// <summary>Gets or sets the width of the locked area, in pixels.</summary>
    public int Width { get; set; }

    /// <summary>Gets or sets the height of the locked area, in pixels.</summary>
    public int Height { get; set; }

    /// <summary>Gets or sets the bytes from the start of one row to the next; a multiple of 4.</summary>
    public int Stride { get; set; }

    /// <summary>Gets or sets the format of the locked pixels.</summary>
    public PixelFormat PixelFormat { get; set; }

    /// <summary>Gets or sets the address of the first locked pixel. The memory is pinned until the bitmap is unlocked.</summary>
    public IntPtr Scan0 { get; set; }

    /// <summary>Gets or sets a value kept for System.Drawing compatibility; Egami does not use it.</summary>
    public int Reserved { get; set; }

    /// <summary>Gets or sets the lock this data belongs to, so the bitmap can find it again on unlock.</summary>
    internal object? Lock { get; set; }
}
