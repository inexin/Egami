using System.Text.Json;

using Egami.Formats;
using Egami.Formats.Jpeg;

namespace Egami.Tests.Formats.Jpeg;

/// <summary>
/// The Multi-Picture Format index (Phase 2.9.1 task 3): every image's type, flags, offset and length equal ExifTool's listing in
/// tests/reference/mpf, and LoadAll loads each JPEG image.
/// </summary>
public class MpfTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "mpf");
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "mpf");

    public static TheoryData<string> Files() => [.. Directory.GetFiles(Images, "*.jpg").Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Files))]
    public void TheIndexEqualsExifTools(string name)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(References, name + ".json")));
        JsonElement exif = json.RootElement;
        RasterInfo info = Raster.Probe(Path.Combine(Images, name), options: new ReaderOptions());
        using Raster raster = Raster.Load(Path.Combine(Images, name));
        MpfData mpf = ((JpegDetails)raster.Metadata.FormatDetails!).MultiPicture!;
        Assert.Equal(exif.GetProperty("MPF0:MPFVersion").GetString(), mpf.Version);
        int count = exif.GetProperty("MPF0:NumberOfImages").GetInt32();
        Assert.Equal(count, mpf.Images.Count);
        Assert.Equal(count, info.ImageCount);
        for (int i = 0; i < count; i++)
        {
            MpfImage image = mpf.Images[i];
            string group = $"MPImage{i + 1}:";
            Assert.Equal(exif.GetProperty(group + "MPImageType").GetInt32(), (int)image.Type);
            Assert.Equal(exif.GetProperty(group + "MPImageFormat").GetInt32(), image.DataFormat);
            Assert.Equal(exif.GetProperty(group + "MPImageLength").GetInt64(), image.Length);
            Assert.Equal(exif.GetProperty(group + "MPImageStart").GetInt64(), image.Offset);
            Assert.Equal(exif.GetProperty(group + "DependentImage1EntryNumber").GetInt32(), image.DependentImage1);
            Assert.Equal(exif.GetProperty(group + "DependentImage2EntryNumber").GetInt32(), image.DependentImage2);
            int flags = exif.GetProperty(group + "MPImageFlags").GetInt32();
            Assert.Equal(((flags & 16) != 0, (flags & 8) != 0, (flags & 4) != 0), (image.IsDependentParent, image.IsDependentChild, image.IsRepresentative));
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void LoadAllLoadsEveryImage(string name)
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Images, name));
        using Raster primary = Raster.Load(file);
        MpfData mpf = ((JpegDetails)primary.Metadata.FormatDetails!).MultiPicture!;
        using RasterList all = Raster.LoadAll(file);
        Assert.Equal(mpf.Images.Count, all.Count);
        Assert.Equal((primary.Width, primary.Height), (all[0].Width, all[0].Height));
        for (int i = 1; i < all.Count; i++)
        {
            // Each one is the JPEG at its offset, loaded on its own.
            using Raster alone = Raster.Load(file.AsMemory((int)mpf.Images[i].Offset));
            Assert.Equal((alone.Width, alone.Height, alone.Layout), (all[i].Width, all[i].Height, all[i].Layout));
        }

        // A stream that cannot seek gives the same.
        using RasterList streamed = Raster.LoadAll(new ForwardOnlyStream(file));
        Assert.Equal(all.Select(r => (r.Width, r.Height)), streamed.Select(r => (r.Width, r.Height)));
    }

    [Fact]
    public void ImageTypesAreNamed()
    {
        using Raster stereo = Raster.Load(Path.Combine(Images, "stereo-be.jpg"));
        MpfData mpf = ((JpegDetails)stereo.Metadata.FormatDetails!).MultiPicture!;
        Assert.Equal([MpfImageType.Disparity, MpfImageType.Disparity, MpfImageType.LargeThumbnailVga], mpf.Images.Select(i => i.Type));
        Assert.True(mpf.Images[0].IsRepresentative && mpf.Images[0].IsDependentParent);

        using Raster uhdr = Raster.Load(Path.Combine(Images, "uhdr-default.jpg"));
        Assert.Equal(MpfImageType.BaselinePrimary, ((JpegDetails)uhdr.Metadata.FormatDetails!).MultiPicture!.Images[0].Type);
    }

    [Fact]
    public void ADamagedIndexWarnsAndLoadsThePrimary()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Images, "stereo-le.jpg"));
        int mpf = file.AsSpan().IndexOf("MPF\0"u8);
        file[mpf + 4] = (byte)'X';
        using Raster raster = Raster.Load(file);
        Assert.Null(((JpegDetails)raster.Metadata.FormatDetails!).MultiPicture);
        Assert.Contains(raster.Metadata.Warnings, w => w.Id == "Jpeg.MpfDamaged");
        using RasterList all = Raster.LoadAll(file);
        Assert.Single(all);
    }

    [Fact]
    public void AnImageCutShortLoadsLenientlyAndAMissingOneThrows()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Images, "preview.jpg"));
        byte[] cut = file[..^200];
        using (RasterList all = Raster.LoadAll(cut))
        {
            Assert.Contains(all[1].Metadata.Warnings, w => w.Id.StartsWith("Jpeg.", StringComparison.Ordinal));
        }

        Assert.Throws<CorruptDataException>(() => Raster.LoadAll(cut, options: new ReaderOptions { Recovery = RecoveryMode.Strict }).Dispose());

        // The primary alone still loads when the image after it is gone.
        byte[] gone = file[..^700];
        using Raster primary = Raster.Load(gone);
        Assert.Throws<CorruptDataException>(() => Raster.LoadAll(gone).Dispose());
    }

    [Fact]
    public void PlainJpegsHaveOneImage()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "images", "jpeg", "duck-420.jpg");
        Assert.Equal(1, Raster.Probe(path).ImageCount);
        using Raster raster = Raster.Load(path);
        Assert.Null(((JpegDetails)raster.Metadata.FormatDetails!).MultiPicture);
    }

    private sealed class ForwardOnlyStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
