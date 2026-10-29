// Written from the System.Drawing reference surface: the constructors that record or take Windows handles, all throwing.
using System.ComponentModel;

using Egami.SystemDrawing.Internal;

namespace Egami.SystemDrawing.Imaging;

/// <summary>The recording and handle members, which throw.</summary>
public sealed partial class Metafile
{
    /// <summary>Not supported: Windows metafile handles. Always throws.</summary>
    /// <param name="hmetafile">A WMF handle.</param>
    /// <param name="wmfHeader">The placeable header.</param>
    /// <param name="deleteWmf">Whether to delete the handle.</param>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public Metafile(IntPtr hmetafile, WmfPlaceableFileHeader wmfHeader, bool deleteWmf)
        : this(Refuse(GdiErrors.WindowsOnly("new Metafile(IntPtr)", "Load the metafile from a file or a stream.")))
    {
    }

    /// <summary>Not supported: Windows metafile handles. Always throws.</summary>
    /// <param name="henhmetafile">An EMF handle.</param>
    /// <param name="deleteEmf">Whether to delete the handle.</param>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public Metafile(IntPtr henhmetafile, bool deleteEmf)
        : this(Refuse(GdiErrors.WindowsOnly("new Metafile(IntPtr)", "Load the metafile from a file or a stream.")))
    {
    }

    /// <summary>Not supported: Egami does not record metafiles. Always throws.</summary>
    /// <param name="referenceHdc">The reference device context.</param>
    /// <param name="frameRect">The frame.</param>
    [Obsolete(Internal.Diagnostics.RecordingMessage, DiagnosticId = Internal.Diagnostics.Recording, UrlFormat = Internal.Diagnostics.Url)]
    public Metafile(IntPtr referenceHdc, Rectangle frameRect)
        : this(Refuse(GdiErrors.Recording()))
    {
    }

    /// <summary>Not supported: Egami does not record metafiles. Always throws.</summary>
    /// <param name="referenceHdc">The reference device context.</param>
    /// <param name="emfType">The kind of EMF.</param>
    [Obsolete(Internal.Diagnostics.RecordingMessage, DiagnosticId = Internal.Diagnostics.Recording, UrlFormat = Internal.Diagnostics.Url)]
    public Metafile(IntPtr referenceHdc, EmfType emfType)
        : this(Refuse(GdiErrors.Recording()))
    {
    }

    /// <summary>Not supported: Egami does not record metafiles. Always throws.</summary>
    /// <param name="referenceHdc">The reference device context.</param>
    /// <param name="frameRect">The frame.</param>
    [Obsolete(Internal.Diagnostics.RecordingMessage, DiagnosticId = Internal.Diagnostics.Recording, UrlFormat = Internal.Diagnostics.Url)]
    public Metafile(IntPtr referenceHdc, RectangleF frameRect)
        : this(Refuse(GdiErrors.Recording()))
    {
    }

    /// <summary>Not supported: Egami does not record metafiles. Always throws.</summary>
    /// <param name="referenceHdc">The reference device context.</param>
    /// <param name="frameRect">The frame.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    [Obsolete(Internal.Diagnostics.RecordingMessage, DiagnosticId = Internal.Diagnostics.Recording, UrlFormat = Internal.Diagnostics.Url)]
    public Metafile(IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit)
        : this(Refuse(GdiErrors.Recording()))
    {
    }

    /// <summary>Not supported: Egami does not record metafiles. Always throws.</summary>
    /// <param name="referenceHdc">The reference device context.</param>
    /// <param name="frameRect">The frame.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    /// <param name="type">The kind of EMF.</param>
    [Obsolete(Internal.Diagnostics.RecordingMessage, DiagnosticId = Internal.Diagnostics.Recording, UrlFormat = Internal.Diagnostics.Url)]
    public Metafile(IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit, EmfType type)
        : this(Refuse(GdiErrors.Recording()))
    {
    }

    /// <summary>Not supported: Egami does not record metafiles. Always throws.</summary>
    /// <param name="referenceHdc">The reference device context.</param>
    /// <param name="frameRect">The frame.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    /// <param name="type">The kind of EMF.</param>
    /// <param name="description">The description.</param>
    [Obsolete(Internal.Diagnostics.RecordingMessage, DiagnosticId = Internal.Diagnostics.Recording, UrlFormat = Internal.Diagnostics.Url)]
    public Metafile(IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit, EmfType type, string description)
        : this(Refuse(GdiErrors.Recording()))
    {
    }

