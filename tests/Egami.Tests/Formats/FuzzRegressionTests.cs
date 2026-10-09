using Egami.Formats;
using Egami.Formats.Jpeg;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests.Formats;

/// <summary>
/// Inputs the fuzzers found (tests/images/fuzz): each once crashed a reader or the EXIF parser. Readers must load them or throw an
/// <see cref="EgamiException"/>; EXIF blocks must parse, and rebuild after an edit to a block that reads back the same.
/// </summary>
public class FuzzRegressionTests
{
    private static readonly string Fuzz = Path.Combine(AppContext.BaseDirectory, "images", "fuzz");

    public static TheoryData<string> JpegFiles() => [.. Directory.GetFiles(Path.Combine(Fuzz, "jpeg")).Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal)];

    public static TheoryData<string> ExifFiles() => [.. Directory.GetFiles(Path.Combine(Fuzz, "exif")).Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(JpegFiles))]
    public void JpegInputsLoadOrFailCleanly(string file)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fuzz, "jpeg", file));
        foreach (ReaderOptions options in (ReaderOptions[])[new() { Recovery = RecoveryMode.Lenient }, new() { Recovery = RecoveryMode.Strict }, new JpegReaderOptions { FastUpsampling = true }, new() { TargetSize = new Size(1, 1) }])
        {
            try
            {
                using Raster raster = Raster.Load(data, options: options);
                using Raster<Rgba8> converted = raster.As<Rgba8>();
                converted.Save(Stream.Null, JpegFormat.Info);
            }
            catch (EgamiException)
            {
            }
        }
    }

    [Theory]
    [MemberData(nameof(ExifFiles))]
    public void ExifBlocksRebuildToTheSameEntries(string file)
    {
        ExifData exif = ExifData.Parse(File.ReadAllBytes(Path.Combine(Fuzz, "exif", file)));

        // The typed getters never throw, whatever the block holds.
        _ = (exif.Orientation, exif.DateTaken, exif.CameraMake, exif.CameraModel, exif.Location);
        exif.Set(ExifKey.Artist, "fuzz");
        ExifData again = ExifData.Parse(exif.ToBytes().Span);
        Assert.False(again.IsDamaged);

        // A rebuilt IFD has its tags sorted, as TIFF requires; a damaged one may not have.
        Assert.Equal(Sorted(exif), Sorted(again));

        static IEnumerable<(ExifKey, ExifType, byte[])> Sorted(ExifData data) =>
            data.Entries.OrderBy(e => e.Key.Ifd).ThenBy(e => e.Key.Tag).Select(e => (e.Key, e.Type, e.RawValue.ToArray()));
    }
}
