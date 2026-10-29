using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

using Egami.Pixels;
using Egami.SystemDrawing.Drawing2D;
using Egami.SystemDrawing.Imaging;
using Egami.SystemDrawing.Imaging.Effects;
using Egami.SystemDrawing.Internal;

namespace Egami.SystemDrawing;

using Rectangle = System.Drawing.Rectangle;
using Size = System.Drawing.Size;

/// <summary>An object that hands out a Windows device context; <see cref="Graphics"/> is one, and Egami's always throws.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="extras"/>
/// </example>
public interface IDeviceContext : IDisposable
{
    /// <summary>The device context.</summary>
    /// <returns>Never returns in Egami.</returns>
    IntPtr GetHdc();

    /// <summary>Releases the device context.</summary>
    void ReleaseHdc();
}

/// <summary>Effects and drawing-surface constructors of <see cref="Bitmap"/>.</summary>
public sealed partial class Bitmap
{
    /// <summary>Initializes a new instance of the <see cref="Bitmap"/> class at a graphics' resolution, 32-bit premultiplied ARGB as in GDI+.</summary>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="g">The graphics whose resolution the bitmap takes.</param>
    public Bitmap(int width, int height, Graphics g)
        : this(width, height, PixelFormat.Format32bppPArgb)
    {
        ArgumentNullException.ThrowIfNull(g);
        SetResolution(g.DpiX, g.DpiY);
    }

    /// <summary>Applies an effect to an area, or to the whole bitmap for an empty rectangle.</summary>
    /// <param name="effect">The effect.</param>
    /// <param name="area">The area, or empty for all of it.</param>
    public void ApplyEffect(Effect effect, Rectangle area)
    {
        ArgumentNullException.ThrowIfNull(effect);
        Rectangle all = new(0, 0, Width, Height);
        Rectangle part = area.IsEmpty ? all : Rectangle.Intersect(area, all);
        if (part.IsEmpty)
        {
            return;
        }

        using Bitmap piece = Clone(part, PixelFormat.Format32bppArgb);
        using Raster<Rgba8> pixels = piece.ToRgba8();
        effect.Apply(pixels);
        for (int y = 0; y < part.Height; y++)
        {
            Span<Rgba8> row = pixels.Row(y);
            for (int x = 0; x < part.Width; x++)
            {
                Rgba8 c = row[x];
                SetPixel(part.X + x, part.Y + y, Color.FromArgb(c.A, c.R, c.G, c.B));
            }
        }
    }
}

/// <summary>An off-screen buffer to draw on and then copy to a graphics in one go, as WinForms double-buffering does.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="extras"/>
/// </example>
public sealed class BufferedGraphics : IDisposable
{
    private readonly Bitmap _buffer;
    private readonly Graphics? _target;
    private readonly Rectangle _area;

    internal BufferedGraphics(Graphics? target, Rectangle area)
    {
        _target = target;
        _area = area;
        _buffer = new Bitmap(Math.Max(1, area.Width), Math.Max(1, area.Height), PixelFormat.Format32bppPArgb);
        if (target is not null)
        {
            _buffer.SetResolution(target.DpiX, target.DpiY);
        }

        Graphics = Graphics.FromImage(_buffer);
        // Drawing is in the target's coordinates: the buffer starts at the area's corner.
        Graphics.TranslateTransform(-area.X, -area.Y);
    }

    /// <summary>Gets the graphics that draws on the buffer, in the target's coordinates.</summary>
    public Graphics Graphics { get; }

    /// <summary>Copies the buffer onto the graphics it was made for.</summary>
    public void Render()
    {
        if (_target is not null)
        {
            Render(_target);
        }
    }

