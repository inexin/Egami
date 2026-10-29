using System.Runtime.InteropServices;

using EgamiFonts = Egami.Fonts;

namespace Egami.SystemDrawing.Text;

/// <summary>A set of font families: the installed fonts, or fonts an application loads itself.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="fonts"/>
/// </example>
public abstract class FontCollection : IDisposable
{
    private protected FontCollection()
    {
    }

    /// <summary>Gets the families, sorted by name.</summary>
    public FontFamily[] Families => [.. Fonts.Families.Select(f => new FontFamily(f))];

    /// <summary>Gets the Egami collection the families come from.</summary>
    internal abstract EgamiFonts.FontCollection Fonts { get; }

    /// <summary>Releases the collection.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases the collection.</summary>
    /// <param name="disposing">Whether this is a <see cref="Dispose()"/> call rather than a finalizer.</param>
    protected virtual void Dispose(bool disposing)
    {
    }
}

/// <summary>
/// The fonts installed on this machine, found without native calls in the platform's font folders (on Linux, the fontconfig
/// folders and the usual share/fonts folders).
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="fonts"/>
/// </example>
public sealed class InstalledFontCollection : FontCollection
{
    /// <summary>Initializes a new instance of the <see cref="InstalledFontCollection"/> class.</summary>
    public InstalledFontCollection()
    {
    }

    internal static InstalledFontCollection Shared { get; } = new();

    internal override EgamiFonts.FontCollection Fonts => EgamiFonts.FontCollection.System;

    /// <inheritdoc/>
    protected override void Dispose(bool disposing) => base.Dispose(disposing);
}

/// <summary>Fonts an application loads from files or memory: TrueType, OpenType, collections and WOFF.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="fonts"/>
/// </example>
public sealed class PrivateFontCollection : FontCollection
{
    private readonly EgamiFonts.FontCollection _fonts = new();
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="PrivateFontCollection"/> class, empty.</summary>
    public PrivateFontCollection()
    {
    }

    internal override EgamiFonts.FontCollection Fonts
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _fonts;
        }
    }

    /// <summary>Adds every face of a font file.</summary>
    /// <param name="filename">The file.</param>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="ExternalException">The file is not a font Egami reads.</exception>
    public void AddFontFile(string filename)
    {
        ArgumentNullException.ThrowIfNull(filename);
        string path = System.IO.Path.GetFullPath(filename);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The font file was not found.", filename);
        }

        int added;
        try
        {
            added = Fonts.AddFile(path);
        }
        catch (Exception e) when (e is EgamiException or InvalidDataException or NotSupportedException or EndOfStreamException)
        {
            throw new ExternalException("File not found.", unchecked((int)0x80004005));
        }

        if (added == 0)
        {
            throw new ExternalException("File not found.", unchecked((int)0x80004005));
        }
    }

    /// <summary>Adds a font from memory; the bytes are copied, so the memory can be freed afterwards.</summary>
    /// <param name="memory">The font data.</param>
    /// <param name="length">Its length in bytes.</param>
    /// <exception cref="ArgumentException"><paramref name="memory"/> is zero or <paramref name="length"/> is not positive.</exception>
    /// <exception cref="ExternalException">The data is not a font Egami reads.</exception>
    public void AddMemoryFont(IntPtr memory, int length)
    {
        if (memory == IntPtr.Zero || length <= 0)
        {
            throw new ArgumentException("Parameter is not valid.");
        }

        byte[] data = new byte[length];
        Marshal.Copy(memory, data, 0, length);
        try
        {
            Fonts.Add(EgamiFonts.Typeface.FromMemory(data));
        }
        catch (Exception e) when (e is EgamiException or InvalidDataException or NotSupportedException or EndOfStreamException or ArgumentException)
        {
            throw new ExternalException("A generic error occurred in GDI+.", unchecked((int)0x80004005));
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        base.Dispose(disposing);
    }
}
