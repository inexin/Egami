using Egami.Colors;
using Egami.Formats;
using Egami.Formats.Jpeg;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests.Formats;

/// <summary>
/// Inputs the fuzzers found (tests/images/fuzz): each once crashed a reader, the EXIF parser or the ICC reader. Readers must load them or
/// throw an <see cref="EgamiException"/>; EXIF blocks must parse, and rebuild after an edit to a block that reads back the same.
/// </summary>
public class FuzzRegressionTests
{
    private static readonly string Fuzz = Path.Combine(AppContext.BaseDirectory, "images", "fuzz");
    private static readonly string[] SimpleFolders = ["bmp", "tga", "qoi", "pnm", "exr"];

    public static TheoryData<string> JpegFiles() => [.. Directory.GetFiles(Path.Combine(Fuzz, "jpeg")).Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal)];

    public static TheoryData<string> SimpleFiles() =>
        [.. SimpleFolders.Where(f => Directory.Exists(Path.Combine(Fuzz, f))).SelectMany(f => Directory.GetFiles(Path.Combine(Fuzz, f)).Select(p => $"{f}/{Path.GetFileName(p)}")).Order(StringComparer.Ordinal)];

    public static TheoryData<string> ExifFiles() => [.. Directory.GetFiles(Path.Combine(Fuzz, "exif")).Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal)];

    public static TheoryData<string> PsdFiles() => [.. Directory.GetFiles(Path.Combine(Fuzz, "psdfile")).Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal)];

    /// <summary>Phase 2.3.3: Deflate data the runtime's zlib rejects mid-stream threw its own exception instead of a short image.</summary>
    [Theory]
    [MemberData(nameof(PsdFiles))]
    public void PsdInputsLoadOrFailCleanly(string file)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fuzz, "psdfile", file));
        foreach (RecoveryMode recovery in (RecoveryMode[])[RecoveryMode.Lenient, RecoveryMode.Strict])
        {
            try
            {
                using Raster raster = Raster.Load(data, options: new ReaderOptions { Recovery = recovery });
            }
            catch (EgamiException)
            {
            }
        }
    }

    [Theory]
    [MemberData(nameof(JpegFiles))]
    public void JpegInputsLoadOrFailCleanly(string file)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fuzz, "jpeg", file));
        foreach (ReaderOptions options in (ReaderOptions[])[new() { Recovery = RecoveryMode.Lenient }, new() { Recovery = RecoveryMode.Strict }, new JpegReaderOptions { FastUpsampling = true }, new() { MinimumSize = new Size(1, 1) }])
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

    /// <summary>
    /// A scan that names one component three times left the other two uncoded, and the streaming decoder then showed whatever
    /// its pooled buffers held from before (found by the Phase 2.2 stream target). Such a scan is now refused.
    /// </summary>
    [Fact]
    public void AScanNamingAComponentTwiceIsRefused()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fuzz, "jpeg", "duplicate-scan-component.jpg"));
        foreach (RecoveryMode recovery in (RecoveryMode[])[RecoveryMode.Lenient, RecoveryMode.Strict])
        {
            CorruptDataException e = Assert.Throws<CorruptDataException>(() => Raster.Load(data, options: new ReaderOptions { Recovery = recovery }));
            Assert.Contains("twice", e.Message, StringComparison.Ordinal);
            Assert.Throws<CorruptDataException>(() => RasterDecoder.Open(data, options: new ReaderOptions { Recovery = recovery }).Dispose());
        }
    }

    /// <summary>
    /// A 674-byte JPEG declaring 65,313 × 32,768 pixels streamed for seconds, since only a full decode's allocation checked the
    /// size (found by the Phase 2.2 stream target). The decoder now checks the stored size against the limits as it opens.
    /// </summary>
    [Fact]
    public void TheDecoderChecksTheStoredSizeAgainstTheLimits()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fuzz, "jpeg", "declared-two-gigapixels.jpg"));
        EgamiContext context = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxPixels = 4_000_000 } };
        ReaderOptions reduced = new() { MinimumSize = new Size(100, 100) };
        Assert.Throws<LimitExceededException>(() => RasterDecoder.Open(data, context, reduced).Dispose());
        using RasterDecoder incremental = RasterDecoder.OpenIncremental(context, reduced);
        Assert.Throws<LimitExceededException>(() => incremental.Append(data));
    }

    /// <summary>
    /// A chad tag that cannot be inverted threw InvalidOperationException from the profile's white (found by the Phase 2.1.2 icc
    /// target). It is now a damaged profile.
    /// </summary>
    [Fact]
    public void AnAdaptationThatCannotBeInvertedIsDamage()
    {
        IccData icc = IccData.Parse(File.ReadAllBytes(Path.Combine(Fuzz, "icc", "singular-chad.icc")));
        CorruptDataException e = Assert.Throws<CorruptDataException>(() => ColorProfile.FromIcc(icc));
        Assert.Contains("cannot be inverted", e.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A Photoshop resource block whose last resource lost its padding byte: replacing the IPTC put the new resource one byte
    /// early, where no reader looks (found by the Phase 2.1.3 psd target). The padding is now restored.
    /// </summary>
    [Fact]
    public void AnUnpaddedLastResourceKeepsTheIptcFindable()
    {
        byte[] block = File.ReadAllBytes(Path.Combine(Fuzz, "psd", "unpadded-last-resource.8bim"));
        byte[] replaced = PhotoshopResources.WithIptc(block, [0x1C, 2, 5, 0, 1, (byte)'x']);
        Assert.Equal(6, PhotoshopResources.FindIptc(replaced, null).Length);
    }

    /// <summary>The BMP, TGA, QOI and PNM inputs load, from memory and from a stream that cannot seek, or fail cleanly.</summary>
    [Theory]
    [MemberData(nameof(SimpleFiles))]
    public void SimpleFormatInputsLoadOrFailCleanly(string file)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fuzz, file));
        foreach (RecoveryMode recovery in (RecoveryMode[])[RecoveryMode.Lenient, RecoveryMode.Strict])
        {
            ReaderOptions options = new() { Recovery = recovery };
            try
            {
                Raster.Probe(data, options: options);
                using Raster raster = Raster.Load(data, options: options);
                using Raster<Rgba8> converted = Raster.Load<Rgba8>(new NonSeekable(data), options: options);
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

    private sealed class NonSeekable(byte[] data) : MemoryStream(data, writable: false)
    {
        public override bool CanSeek => false;

        public override long Position { get => base.Position; set => throw new NotSupportedException(); }

        public override long Length => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
    }
}
