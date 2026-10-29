using System.ComponentModel;

using Egami.SystemDrawing.Imaging;
using Egami.SystemDrawing.Internal;

namespace Egami.SystemDrawing;

using Size = System.Drawing.Size;

/// <summary>
/// The base of <see cref="Bitmap"/> and the metafile type: size, resolution, pixel format, palette, frames, metadata and saving,
/// as in System.Drawing. Load one with <see cref="FromFile(string)"/> or <see cref="FromStream(Stream)"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="codecs"/>
/// </example>
public abstract class Image : MarshalByRefObject, IDisposable, ICloneable
{
    private readonly List<PropertyItem> _properties = [];
    private List<Frame> _frames = [];
    private int _active;
    private bool _disposed;

    private protected Image()
    {
    }

    /// <summary>Called when <see cref="GetThumbnailImage(int, int, GetThumbnailImageAbort?, IntPtr)"/> may stop early; GDI+ never calls it.</summary>
    /// <returns><see langword="true"/> to stop.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="frames"/>
    /// </example>
    public delegate bool GetThumbnailImageAbort();

    /// <summary>Gets or sets an object the caller keeps with the image.</summary>
    public object? Tag { get; set; }

    /// <summary>Gets the width in pixels.</summary>
    public int Width => Peek.Width;

    /// <summary>Gets the height in pixels.</summary>
    public int Height => Peek.Height;

    /// <summary>Gets the size in pixels.</summary>
    public Size Size => new(Width, Height);

    /// <summary>Gets the size: in pixels for a bitmap, in hundredths of a millimeter for a metafile.</summary>
    public SizeF PhysicalDimension => this is Metafile metafile
        ? new(metafile.FrameBounds.Width / HorizontalResolution * 2540, metafile.FrameBounds.Height / VerticalResolution * 2540)
        : new(Width, Height);

    /// <summary>Gets the horizontal resolution in pixels per inch; 96 unless the file or <see cref="Bitmap.SetResolution(float, float)"/> says otherwise.</summary>
    public float HorizontalResolution
    {
        get
        {
            CheckAlive();
            return DpiX;
        }
    }

    /// <summary>Gets the vertical resolution in pixels per inch.</summary>
    public float VerticalResolution
    {
        get
        {
            CheckAlive();
            return DpiY;
        }
    }

    /// <summary>Gets the image's <see cref="ImageFlags"/> as an integer.</summary>
    public int Flags
    {
        get
        {
            CheckAlive();
            return FileFlags ?? (GdiPixels.HasAlpha(Current.Format) || GdiPixels.IsIndexed(Current.Format) ? (int)ImageFlags.HasAlpha : 0);
        }
    }

    /// <summary>Gets the pixel format.</summary>
    public PixelFormat PixelFormat => this is Metafile ? PixelFormat.Format32bppRgb : Current.Format;

    /// <summary>Gets the format of the file the image came from, or <see cref="ImageFormat.MemoryBmp"/> for one made in memory.</summary>
    public ImageFormat RawFormat
    {
        get
        {
            CheckAlive();
            return new ImageFormat(Raw);
        }
    }

