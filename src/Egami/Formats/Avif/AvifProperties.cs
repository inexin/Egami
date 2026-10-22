using System.Buffers.Binary;
using System.Text;

using Egami.Formats.Heif;

namespace Egami.Formats.Avif;

/// <summary>The av1C property (AV1 Codec ISO Media File Format Binding, section 2.3): the AV1 configuration of an item.</summary>
internal sealed record Av1Config(int Profile, int Level, int Tier, int BitDepth, bool Monochrome, int SubsamplingX, int SubsamplingY,
    int ChromaSamplePosition, ReadOnlyMemory<byte> ConfigObus)
{
    /// <summary>Gets the chroma format.</summary>
    internal Pixels.ChromaFormat Chroma => Monochrome ? Pixels.ChromaFormat.Yuv400
        : SubsamplingX == 1 && SubsamplingY == 1 ? Pixels.ChromaFormat.Yuv420
        : SubsamplingX == 1 ? Pixels.ChromaFormat.Yuv422 : Pixels.ChromaFormat.Yuv444;
}

/// <summary>An nclx colr property: CICP code points (ITU-T H.273) and the range flag.</summary>
internal readonly record struct NclxColor(int Primaries, int Transfer, int Matrix, bool FullRange);

/// <summary>A clap property: the clean aperture as fractions (ISO/IEC 14496-12, 12.1.4).</summary>
internal readonly record struct CleanAperture(int WidthN, int WidthD, int HeightN, int HeightD, int HorizOffN, int HorizOffD, int VertOffN, int VertOffD)
{
    /// <summary>
    /// Gets the crop rectangle the aperture describes in an image of the size, or <see langword="null"/> when it is not a whole
    /// number of pixels inside the image: the aperture is centered at (W - 1) / 2 + horizOff, (H - 1) / 2 + vertOff.
    /// </summary>
    internal (int X, int Y, int Width, int Height)? CropRectangle(int imageWidth, int imageHeight)
    {
        if (WidthD <= 0 || HeightD <= 0 || HorizOffD <= 0 || VertOffD <= 0 || WidthN < 0 || HeightN < 0
            || WidthN % WidthD != 0 || HeightN % HeightD != 0)
        {
            return null;
        }

        long width = WidthN / WidthD, height = HeightN / HeightD;

        // x0 = horizOff + (W - width) / 2, kept exact as a fraction over 2 * horizOffD.
        long xNum = (2L * HorizOffN) + ((imageWidth - width) * HorizOffD), xDen = 2L * HorizOffD;
        long yNum = (2L * VertOffN) + ((imageHeight - height) * VertOffD), yDen = 2L * VertOffD;
        if (width == 0 || height == 0 || xNum % xDen != 0 || yNum % yDen != 0)
        {
            return null;
        }

        long x = xNum / xDen, y = yNum / yDen;
        return x < 0 || y < 0 || x + width > imageWidth || y + height > imageHeight ? null : ((int)x, (int)y, (int)width, (int)height);
    }
}

/// <summary>An mdcv property: the mastering display's primaries and white point in 0.00002 units, and its luminance in 0.0001 cd/m².</summary>
internal readonly record struct MasteringDisplay(ushort[] PrimariesXY, ushort WhiteX, ushort WhiteY, uint MaxLuminance, uint MinLuminance);

