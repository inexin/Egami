namespace Egami.Fonts;

/// <summary>
/// The embedding permissions a face declares in its OS/2 fsType field. Egami reports them and never enforces them; a caller that
/// embeds fonts, such as a PDF writer, decides what to do.
/// </summary>
[Flags]
public enum FontEmbedding
{
    /// <summary>No restriction: the face may be embedded and installed (fsType 0).</summary>
    Installable = 0,

    /// <summary>The face must not be embedded, changed or exchanged without the owner's permission (bit 1).</summary>
    Restricted = 0x0002,

    /// <summary>The face may be embedded for previewing and printing only (bit 2).</summary>
    PreviewAndPrint = 0x0004,

    /// <summary>The face may be embedded in documents that are edited (bit 3).</summary>
    Editable = 0x0008,

    /// <summary>The face must not be subsetted before embedding (bit 8).</summary>
    NoSubsetting = 0x0100,

    /// <summary>Only bitmaps may be embedded, not outlines (bit 9).</summary>
    BitmapOnly = 0x0200,
}