    /// <summary>
    /// Copies the buffer onto a graphics at the area it was made for, as GDI copies it: the premultiplied colors, opaque, replacing
    /// what was there.
    /// </summary>
    /// <param name="target">The graphics to copy to.</param>
    public void Render(Graphics? target)
    {
        if (target is null)
        {
            return;
        }

        using Bitmap opaque = _buffer.Clone(new Rectangle(0, 0, _buffer.Width, _buffer.Height), PixelFormat.Format32bppRgb);
        GraphicsState saved = target.Save();
        try
        {
            target.ResetTransform();
            target.PageUnit = GraphicsUnit.Pixel;
            target.CompositingMode = Drawing2D.CompositingMode.SourceCopy;
            target.InterpolationMode = Drawing2D.InterpolationMode.NearestNeighbor;
            target.PixelOffsetMode = Drawing2D.PixelOffsetMode.Half;
            target.DrawImage(opaque, new RectangleF(_area.X, _area.Y, _buffer.Width, _buffer.Height));
        }
        finally
        {
            target.Restore(saved);
        }
    }

    /// <summary>Not supported: Windows device contexts. Always throws.</summary>
    /// <param name="targetDC">The device context.</param>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    public void Render(IntPtr targetDC) => throw GdiErrors.WindowsOnly("BufferedGraphics.Render(IntPtr)", "Render onto a Graphics.");

    /// <summary>Releases the buffer.</summary>
    public void Dispose()
    {
        Graphics.Dispose();
        _buffer.Dispose();
    }
}

/// <summary>Makes <see cref="BufferedGraphics"/> buffers.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="extras"/>
/// </example>
public sealed class BufferedGraphicsContext : IDisposable
{
    /// <summary>Gets or sets the largest buffer kept for reuse; larger ones are made for the call, as in GDI+. 225 by 96 by default.</summary>
    /// <exception cref="ArgumentException">A dimension is zero or less.</exception>
    public Size MaximumBuffer
    {
        get;
        set => field = value.Width > 0 && value.Height > 0 ? value : throw new ArgumentException("Parameter is not valid.", nameof(value));
    } = new(225, 96);

    /// <summary>A buffer for an area of a graphics.</summary>
    /// <param name="targetGraphics">The graphics to render to.</param>
    /// <param name="targetRectangle">The area.</param>
    /// <returns>The buffer.</returns>
    public BufferedGraphics Allocate(Graphics targetGraphics, Rectangle targetRectangle)
    {
        ArgumentNullException.ThrowIfNull(targetGraphics);
        return new BufferedGraphics(targetGraphics, targetRectangle);
    }

    /// <summary>Not supported: Windows device contexts. Always throws.</summary>
    /// <param name="targetDC">The device context.</param>
    /// <param name="targetRectangle">The area.</param>
    /// <returns>Never returns.</returns>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    public BufferedGraphics Allocate(IntPtr targetDC, Rectangle targetRectangle) => throw GdiErrors.WindowsOnly("BufferedGraphicsContext.Allocate(IntPtr)", "Allocate for a Graphics.");

    /// <summary>Drops a kept buffer; Egami keeps none.</summary>
    public void Invalidate()
    {
    }

    /// <summary>Releases the context.</summary>
    public void Dispose()
    {
    }
}

/// <summary>The process's shared <see cref="BufferedGraphicsContext"/>.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="extras"/>
/// </example>
public static class BufferedGraphicsManager
{
    /// <summary>Gets the shared context.</summary>
    public static BufferedGraphicsContext Current { get; } = new();
}

/// <summary>
/// Steps animated GIFs (and other images with timed frames) through their frames at their delays: a timer marks images due for
/// their next frame and calls their handlers; <see cref="UpdateFrames(Image)"/> then selects the frame.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="extras"/>
/// </example>
public static class ImageAnimator
{
    /// <summary>The timer's step, 50 milliseconds as in System.Drawing.</summary>
    private const int Step = 50;

    private static readonly Lock Gate = new();
    private static readonly List<Animation> Animations = [];
    private static Timer? s_timer;

    /// <summary>Whether an image has more than one timed frame.</summary>
    /// <param name="image">The image, or null.</param>
    /// <returns><see langword="true"/> when it can animate.</returns>
    public static bool CanAnimate(Image? image) =>
        image is not null && image.FrameDimensionsList.Contains(FrameDimension.Time.Guid) && image.GetFrameCount(FrameDimension.Time) > 1;