/// <summary>Reads the HEIF and AVIF properties the AVIF reader understands.</summary>
internal static class AvifProperties
{
    internal static readonly uint Av1C = IsoTypes.Of("av1C");
    internal static readonly uint Ispe = IsoTypes.Of("ispe");
    internal static readonly uint Pixi = IsoTypes.Of("pixi");
    internal static readonly uint Colr = IsoTypes.Of("colr");
    internal static readonly uint AuxC = IsoTypes.Of("auxC");
    internal static readonly uint Clli = IsoTypes.Of("clli");
    internal static readonly uint Mdcv = IsoTypes.Of("mdcv");
    internal static readonly uint Irot = IsoTypes.Of("irot");
    internal static readonly uint Imir = IsoTypes.Of("imir");
    internal static readonly uint Clap = IsoTypes.Of("clap");
    internal static readonly uint Pasp = IsoTypes.Of("pasp");
    internal static readonly uint A1op = IsoTypes.Of("a1op");
    internal static readonly uint A1lx = IsoTypes.Of("a1lx");
    internal static readonly uint Lsel = IsoTypes.Of("lsel");
    internal static readonly uint Cclv = IsoTypes.Of("cclv");
    internal static readonly uint Amve = IsoTypes.Of("amve");

    /// <summary>The property types the AVIF reader understands, so an essential one of them does not make an item unusable.</summary>
    internal static readonly HashSet<uint> Understood = [Av1C, Ispe, Pixi, Colr, AuxC, Clli, Mdcv, Irot, Imir, Clap, Pasp, A1op, A1lx, Lsel, Cclv, Amve];

    /// <summary>The auxiliary type of an alpha plane (ISO/IEC 23008-12 and the AVIF specification's older URN).</summary>
    internal const string AlphaUrn = "urn:mpeg:mpegB:cicp:systems:auxiliary:alpha";

    /// <summary>HEVC's alpha URN, which some AVIF writers used.</summary>
    internal const string HevcAlphaUrn = "urn:mpeg:hevc:2015:auxid:1";

    internal static Av1Config ReadAv1Config(HeifFile file, HeifProperty property)
    {
        ReadOnlySpan<byte> c = file.Content(property);
        if (c.Length < 4)
        {
            throw Corrupt(file, property, "is shorter than 4 bytes");
        }

        if (c[0] != 0x81)
        {
            throw Corrupt(file, property, $"has marker and version 0x{c[0]:X2}, not 0x81");
        }

        int profile = c[1] >> 5;
        bool high = (c[2] & 0x40) != 0, twelve = (c[2] & 0x20) != 0;
        return new Av1Config(profile, c[1] & 31, c[2] >> 7, twelve ? 12 : high ? 10 : 8, (c[2] & 0x10) != 0, (c[2] >> 3) & 1, (c[2] >> 2) & 1,
            c[2] & 3, file.Bytes.Slice(property.Box.ContentIndex + 4, c.Length - 4));
    }

    internal static (int Width, int Height) ReadSpatialExtents(HeifFile file, HeifProperty property)
    {
        ReadOnlySpan<byte> c = file.Content(property);
        if (c.Length < 12)
        {
            throw Corrupt(file, property, "is shorter than 12 bytes");
        }

        uint width = BinaryPrimitives.ReadUInt32BigEndian(c[4..]), height = BinaryPrimitives.ReadUInt32BigEndian(c[8..]);
        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
        {
            throw Corrupt(file, property, $"gives a size of {width} x {height}");
        }

        return ((int)width, (int)height);
    }

    /// <summary>Reads pixi: the bits of each channel.</summary>
    internal static byte[] ReadPixelInformation(HeifFile file, HeifProperty property)
    {
        ReadOnlySpan<byte> c = file.Content(property);
        if (c.Length < 5 || c.Length < 5 + c[4])
        {
            throw Corrupt(file, property, "is cut off");
        }

        return c.Slice(5, c[4]).ToArray();
    }