    /// <summary>Not supported: Egami does not record metafiles. Always throws.</summary>
    /// <param name="referenceHdc">The reference device context.</param>
    /// <param name="frameRect">The frame.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    [Obsolete(Internal.Diagnostics.RecordingMessage, DiagnosticId = Internal.Diagnostics.Recording, UrlFormat = Internal.Diagnostics.Url)]
    public Metafile(IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit)
        : this(Refuse(GdiErrors.Recording()))
    {
    }

    /// <summary>Not supported: Egami does not record metafiles. Always throws.</summary>
    /// <param name="referenceHdc">The reference device context.</param>
    /// <param name="frameRect">The frame.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    /// <param name="type">The kind of EMF.</param>
    [Obsolete(Internal.Diagnostics.RecordingMessage, DiagnosticId = Internal.Diagnostics.Recording, UrlFormat = Internal.Diagnostics.Url)]
    public Metafile(IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit, EmfType type)
        : this(Refuse(GdiErrors.Recording()))
    {
    }

    /// <summary>Not supported: Egami does not record metafiles. Always throws.</summary>
    /// <param name="fileName">The file to record to.</param>
    /// <param name="referenceHdc">The reference device context.</param>
    [Obsolete(Internal.Diagnostics.RecordingMessage, DiagnosticId = Internal.Diagnostics.Recording, UrlFormat = Internal.Diagnostics.Url)]
    public Metafile(string fileName, IntPtr referenceHdc)
        : this(Refuse(GdiErrors.Recording()))
    {
    }

    /// <summary>Not supported: Egami does not record metafiles. Always throws.</summary>
    /// <param name="fileName">The file to record to.</param>
    /// <param name="referenceHdc">The reference device context.</param>
    /// <param name="type">The kind of EMF.</param>
    [Obsolete(Internal.Diagnostics.RecordingMessage, DiagnosticId = Internal.Diagnostics.Recording, UrlFormat = Internal.Diagnostics.Url)]
    public Metafile(string fileName, IntPtr referenceHdc, EmfType type)
        : this(Refuse(GdiErrors.Recording()))
    {
    }

    /// <summary>Not supported: Egami does not record metafiles. Always throws.</summary>
    /// <param name="fileName">The file to record to.</param>
    /// <param name="referenceHdc">The reference device context.</param>
    /// <param name="frameRect">The frame.</param>
    [Obsolete(Internal.Diagnostics.RecordingMessage, DiagnosticId = Internal.Diagnostics.Recording, UrlFormat = Internal.Diagnostics.Url)]
    public Metafile(string fileName, IntPtr referenceHdc, RectangleF frameRect)
        : this(Refuse(GdiErrors.Recording()))
    {
    }

    /// <summary>Not supported: Egami does not record metafiles. Always throws.</summary>
    /// <param name="fileName">The file to record to.</param>
    /// <param name="referenceHdc">The reference device context.</param>
    /// <param name="frameRect">The frame.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    [Obsolete(Internal.Diagnostics.RecordingMessage, DiagnosticId = Internal.Diagnostics.Recording, UrlFormat = Internal.Diagnostics.Url)]
    public Metafile(string fileName, IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit)
        : this(Refuse(GdiErrors.Recording()))
    {
    }

    /// <summary>Not supported: Egami does not record metafiles. Always throws.</summary>
    /// <param name="fileName">The file to record to.</param>
    /// <param name="referenceHdc">The reference device context.</param>
    /// <param name="frameRect">The frame.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    /// <param name="type">The kind of EMF.</param>
    [Obsolete(Internal.Diagnostics.RecordingMessage, DiagnosticId = Internal.Diagnostics.Recording, UrlFormat = Internal.Diagnostics.Url)]
    public Metafile(string fileName, IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit, EmfType type)
        : this(Refuse(GdiErrors.Recording()))
    {
    }

    /// <summary>Not supported: Egami does not record metafiles. Always throws.</summary>
    /// <param name="fileName">The file to record to.</param>
    /// <param name="referenceHdc">The reference device context.</param>
    /// <param name="frameRect">The frame.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    /// <param name="desc">The description.</param>
    [Obsolete(Internal.Diagnostics.RecordingMessage, DiagnosticId = Internal.Diagnostics.Recording, UrlFormat = Internal.Diagnostics.Url)]
    public Metafile(string fileName, IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit, string desc)
        : this(Refuse(GdiErrors.Recording()))
    {
    }