    /// <summary>
    /// Gets or sets the palette. Getting gives a copy: change its entries, then set it to change the image. A non-indexed image
    /// has an empty palette.
    /// </summary>
    public ColorPalette Palette
    {
        get => Current.GetPalette();
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Current.SetPalette(value);
        }
    }

    /// <summary>Gets the GUIDs of the dimensions the image's frames are counted in: one, Page, Time or Resolution.</summary>
    public Guid[] FrameDimensionsList
    {
        get
        {
            CheckAlive();
            return [Dimension];
        }
    }

    /// <summary>Gets the ids of the image's property items.</summary>
    public int[] PropertyIdList
    {
        get
        {
            CheckAlive();
            return [.. _properties.Select(p => p.Id)];
        }
    }

    /// <summary>Gets copies of the image's property items: its EXIF tags, a GIF's frame delays and loop count, a JPEG's quantization tables.</summary>
    public PropertyItem[] PropertyItems
    {
        get
        {
            CheckAlive();
            return [.. _properties.Select(p => p.Copy())];
        }
    }

    /// <summary>Gets or sets the resolution in pixels per inch.</summary>
    private protected float DpiX { get; set; } = 96;

    /// <summary>Gets or sets the vertical resolution in pixels per inch.</summary>
    private protected float DpiY { get; set; } = 96;

    /// <summary>Gets or sets the flags a file gave, or null for an image made in memory.</summary>
    private protected int? FileFlags { get; set; }

    /// <summary>Gets or sets the GUID of the file format.</summary>
    private protected Guid Raw { get; set; } = ImageFormat.MemoryBmp.Guid;

    /// <summary>Gets or sets the GUID of the dimension the frames are counted in.</summary>
    private protected Guid Dimension { get; set; } = FrameDimension.Page.Guid;

    /// <summary>Gets the frame <see cref="SelectActiveFrame(FrameDimension, int)"/> chose.</summary>
    internal Frame Current
    {
        get
        {
            CheckAlive();
            return _frames[_active] = Realize(_frames[_active]);
        }
    }

    /// <summary>Gets the active frame without drawing a metafile's pixels, for its size.</summary>
    private Frame Peek
    {
        get
        {
            CheckAlive();
            return _frames[_active];
        }
    }

    /// <summary>Gets the frames.</summary>
    internal IReadOnlyList<Frame> Frames
    {
        get
        {
            for (int i = 0; i < _frames.Count; i++)
            {
                _frames[i] = Realize(_frames[i]);
            }

            return _frames;
        }
    }

    /// <summary>Gets the property items, for codecs.</summary>
    internal List<PropertyItem> Properties => _properties;

    /// <summary>Gets or sets a multi-frame save waiting for <see cref="SaveAdd(EncoderParameters)"/>.</summary>
    internal MultiFrameSave? PendingSave { get; set; }

    /// <summary>Loads an image from a file.</summary>
    /// <param name="filename">The file.</param>
    /// <returns>A <see cref="Bitmap"/>, or a metafile for WMF and EMF files.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="OutOfMemoryException">The file is not an image Egami can read, as GDI+ reports it.</exception>
    public static Image FromFile(string filename) => FromFile(filename, false);

    /// <summary>Loads an image from a file.</summary>
    /// <param name="filename">The file.</param>
    /// <param name="useEmbeddedColorManagement">Whether to apply the file's color profile; Egami leaves colors as stored, as GDI+ does by default.</param>
    /// <returns>A <see cref="Bitmap"/>, or a metafile for WMF and EMF files.</returns>
    public static Image FromFile(string filename, bool useEmbeddedColorManagement)
    {
        ArgumentNullException.ThrowIfNull(filename);
        string path = System.IO.Path.GetFullPath(filename);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(path);
        }

        byte[] data = File.ReadAllBytes(path);
        return Codecs.Load(data) ?? throw GdiErrors.OutOfMemory();
    }

    /// <summary>Loads an image from a stream, read to its end.</summary>
    /// <param name="stream">The stream.</param>
    /// <returns>A <see cref="Bitmap"/>, or a metafile for WMF and EMF data.</returns>
    /// <exception cref="ArgumentException">The data is not an image Egami can read, as GDI+ reports it.</exception>
    public static Image FromStream(Stream stream) => FromStream(stream, false, true);

    /// <summary>Loads an image from a stream, read to its end.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="useEmbeddedColorManagement">Whether to apply the file's color profile; Egami leaves colors as stored.</param>
    /// <returns>A <see cref="Bitmap"/>, or a metafile for WMF and EMF data.</returns>
    public static Image FromStream(Stream stream, bool useEmbeddedColorManagement) => FromStream(stream, useEmbeddedColorManagement, true);

    /// <summary>Loads an image from a stream, read to its end.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="useEmbeddedColorManagement">Whether to apply the file's color profile; Egami leaves colors as stored.</param>
    /// <param name="validateImageData">Whether to check the data; Egami always decodes it whole.</param>
    /// <returns>A <see cref="Bitmap"/>, or a metafile for WMF and EMF data.</returns>
    public static Image FromStream(Stream stream, bool useEmbeddedColorManagement, bool validateImageData)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return Codecs.Load(copy.ToArray()) ?? throw GdiErrors.InvalidParameter();
    }

    /// <summary>Not supported: Windows bitmap handles. Always throws.</summary>
    /// <param name="hbitmap">The handle.</param>
    /// <returns>Never returns.</returns>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public static Bitmap FromHbitmap(IntPtr hbitmap) => throw GdiErrors.WindowsOnly("Image.FromHbitmap", "Use Bitmap.LockBits to reach the pixels.");

    /// <summary>Not supported: Windows bitmap and palette handles. Always throws.</summary>
    /// <param name="hbitmap">The bitmap handle.</param>
    /// <param name="hpalette">The palette handle.</param>
    /// <returns>Never returns.</returns>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public static Bitmap FromHbitmap(IntPtr hbitmap, IntPtr hpalette) => throw GdiErrors.WindowsOnly("Image.FromHbitmap", "Use Bitmap.LockBits to reach the pixels.");

    /// <summary>Gets the bits a pixel of a format takes.</summary>
    /// <param name="pixfmt">The format.</param>
    /// <returns>The bits, 1 to 64.</returns>
    public static int GetPixelFormatSize(PixelFormat pixfmt) => GdiPixels.Bits(pixfmt);

    /// <summary>Gets whether a format holds alpha.</summary>
    /// <param name="pixfmt">The format.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    public static bool IsAlphaPixelFormat(PixelFormat pixfmt) => ((int)pixfmt & (int)PixelFormat.Alpha) != 0;

    /// <summary>Gets whether a format holds 16 bits a channel.</summary>
    /// <param name="pixfmt">The format.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    public static bool IsExtendedPixelFormat(PixelFormat pixfmt) => ((int)pixfmt & (int)PixelFormat.Extended) != 0;

    /// <summary>Gets whether a format is the default 32-bit ARGB.</summary>
    /// <param name="pixfmt">The format.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public static bool IsCanonicalPixelFormat(PixelFormat pixfmt) => ((int)pixfmt & (int)PixelFormat.Canonical) != 0;

    /// <summary>Gets the bounds in a unit; for a bitmap, its size in pixels.</summary>
    /// <param name="pageUnit">Set to the unit of the bounds.</param>
    /// <returns>The bounds.</returns>
    public RectangleF GetBounds(ref GraphicsUnit pageUnit)
    {
        pageUnit = GraphicsUnit.Pixel;
        return this is Metafile metafile ? metafile.FrameBounds : new RectangleF(0, 0, Width, Height);
    }

    /// <summary>Gets the number of frames in a dimension: pages, animation frames or resolutions.</summary>
    /// <param name="dimension">The dimension.</param>
    /// <returns>The number of frames.</returns>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The image has no frames in that dimension.</exception>
    public int GetFrameCount(FrameDimension dimension)
    {
        ArgumentNullException.ThrowIfNull(dimension);
        CheckAlive();
        return dimension.Guid == Dimension ? _frames.Count : throw GdiErrors.Generic();
    }

    /// <summary>Makes a frame the one the image shows, draws and saves.</summary>
    /// <param name="dimension">The dimension.</param>
    /// <param name="frameIndex">The frame, from 0.</param>
    /// <returns>0.</returns>
    public int SelectActiveFrame(FrameDimension dimension, int frameIndex)
    {
        ArgumentNullException.ThrowIfNull(dimension);
        CheckAlive();
        if (dimension.Guid != Dimension || frameIndex < 0 || frameIndex >= _frames.Count)
        {
            throw GdiErrors.InvalidParameter();
        }

        _active = frameIndex;
        return 0;
    }

    /// <summary>Gets a copy of a property item.</summary>
    /// <param name="propid">The id.</param>
    /// <returns>The item.</returns>
    /// <exception cref="ArgumentException">The image has no item with that id.</exception>
    public PropertyItem? GetPropertyItem(int propid)
    {
        CheckAlive();
        return _properties.FirstOrDefault(p => p.Id == propid)?.Copy() ?? throw GdiErrors.InvalidParameter();
    }

    /// <summary>Adds a property item, or replaces the one with its id.</summary>
    /// <param name="propitem">The item.</param>
    public void SetPropertyItem(PropertyItem propitem)
    {
        ArgumentNullException.ThrowIfNull(propitem);
        CheckAlive();
        int at = _properties.FindIndex(p => p.Id == propitem.Id);
        if (at >= 0)
        {
            _properties[at] = propitem.Copy();
        }
        else
        {
            _properties.Add(propitem.Copy());
        }
    }

    /// <summary>Removes a property item.</summary>
    /// <param name="propid">The id.</param>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The image has no item with that id.</exception>
    public void RemovePropertyItem(int propid)
    {
        CheckAlive();
        if (_properties.RemoveAll(p => p.Id == propid) == 0)
        {
            throw GdiErrors.Generic();
        }
    }

    /// <summary>Gets the settings an encoder takes.</summary>
    /// <param name="encoder">The encoder's <see cref="ImageCodecInfo.Clsid"/>.</param>
    /// <returns>The settings, or null when the encoder takes none.</returns>
    /// <exception cref="NotImplementedException">The BMP encoder, as in GDI+.</exception>
    public EncoderParameters? GetEncoderParameterList(Guid encoder)
    {
        CheckAlive();
        return Codecs.ParameterList(encoder);
    }

    /// <summary>Saves the image in the format it was loaded from, or as PNG when it was made in memory or no encoder writes its format.</summary>
    /// <param name="filename">The file.</param>
    public void Save(string filename) => Save(filename, ImageCodecInfo.EncoderFor(Raw) is null ? ImageFormat.Png : new ImageFormat(Raw));

    /// <summary>Saves the image in a format.</summary>
    /// <param name="filename">The file.</param>
    /// <param name="format">The format.</param>
    public void Save(string filename, ImageFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        Save(filename, ImageCodecInfo.EncoderFor(format.Guid) ?? ImageCodecInfo.EncoderFor(ImageFormat.Png.Guid)!, null);
    }

    /// <summary>Saves the image with an encoder and its settings.</summary>
    /// <param name="filename">The file.</param>
    /// <param name="encoder">The encoder.</param>
    /// <param name="encoderParams">The settings, or null.</param>
    public void Save(string filename, ImageCodecInfo encoder, EncoderParameters? encoderParams)
    {
        ArgumentNullException.ThrowIfNull(filename);
        ArgumentNullException.ThrowIfNull(encoder);
        CheckAlive();
        Codecs.Save(this, filename, null, encoder, encoderParams);
    }

    /// <summary>Saves the image to a stream in a format.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="format">The format.</param>
    /// <exception cref="ArgumentNullException">No encoder writes the format, as in GDI+.</exception>
    public void Save(Stream stream, ImageFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        Save(stream, ImageCodecInfo.EncoderFor(format.Guid)!, null);
    }

    /// <summary>Saves the image to a stream with an encoder and its settings.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="encoder">The encoder.</param>
    /// <param name="encoderParams">The settings, or null.</param>
    public void Save(Stream stream, ImageCodecInfo encoder, EncoderParameters? encoderParams)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(encoder);
        CheckAlive();
        Codecs.Save(this, null, stream, encoder, encoderParams);
    }

    /// <summary>Adds this image as the next frame of a multi-frame save, or with <see cref="EncoderValue.Flush"/> finishes it.</summary>
    /// <param name="encoderParams">The settings: <see cref="Encoder.SaveFlag"/> with <see cref="EncoderValue.FrameDimensionPage"/> or <see cref="EncoderValue.Flush"/>.</param>
    public void SaveAdd(EncoderParameters encoderParams)
    {
        ArgumentNullException.ThrowIfNull(encoderParams);
        CheckAlive();
        Codecs.SaveAdd(this, null, encoderParams);
    }

    /// <summary>Adds another image as the next frame of a multi-frame save this image started.</summary>
    /// <param name="image">The image to add.</param>
    /// <param name="encoderParams">The settings: <see cref="Encoder.SaveFlag"/> with <see cref="EncoderValue.FrameDimensionPage"/>.</param>
    public void SaveAdd(Image image, EncoderParameters encoderParams)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(encoderParams);
        CheckAlive();
        Codecs.SaveAdd(this, image, encoderParams);
    }

    /// <summary>Rotates and flips the image in place.</summary>
    /// <param name="rotateFlipType">The rotation and flip.</param>
    public void RotateFlip(RotateFlipType rotateFlipType)
    {
        if (this is Metafile)
        {
            throw new NotImplementedException("Not implemented.");
        }

        CheckAlive();
        _frames[_active] = Transforms.RotateFlip(Current, rotateFlipType);
    }

    /// <summary>A small copy of the image, as 32-bit premultiplied ARGB.</summary>
    /// <param name="thumbWidth">The width.</param>
    /// <param name="thumbHeight">The height.</param>
    /// <param name="callback">Ignored, as in GDI+.</param>
    /// <param name="callbackData">Ignored; must be zero.</param>
    /// <returns>The thumbnail.</returns>
    public Image GetThumbnailImage(int thumbWidth, int thumbHeight, GetThumbnailImageAbort? callback, IntPtr callbackData)
    {
        CheckAlive();
        if (thumbWidth <= 0 || thumbHeight <= 0)
        {
            throw GdiErrors.OutOfMemory();
        }

        return new Bitmap(Transforms.Thumbnail(Current, thumbWidth, thumbHeight));
    }

    /// <summary>A copy of the image with all its frames and properties.</summary>
    /// <returns>The copy.</returns>
    public abstract object Clone();

    /// <summary>Releases the image's pixels.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases the image's pixels and finishes a multi-frame save it started.</summary>
    /// <param name="disposing">Whether <see cref="Dispose()"/> was called.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing && PendingSave is not null)
        {
            Codecs.Finish(this);
        }

        _disposed = true;
        _frames = [];
    }

    /// <summary>Finishes nothing: the pixels are managed memory.</summary>
    ~Image()
    {
        Dispose(false);
    }

    /// <summary>Sets the frames, the first active.</summary>
    internal void SetFrames(List<Frame> frames, Guid dimension)
    {
        _frames = frames;
        _active = 0;
        Dimension = dimension;
    }

    /// <summary>A frame with its pixels: a metafile draws its own when they are first needed; others have them already.</summary>
    private protected virtual Frame Realize(Frame frame) => frame;

    /// <summary>Replaces the active frame.</summary>
    internal void ReplaceCurrent(Frame frame) => _frames[_active] = frame;

    /// <summary>Copies the state that is not pixels onto a copy of this image.</summary>
    private protected void CopyStateTo(Image copy)
    {
        copy.DpiX = DpiX;
        copy.DpiY = DpiY;
        copy.FileFlags = FileFlags;
        copy.Raw = Raw;
        copy._properties.AddRange(_properties.Select(p => p.Copy()));
        copy.SetFrames([.. Frames.Select(f => f.Copy())], Dimension);
        copy._active = _active;
    }

    /// <summary>Sets the state a codec read.</summary>
    internal void SetFileState(Guid raw, int flags, float dpiX, float dpiY)
    {
        Raw = raw;
        FileFlags = flags;
        DpiX = dpiX;
        DpiY = dpiY;
    }

    /// <summary>Sets the resolution.</summary>
    internal void SetDpi(float x, float y)
    {
        DpiX = x;
        DpiY = y;
    }

    /// <summary>Throws as GDI+ does when the image is disposed.</summary>
    internal void CheckAlive()
    {
        if (_disposed)
        {
            throw GdiErrors.InvalidParameter();
        }
    }
}