    /// <summary>Starts animating an image: the handler is called each time the image is due for its next frame.</summary>
    /// <param name="image">The image.</param>
    /// <param name="onFrameChangedHandler">The handler.</param>
    public static void Animate(Image image, EventHandler onFrameChangedHandler)
    {
        if (!CanAnimate(image))
        {
            return;
        }

        lock (Gate)
        {
            Animation? a = Animations.Find(x => ReferenceEquals(x.Image, image));
            if (a is null)
            {
                a = new Animation(image);
                Animations.Add(a);
            }

            a.Handlers += onFrameChangedHandler;
            s_timer ??= new Timer(_ => Tick(), null, Step, Step);
        }
    }

    /// <summary>Stops calling a handler for an image, and stops animating it once no handler is left.</summary>
    /// <param name="image">The image.</param>
    /// <param name="onFrameChangedHandler">The handler.</param>
    public static void StopAnimate(Image image, EventHandler onFrameChangedHandler)
    {
        lock (Gate)
        {
            Animation? a = Animations.Find(x => ReferenceEquals(x.Image, image));
            if (a is null)
            {
                return;
            }

            a.Handlers -= onFrameChangedHandler;
            if (a.Handlers is null)
            {
                Animations.Remove(a);
            }

            if (Animations.Count == 0)
            {
                s_timer?.Dispose();
                s_timer = null;
            }
        }
    }

    /// <summary>Selects the frame each animated image is due for.</summary>
    public static void UpdateFrames()
    {
        lock (Gate)
        {
            foreach (Animation a in Animations)
            {
                a.Update();
            }
        }
    }

    /// <summary>Selects the frame an animated image is due for.</summary>
    /// <param name="image">The image.</param>
    public static void UpdateFrames(Image? image)
    {
        lock (Gate)
        {
            Animations.Find(x => ReferenceEquals(x.Image, image))?.Update();
        }
    }

    private static void Tick()
    {
        List<(Animation, EventHandler?)> due = [];
        lock (Gate)
        {
            foreach (Animation a in Animations)
            {
                if (a.Advance(Step))
                {
                    due.Add((a, a.Handlers));
                }
            }
        }

        foreach ((Animation a, EventHandler? handlers) in due)
        {
            handlers?.Invoke(a.Image, EventArgs.Empty);
        }
    }

    /// <summary>One image's place in its frames.</summary>
    private sealed class Animation
    {
        private readonly int[] _delays;
        private int _frame;
        private int _shown;
        private int _elapsed;

        public Animation(Image image)
        {
            Image = image;
            int count = image.GetFrameCount(FrameDimension.Time);
            _delays = new int[count];
            PropertyItem? delays = image.PropertyIdList.Contains(0x5100) ? image.GetPropertyItem(0x5100) : null;
            for (int i = 0; i < count; i++)
            {
                // Frame delays are in hundredths of a second; GDI+ treats a delay under 2 as 10, as browsers do.
                int d = delays?.Value is byte[] v && v.Length >= (i + 1) * 4 ? BitConverter.ToInt32(v, i * 4) : 0;
                _delays[i] = Math.Max(d < 2 ? 10 : d, 1) * 10;
            }
        }

        public Image Image { get; }

        public EventHandler? Handlers { get; set; }

        /// <summary>Moves time on; returns whether the next frame is now due.</summary>
        public bool Advance(int ms)
        {
            _elapsed += ms;
            bool changed = false;
            while (_elapsed >= _delays[_frame])
            {
                _elapsed -= _delays[_frame];
                _frame = (_frame + 1) % _delays.Length;
                changed = true;
            }

            return changed;
        }

        public void Update()
        {
            if (_shown != _frame)
            {
                Image.SelectActiveFrame(FrameDimension.Time, _frame);
                _shown = _frame;
            }
        }
    }
}

/// <summary>Names a type's toolbox image: a bitmap or icon resource next to the type, as the Windows Forms designer looks it up.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="extras"/>
/// </example>
[AttributeUsage(AttributeTargets.Class)]
public class ToolboxBitmapAttribute : Attribute
{
    /// <summary>The attribute for a type with no toolbox image.</summary>
    public static readonly ToolboxBitmapAttribute Default = new(null, null);

    private readonly Type? _type;
    private readonly string? _name;

