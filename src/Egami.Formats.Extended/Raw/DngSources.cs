using System.Runtime.InteropServices;

using Egami.Colors;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Extended.Raw;

/// <summary>
/// Builds what a DNG is written from: a RAW file's frame and decoded sensor data, a mosaic raster with its
/// <see cref="RawDetails"/>, or any other raster as LinearRaw.
/// </summary>
internal static class DngSources
{
    /// <summary>
    /// Reads a RAW file for conversion: its frame, its sensor data as decoded, its EXIF and XMP. A DNG source keeps its IFD0 and
    /// raw IFD, whose tags the output copies.
    /// </summary>
    public static DngContent FromFile(RawFile file, EgamiInput input, RawWarnings warnings)
    {
        RawFrame frame = file.Frame;
        RawSensor sensor = frame.Sensor;
        if (sensor.Codec == RawCodec.CanonCrx)
        {
            throw new NotSupportedException("DNG: Egami cannot convert CR3 files yet; their CRX sensor data is not decoded for DNG writing.");
        }

        if (sensor.Samples is not (1 or 3) || (frame.Cfa is null && sensor.Samples != 3) || (frame.Cfa is not null && sensor.Samples != 1))
        {
            throw new NotSupportedException($"DNG: Egami writes raw data with 1 sample a pixel (a colour filter array) or 3 (LinearRaw), not {sensor.Samples}.");
        }

        foreach ((string id, string message) in frame.Repairs)
        {
            warnings.Add(id, message);
        }

        RawPixels pixels = RawReader.Pixels(file, sensor, input, strict: false, warnings);
        try
        {
            RawReader.MeasureBlack(frame, pixels);
            if (frame.Profile.ColorMatrix1 is null)
            {
                Unknown(frame, warnings);
            }

            RasterMetadata metadata = new();
            file.ReadMetadata(metadata, input);
            bool dng = RawDetect.IsDng(file.Source.Data.AsSpan(0, Math.Min(file.Source.Length, RawDetect.SignatureLength)));
            (bool noteBigEndian, long noteOffset) = MakerNoteSource(file);
            return new DngContent
            {
                Frame = frame,
                Pixels = pixels,
                SourceIfd0 = dng ? frame.MetadataIfd : null,
                SourceRaw = dng ? sensor.Ifd : null,
                Exif = metadata.Exif,
                Xmp = metadata.Xmp.IsEmpty ? null : metadata.Xmp.ToArray(),
                MakerNoteBigEndian = noteBigEndian,
                MakerNoteOffset = noteOffset,
            };
        }
        catch
        {
            pixels.Dispose();
            throw;
        }
    }

    /// <summary>Where the maker note was: its file's byte order and the offset of EXIF's MakerNote, for DNGPrivateData.</summary>
    private static (bool BigEndian, long Offset) MakerNoteSource(RawFile file)
    {
        RawIfd? exif = file.Frame.ExifIfd ?? file.Frame.MetadataIfd?.Sub(RawTags.ExifIfd);
        if (exif is not null && exif.TryGet(RawTags.MakerNote, out RawEntry entry))
        {
            return (exif.File.BigEndian, entry.ValueAt - exif.File.Origin);
        }

        return (file.Tiff.BigEndian, 0);
    }

    /// <summary>
    /// A camera neither the file nor the camera table has colour data for: the profile Egami develops it with (the camera
    /// treated as sRGB), as a ColorMatrix for D50, with a warning (decision Q8).
    /// </summary>
    private static void Unknown(RawFrame frame, RawWarnings warnings)
    {
        frame.Profile.ColorMatrix1 = RawColor.Multiply(RawColor.XyzToRgb(ColorProfile.Srgb.Primaries!), RawColor.Adaptation(RawColor.D50, (0.3127, 0.3290)));
        frame.Profile.Illuminant1 = 23;
        warnings.Add("DngCameraUnknown", $"Neither the file nor Egami's camera table has colour data for the {frame.Make} {frame.Model}; the DNG carries the profile Egami develops it with, which treats the camera as sRGB.");
    }

