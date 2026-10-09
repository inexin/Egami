using Egami.Formats.Jpeg;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>
/// Stand-ins for the design's 50 camera JPEGs: the 4000 × 3000 4:2:0 benchmark file with an EXIF block like a camera's in front
/// (IFD0, EXIF, interoperability and GPS tags, a 6 KB maker note and a 160 × 120 thumbnail), each with its own orientation,
/// dates and position. Also the thumbnail scenario's file, the same photo tagged with orientation 6.
/// </summary>
internal static class CameraFiles
{
    internal const int Count = 50;

    /// <summary>Writes camera-00.jpg to camera-49.jpg and photo-orientation-6.jpg into <paramref name="directory"/>.</summary>
    internal static void Write(string directory, byte[] photo420)
    {
        byte[] thumbnail = Thumbnail();
        for (int i = 0; i < Count; i++)
        {
            File.WriteAllBytes(Path.Combine(directory, $"camera-{i:00}.jpg"), WithExif(photo420, Exif(i, thumbnail)));
        }

        ExifData oriented = new() { Orientation = ImageOrientation.Rotate90 };
        File.WriteAllBytes(Path.Combine(directory, "photo-orientation-6.jpg"), WithExif(photo420, oriented));
    }

    private static ExifData Exif(int i, byte[] thumbnail)
    {
        Random random = new(i);
        ExifData exif = new();
        exif.Set(ExifKey.Make, i % 2 == 0 ? "Egami Optics" : "Benchmark Camera Co.");
        exif.Set(ExifKey.Model, $"Model {i % 7}");
        exif.Orientation = (ImageOrientation)(1 + (i % 8));
        exif.Set(ExifKey.XResolution, new ExifFraction(72, 1));
        exif.Set(ExifKey.YResolution, new ExifFraction(72, 1));
        exif.Set(ExifKey.ResolutionUnit, (ushort)2);
        exif.Set(ExifKey.Software, "Firmware 1.2.3");
        string date = $"2026:{1 + (i % 12):00}:{1 + (i % 28):00} {i % 24:00}:{i % 60:00}:{(i * 7) % 60:00}";
        exif.Set(ExifKey.DateTime, date);
        exif.Set(ExifKey.Artist, "Egami");
        exif.Set(ExifKey.Copyright, "Public domain");
        exif.Set(ExifKey.ExposureTime, new ExifFraction(1, 30 + (i * 10)));
        exif.Set(ExifKey.FNumber, new ExifFraction(18 + i, 10));
        exif.Set(ExifKey.IsoSpeed, (ushort)(100 * (1 + (i % 16))));
        exif.Set(ExifKey.ExifVersion, ExifType.Undefined, "0232"u8);
        exif.Set(ExifKey.DateTimeOriginal, date);
        exif.Set(ExifKey.DateTimeDigitized, date);
        exif.Set(ExifKey.OffsetTimeOriginal, "+02:00");
        exif.Set(ExifKey.ExposureBias, new ExifFraction(-(i % 3), 3));
        exif.Set(ExifKey.MeteringMode, (ushort)5);
        exif.Set(ExifKey.Flash, (ushort)16);
        exif.Set(ExifKey.FocalLength, new ExifFraction(240 + (i * 3), 10));
        exif.Set(ExifKey.ColorSpace, (ushort)1);
        exif.Set(ExifKey.PixelXDimension, 4000u);
        exif.Set(ExifKey.PixelYDimension, 3000u);
        exif.Set(ExifKey.FocalLengthIn35mmFilm, (ushort)(35 + i));
        exif.Set(ExifKey.LensMake, "Egami Optics");
        exif.Set(ExifKey.LensModel, "24-70mm F2.8");
        byte[] makerNote = new byte[6 * 1024];
        random.NextBytes(makerNote);
        exif.Set(ExifKey.MakerNote, ExifType.Undefined, makerNote);
        exif.Set(ExifKey.UserComment, ExifType.Undefined, [.. "ASCII\0\0\0"u8, .. "Benchmark file"u8]);
        exif.Set(new ExifKey(0x0001, ExifIfd.Interoperability), "R98");
        exif.Set(ExifKey.GpsLatitudeRef, i % 2 == 0 ? "N" : "S");
        exif.Set(ExifKey.GpsLatitude, new ExifFraction(59, 1), new ExifFraction(i, 1), new ExifFraction(1234 + i, 100));
        exif.Set(ExifKey.GpsLongitudeRef, "E");
        exif.Set(ExifKey.GpsLongitude, new ExifFraction(10, 1), new ExifFraction(45, 1), new ExifFraction(i * 100, 100));
        exif.Set(ExifKey.GpsAltitudeRef, ExifType.Byte, [0]);
        exif.Set(ExifKey.GpsAltitude, new ExifFraction(1000 + i, 10));
        exif.SetThumbnail(thumbnail);
        return exif;
    }

    private static byte[] Thumbnail()
    {
        using Raster<Rgba8> small = BenchImages.Photo(160, 120);
        using MemoryStream stream = new();
        small.Save(stream, JpegFormat.Info, new JpegWriterOptions { StripMetadata = true });
        return stream.ToArray();
    }

    /// <summary>The file with an APP1 EXIF segment right after the start-of-image marker, as cameras write it.</summary>
    private static byte[] WithExif(byte[] jpeg, ExifData exif)
    {
        byte[] block = exif.ToBytes().ToArray();
        int length = 2 + 6 + block.Length;
        byte[] segment = [0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. "Exif\0\0"u8, .. block];
        return [.. jpeg.AsSpan(0, 2), .. segment, .. jpeg.AsSpan(2)];
    }
}