    /// <summary>Initializes a new instance of the <see cref="ToolboxBitmapAttribute"/> class from a file path.</summary>
    /// <param name="imageFile">The image file.</param>
    public ToolboxBitmapAttribute(string imageFile)
        : this(null, imageFile)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ToolboxBitmapAttribute"/> class: the resource named after a type.</summary>
    /// <param name="t">The type.</param>
    public ToolboxBitmapAttribute(Type t)
        : this(t, (string?)null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ToolboxBitmapAttribute"/> class: a named resource next to a type.</summary>
    /// <param name="t">The type.</param>
    /// <param name="name">The resource name.</param>
    public ToolboxBitmapAttribute(Type? t, string? name)
    {
        _type = t;
        _name = name;
    }

    /// <inheritdoc/>
    public override bool Equals(object? value) => value is ToolboxBitmapAttribute other && other._type == _type && other._name == _name;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_type, _name);

    /// <summary>The small image for a component.</summary>
    /// <param name="component">The component.</param>
    /// <returns>The image, or null.</returns>
    public Image? GetImage(object? component) => GetImage(component, true);

    /// <summary>The image for a component.</summary>
    /// <param name="component">The component.</param>
    /// <param name="large">Whether the large image is wanted.</param>
    /// <returns>The image, or null.</returns>
    public Image? GetImage(object? component, bool large) => component is null ? null : GetImage(component.GetType(), large);

    /// <summary>The small image for a type.</summary>
    /// <param name="type">The type.</param>
    /// <returns>The image, or null.</returns>
    public Image? GetImage(Type type) => GetImage(type, false);

    /// <summary>The image for a type.</summary>
    /// <param name="type">The type.</param>
    /// <param name="large">Whether the large image is wanted.</param>
    /// <returns>The image, or null.</returns>
    public Image? GetImage(Type type, bool large) => GetImage(type, null, large);

    /// <summary>The image for a type from a named resource or file.</summary>
    /// <param name="type">The type.</param>
    /// <param name="imgName">The resource or file name, or null for the one the attribute names.</param>
    /// <param name="large">Whether the large image is wanted.</param>
    /// <returns>The image, or null.</returns>
    public Image? GetImage(Type type, string? imgName, bool large)
    {
        string? name = imgName ?? _name;
        if (_type is null && name is not null && File.Exists(name))
        {
            return Image.FromFile(name);
        }

        return GetImageFromResource(_type ?? type, name ?? (_type ?? type).Name + ".bmp", large);
    }

    /// <summary>An image from a resource next to a type, or null when there is none.</summary>
    /// <param name="t">The type.</param>
    /// <param name="imageName">The resource name.</param>
    /// <param name="large">Whether the large image is wanted.</param>
    /// <returns>The image, or null.</returns>
    public static Image? GetImageFromResource(Type t, string? imageName, bool large)
    {
        ArgumentNullException.ThrowIfNull(t);
        if (imageName is null)
        {
            return null;
        }

        using Stream? stream = t.Assembly.GetManifestResourceStream(t, imageName) ?? t.Assembly.GetManifestResourceStream(imageName);
        if (stream is null)
        {
            return null;
        }

        Image image = Image.FromStream(stream);
        int size = large ? 32 : 16;
        if (image.Width == size && image.Height == size)
        {
            return image;
        }

        using (image)
        {
            return new Bitmap(image, size, size);
        }
    }
}

/// <summary>Marks an assembly whose resource bitmaps have a suffix in the same assembly, for the designer.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="extras"/>
/// </example>
[AttributeUsage(AttributeTargets.Assembly)]
public class BitmapSuffixInSameAssemblyAttribute : Attribute
{
}

/// <summary>Marks an assembly whose resource bitmaps have a suffix in a satellite assembly, for the designer.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="extras"/>
/// </example>
[AttributeUsage(AttributeTargets.Assembly)]
public class BitmapSuffixInSatelliteAssemblyAttribute : Attribute
{
}

/// <summary>Not supported: Windows' own icons. Every member throws.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="extras"/>
/// </example>
public static class SystemIcons
{
    /// <summary>Gets Windows' application icon; always throws.</summary>
    [Obsolete(Internal.Diagnostics.StockIconsMessage, DiagnosticId = Internal.Diagnostics.StockIcons, UrlFormat = Internal.Diagnostics.Url)]
    public static Icon Application => throw Refuse();