    /// <summary>Not supported: Egami does not record metafiles. Always throws.</summary>
    /// <param name="fileName">The file to record to.</param>
    /// <param name="referenceHdc">The reference device context.</param>
    /// <param name="frameRect">The frame.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    /// <param name="type">The kind of EMF.</param>
    /// <param name="description">The description.</param>
    [Obsolete(Internal.Diagnostics.RecordingMessage, DiagnosticId = Internal.Diagnostics.Recording, UrlFormat = Internal.Diagnostics.Url)]
    public Metafile(string fileName, IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit, EmfType type, string description)
        : this(Refuse(GdiErrors.Recording()))
    {
    }

    /// <summary>Not supported: Egami does not record metafiles. Always throws.</summary>
    /// <param name="fileName">The file to record to.</param>
    /// <param name="referenceHdc">The reference device context.</param>
    /// <param name="frameRect">The frame.</param>
    [Obsolete(Internal.Diagnostics.RecordingMessage, DiagnosticId = Internal.Diagnostics.Recording, UrlFormat = Internal.Diagnostics.Url)]
    public Metafile(string fileName, IntPtr referenceHdc, Rectangle frameRect)
        : this(Refuse(GdiErrors.Recording()))
    {
    }

    /// <summary>Not supported: Egami does not record metafiles. Always throws.</summary>
    /// <param name="fileName">The file to record to.</param>
    /// <param name="referenceHdc">The reference device context.</param>
    /// <param name="frameRect">The frame.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    [Obsolete(Internal.Diagnostics.RecordingMessage, DiagnosticId = Internal.Diagnostics.Recording, UrlFormat = Internal.Diagnostics.Url)]
    public Metafile(string fileName, IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit)
        : this(Refuse(GdiErrors.Recording()))
    {
    }

    /// <summary>Not supported: Egami does not record metafiles. Always throws.</summary>
    /// <param name="fileName">The file to record to.</param>
    /// <param name="referenceHdc">The reference device context.</param>
    /// <param name="frameRect">The frame.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    /// <param name="type">The kind of EMF.</param>
    [Obsolete(Internal.Diagnostics.RecordingMessage, DiagnosticId = Internal.Diagnostics.Recording, UrlFormat = Internal.Diagnostics.Url)]
    public Metafile(string fileName, IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit, EmfType type)
        : this(Refuse(GdiErrors.Recording()))
    {
    }

    /// <summary>Not supported: Egami does not record metafiles. Always throws.</summary>
    /// <param name="fileName">The file to record to.</param>
    /// <param name="referenceHdc">The reference device context.</param>
    /// <param name="frameRect">The frame.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    /// <param name="description">The description.</param>
    [Obsolete(Internal.Diagnostics.RecordingMessage, DiagnosticId = Internal.Diagnostics.Recording, UrlFormat = Internal.Diagnostics.Url)]
    public Metafile(string fileName, IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit, string description)
        : this(Refuse(GdiErrors.Recording()))
    {
    }

    /// <summary>Not supported: Egami does not record metafiles. Always throws.</summary>
    /// <param name="stream">The stream to record to.</param>
    /// <param name="referenceHdc">The reference device context.</param>
    [Obsolete(Internal.Diagnostics.RecordingMessage, DiagnosticId = Internal.Diagnostics.Recording, UrlFormat = Internal.Diagnostics.Url)]
    public Metafile(Stream stream, IntPtr referenceHdc)
        : this(Refuse(GdiErrors.Recording()))
    {
    }

    /// <summary>Not supported: Egami does not record metafiles. Always throws.</summary>
    /// <param name="stream">The stream to record to.</param>
    /// <param name="referenceHdc">The reference device context.</param>
    /// <param name="type">The kind of EMF.</param>
    [Obsolete(Internal.Diagnostics.RecordingMessage, DiagnosticId = Internal.Diagnostics.Recording, UrlFormat = Internal.Diagnostics.Url)]
    public Metafile(Stream stream, IntPtr referenceHdc, EmfType type)
        : this(Refuse(GdiErrors.Recording()))
    {
    }

    /// <summary>Not supported: Egami does not record metafiles. Always throws.</summary>
    /// <param name="stream">The stream to record to.</param>
    /// <param name="referenceHdc">The reference device context.</param>
    /// <param name="frameRect">The frame.</param>
    [Obsolete(Internal.Diagnostics.RecordingMessage, DiagnosticId = Internal.Diagnostics.Recording, UrlFormat = Internal.Diagnostics.Url)]
    public Metafile(Stream stream, IntPtr referenceHdc, RectangleF frameRect)
        : this(Refuse(GdiErrors.Recording()))
    {
    }

