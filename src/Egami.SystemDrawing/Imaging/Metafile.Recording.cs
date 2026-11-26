// Written from the System.Drawing reference surface: the constructors that record, and those that take Windows handles, which throw.
using System.ComponentModel;

using Egami.SystemDrawing.Internal;

namespace Egami.SystemDrawing.Imaging;

/// <summary>The recording constructors, and the handle members, which throw.</summary>
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

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class that records into memory.</summary>
    /// <param name="referenceHdc">Ignored: Egami records for a 96 DPI screen, whatever device context is given, <see cref="IntPtr.Zero"/> included.</param>
    /// <param name="frameRect">The frame, the part of the drawing the metafile shows; in <see cref="MetafileFrameUnit.GdiCompatible"/> units, 0.01 mm, unless a unit is given.</param>
    /// <exception cref="ArgumentException">The EMF type or frame unit is not one, as GDI+ reports it.</exception>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The frame is negative, or empty in a unit other than GdiCompatible, as GDI+ reports it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="recording"/>
    /// </example>
    public Metafile(IntPtr referenceHdc, Rectangle frameRect)
        : this(MetafileRecording.Create(null, null, 0, frameRect, MetafileFrameUnit.GdiCompatible, EmfType.EmfPlusDual, null))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class that records into memory. Without a frame, the frame is the bounds of what is drawn.</summary>
    /// <param name="referenceHdc">Ignored: Egami records for a 96 DPI screen, whatever device context is given, <see cref="IntPtr.Zero"/> included.</param>
    /// <param name="emfType">The kind of EMF: GDI records only, EMF+ records only, or both.</param>
    /// <exception cref="ArgumentException">The EMF type or frame unit is not one, as GDI+ reports it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="recording"/>
    /// </example>
    public Metafile(IntPtr referenceHdc, EmfType emfType)
        : this(MetafileRecording.Create(null, null, 0, null, MetafileFrameUnit.GdiCompatible, emfType, null))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class that records into memory.</summary>
    /// <param name="referenceHdc">Ignored: Egami records for a 96 DPI screen, whatever device context is given, <see cref="IntPtr.Zero"/> included.</param>
    /// <param name="frameRect">The frame, the part of the drawing the metafile shows; in <see cref="MetafileFrameUnit.GdiCompatible"/> units, 0.01 mm, unless a unit is given.</param>
    /// <exception cref="ArgumentException">The EMF type or frame unit is not one, as GDI+ reports it.</exception>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The frame is negative, or empty in a unit other than GdiCompatible, as GDI+ reports it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="recording"/>
    /// </example>
    public Metafile(IntPtr referenceHdc, RectangleF frameRect)
        : this(MetafileRecording.Create(null, null, 0, frameRect, MetafileFrameUnit.GdiCompatible, EmfType.EmfPlusDual, null))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class that records into memory.</summary>
    /// <param name="referenceHdc">Ignored: Egami records for a 96 DPI screen, whatever device context is given, <see cref="IntPtr.Zero"/> included.</param>
    /// <param name="frameRect">The frame, the part of the drawing the metafile shows; in <see cref="MetafileFrameUnit.GdiCompatible"/> units, 0.01 mm, unless a unit is given.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    /// <exception cref="ArgumentException">The EMF type or frame unit is not one, as GDI+ reports it.</exception>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The frame is negative, or empty in a unit other than GdiCompatible, as GDI+ reports it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="recording"/>
    /// </example>
    public Metafile(IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit)
        : this(MetafileRecording.Create(null, null, 0, frameRect, frameUnit, EmfType.EmfPlusDual, null))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class that records into memory.</summary>
    /// <param name="referenceHdc">Ignored: Egami records for a 96 DPI screen, whatever device context is given, <see cref="IntPtr.Zero"/> included.</param>
    /// <param name="frameRect">The frame, the part of the drawing the metafile shows; in <see cref="MetafileFrameUnit.GdiCompatible"/> units, 0.01 mm, unless a unit is given.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    /// <param name="type">The kind of EMF: GDI records only, EMF+ records only, or both.</param>
    /// <exception cref="ArgumentException">The EMF type or frame unit is not one, as GDI+ reports it.</exception>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The frame is negative, or empty in a unit other than GdiCompatible, as GDI+ reports it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="recording"/>
    /// </example>
    public Metafile(IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit, EmfType type)
        : this(MetafileRecording.Create(null, null, 0, frameRect, frameUnit, type, null))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class that records into memory.</summary>
    /// <param name="referenceHdc">Ignored: Egami records for a 96 DPI screen, whatever device context is given, <see cref="IntPtr.Zero"/> included.</param>
    /// <param name="frameRect">The frame, the part of the drawing the metafile shows; in <see cref="MetafileFrameUnit.GdiCompatible"/> units, 0.01 mm, unless a unit is given.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    /// <param name="type">The kind of EMF: GDI records only, EMF+ records only, or both.</param>
    /// <param name="description">The description stored in the header, or null.</param>
    /// <exception cref="ArgumentException">The EMF type or frame unit is not one, as GDI+ reports it.</exception>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The frame is negative, or empty in a unit other than GdiCompatible, as GDI+ reports it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="recording"/>
    /// </example>
    public Metafile(IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit, EmfType type, string description)
        : this(MetafileRecording.Create(null, null, 0, frameRect, frameUnit, type, description))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class that records into memory.</summary>
    /// <param name="referenceHdc">Ignored: Egami records for a 96 DPI screen, whatever device context is given, <see cref="IntPtr.Zero"/> included.</param>
    /// <param name="frameRect">The frame, the part of the drawing the metafile shows; in <see cref="MetafileFrameUnit.GdiCompatible"/> units, 0.01 mm, unless a unit is given.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    /// <exception cref="ArgumentException">The EMF type or frame unit is not one, as GDI+ reports it.</exception>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The frame is negative, or empty in a unit other than GdiCompatible, as GDI+ reports it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="recording"/>
    /// </example>
    public Metafile(IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit)
        : this(MetafileRecording.Create(null, null, 0, frameRect, frameUnit, EmfType.EmfPlusDual, null))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class that records into memory.</summary>
    /// <param name="referenceHdc">Ignored: Egami records for a 96 DPI screen, whatever device context is given, <see cref="IntPtr.Zero"/> included.</param>
    /// <param name="frameRect">The frame, the part of the drawing the metafile shows; in <see cref="MetafileFrameUnit.GdiCompatible"/> units, 0.01 mm, unless a unit is given.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    /// <param name="type">The kind of EMF: GDI records only, EMF+ records only, or both.</param>
    /// <exception cref="ArgumentException">The EMF type or frame unit is not one, as GDI+ reports it.</exception>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The frame is negative, or empty in a unit other than GdiCompatible, as GDI+ reports it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="recording"/>
    /// </example>
    public Metafile(IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit, EmfType type)
        : this(MetafileRecording.Create(null, null, 0, frameRect, frameUnit, type, null))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class that records into a file. Without a frame, the frame is the bounds of what is drawn.</summary>
    /// <param name="fileName">The file to write, created at once and written when the recording Graphics is disposed.</param>
    /// <param name="referenceHdc">Ignored: Egami records for a 96 DPI screen, whatever device context is given, <see cref="IntPtr.Zero"/> included.</param>
    /// <exception cref="ArgumentException">The EMF type or frame unit is not one, as GDI+ reports it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="recording"/>
    /// </example>
    public Metafile(string fileName, IntPtr referenceHdc)
        : this(MetafileRecording.Create(fileName, null, 1, null, MetafileFrameUnit.GdiCompatible, EmfType.EmfPlusDual, null))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class that records into a file. Without a frame, the frame is the bounds of what is drawn.</summary>
    /// <param name="fileName">The file to write, created at once and written when the recording Graphics is disposed.</param>
    /// <param name="referenceHdc">Ignored: Egami records for a 96 DPI screen, whatever device context is given, <see cref="IntPtr.Zero"/> included.</param>
    /// <param name="type">The kind of EMF: GDI records only, EMF+ records only, or both.</param>
    /// <exception cref="ArgumentException">The EMF type or frame unit is not one, as GDI+ reports it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="recording"/>
    /// </example>
    public Metafile(string fileName, IntPtr referenceHdc, EmfType type)
        : this(MetafileRecording.Create(fileName, null, 1, null, MetafileFrameUnit.GdiCompatible, type, null))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class that records into a file.</summary>
    /// <param name="fileName">The file to write, created at once and written when the recording Graphics is disposed.</param>
    /// <param name="referenceHdc">Ignored: Egami records for a 96 DPI screen, whatever device context is given, <see cref="IntPtr.Zero"/> included.</param>
    /// <param name="frameRect">The frame, the part of the drawing the metafile shows; in <see cref="MetafileFrameUnit.GdiCompatible"/> units, 0.01 mm, unless a unit is given.</param>
    /// <exception cref="ArgumentException">The EMF type or frame unit is not one, as GDI+ reports it.</exception>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The frame is negative, or empty in a unit other than GdiCompatible, as GDI+ reports it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="recording"/>
    /// </example>
    public Metafile(string fileName, IntPtr referenceHdc, RectangleF frameRect)
        : this(MetafileRecording.Create(fileName, null, 1, frameRect, MetafileFrameUnit.GdiCompatible, EmfType.EmfPlusDual, null))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class that records into a file.</summary>
    /// <param name="fileName">The file to write, created at once and written when the recording Graphics is disposed.</param>
    /// <param name="referenceHdc">Ignored: Egami records for a 96 DPI screen, whatever device context is given, <see cref="IntPtr.Zero"/> included.</param>
    /// <param name="frameRect">The frame, the part of the drawing the metafile shows; in <see cref="MetafileFrameUnit.GdiCompatible"/> units, 0.01 mm, unless a unit is given.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    /// <exception cref="ArgumentException">The EMF type or frame unit is not one, as GDI+ reports it.</exception>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The frame is negative, or empty in a unit other than GdiCompatible, as GDI+ reports it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="recording"/>
    /// </example>
    public Metafile(string fileName, IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit)
        : this(MetafileRecording.Create(fileName, null, 1, frameRect, frameUnit, EmfType.EmfPlusDual, null))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class that records into a file.</summary>
    /// <param name="fileName">The file to write, created at once and written when the recording Graphics is disposed.</param>
    /// <param name="referenceHdc">Ignored: Egami records for a 96 DPI screen, whatever device context is given, <see cref="IntPtr.Zero"/> included.</param>
    /// <param name="frameRect">The frame, the part of the drawing the metafile shows; in <see cref="MetafileFrameUnit.GdiCompatible"/> units, 0.01 mm, unless a unit is given.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    /// <param name="type">The kind of EMF: GDI records only, EMF+ records only, or both.</param>
    /// <exception cref="ArgumentException">The EMF type or frame unit is not one, as GDI+ reports it.</exception>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The frame is negative, or empty in a unit other than GdiCompatible, as GDI+ reports it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="recording"/>
    /// </example>
    public Metafile(string fileName, IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit, EmfType type)
        : this(MetafileRecording.Create(fileName, null, 1, frameRect, frameUnit, type, null))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class that records into a file.</summary>
    /// <param name="fileName">The file to write, created at once and written when the recording Graphics is disposed.</param>
    /// <param name="referenceHdc">Ignored: Egami records for a 96 DPI screen, whatever device context is given, <see cref="IntPtr.Zero"/> included.</param>
    /// <param name="frameRect">The frame, the part of the drawing the metafile shows; in <see cref="MetafileFrameUnit.GdiCompatible"/> units, 0.01 mm, unless a unit is given.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    /// <param name="desc">The description stored in the header, or null.</param>
    /// <exception cref="ArgumentException">The EMF type or frame unit is not one, as GDI+ reports it.</exception>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The frame is negative, or empty in a unit other than GdiCompatible, as GDI+ reports it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="recording"/>
    /// </example>
    public Metafile(string fileName, IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit, string desc)
        : this(MetafileRecording.Create(fileName, null, 1, frameRect, frameUnit, EmfType.EmfPlusDual, desc))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class that records into a file.</summary>
    /// <param name="fileName">The file to write, created at once and written when the recording Graphics is disposed.</param>
    /// <param name="referenceHdc">Ignored: Egami records for a 96 DPI screen, whatever device context is given, <see cref="IntPtr.Zero"/> included.</param>
    /// <param name="frameRect">The frame, the part of the drawing the metafile shows; in <see cref="MetafileFrameUnit.GdiCompatible"/> units, 0.01 mm, unless a unit is given.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    /// <param name="type">The kind of EMF: GDI records only, EMF+ records only, or both.</param>
    /// <param name="description">The description stored in the header, or null.</param>
    /// <exception cref="ArgumentException">The EMF type or frame unit is not one, as GDI+ reports it.</exception>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The frame is negative, or empty in a unit other than GdiCompatible, as GDI+ reports it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="recording"/>
    /// </example>
    public Metafile(string fileName, IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit, EmfType type, string description)
        : this(MetafileRecording.Create(fileName, null, 1, frameRect, frameUnit, type, description))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class that records into a file.</summary>
    /// <param name="fileName">The file to write, created at once and written when the recording Graphics is disposed.</param>
    /// <param name="referenceHdc">Ignored: Egami records for a 96 DPI screen, whatever device context is given, <see cref="IntPtr.Zero"/> included.</param>
    /// <param name="frameRect">The frame, the part of the drawing the metafile shows; in <see cref="MetafileFrameUnit.GdiCompatible"/> units, 0.01 mm, unless a unit is given.</param>
    /// <exception cref="ArgumentException">The EMF type or frame unit is not one, as GDI+ reports it.</exception>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The frame is negative, or empty in a unit other than GdiCompatible, as GDI+ reports it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="recording"/>
    /// </example>
    public Metafile(string fileName, IntPtr referenceHdc, Rectangle frameRect)
        : this(MetafileRecording.Create(fileName, null, 1, frameRect, MetafileFrameUnit.GdiCompatible, EmfType.EmfPlusDual, null))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class that records into a file.</summary>
    /// <param name="fileName">The file to write, created at once and written when the recording Graphics is disposed.</param>
    /// <param name="referenceHdc">Ignored: Egami records for a 96 DPI screen, whatever device context is given, <see cref="IntPtr.Zero"/> included.</param>
    /// <param name="frameRect">The frame, the part of the drawing the metafile shows; in <see cref="MetafileFrameUnit.GdiCompatible"/> units, 0.01 mm, unless a unit is given.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    /// <exception cref="ArgumentException">The EMF type or frame unit is not one, as GDI+ reports it.</exception>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The frame is negative, or empty in a unit other than GdiCompatible, as GDI+ reports it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="recording"/>
    /// </example>
    public Metafile(string fileName, IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit)
        : this(MetafileRecording.Create(fileName, null, 1, frameRect, frameUnit, EmfType.EmfPlusDual, null))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class that records into a file.</summary>
    /// <param name="fileName">The file to write, created at once and written when the recording Graphics is disposed.</param>
    /// <param name="referenceHdc">Ignored: Egami records for a 96 DPI screen, whatever device context is given, <see cref="IntPtr.Zero"/> included.</param>
    /// <param name="frameRect">The frame, the part of the drawing the metafile shows; in <see cref="MetafileFrameUnit.GdiCompatible"/> units, 0.01 mm, unless a unit is given.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    /// <param name="type">The kind of EMF: GDI records only, EMF+ records only, or both.</param>
    /// <exception cref="ArgumentException">The EMF type or frame unit is not one, as GDI+ reports it.</exception>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The frame is negative, or empty in a unit other than GdiCompatible, as GDI+ reports it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="recording"/>
    /// </example>
    public Metafile(string fileName, IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit, EmfType type)
        : this(MetafileRecording.Create(fileName, null, 1, frameRect, frameUnit, type, null))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class that records into a file.</summary>
    /// <param name="fileName">The file to write, created at once and written when the recording Graphics is disposed.</param>
    /// <param name="referenceHdc">Ignored: Egami records for a 96 DPI screen, whatever device context is given, <see cref="IntPtr.Zero"/> included.</param>
    /// <param name="frameRect">The frame, the part of the drawing the metafile shows; in <see cref="MetafileFrameUnit.GdiCompatible"/> units, 0.01 mm, unless a unit is given.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    /// <param name="description">The description stored in the header, or null.</param>
    /// <exception cref="ArgumentException">The EMF type or frame unit is not one, as GDI+ reports it.</exception>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The frame is negative, or empty in a unit other than GdiCompatible, as GDI+ reports it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="recording"/>
    /// </example>
    public Metafile(string fileName, IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit, string description)
        : this(MetafileRecording.Create(fileName, null, 1, frameRect, frameUnit, EmfType.EmfPlusDual, description))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class that records into a stream. Without a frame, the frame is the bounds of what is drawn.</summary>
    /// <param name="stream">The stream to write to, from its position when the recording Graphics is disposed; left open.</param>
    /// <param name="referenceHdc">Ignored: Egami records for a 96 DPI screen, whatever device context is given, <see cref="IntPtr.Zero"/> included.</param>
    /// <exception cref="ArgumentException">The EMF type or frame unit is not one, as GDI+ reports it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="recording"/>
    /// </example>
    public Metafile(Stream stream, IntPtr referenceHdc)
        : this(MetafileRecording.Create(null, stream, 2, null, MetafileFrameUnit.GdiCompatible, EmfType.EmfPlusDual, null))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class that records into a stream. Without a frame, the frame is the bounds of what is drawn.</summary>
    /// <param name="stream">The stream to write to, from its position when the recording Graphics is disposed; left open.</param>
    /// <param name="referenceHdc">Ignored: Egami records for a 96 DPI screen, whatever device context is given, <see cref="IntPtr.Zero"/> included.</param>
    /// <param name="type">The kind of EMF: GDI records only, EMF+ records only, or both.</param>
    /// <exception cref="ArgumentException">The EMF type or frame unit is not one, as GDI+ reports it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="recording"/>
    /// </example>
    public Metafile(Stream stream, IntPtr referenceHdc, EmfType type)
        : this(MetafileRecording.Create(null, stream, 2, null, MetafileFrameUnit.GdiCompatible, type, null))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class that records into a stream.</summary>
    /// <param name="stream">The stream to write to, from its position when the recording Graphics is disposed; left open.</param>
    /// <param name="referenceHdc">Ignored: Egami records for a 96 DPI screen, whatever device context is given, <see cref="IntPtr.Zero"/> included.</param>
    /// <param name="frameRect">The frame, the part of the drawing the metafile shows; in <see cref="MetafileFrameUnit.GdiCompatible"/> units, 0.01 mm, unless a unit is given.</param>
    /// <exception cref="ArgumentException">The EMF type or frame unit is not one, as GDI+ reports it.</exception>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The frame is negative, or empty in a unit other than GdiCompatible, as GDI+ reports it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="recording"/>
    /// </example>
    public Metafile(Stream stream, IntPtr referenceHdc, RectangleF frameRect)
        : this(MetafileRecording.Create(null, stream, 2, frameRect, MetafileFrameUnit.GdiCompatible, EmfType.EmfPlusDual, null))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class that records into a stream.</summary>
    /// <param name="stream">The stream to write to, from its position when the recording Graphics is disposed; left open.</param>
    /// <param name="referenceHdc">Ignored: Egami records for a 96 DPI screen, whatever device context is given, <see cref="IntPtr.Zero"/> included.</param>
    /// <param name="frameRect">The frame, the part of the drawing the metafile shows; in <see cref="MetafileFrameUnit.GdiCompatible"/> units, 0.01 mm, unless a unit is given.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    /// <exception cref="ArgumentException">The EMF type or frame unit is not one, as GDI+ reports it.</exception>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The frame is negative, or empty in a unit other than GdiCompatible, as GDI+ reports it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="recording"/>
    /// </example>
    public Metafile(Stream stream, IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit)
        : this(MetafileRecording.Create(null, stream, 2, frameRect, frameUnit, EmfType.EmfPlusDual, null))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class that records into a stream.</summary>
    /// <param name="stream">The stream to write to, from its position when the recording Graphics is disposed; left open.</param>
    /// <param name="referenceHdc">Ignored: Egami records for a 96 DPI screen, whatever device context is given, <see cref="IntPtr.Zero"/> included.</param>
    /// <param name="frameRect">The frame, the part of the drawing the metafile shows; in <see cref="MetafileFrameUnit.GdiCompatible"/> units, 0.01 mm, unless a unit is given.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    /// <param name="type">The kind of EMF: GDI records only, EMF+ records only, or both.</param>
    /// <exception cref="ArgumentException">The EMF type or frame unit is not one, as GDI+ reports it.</exception>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The frame is negative, or empty in a unit other than GdiCompatible, as GDI+ reports it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="recording"/>
    /// </example>
    public Metafile(Stream stream, IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit, EmfType type)
        : this(MetafileRecording.Create(null, stream, 2, frameRect, frameUnit, type, null))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class that records into a stream.</summary>
    /// <param name="stream">The stream to write to, from its position when the recording Graphics is disposed; left open.</param>
    /// <param name="referenceHdc">Ignored: Egami records for a 96 DPI screen, whatever device context is given, <see cref="IntPtr.Zero"/> included.</param>
    /// <param name="frameRect">The frame, the part of the drawing the metafile shows; in <see cref="MetafileFrameUnit.GdiCompatible"/> units, 0.01 mm, unless a unit is given.</param>
    /// <exception cref="ArgumentException">The EMF type or frame unit is not one, as GDI+ reports it.</exception>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The frame is negative, or empty in a unit other than GdiCompatible, as GDI+ reports it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="recording"/>
    /// </example>
    public Metafile(Stream stream, IntPtr referenceHdc, Rectangle frameRect)
        : this(MetafileRecording.Create(null, stream, 2, frameRect, MetafileFrameUnit.GdiCompatible, EmfType.EmfPlusDual, null))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class that records into a stream.</summary>
    /// <param name="stream">The stream to write to, from its position when the recording Graphics is disposed; left open.</param>
    /// <param name="referenceHdc">Ignored: Egami records for a 96 DPI screen, whatever device context is given, <see cref="IntPtr.Zero"/> included.</param>
    /// <param name="frameRect">The frame, the part of the drawing the metafile shows; in <see cref="MetafileFrameUnit.GdiCompatible"/> units, 0.01 mm, unless a unit is given.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    /// <exception cref="ArgumentException">The EMF type or frame unit is not one, as GDI+ reports it.</exception>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The frame is negative, or empty in a unit other than GdiCompatible, as GDI+ reports it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="recording"/>
    /// </example>
    public Metafile(Stream stream, IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit)
        : this(MetafileRecording.Create(null, stream, 2, frameRect, frameUnit, EmfType.EmfPlusDual, null))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class that records into a stream.</summary>
    /// <param name="stream">The stream to write to, from its position when the recording Graphics is disposed; left open.</param>
    /// <param name="referenceHdc">Ignored: Egami records for a 96 DPI screen, whatever device context is given, <see cref="IntPtr.Zero"/> included.</param>
    /// <param name="frameRect">The frame, the part of the drawing the metafile shows; in <see cref="MetafileFrameUnit.GdiCompatible"/> units, 0.01 mm, unless a unit is given.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    /// <param name="type">The kind of EMF: GDI records only, EMF+ records only, or both.</param>
    /// <exception cref="ArgumentException">The EMF type or frame unit is not one, as GDI+ reports it.</exception>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The frame is negative, or empty in a unit other than GdiCompatible, as GDI+ reports it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="recording"/>
    /// </example>
    public Metafile(Stream stream, IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit, EmfType type)
        : this(MetafileRecording.Create(null, stream, 2, frameRect, frameUnit, type, null))
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

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class that records into memory. Without a frame, the frame is the bounds of what is drawn.</summary>
    /// <param name="referenceHdc">Ignored: Egami records for a 96 DPI screen, whatever device context is given, <see cref="IntPtr.Zero"/> included.</param>
    /// <param name="emfType">The kind of EMF: GDI records only, EMF+ records only, or both.</param>
    /// <param name="description">The description stored in the header, or null.</param>
    /// <exception cref="ArgumentException">The EMF type or frame unit is not one, as GDI+ reports it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="recording"/>
    /// </example>
    public Metafile(IntPtr referenceHdc, EmfType emfType, string description)
        : this(MetafileRecording.Create(null, null, 0, null, MetafileFrameUnit.GdiCompatible, emfType, description))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class that records into memory.</summary>
    /// <param name="referenceHdc">Ignored: Egami records for a 96 DPI screen, whatever device context is given, <see cref="IntPtr.Zero"/> included.</param>
    /// <param name="frameRect">The frame, the part of the drawing the metafile shows; in <see cref="MetafileFrameUnit.GdiCompatible"/> units, 0.01 mm, unless a unit is given.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    /// <param name="type">The kind of EMF: GDI records only, EMF+ records only, or both.</param>
    /// <param name="desc">The description stored in the header, or null.</param>
    /// <exception cref="ArgumentException">The EMF type or frame unit is not one, as GDI+ reports it.</exception>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The frame is negative, or empty in a unit other than GdiCompatible, as GDI+ reports it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="recording"/>
    /// </example>
    public Metafile(IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit, EmfType type, string desc)
        : this(MetafileRecording.Create(null, null, 0, frameRect, frameUnit, type, desc))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class that records into a file. Without a frame, the frame is the bounds of what is drawn.</summary>
    /// <param name="fileName">The file to write, created at once and written when the recording Graphics is disposed.</param>
    /// <param name="referenceHdc">Ignored: Egami records for a 96 DPI screen, whatever device context is given, <see cref="IntPtr.Zero"/> included.</param>
    /// <param name="type">The kind of EMF: GDI records only, EMF+ records only, or both.</param>
    /// <param name="description">The description stored in the header, or null.</param>
    /// <exception cref="ArgumentException">The EMF type or frame unit is not one, as GDI+ reports it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="recording"/>
    /// </example>
    public Metafile(string fileName, IntPtr referenceHdc, EmfType type, string description)
        : this(MetafileRecording.Create(fileName, null, 1, null, MetafileFrameUnit.GdiCompatible, type, description))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class that records into a file.</summary>
    /// <param name="fileName">The file to write, created at once and written when the recording Graphics is disposed.</param>
    /// <param name="referenceHdc">Ignored: Egami records for a 96 DPI screen, whatever device context is given, <see cref="IntPtr.Zero"/> included.</param>
    /// <param name="frameRect">The frame, the part of the drawing the metafile shows; in <see cref="MetafileFrameUnit.GdiCompatible"/> units, 0.01 mm, unless a unit is given.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    /// <param name="type">The kind of EMF: GDI records only, EMF+ records only, or both.</param>
    /// <param name="description">The description stored in the header, or null.</param>
    /// <exception cref="ArgumentException">The EMF type or frame unit is not one, as GDI+ reports it.</exception>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The frame is negative, or empty in a unit other than GdiCompatible, as GDI+ reports it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="recording"/>
    /// </example>
    public Metafile(string fileName, IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit, EmfType type, string description)
        : this(MetafileRecording.Create(fileName, null, 1, frameRect, frameUnit, type, description))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class that records into a stream. Without a frame, the frame is the bounds of what is drawn.</summary>
    /// <param name="stream">The stream to write to, from its position when the recording Graphics is disposed; left open.</param>
    /// <param name="referenceHdc">Ignored: Egami records for a 96 DPI screen, whatever device context is given, <see cref="IntPtr.Zero"/> included.</param>
    /// <param name="type">The kind of EMF: GDI records only, EMF+ records only, or both.</param>
    /// <param name="description">The description stored in the header, or null.</param>
    /// <exception cref="ArgumentException">The EMF type or frame unit is not one, as GDI+ reports it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="recording"/>
    /// </example>
    public Metafile(Stream stream, IntPtr referenceHdc, EmfType type, string description)
        : this(MetafileRecording.Create(null, stream, 2, null, MetafileFrameUnit.GdiCompatible, type, description))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class that records into a stream.</summary>
    /// <param name="stream">The stream to write to, from its position when the recording Graphics is disposed; left open.</param>
    /// <param name="referenceHdc">Ignored: Egami records for a 96 DPI screen, whatever device context is given, <see cref="IntPtr.Zero"/> included.</param>
    /// <param name="frameRect">The frame, the part of the drawing the metafile shows; in <see cref="MetafileFrameUnit.GdiCompatible"/> units, 0.01 mm, unless a unit is given.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    /// <param name="type">The kind of EMF: GDI records only, EMF+ records only, or both.</param>
    /// <param name="description">The description stored in the header, or null.</param>
    /// <exception cref="ArgumentException">The EMF type or frame unit is not one, as GDI+ reports it.</exception>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The frame is negative, or empty in a unit other than GdiCompatible, as GDI+ reports it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="recording"/>
    /// </example>
    public Metafile(Stream stream, IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit, EmfType type, string description)
        : this(MetafileRecording.Create(null, stream, 2, frameRect, frameUnit, type, description))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class that records into a stream.</summary>
    /// <param name="stream">The stream to write to, from its position when the recording Graphics is disposed; left open.</param>
    /// <param name="referenceHdc">Ignored: Egami records for a 96 DPI screen, whatever device context is given, <see cref="IntPtr.Zero"/> included.</param>
    /// <param name="frameRect">The frame, the part of the drawing the metafile shows; in <see cref="MetafileFrameUnit.GdiCompatible"/> units, 0.01 mm, unless a unit is given.</param>
    /// <param name="frameUnit">The frame's unit.</param>
    /// <param name="type">The kind of EMF: GDI records only, EMF+ records only, or both.</param>
    /// <param name="description">The description stored in the header, or null.</param>
    /// <exception cref="ArgumentException">The EMF type or frame unit is not one, as GDI+ reports it.</exception>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The frame is negative, or empty in a unit other than GdiCompatible, as GDI+ reports it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="recording"/>
    /// </example>
    public Metafile(Stream stream, IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit, EmfType type, string description)
        : this(MetafileRecording.Create(null, stream, 2, frameRect, frameUnit, type, description))
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