    /// <summary>Gets Windows' asterisk icon; always throws.</summary>
    [Obsolete(Internal.Diagnostics.StockIconsMessage, DiagnosticId = Internal.Diagnostics.StockIcons, UrlFormat = Internal.Diagnostics.Url)]
    public static Icon Asterisk => throw Refuse();

    /// <summary>Gets Windows' error icon; always throws.</summary>
    [Obsolete(Internal.Diagnostics.StockIconsMessage, DiagnosticId = Internal.Diagnostics.StockIcons, UrlFormat = Internal.Diagnostics.Url)]
    public static Icon Error => throw Refuse();

    /// <summary>Gets Windows' exclamation icon; always throws.</summary>
    [Obsolete(Internal.Diagnostics.StockIconsMessage, DiagnosticId = Internal.Diagnostics.StockIcons, UrlFormat = Internal.Diagnostics.Url)]
    public static Icon Exclamation => throw Refuse();

    /// <summary>Gets Windows' hand icon; always throws.</summary>
    [Obsolete(Internal.Diagnostics.StockIconsMessage, DiagnosticId = Internal.Diagnostics.StockIcons, UrlFormat = Internal.Diagnostics.Url)]
    public static Icon Hand => throw Refuse();

    /// <summary>Gets Windows' information icon; always throws.</summary>
    [Obsolete(Internal.Diagnostics.StockIconsMessage, DiagnosticId = Internal.Diagnostics.StockIcons, UrlFormat = Internal.Diagnostics.Url)]
    public static Icon Information => throw Refuse();

    /// <summary>Gets Windows' question icon; always throws.</summary>
    [Obsolete(Internal.Diagnostics.StockIconsMessage, DiagnosticId = Internal.Diagnostics.StockIcons, UrlFormat = Internal.Diagnostics.Url)]
    public static Icon Question => throw Refuse();

    /// <summary>Gets Windows' shield icon; always throws.</summary>
    [Obsolete(Internal.Diagnostics.StockIconsMessage, DiagnosticId = Internal.Diagnostics.StockIcons, UrlFormat = Internal.Diagnostics.Url)]
    public static Icon Shield => throw Refuse();

    /// <summary>Gets Windows' warning icon; always throws.</summary>
    [Obsolete(Internal.Diagnostics.StockIconsMessage, DiagnosticId = Internal.Diagnostics.StockIcons, UrlFormat = Internal.Diagnostics.Url)]
    public static Icon Warning => throw Refuse();

    /// <summary>Gets Windows' logo icon; always throws.</summary>
    [Obsolete(Internal.Diagnostics.StockIconsMessage, DiagnosticId = Internal.Diagnostics.StockIcons, UrlFormat = Internal.Diagnostics.Url)]
    public static Icon WinLogo => throw Refuse();

    /// <summary>Not supported: Windows' stock icons. Always throws.</summary>
    /// <param name="stockIcon">The icon.</param>
    /// <param name="options">How to draw it.</param>
    /// <returns>Never returns.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="extras"/>
    /// </example>
    [Obsolete(Internal.Diagnostics.StockIconsMessage, DiagnosticId = Internal.Diagnostics.StockIcons, UrlFormat = Internal.Diagnostics.Url)]
    public static Icon GetStockIcon(StockIconId stockIcon, StockIconOptions options = StockIconOptions.Default) => throw Refuse();

    /// <summary>Not supported: Windows' stock icons. Always throws.</summary>
    /// <param name="stockIcon">The icon.</param>
    /// <param name="size">The size in pixels.</param>
    /// <returns>Never returns.</returns>
    [Obsolete(Internal.Diagnostics.StockIconsMessage, DiagnosticId = Internal.Diagnostics.StockIcons, UrlFormat = Internal.Diagnostics.Url)]
    public static Icon GetStockIcon(StockIconId stockIcon, int size) => throw Refuse();

    private static PlatformNotSupportedException Refuse() => GdiErrors.WindowsOnly("SystemIcons", "Ship your own icon files and load them with new Icon(path).");
}
