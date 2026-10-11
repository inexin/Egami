namespace Egami.Metadata;

/// <summary>An EXIF tag: its number and the IFD it lives in. The named keys cover the tags most programs read and write.</summary>
/// <param name="Tag">The tag number.</param>
/// <param name="Ifd">The IFD the tag is in.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/AnimationAndMetadata.cs" region="exif"/>
/// </example>
public readonly record struct ExifKey(ushort Tag, ExifIfd Ifd)
{
    /// <summary>A title or description of the image (IFD0 0x010E).</summary>
    public static readonly ExifKey ImageDescription = new(0x010E, ExifIfd.Primary);

    /// <summary>The camera maker (IFD0 0x010F).</summary>
    public static readonly ExifKey Make = new(0x010F, ExifIfd.Primary);

    /// <summary>The camera model (IFD0 0x0110).</summary>
    public static readonly ExifKey Model = new(0x0110, ExifIfd.Primary);

    /// <summary>How the image is turned (IFD0 0x0112); see <see cref="ImageOrientation"/>.</summary>
    public static readonly ExifKey Orientation = new(0x0112, ExifIfd.Primary);

    /// <summary>Horizontal resolution (IFD0 0x011A).</summary>
    public static readonly ExifKey XResolution = new(0x011A, ExifIfd.Primary);

    /// <summary>Vertical resolution (IFD0 0x011B).</summary>
    public static readonly ExifKey YResolution = new(0x011B, ExifIfd.Primary);

    /// <summary>The unit of the resolutions: 2 inches, 3 centimeters (IFD0 0x0128).</summary>
    public static readonly ExifKey ResolutionUnit = new(0x0128, ExifIfd.Primary);

    /// <summary>The software that made or last edited the file (IFD0 0x0131).</summary>
    public static readonly ExifKey Software = new(0x0131, ExifIfd.Primary);

    /// <summary>When the file was last changed, as "YYYY:MM:DD HH:MM:SS" (IFD0 0x0132).</summary>
    public static readonly ExifKey DateTime = new(0x0132, ExifIfd.Primary);

    /// <summary>Who made the image (IFD0 0x013B).</summary>
    public static readonly ExifKey Artist = new(0x013B, ExifIfd.Primary);

    /// <summary>The copyright notice (IFD0 0x8298).</summary>
    public static readonly ExifKey Copyright = new(0x8298, ExifIfd.Primary);

    /// <summary>The title Windows Explorer shows, as UTF-16 (IFD0 0x9C9B, XPTitle).</summary>
    public static readonly ExifKey XPTitle = new(0x9C9B, ExifIfd.Primary);

    /// <summary>The comments Windows Explorer shows, as UTF-16 (IFD0 0x9C9C, XPComment).</summary>
    public static readonly ExifKey XPComment = new(0x9C9C, ExifIfd.Primary);

    /// <summary>The authors Windows Explorer shows, semicolon separated, as UTF-16 (IFD0 0x9C9D, XPAuthor).</summary>
    public static readonly ExifKey XPAuthor = new(0x9C9D, ExifIfd.Primary);

    /// <summary>The tags Windows Explorer shows, semicolon separated, as UTF-16 (IFD0 0x9C9E, XPKeywords).</summary>
    public static readonly ExifKey XPKeywords = new(0x9C9E, ExifIfd.Primary);

    /// <summary>The subject Windows Explorer shows, as UTF-16 (IFD0 0x9C9F, XPSubject).</summary>
    public static readonly ExifKey XPSubject = new(0x9C9F, ExifIfd.Primary);

    /// <summary>The pointer to the GPS sub-IFD (IFD0 0x8825). Removing it removes the whole GPS IFD.</summary>
    public static readonly ExifKey GpsInfo = new(0x8825, ExifIfd.Primary);

    /// <summary>The pointer to the EXIF sub-IFD (IFD0 0x8769). Removing it removes the whole EXIF IFD, interoperability IFD included.</summary>
    public static readonly ExifKey ExifInfo = new(0x8769, ExifIfd.Primary);

    /// <summary>Exposure time in seconds (EXIF 0x829A).</summary>
    public static readonly ExifKey ExposureTime = new(0x829A, ExifIfd.Exif);

    /// <summary>The f-number (EXIF 0x829D).</summary>
    public static readonly ExifKey FNumber = new(0x829D, ExifIfd.Exif);

    /// <summary>The exposure program (EXIF 0x8822).</summary>
    public static readonly ExifKey ExposureProgram = new(0x8822, ExifIfd.Exif);

    /// <summary>The ISO speed (EXIF 0x8827, PhotographicSensitivity).</summary>
    public static readonly ExifKey IsoSpeed = new(0x8827, ExifIfd.Exif);

    /// <summary>The EXIF version, four ASCII digits such as "0232" (EXIF 0x9000).</summary>
    public static readonly ExifKey ExifVersion = new(0x9000, ExifIfd.Exif);

    /// <summary>When the photo was taken, as "YYYY:MM:DD HH:MM:SS" (EXIF 0x9003).</summary>
    public static readonly ExifKey DateTimeOriginal = new(0x9003, ExifIfd.Exif);

    /// <summary>When the photo was stored digitally (EXIF 0x9004).</summary>
    public static readonly ExifKey DateTimeDigitized = new(0x9004, ExifIfd.Exif);

    /// <summary>The UTC offset of <see cref="DateTimeOriginal"/>, such as "+02:00" (EXIF 0x9011).</summary>
    public static readonly ExifKey OffsetTimeOriginal = new(0x9011, ExifIfd.Exif);

    /// <summary>The exposure bias in EV (EXIF 0x9204).</summary>
    public static readonly ExifKey ExposureBias = new(0x9204, ExifIfd.Exif);

    /// <summary>The metering mode (EXIF 0x9207).</summary>
    public static readonly ExifKey MeteringMode = new(0x9207, ExifIfd.Exif);

    /// <summary>Whether and how the flash fired (EXIF 0x9209).</summary>
    public static readonly ExifKey Flash = new(0x9209, ExifIfd.Exif);

    /// <summary>The focal length in millimeters (EXIF 0x920A).</summary>
    public static readonly ExifKey FocalLength = new(0x920A, ExifIfd.Exif);

    /// <summary>The camera maker's own data, kept as an opaque block (EXIF 0x927C).</summary>
    public static readonly ExifKey MakerNote = new(0x927C, ExifIfd.Exif);

    /// <summary>A comment, with an 8-byte character-code prefix (EXIF 0x9286).</summary>
    public static readonly ExifKey UserComment = new(0x9286, ExifIfd.Exif);

    /// <summary>The color space: 1 sRGB, 0xFFFF uncalibrated (EXIF 0xA001).</summary>
    public static readonly ExifKey ColorSpace = new(0xA001, ExifIfd.Exif);

    /// <summary>The image width the camera recorded (EXIF 0xA002).</summary>
    public static readonly ExifKey PixelXDimension = new(0xA002, ExifIfd.Exif);

    /// <summary>The image height the camera recorded (EXIF 0xA003).</summary>
    public static readonly ExifKey PixelYDimension = new(0xA003, ExifIfd.Exif);

    /// <summary>The focal length for a 35 mm camera (EXIF 0xA405).</summary>
    public static readonly ExifKey FocalLengthIn35mmFilm = new(0xA405, ExifIfd.Exif);

    /// <summary>The lens maker (EXIF 0xA433).</summary>
    public static readonly ExifKey LensMake = new(0xA433, ExifIfd.Exif);

    /// <summary>The lens model (EXIF 0xA434).</summary>
    public static readonly ExifKey LensModel = new(0xA434, ExifIfd.Exif);

    /// <summary>"N" or "S" (GPS 0x0001).</summary>
    public static readonly ExifKey GpsLatitudeRef = new(0x0001, ExifIfd.Gps);

    /// <summary>Degrees, minutes and seconds, as three rationals (GPS 0x0002).</summary>
    public static readonly ExifKey GpsLatitude = new(0x0002, ExifIfd.Gps);

    /// <summary>"E" or "W" (GPS 0x0003).</summary>
    public static readonly ExifKey GpsLongitudeRef = new(0x0003, ExifIfd.Gps);

    /// <summary>Degrees, minutes and seconds, as three rationals (GPS 0x0004).</summary>
    public static readonly ExifKey GpsLongitude = new(0x0004, ExifIfd.Gps);

    /// <summary>0 above sea level, 1 below (GPS 0x0005).</summary>
    public static readonly ExifKey GpsAltitudeRef = new(0x0005, ExifIfd.Gps);

    /// <summary>Meters, as a rational (GPS 0x0006).</summary>
    public static readonly ExifKey GpsAltitude = new(0x0006, ExifIfd.Gps);

    /// <summary>How the position was found, such as "GPS" or "NETWORK", with an 8-byte character-code prefix (GPS 0x001B).</summary>
    public static readonly ExifKey GpsProcessingMethod = new(0x001B, ExifIfd.Gps);

    /// <summary>The name of the area, with an 8-byte character-code prefix (GPS 0x001C).</summary>
    public static readonly ExifKey GpsAreaInformation = new(0x001C, ExifIfd.Gps);

    /// <inheritdoc/>
    public override string ToString() => $"{Ifd} 0x{Tag:X4}";
}
