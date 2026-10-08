namespace Egami.Metadata;

/// <summary>
/// How stored pixels must be turned to show the image upright: the EXIF Orientation tag (274). The value says which way the camera
/// was held; <c>AutoOrient</c> applies it.
/// </summary>
public enum ImageOrientation
{
    /// <summary>Stored upright (EXIF 1). Also used when a file says nothing.</summary>
    Normal = 1,

    /// <summary>Mirrored left to right (EXIF 2).</summary>
    FlipHorizontal = 2,

    /// <summary>Upside down (EXIF 3).</summary>
    Rotate180 = 3,

    /// <summary>Mirrored top to bottom (EXIF 4).</summary>
    FlipVertical = 4,

    /// <summary>Mirrored along the top-left to bottom-right diagonal (EXIF 5).</summary>
    Transpose = 5,

    /// <summary>Needs a 90° clockwise turn to show upright (EXIF 6), as from a phone held upright.</summary>
    Rotate90 = 6,

    /// <summary>Mirrored along the top-right to bottom-left diagonal (EXIF 7).</summary>
    Transverse = 7,

    /// <summary>Needs a 270° clockwise turn to show upright (EXIF 8).</summary>
    Rotate270 = 8,
}
