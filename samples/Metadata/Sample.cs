using Egami.Formats;
using Egami.Metadata;

namespace Egami.Samples.Metadata;

/// <summary>Reads a photo's EXIF, edits it, adds text and resolution, and saves a copy without any metadata.</summary>
public static class Sample
{
    /// <summary>Runs the sample.</summary>
    /// <param name="output">Where to write.</param>
    public static void Run(string output)
    {
        using Raster photo = Raster.Load(SampleRunner.Image("sideways.jpg"));
        RasterMetadata metadata = photo.Metadata;
        if (metadata.Exif is ExifData exif)
        {
            Console.WriteLine($"Camera: {exif.CameraModel ?? "(not recorded)"}; taken {exif.DateTaken?.ToString("u", System.Globalization.CultureInfo.InvariantCulture) ?? "(not recorded)"}; orientation {exif.Orientation}");
            foreach (ExifEntry entry in exif.Entries.Take(10))
            {
                Console.WriteLine($"  {entry.Key}: {entry.Type} × {entry.Count}");
            }

            exif.Set(ExifKey.Artist, "Egami sample");
            exif.Remove(ExifKey.GpsLatitude);
        }

        metadata.Text.Add(new TextEntry("Comment", "Edited by the Egami metadata sample"));
        metadata.Resolution = new Resolution(300, 300, ResolutionUnit.PerInch);
        photo.Save(Path.Combine(output, "edited.jpg"));
        photo.Save(Path.Combine(output, "edited.png"));
        photo.Save(Path.Combine(output, "clean.jpg"), new Egami.Formats.Jpeg.JpegWriterOptions { StripMetadata = true });

        using Raster back = Raster.Load(Path.Combine(output, "edited.png"));
        Console.WriteLine($"PNG keeps: {back.Metadata.Text.Count} text entries, EXIF {(back.Metadata.Exif is null ? "no" : "yes")}, {back.Metadata.Resolution}");
    }
}
