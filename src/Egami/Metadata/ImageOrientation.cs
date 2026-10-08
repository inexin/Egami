using System.Buffers.Binary;

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

/// <summary>
/// Reads only the Orientation tag from IFD0 of an EXIF (TIFF) block, bounds-checked, for <c>Probe</c>. The full typed reader comes
/// with ExifData.
/// </summary>
internal static class ExifOrientation
{
    private const ushort OrientationTag = 0x0112;

    /// <summary>Returns the orientation in <paramref name="tiff"/>, or <see cref="ImageOrientation.Normal"/> if it is missing or invalid.</summary>
    internal static ImageOrientation Read(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8)
        {
            return ImageOrientation.Normal;
        }

        bool little;
        if (tiff[0] == (byte)'I' && tiff[1] == (byte)'I')
        {
            little = true;
        }
        else if (tiff[0] == (byte)'M' && tiff[1] == (byte)'M')
        {
            little = false;
        }
        else
        {
            return ImageOrientation.Normal;
        }

        if (U16(tiff[2..], little) != 42)
        {
            return ImageOrientation.Normal;
        }

        uint ifd = U32(tiff[4..], little);
        if (ifd > (uint)tiff.Length - 2)
        {
            return ImageOrientation.Normal;
        }

        int count = U16(tiff[(int)ifd..], little);
        for (int i = 0; i < count; i++)
        {
            long entry = ifd + 2 + (12L * i);
            if (entry + 12 > tiff.Length)
            {
                break;
            }

            ReadOnlySpan<byte> e = tiff.Slice((int)entry, 12);
            if (U16(e, little) != OrientationTag)
            {
                continue;
            }

            // SHORT (type 3), count 1: the value sits in the first two bytes of the value field.
            ushort type = U16(e[2..], little);
            uint n = U32(e[4..], little);
            int value = type == 3 && n >= 1 ? U16(e[8..], little) : 0;
            return value is >= 1 and <= 8 ? (ImageOrientation)value : ImageOrientation.Normal;
        }

        return ImageOrientation.Normal;
    }

    private static ushort U16(ReadOnlySpan<byte> b, bool little)
    {
        return little ? BinaryPrimitives.ReadUInt16LittleEndian(b) : BinaryPrimitives.ReadUInt16BigEndian(b);
    }

    private static uint U32(ReadOnlySpan<byte> b, bool little)
    {
        return little ? BinaryPrimitives.ReadUInt32LittleEndian(b) : BinaryPrimitives.ReadUInt32BigEndian(b);
    }
}