    /// <summary>Reads colr: an nclx color, or an ICC profile (rICC or prof); any other colour type gives neither.</summary>
    internal static (NclxColor? Nclx, ReadOnlyMemory<byte> Icc) ReadColor(HeifFile file, HeifProperty property)
    {
        ReadOnlySpan<byte> c = file.Content(property);
        if (c.Length < 4)
        {
            throw Corrupt(file, property, "is shorter than 4 bytes");
        }

        uint type = BinaryPrimitives.ReadUInt32BigEndian(c);
        if (type == IsoTypes.Of("nclx"))
        {
            if (c.Length < 11)
            {
                throw Corrupt(file, property, "is cut off");
            }

            return (new NclxColor(BinaryPrimitives.ReadUInt16BigEndian(c[4..]), BinaryPrimitives.ReadUInt16BigEndian(c[6..]),
                BinaryPrimitives.ReadUInt16BigEndian(c[8..]), (c[10] & 0x80) != 0), default);
        }

        return type == IsoTypes.Of("rICC") || type == IsoTypes.Of("prof")
            ? (null, file.Bytes.Slice(property.Box.ContentIndex + 4, c.Length - 4))
            : (null, default);
    }

    /// <summary>Reads auxC's auxiliary type URN.</summary>
    internal static string ReadAuxiliaryType(HeifFile file, HeifProperty property)
    {
        ReadOnlySpan<byte> c = file.Content(property);
        if (c.Length < 4)
        {
            throw Corrupt(file, property, "is shorter than 4 bytes");
        }

        ReadOnlySpan<byte> text = c[4..];
        int end = text.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? text : text[..end]);
    }

    /// <summary>Reads clli: the maximum content light level and maximum frame-average light level, in cd/m².</summary>
    internal static (int MaxCll, int MaxFall) ReadContentLightLevel(HeifFile file, HeifProperty property)
    {
        ReadOnlySpan<byte> c = file.Content(property);
        return c.Length < 4 ? throw Corrupt(file, property, "is shorter than 4 bytes")
            : (BinaryPrimitives.ReadUInt16BigEndian(c), BinaryPrimitives.ReadUInt16BigEndian(c[2..]));
    }

    internal static MasteringDisplay ReadMasteringDisplay(HeifFile file, HeifProperty property)
    {
        ReadOnlySpan<byte> c = file.Content(property);
        if (c.Length < 24)
        {
            throw Corrupt(file, property, "is shorter than 24 bytes");
        }

        ushort[] primaries = new ushort[6];
        for (int i = 0; i < 6; i++)
        {
            primaries[i] = BinaryPrimitives.ReadUInt16BigEndian(c[(2 * i)..]);
        }

        return new MasteringDisplay(primaries, BinaryPrimitives.ReadUInt16BigEndian(c[12..]), BinaryPrimitives.ReadUInt16BigEndian(c[14..]),
            BinaryPrimitives.ReadUInt32BigEndian(c[16..]), BinaryPrimitives.ReadUInt32BigEndian(c[20..]));
    }

    /// <summary>Reads irot: the anticlockwise rotation in quarter turns.</summary>
    internal static int ReadRotation(HeifFile file, HeifProperty property)
    {
        ReadOnlySpan<byte> c = file.Content(property);
        return c.Length < 1 ? throw Corrupt(file, property, "is empty") : c[0] & 3;
    }

    /// <summary>Reads imir: 0 mirrors top to bottom, 1 left to right (as libavif and the HEIF amendment name the axis).</summary>
    internal static int ReadMirror(HeifFile file, HeifProperty property)
    {
        ReadOnlySpan<byte> c = file.Content(property);
        return c.Length < 1 ? throw Corrupt(file, property, "is empty") : c[0] & 1;
    }

    internal static CleanAperture ReadCleanAperture(HeifFile file, HeifProperty property)
    {
        ReadOnlySpan<byte> c = file.Content(property);
        if (c.Length < 32)
        {
            throw Corrupt(file, property, "is shorter than 32 bytes");
        }

        Span<int> f = stackalloc int[8];
        for (int i = 0; i < 8; i++)
        {
            f[i] = BinaryPrimitives.ReadInt32BigEndian(c[(4 * i)..]);
        }

        return new CleanAperture(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7]);
    }

    private static CorruptDataException Corrupt(HeifFile file, HeifProperty property, string problem) =>
        new(file.FormatName, property.Box.StartIndex, $"The {IsoTypes.ToString(property.Type)} property {problem}.");
}