    /// <summary>Not supported: Egami does not record metafiles. Always throws.</summary>
    /// <param name="stream">The stream to record to.</param>
    /// <param name="referenceHdc">The reference device context.</param>
    /// <param name="frameRect">The frame.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    [Obsolete(Internal.Diagnostics.RecordingMessage, DiagnosticId = Internal.Diagnostics.Recording, UrlFormat = Internal.Diagnostics.Url)]
    public Metafile(Stream stream, IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit)
        : this(Refuse(GdiErrors.Recording()))
    {
    }

    /// <summary>Not supported: Egami does not record metafiles. Always throws.</summary>
    /// <param name="stream">The stream to record to.</param>
    /// <param name="referenceHdc">The reference device context.</param>
    /// <param name="frameRect">The frame.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    /// <param name="type">The kind of EMF.</param>
    [Obsolete(Internal.Diagnostics.RecordingMessage, DiagnosticId = Internal.Diagnostics.Recording, UrlFormat = Internal.Diagnostics.Url)]
    public Metafile(Stream stream, IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit, EmfType type)
        : this(Refuse(GdiErrors.Recording()))
    {
    }

    /// <summary>Not supported: Egami does not record metafiles. Always throws.</summary>
    /// <param name="stream">The stream to record to.</param>
    /// <param name="referenceHdc">The reference device context.</param>
    /// <param name="frameRect">The frame.</param>
    [Obsolete(Internal.Diagnostics.RecordingMessage, DiagnosticId = Internal.Diagnostics.Recording, UrlFormat = Internal.Diagnostics.Url)]
    public Metafile(Stream stream, IntPtr referenceHdc, Rectangle frameRect)
        : this(Refuse(GdiErrors.Recording()))
    {
    }

    /// <summary>Not supported: Egami does not record metafiles. Always throws.</summary>
    /// <param name="stream">The stream to record to.</param>
    /// <param name="referenceHdc">The reference device context.</param>
    /// <param name="frameRect">The frame.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    [Obsolete(Internal.Diagnostics.RecordingMessage, DiagnosticId = Internal.Diagnostics.Recording, UrlFormat = Internal.Diagnostics.Url)]
    public Metafile(Stream stream, IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit)
        : this(Refuse(GdiErrors.Recording()))
    {
    }

    /// <summary>Not supported: Egami does not record metafiles. Always throws.</summary>
    /// <param name="stream">The stream to record to.</param>
    /// <param name="referenceHdc">The reference device context.</param>
    /// <param name="frameRect">The frame.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    /// <param name="type">The kind of EMF.</param>
    [Obsolete(Internal.Diagnostics.RecordingMessage, DiagnosticId = Internal.Diagnostics.Recording, UrlFormat = Internal.Diagnostics.Url)]
    public Metafile(Stream stream, IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit, EmfType type)
        : this(Refuse(GdiErrors.Recording()))
    {
    }

    /// <summary>Not supported: Windows metafile handles. Always throws.</summary>
    /// <param name="hmetafile">A WMF handle.</param>
    /// <param name="wmfHeader">The placeable header.</param>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public Metafile(IntPtr hmetafile, WmfPlaceableFileHeader wmfHeader)
        : this(Refuse(GdiErrors.WindowsOnly("new Metafile(IntPtr)", "Load the metafile from a file or a stream.")))
    {
    }

    /// <summary>Not supported: Egami does not record metafiles. Always throws.</summary>
    /// <param name="referenceHdc">The reference device context.</param>
    /// <param name="emfType">The kind of EMF.</param>
    /// <param name="description">The description.</param>
    [Obsolete(Internal.Diagnostics.RecordingMessage, DiagnosticId = Internal.Diagnostics.Recording, UrlFormat = Internal.Diagnostics.Url)]
    public Metafile(IntPtr referenceHdc, EmfType emfType, string description)
        : this(Refuse(GdiErrors.Recording()))
    {
    }

    /// <summary>Not supported: Egami does not record metafiles. Always throws.</summary>
    /// <param name="referenceHdc">The reference device context.</param>
    /// <param name="frameRect">The frame.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    /// <param name="type">The kind of EMF.</param>
    /// <param name="desc">The description.</param>
    [Obsolete(Internal.Diagnostics.RecordingMessage, DiagnosticId = Internal.Diagnostics.Recording, UrlFormat = Internal.Diagnostics.Url)]
    public Metafile(IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit, EmfType type, string desc)
        : this(Refuse(GdiErrors.Recording()))
    {
    }

