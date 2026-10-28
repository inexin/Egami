using System.ComponentModel;
using System.Runtime.InteropServices;

using Egami.Pixels;
using Egami.SystemDrawing.Imaging;
using Egami.SystemDrawing.Internal;

namespace Egami.SystemDrawing;

using Size = System.Drawing.Size;

/// <summary>
/// An image made of pixels, as System.Drawing's Bitmap: any of GDI+'s 14 pixel formats, with GetPixel, SetPixel, LockBits,
/// Clone, MakeTransparent and the codecs. Pixels are kept in GDI+'s own memory layout and converted exactly as GDI+ converts
/// them, so LockBits code and pixel values carry over.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="bitmap"/>
/// </example>
public sealed class Bitmap : Image
{
    private Lock? _lock;

    /// <summary>Initializes a new instance of the <see cref="Bitmap"/> class: transparent black, 32-bit ARGB.</summary>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <exception cref="ArgumentException">A size is zero or negative.</exception>
    public Bitmap(int width, int height)
        : this(width, height, PixelFormat.Format32bppArgb)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Bitmap"/> class in a pixel format, all zero bits.</summary>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="format">The pixel format.</param>
    /// <exception cref="ArgumentException">A size is zero or negative, or the format is not a bitmap format.</exception>
    public Bitmap(int width, int height, PixelFormat format)
    {
        Check(width, height, format);
        SetFrames([new Frame(width, height, format)], FrameDimension.Page.Guid);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Bitmap"/> class over pixels in the caller's memory, which the bitmap reads and
    /// writes in place and which must outlive it. A zero address gets the bitmap its own memory.
    /// </summary>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="stride">The bytes from one row to the next; negative for bottom-up rows.</param>
    /// <param name="format">The pixel format.</param>
    /// <param name="scan0">The address of the first row, or zero.</param>
    public Bitmap(int width, int height, int stride, PixelFormat format, IntPtr scan0)
    {
        Check(width, height, format);
        if (scan0 == IntPtr.Zero)
        {
            SetFrames([new Frame(width, height, format)], FrameDimension.Page.Guid);
            return;
        }

        if (Math.Abs((long)stride) < GdiPixels.RowBytes(width, format))
        {
            throw GdiErrors.InvalidParameter();
        }

        SetFrames([new Frame(width, height, format, stride, scan0)], FrameDimension.Page.Guid);
    }

    /// <summary>Initializes a new instance of the <see cref="Bitmap"/> class from an image file.</summary>
    /// <param name="filename">The file.</param>
    public Bitmap(string filename)
        : this(filename, false)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Bitmap"/> class from an image file.</summary>
    /// <param name="filename">The file.</param>
    /// <param name="useIcm">Whether to apply the file's color profile; Egami leaves colors as stored.</param>
    public Bitmap(string filename, bool useIcm)
        : this(Loaded(FromFile(filename, useIcm)))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Bitmap"/> class from image data in a stream.</summary>
    /// <param name="stream">The stream.</param>
    public Bitmap(Stream stream)
        : this(stream, false)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Bitmap"/> class from image data in a stream.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="useIcm">Whether to apply the file's color profile; Egami leaves colors as stored.</param>
    public Bitmap(Stream stream, bool useIcm)
        : this(Loaded(FromStream(stream, useIcm)))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Bitmap"/> class from an image embedded in an assembly.</summary>
    /// <param name="type">A type whose assembly and namespace hold the resource.</param>
    /// <param name="resource">The resource's name within the type's namespace.</param>
    public Bitmap(Type type, string resource)
        : this(Loaded(FromStream(Resource(type, resource))))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Bitmap"/> class drawn from another image, as 32-bit ARGB.</summary>
    /// <param name="original">The image.</param>
    public Bitmap(Image original)
        : this(Transforms.Resize((original ?? throw new ArgumentNullException(nameof(original))).Current, original.Width, original.Height, PixelFormat.Format32bppArgb))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Bitmap"/> class drawn from another image at a size, as 32-bit ARGB.</summary>
    /// <param name="original">The image.</param>
    /// <param name="newSize">The size.</param>
    public Bitmap(Image original, Size newSize)
        : this(original, newSize.Width, newSize.Height)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Bitmap"/> class drawn from another image at a size, as 32-bit ARGB.</summary>
    /// <param name="original">The image.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    public Bitmap(Image original, int width, int height)
        : this(Transforms.Resize((original ?? throw new ArgumentNullException(nameof(original))).Current, width, height, PixelFormat.Format32bppArgb))
    {
    }

    internal Bitmap(Frame frame)
    {
        SetFrames([frame], FrameDimension.Page.Guid);
    }

    private Bitmap(Bitmap loaded)
    {
        loaded.CopyStateTo(this);
        loaded.Dispose();
    }

    /// <summary>Not supported: Windows icon handles. Always throws.</summary>
    /// <param name="hicon">The handle.</param>
    /// <returns>Never returns.</returns>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    public static Bitmap FromHicon(IntPtr hicon) => throw GdiErrors.WindowsOnly("Bitmap.FromHicon", "Load the icon with new Icon(path) and call ToBitmap.");

    /// <summary>Not supported: Windows module resources. Always throws.</summary>
    /// <param name="hinstance">The module handle.</param>
    /// <param name="bitmapName">The resource.</param>
    /// <returns>Never returns.</returns>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    public static Bitmap FromResource(IntPtr hinstance, string bitmapName) => throw GdiErrors.WindowsOnly("Bitmap.FromResource", "Embed the image as a managed resource and use new Bitmap(type, resource).");

    /// <summary>Reads a pixel as a color, as GDI+ converts it from the pixel format.</summary>
    /// <param name="x">The column.</param>
    /// <param name="y">The row.</param>
    /// <returns>The color.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The pixel is outside the bitmap.</exception>
    /// <exception cref="ArgumentException">The format is 16-bit gray.</exception>
    public Color GetPixel(int x, int y)
    {
        Frame frame = Unlocked();
        CheckPixel(frame, x, y);
        if (frame.Format == PixelFormat.Format16bppGrayScale)
        {
            throw GdiErrors.InvalidParameter();
        }

        return Color.FromArgb((int)GdiPixels.Read(frame.Format, frame.Row(y), x, frame.Palette));
    }

    /// <summary>Writes a pixel, as GDI+ converts the color to the pixel format.</summary>
    /// <param name="x">The column.</param>
    /// <param name="y">The row.</param>
    /// <param name="color">The color.</param>
    /// <exception cref="InvalidOperationException">The format is indexed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The pixel is outside the bitmap.</exception>
    public void SetPixel(int x, int y, Color color)
    {
        Frame frame = Unlocked();
        if (GdiPixels.IsIndexed(frame.Format))
        {
            throw GdiErrors.IndexedSetPixel();
        }

        CheckPixel(frame, x, y);
        if (frame.Format == PixelFormat.Format16bppGrayScale)
        {
            throw GdiErrors.InvalidParameter();
        }

        GdiPixels.Write(frame.Format, frame.Row(y), x, (uint)color.ToArgb());
    }

    /// <summary>Sets the resolution.</summary>
    /// <param name="xDpi">The horizontal resolution in pixels per inch.</param>
    /// <param name="yDpi">The vertical resolution in pixels per inch.</param>
    /// <exception cref="ArgumentException">A resolution is zero or negative.</exception>
    public void SetResolution(float xDpi, float yDpi)
    {
        CheckAlive();
        if (!(xDpi > 0) || !(yDpi > 0))
        {
            throw GdiErrors.InvalidParameter();
        }

        SetDpi(xDpi, yDpi);
    }

    /// <summary>A copy of an area in a pixel format, converted as GDI+ converts.</summary>
    /// <param name="rect">The area.</param>
    /// <param name="format">The format.</param>
    /// <returns>The copy.</returns>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The area is not inside the bitmap.</exception>
    public Bitmap Clone(Rectangle rect, PixelFormat format)
    {
        Frame frame = Unlocked();
        if (rect.Width <= 0 || rect.Height <= 0 || rect.X < 0 || rect.Y < 0 || rect.Right > frame.Width || rect.Bottom > frame.Height || !GdiPixels.IsValid(format))
        {
            throw rect.Width <= 0 || rect.Height <= 0 ? GdiErrors.InvalidParameter() : GdiErrors.Generic();
        }

        if (format == PixelFormat.Format16bppGrayScale && frame.Format != format)
        {
            throw GdiErrors.Generic();
        }

        Bitmap copy = new(frame.Convert(rect, format));
        copy.SetDpi(DpiX, DpiY);
        return copy;
    }

    /// <summary>A copy of an area in a pixel format; the area's position and size are rounded to whole pixels.</summary>
    /// <param name="rect">The area.</param>
    /// <param name="format">The format.</param>
    /// <returns>The copy.</returns>
    public Bitmap Clone(RectangleF rect, PixelFormat format) =>
        Clone(new Rectangle((int)MathF.Round(rect.X), (int)MathF.Round(rect.Y), (int)MathF.Round(rect.Width), (int)MathF.Round(rect.Height)), format);

    /// <summary>A copy with every frame, the palette, resolution and properties.</summary>
    /// <returns>The copy.</returns>
    public override object Clone()
    {
        Unlocked();
        Bitmap copy = new(1, 1);
        CopyStateTo(copy);
        return copy;
    }

    /// <summary>
    /// Locks an area of the pixels in a format for direct access: in the bitmap's own format the pixels are the bitmap's own
    /// memory; in another format they are a converted copy, written back on unlock.
    /// </summary>
    /// <param name="rect">The area.</param>
    /// <param name="flags">Read, write or both.</param>
    /// <param name="format">The format to see the pixels in.</param>
    /// <returns>Where the pixels are.</returns>
    public BitmapData LockBits(Rectangle rect, ImageLockMode flags, PixelFormat format) => LockBits(rect, flags, format, new BitmapData());

    /// <summary>
    /// Locks an area of the pixels in a format, into <paramref name="bitmapData"/>; with <see cref="ImageLockMode.UserInputBuffer"/>
    /// the pixels go to the buffer it points to.
    /// </summary>
    /// <param name="rect">The area.</param>
    /// <param name="flags">Read, write or both, and the user buffer flag.</param>
    /// <param name="format">The format to see the pixels in.</param>
    /// <param name="bitmapData">The data to fill in, and with the user buffer flag, the buffer.</param>
    /// <returns><paramref name="bitmapData"/>.</returns>
    /// <exception cref="ArgumentException">The area is empty or outside the bitmap, or the format is not a bitmap format.</exception>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The bitmap is already locked.</exception>
    public BitmapData LockBits(Rectangle rect, ImageLockMode flags, PixelFormat format, BitmapData bitmapData)
    {
        ArgumentNullException.ThrowIfNull(bitmapData);
        Frame frame = Unlocked();
        if (rect.Width <= 0 || rect.Height <= 0 || rect.X < 0 || rect.Y < 0 || rect.Right > frame.Width || rect.Bottom > frame.Height || !GdiPixels.IsValid(format))
        {
            throw GdiErrors.InvalidParameter();
        }

        bool user = (flags & ImageLockMode.UserInputBuffer) != 0;
        int bits = GdiPixels.Bits(format);
        Lock state = new(frame, rect, flags, format);
        if (format == frame.Format && !user && (rect.X * bits) % 8 == 0)
        {
            bitmapData.Scan0 = frame.Scan0 + (rect.Y * frame.Stride) + (rect.X * bits / 8);
            bitmapData.Stride = frame.Stride;
        }
        else
        {
            if (frame.Format == PixelFormat.Format16bppGrayScale || format == PixelFormat.Format16bppGrayScale)
            {
                throw GdiErrors.Generic();
            }

            if (user)
            {
                if (bitmapData.Scan0 == IntPtr.Zero)
                {
                    throw GdiErrors.InvalidParameter();
                }

                state.Buffer = new Frame(rect.Width, rect.Height, format, bitmapData.Stride, bitmapData.Scan0);
            }
            else
            {
                state.Buffer = new Frame(rect.Width, rect.Height, format);
                bitmapData.Scan0 = state.Buffer.Scan0;
                bitmapData.Stride = state.Buffer.Stride;
            }

            if ((flags & ImageLockMode.ReadOnly) != 0 || (flags & (ImageLockMode.ReadOnly | ImageLockMode.WriteOnly)) == 0)
            {
                Frame converted = frame.Convert(rect, format, lockPalette: true);
                for (int y = 0; y < rect.Height; y++)
                {
                    converted.Row(y).CopyTo(state.Buffer.Row(y));
                }
            }
        }

        bitmapData.Width = rect.Width;
        bitmapData.Height = rect.Height;
        bitmapData.PixelFormat = format;
        bitmapData.Lock = state;
        _lock = state;
        return bitmapData;
    }

    /// <summary>Unlocks pixels <see cref="LockBits(Rectangle, ImageLockMode, PixelFormat)"/> locked, writing a converted copy back when the lock writes.</summary>
    /// <param name="bitmapdata">What LockBits returned.</param>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The data is not this bitmap's current lock.</exception>
    public void UnlockBits(BitmapData bitmapdata)
    {
        ArgumentNullException.ThrowIfNull(bitmapdata);
        CheckAlive();
        if (_lock is null || !ReferenceEquals(bitmapdata.Lock, _lock))
        {
            throw GdiErrors.Generic();
        }

        Lock state = _lock;
        _lock = null;
        bitmapdata.Lock = null;
        if (state.Buffer is null || (state.Mode & ImageLockMode.WriteOnly) == 0)
        {
            return;
        }

        // The locked rows go back into the bitmap's format and palette.
        Frame target = state.Frame;
        Frame buffer = state.Buffer;
        byte[]? table = GdiPixels.IsIndexed(target.Format) ? GdiPalettes.InverseTable(target.Palette) : null;
        byte[] converted = new byte[GdiPixels.RowBytes(state.Area.Width, target.Format)];
        int bits = GdiPixels.Bits(target.Format);
        for (int y = 0; y < state.Area.Height; y++)
        {
            GdiPixels.ConvertRow(buffer.Format, buffer.Row(y), buffer.Palette, target.Format, converted, table, state.Area.Width);
            Span<byte> row = target.Row(state.Area.Y + y);
            if ((state.Area.X * bits) % 8 == 0)
            {
                converted.CopyTo(row[(state.Area.X * bits / 8)..]);
            }
            else
            {
                for (int x = 0; x < state.Area.Width; x++)
                {
                    GdiPixels.SetIndex(target.Format, row, state.Area.X + x, GdiPixels.Index(target.Format, converted, x));
                }
            }
        }
    }

    /// <summary>Makes the color of the bottom-left pixel transparent; the bitmap becomes 32-bit ARGB.</summary>
    public void MakeTransparent()
    {
        Frame frame = Unlocked();
        MakeTransparent(Color.FromArgb((int)GdiPixels.Read(frame.Format, frame.Row(frame.Height - 1), 0, frame.Palette)));
    }

    /// <summary>Makes a color transparent; the bitmap becomes 32-bit ARGB, drawn as GDI+ draws it, through premultiplied color.</summary>
    /// <param name="transparentColor">The color.</param>
    public void MakeTransparent(Color transparentColor)
    {
        Frame frame = Unlocked();
        ReplaceCurrent(Transforms.ColorKey(frame, (uint)transparentColor.ToArgb()));
    }

    /// <summary>Converts the bitmap to another pixel format in place.</summary>
    /// <param name="format">The format.</param>
    public void ConvertFormat(PixelFormat format)
    {
        Frame frame = Unlocked();
        if (!GdiPixels.IsValid(format))
        {
            throw GdiErrors.InvalidParameter();
        }

        ReplaceCurrent(frame.Convert(new Rectangle(0, 0, frame.Width, frame.Height), format));
    }

    /// <summary>Converts the bitmap to another pixel format in place, to a palette for an indexed format.</summary>
    /// <param name="format">The format.</param>
    /// <param name="ditherType">The dithering; Egami maps each pixel to the nearest color for every type.</param>
    /// <param name="paletteType">The fixed palette when <paramref name="palette"/> is null.</param>
    /// <param name="palette">The palette for an indexed format, or null.</param>
    /// <param name="alphaThresholdPercent">The alpha, 0 to 100 percent, below which a pixel takes a transparent palette entry.</param>
    public void ConvertFormat(PixelFormat format, DitherType ditherType, PaletteType paletteType = PaletteType.Custom, ColorPalette? palette = null, float alphaThresholdPercent = 0)
    {
        Frame frame = Unlocked();
        if (!GdiPixels.IsValid(format))
        {
            throw GdiErrors.InvalidParameter();
        }

        Frame converted = new(frame.Width, frame.Height, format);
        if (GdiPixels.IsIndexed(format))
        {
            converted.SetPalette(palette ?? (paletteType == PaletteType.Custom ? GdiPalettes.Default(format) : new ColorPalette(paletteType)));
        }

        int threshold = (int)Math.Round(Math.Clamp(alphaThresholdPercent, 0, 100) * 255 / 100);
        int transparent = Array.FindIndex(converted.Palette, c => c >> 24 == 0);
        byte[]? key = GdiPixels.IsIndexed(format) ? GdiPalettes.InverseTable(converted.Palette) : null;
        for (int y = 0; y < frame.Height; y++)
        {
            Span<byte> row = converted.Row(y);
            for (int x = 0; x < frame.Width; x++)
            {
                uint argb = GdiPixels.Read(frame.Format, frame.Row(y), x, frame.Palette);
                if (GdiPixels.IsIndexed(format))
                {
                    int index = transparent >= 0 && (argb >> 24) < threshold ? transparent : GdiPalettes.Lookup(key!, argb);
                    GdiPixels.SetIndex(format, row, x, index);
                }
                else
                {
                    GdiPixels.Write(format, row, x, argb);
                }
            }
        }

        ReplaceCurrent(converted);
    }

    /// <summary>Not supported: Windows bitmap handles. Always throws.</summary>
    /// <returns>Never returns.</returns>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public IntPtr GetHbitmap() => throw GdiErrors.WindowsOnly("Bitmap.GetHbitmap", "Use LockBits to reach the pixels.");

    /// <summary>Not supported: Windows bitmap handles. Always throws.</summary>
    /// <param name="background">The color under transparent pixels.</param>
    /// <returns>Never returns.</returns>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public IntPtr GetHbitmap(Color background) => throw GdiErrors.WindowsOnly("Bitmap.GetHbitmap", "Use LockBits to reach the pixels.");

    /// <summary>Not supported: Windows icon handles. Always throws.</summary>
    /// <returns>Never returns.</returns>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public IntPtr GetHicon() => throw GdiErrors.WindowsOnly("Bitmap.GetHicon", "Save the bitmap as an icon with Icon.Save, or as PNG.");

    /// <summary>The current frame as 8-bit RGBA, converted as GetPixel converts.</summary>
    internal Raster<Rgba8> ToRgba8()
    {
        Frame frame = Current;
        Frame argb = frame.Format == PixelFormat.Format16bppGrayScale ? Transforms.Gray16ToArgb(frame) : frame.Convert(new Rectangle(0, 0, frame.Width, frame.Height), PixelFormat.Format32bppArgb);
        Raster<Rgba8> raster = Raster.Create<Rgba8>(frame.Width, frame.Height);
        for (int y = 0; y < frame.Height; y++)
        {
            PixelConvert.Convert<Bgra8, Rgba8>(MemoryMarshal.Cast<byte, Bgra8>(argb.Row(y)), raster.Row(y));
        }

        return raster;
    }

    private static void Check(int width, int height, PixelFormat format)
    {
        if (width <= 0 || height <= 0 || !GdiPixels.IsValid(format))
        {
            throw GdiErrors.InvalidParameter();
        }
    }

    private static void CheckPixel(Frame frame, int x, int y)
    {
        if ((uint)x >= (uint)frame.Width)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Parameter must be positive and < Width.");
        }

        if ((uint)y >= (uint)frame.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(y), "Parameter must be positive and < Height.");
        }
    }

    private static Bitmap Loaded(Image image) => image as Bitmap ?? throw GdiErrors.InvalidParameter();

    private static Stream Resource(Type type, string resource)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(resource);
        return type.Assembly.GetManifestResourceStream(type, resource) ?? throw new ArgumentException($"Resource '{resource}' cannot be found in class '{type.FullName}'.");
    }

    /// <summary>The current frame, after checking the bitmap is alive and not locked.</summary>
    private Frame Unlocked()
    {
        Frame frame = Current;
        if (_lock is not null)
        {
            throw GdiErrors.Generic();
        }

        return frame;
    }

    /// <summary>One LockBits: the area, the mode, the format, and the converted copy when the format differs.</summary>
    private sealed class Lock(Frame frame, Rectangle area, ImageLockMode mode, PixelFormat format)
    {
        public Frame Frame { get; } = frame;

        public Rectangle Area { get; } = area;

        public ImageLockMode Mode { get; } = mode;

        public PixelFormat Format { get; } = format;

        public Frame? Buffer { get; set; }
    }
}
