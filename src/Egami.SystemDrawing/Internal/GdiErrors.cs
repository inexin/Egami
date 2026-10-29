using System.Runtime.InteropServices;

namespace Egami.SystemDrawing.Internal;

/// <summary>The exceptions GDI+'s status codes turn into, with System.Drawing's messages, so code that catches them keeps working.</summary>
internal static class GdiErrors
{
    /// <summary>InvalidParameter: an argument GDI+ rejects.</summary>
    public static ArgumentException InvalidParameter() => new("Parameter is not valid.");

    /// <summary>GenericError, the error GDI+ gives when an object is in the wrong state or a save fails.</summary>
    public static ExternalException Generic() => new("A generic error occurred in GDI+.", unchecked((int)0x80004005));

    /// <summary>OutOfMemory, which GDI+ also gives for a file it cannot read.</summary>
    public static OutOfMemoryException OutOfMemory() => new("Out of memory.");

    /// <summary>The exception for an indexed bitmap's SetPixel.</summary>
    public static InvalidOperationException IndexedSetPixel() => new("SetPixel is not supported for images with indexed pixel formats.");

    /// <summary>A metafile recording member: Egami reads metafiles and does not write them.</summary>
    public static NotSupportedException Recording() => new("Egami.SystemDrawing reads metafiles but does not record them. Draw on a Bitmap, or write SVG or PDF with Egami.Drawing's canvases.");

    /// <summary>A Windows-only member: the message names what to use instead.</summary>
    public static PlatformNotSupportedException WindowsOnly(string member, string alternative) =>
        new($"{member} needs Windows GDI handles, which Egami.SystemDrawing does not use. {alternative}");
}

/// <summary>The diagnostic ids of the Windows-only members, one per group; suppress or raise them in .editorconfig.</summary>
internal static class Diagnostics
{
    /// <summary>Windows handles: HBITMAP, HICON, HDC, HWND, HRGN, HFONT and LOGFONT.</summary>
    public const string Handles = "EGSD0001";

    /// <summary>Screen capture: Graphics.CopyFromScreen.</summary>
    public const string Screen = "EGSD0002";

    /// <summary>Windows' own icons: SystemIcons, stock icons and icons extracted from files.</summary>
    public const string StockIcons = "EGSD0003";

    /// <summary>Metafile recording: the Metafile constructors that write a new metafile.</summary>
    public const string Recording = "EGSD0004";

    /// <summary>The page each id links to, with the id as the anchor.</summary>
    public const string Url = "https://inexin.github.io/Egami/v1/articles/guides/system-drawing.html#{0}";

    public const string HandlesMessage = "Windows GDI handles are not available in Egami.SystemDrawing; it always throws PlatformNotSupportedException. Use Bitmap.LockBits to reach the pixels.";

    public const string ScreenMessage = "Screen capture is not available in Egami.SystemDrawing; it always throws PlatformNotSupportedException.";

    public const string RecordingMessage = "Egami.SystemDrawing does not record metafiles; it always throws NotSupportedException. Draw on a Bitmap instead.";

    public const string StockIconsMessage = "Windows' own icons are not available in Egami.SystemDrawing; it always throws PlatformNotSupportedException. Load an icon file with new Icon(path).";
}