    /// <summary>Not supported: Egami does not record metafiles. Always throws.</summary>
    /// <param name="fileName">The file to record to.</param>
    /// <param name="referenceHdc">The reference device context.</param>
    /// <param name="type">The kind of EMF.</param>
    /// <param name="description">The description.</param>
    [Obsolete(Internal.Diagnostics.RecordingMessage, DiagnosticId = Internal.Diagnostics.Recording, UrlFormat = Internal.Diagnostics.Url)]
    public Metafile(string fileName, IntPtr referenceHdc, EmfType type, string description)
        : this(Refuse(GdiErrors.Recording()))
    {
    }

    /// <summary>Not supported: Egami does not record metafiles. Always throws.</summary>
    /// <param name="fileName">The file to record to.</param>
    /// <param name="referenceHdc">The reference device context.</param>
    /// <param name="frameRect">The frame.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    /// <param name="type">The kind of EMF.</param>
    /// <param name="description">The description.</param>
    [Obsolete(Internal.Diagnostics.RecordingMessage, DiagnosticId = Internal.Diagnostics.Recording, UrlFormat = Internal.Diagnostics.Url)]
    public Metafile(string fileName, IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit, EmfType type, string description)
        : this(Refuse(GdiErrors.Recording()))
    {
    }

    /// <summary>Not supported: Egami does not record metafiles. Always throws.</summary>
    /// <param name="stream">The stream to record to.</param>
    /// <param name="referenceHdc">The reference device context.</param>
    /// <param name="type">The kind of EMF.</param>
    /// <param name="description">The description.</param>
    [Obsolete(Internal.Diagnostics.RecordingMessage, DiagnosticId = Internal.Diagnostics.Recording, UrlFormat = Internal.Diagnostics.Url)]
    public Metafile(Stream stream, IntPtr referenceHdc, EmfType type, string description)
        : this(Refuse(GdiErrors.Recording()))
    {
    }

    /// <summary>Not supported: Egami does not record metafiles. Always throws.</summary>
    /// <param name="stream">The stream to record to.</param>
    /// <param name="referenceHdc">The reference device context.</param>
    /// <param name="frameRect">The frame.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    /// <param name="type">The kind of EMF.</param>
    /// <param name="description">The description.</param>
    [Obsolete(Internal.Diagnostics.RecordingMessage, DiagnosticId = Internal.Diagnostics.Recording, UrlFormat = Internal.Diagnostics.Url)]
    public Metafile(Stream stream, IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit, EmfType type, string description)
        : this(Refuse(GdiErrors.Recording()))
    {
    }

    /// <summary>Not supported: Egami does not record metafiles. Always throws.</summary>
    /// <param name="stream">The stream to record to.</param>
    /// <param name="referenceHdc">The reference device context.</param>
    /// <param name="frameRect">The frame.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    /// <param name="type">The kind of EMF.</param>
    /// <param name="description">The description.</param>
    [Obsolete(Internal.Diagnostics.RecordingMessage, DiagnosticId = Internal.Diagnostics.Recording, UrlFormat = Internal.Diagnostics.Url)]
    public Metafile(Stream stream, IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit, EmfType type, string description)
        : this(Refuse(GdiErrors.Recording()))
    {
    }

    private static MetafileScan Refuse(Exception e) => throw e;

    /// <summary>Not supported: Windows metafile handles. Always throws.</summary>
    /// <returns>Never returns.</returns>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    public IntPtr GetHenhmetafile() => throw GdiErrors.WindowsOnly("Metafile.GetHenhmetafile", "Keep the metafile's file or bytes.");

    /// <summary>Not supported: Windows metafile handles. Always throws.</summary>
    /// <param name="hmetafile">A WMF handle.</param>
    /// <param name="wmfHeader">The placeable header.</param>
    /// <returns>Never returns.</returns>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    public static MetafileHeader GetMetafileHeader(IntPtr hmetafile, WmfPlaceableFileHeader wmfHeader) => throw GdiErrors.WindowsOnly("Metafile.GetMetafileHeader(IntPtr)", "Read the header from the file or stream.");

    /// <summary>Not supported: Windows metafile handles. Always throws.</summary>
    /// <param name="henhmetafile">An EMF handle.</param>
    /// <returns>Never returns.</returns>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    public static MetafileHeader GetMetafileHeader(IntPtr henhmetafile) => throw GdiErrors.WindowsOnly("Metafile.GetMetafileHeader(IntPtr)", "Read the header from the file or stream.");
}