    /// <summary>
    /// Builds the content for a raster: a <see cref="Gray16"/> or <see cref="GrayF32"/> mosaic whose details are a RAW mosaic's
    /// becomes a colour filter array; anything else becomes LinearRaw.
    /// </summary>
    public static DngContent FromRaster<TPixel>(Raster<TPixel> raster, EgamiInput input, RawWarnings warnings)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (raster.Metadata.FormatDetails is RawDetails { Output: RawOutput.Mosaic, CfaPattern: string pattern } details
            && (typeof(TPixel) == typeof(Gray16) || typeof(TPixel) == typeof(GrayF32)))
        {
            return Mosaic(raster, details, pattern, input, warnings);
        }

        return LinearRaw(raster, input);
    }

    private static RawFrame Frame(int width, int height, int samples, bool isFloat)
    {
        RawIfd none = new(new RawTiff(new RawSource([], 0), false), 0, [], 0);
        return new RawFrame
        {
            Sensor = new RawSensor
            {
                Ifd = none,
                Width = width,
                Height = height,
                Samples = samples,
                Bits = isFloat ? 32 : 16,
                Float = isFloat,
                UnitWidth = width,
                UnitHeight = height,
            },
        };
    }

    private static DngContent Mosaic<TPixel>(Raster<TPixel> raster, RawDetails details, string pattern, EgamiInput input, RawWarnings warnings)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        bool isFloat = typeof(TPixel) == typeof(GrayF32);
        int rows = details.CfaHeight, columns = details.CfaWidth;
        if (rows * columns != pattern.Length || rows < 1 || columns < 1)
        {
            (rows, columns) = pattern.Length == 4 ? (2, 2) : pattern.Length == 36 ? (6, 6) : (0, 0);
        }

        if (rows == 0 || pattern.Any(c => c is not ('R' or 'G' or 'B')))
        {
            throw new NotSupportedException($"DNG: Egami writes red, green and blue colour filter arrays; the pattern {pattern} is not one.");
        }

        RawFrame frame = Frame(raster.Width, raster.Height, 1, isFloat);
        frame.Make = details.Make;
        frame.Model = details.Model;
        frame.UniqueCameraModel = details.UniqueCameraModel;
        frame.Cfa = new RawCfa(rows, columns, [.. pattern.Select(c => (byte)(c == 'R' ? 0 : c == 'G' ? 1 : 2))]);
        double[] black = details.BlackLevels.Count > 0 ? [.. details.BlackLevels] : [0];
        (frame.BlackRows, frame.BlackColumns) = black.Length == rows * columns ? (rows, columns) : black.Length == 4 ? (2, 2) : (1, 1);
        frame.Black = black.Length == frame.BlackRows * frame.BlackColumns ? black : [black.Average()];
        frame.White = details.WhiteLevels.Count > 0 ? [details.WhiteLevels[0]] : [isFloat ? 1 : 65535];
        Rect active = details.ActiveArea.Width > 0 ? details.ActiveArea : new Rect(0, 0, raster.Width, raster.Height);
        frame.Active = active;
        Rect crop = details.CropArea.Width > 0 ? details.CropArea : active;
        frame.Crop = (crop.X - active.X, crop.Y - active.Y, crop.Width, crop.Height);
        frame.AsShotNeutral = details.AsShotNeutral.Count == 3 ? [.. details.AsShotNeutral] : null;
        frame.BaselineExposure = details.BaselineExposure;
        frame.MakerNote = details.MakerNote.IsEmpty ? null : details.MakerNote.ToArray();
        if (RawCameraTable.Find(details.Make, details.Model) is RawCameraEntry camera)
        {
            frame.Profile = camera.Profile;
            frame.BaselineExposure = details.BaselineExposure != 0 ? details.BaselineExposure : camera.Profile.BaselineExposure;
        }
        else if (details.CameraToXyz.Count == 9)
        {
            // The develop's own transform: white-balanced camera RGB to XYZ D50, so ColorMatrix maps D50 back to the camera.
            double[] toXyz = [.. details.CameraToXyz];
            double[] neutral = frame.AsShotNeutral ?? [1, 1, 1];
            frame.Profile.ForwardMatrix1 = toXyz;
            frame.Profile.ColorMatrix1 = RawColor.Multiply(RawColor.Diagonal(neutral[0], neutral[1], neutral[2]), RawColor.Invert(toXyz));
            frame.Profile.Illuminant1 = 23;
        }
        else
        {
            Unknown(frame, warnings);
        }

        RawPixels pixels = new(input, raster.Width, raster.Height, 1, isFloat);
        for (int y = 0; y < raster.Height; y++)
        {
            ReadOnlySpan<byte> row = MemoryMarshal.AsBytes(raster.Row(y));
            if (isFloat)
            {
                MemoryMarshal.Cast<byte, float>(row).CopyTo(pixels.F32Row(y));
            }
            else
            {
                MemoryMarshal.Cast<byte, ushort>(row).CopyTo(pixels.U16Row(y));
            }
        }

        return new DngContent { Frame = frame, Pixels = pixels, Exif = raster.Metadata.Exif, Xmp = raster.Metadata.Xmp.IsEmpty ? null : raster.Metadata.Xmp.ToArray() };
    }

    /// <summary>
    /// Any other raster: linear RGB in its own primaries (sRGB when it has no RGB profile), 16-bit or float, black 0, with a
    /// ColorMatrix and ForwardMatrix for D50 worked out from the primaries and a neutral white balance.
    /// </summary>
    private static DngContent LinearRaw<TPixel>(Raster<TPixel> raster, EgamiInput input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        bool isFloat = TPixel.Layout.ChannelType is PixelChannelType.Float16 or PixelChannelType.Float32;
        // Floating-point pixels with nothing said about them are linear light, as Egami's float formats are.
        RasterMetadata m = raster.Metadata;
        ColorProfile source = isFloat && m.IccProfile.IsEmpty && m.ColorSpace is ColorSpaceHint.Unknown or ColorSpaceHint.Linear
            ? ColorProfile.LinearSrgb
            : m.GetColorProfile();
        bool asIs = source.Transfer?.IsLinear == true && source.Model == ColorModel.Rgb;
        ColorPrimaries primaries = source is { Model: ColorModel.Rgb, Primaries: ColorPrimaries p } ? p : ColorPrimaries.Rec709;
        ColorProfile linear = ColorProfile.Create(primaries, TransferFunction.Linear);
        RawFrame frame = Frame(raster.Width, raster.Height, 3, isFloat);
        frame.Active = new Rect(0, 0, raster.Width, raster.Height);
        frame.Crop = (0, 0, raster.Width, raster.Height);
        frame.Black = [0, 0, 0];
        frame.White = isFloat ? [1, 1, 1] : [65535, 65535, 65535];
        frame.AsShotNeutral = [1, 1, 1];
        double[] toXyz = RawColor.Multiply(RawColor.Adaptation((primaries.White.X, primaries.White.Y), RawColor.D50), RawColor.RgbToXyz(primaries));
        frame.Profile.ForwardMatrix1 = toXyz;
        frame.Profile.ColorMatrix1 = RawColor.Invert(toXyz);
        frame.Profile.Illuminant1 = 23;
        frame.UniqueCameraModel = "Egami LinearRaw";
        if (raster.Metadata.Exif is ExifData exif)
        {
            frame.Make = exif.CameraMake ?? "";
            frame.Model = exif.CameraModel ?? "";
        }

        RawPixels pixels = new(input, raster.Width, raster.Height, 3, isFloat);
        try
        {
            if (isFloat)
            {
                using Raster<RgbF32> converted = asIs ? raster.As<RgbF32>() : raster.ConvertColor<RgbF32>(linear);
                for (int y = 0; y < raster.Height; y++)
                {
                    MemoryMarshal.Cast<RgbF32, float>(converted.Row(y)).CopyTo(pixels.F32Row(y));
                }
            }
            else
            {
                using Raster<Rgb16> converted = asIs ? raster.As<Rgb16>() : raster.ConvertColor<Rgb16>(linear);
                for (int y = 0; y < raster.Height; y++)
                {
                    MemoryMarshal.Cast<Rgb16, ushort>(converted.Row(y)).CopyTo(pixels.U16Row(y));
                }
            }
        }
        catch
        {
            pixels.Dispose();
            throw;
        }

        return new DngContent
        {
            Frame = frame,
            Pixels = pixels,
            Exif = raster.Metadata.Exif,
            Xmp = raster.Metadata.Xmp.IsEmpty ? null : raster.Metadata.Xmp.ToArray(),
            Rendered = raster,
        };
    }
}
