using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;

using Egami.Formats;
using Egami.Formats.Jxl;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jxl;

/// <summary>
/// Phase 2.9.3 task 2: the container and the headers. Egami's box list, basic information, color encoding and frame list
/// equal jxlinfo's (libjxl 0.12.0) on the committed corpus and, when fetched, the 18181-3 conformance suite and Chromium's
/// files. Frames after a compressed ICC profile or a permuted TOC need the entropy decoder (task 3) and are left out then.
/// </summary>
public class JxlHeaderTests(ITestOutputHelper output)
{
    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "images", "jxl");
    private static readonly Lazy<JsonElement> Manifest = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "reference", "jxl", "manifest.json"))).RootElement.GetProperty("files").Clone());

    public static TheoryData<string> CorpusFiles() => JxlReferenceTests.CorpusFiles();

    public static TheoryData<string> CachedFiles()
    {
        TheoryData<string> data = [];
        if (JxlReferenceTests.Cache() is not string cache)
        {
            data.Add("(none)");
            return data;
        }

        foreach (string folder in Directory.GetDirectories(Path.Combine(cache, "reference", "conformance")).Order(StringComparer.Ordinal))
        {
            data.Add("conformance/" + Path.GetFileName(folder));
        }

        foreach (string folder in Directory.GetDirectories(Path.Combine(cache, "reference", "chromium")).Order(StringComparer.Ordinal))
        {
            data.Add("chromium/" + Path.GetFileName(folder));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void CorpusHeadersEqualJxlinfo(string file)
    {
        Compare(file, File.ReadAllBytes(Path.Combine(Corpus, file)), Manifest.Value.GetProperty(file).GetProperty("jxlinfo").GetString()!);
    }

    [Theory]
    [MemberData(nameof(CachedFiles))]
    public void ConformanceAndChromiumHeadersEqualJxlinfo(string name)
    {
        if (JxlReferenceTests.Cache() is not string cache)
        {
            return;
        }

        string[] parts = name.Split('/');
        string input = parts[0] == "conformance"
            ? Path.Combine(cache, "conformance", "testcases", parts[1], "input.jxl")
            : Path.Combine(cache, "chromium", parts[1] + ".jxl");
        Compare(name, File.ReadAllBytes(input), File.ReadAllText(Path.Combine(cache, "reference", parts[0], parts[1], "jxlinfo.txt")));
    }

    [Theory]
    [MemberData(nameof(CachedFiles))]
    public void IccProfilesEqualTheConformanceSuiteAndDjxl(string name)
    {
        if (JxlReferenceTests.Cache() is not string cache || !name.StartsWith("conformance/", StringComparison.Ordinal))
        {
            return;
        }

        string folder = Path.Combine(cache, "conformance", "testcases", name[12..]);
        JxlFile file = JxlInfoText.Read(File.ReadAllBytes(Path.Combine(folder, "input.jxl")));
        if (!file.Header.Color.WantIcc)
        {
            Assert.Null(file.Icc);
            return;
        }

        // original.icc is the profile the suite says the file carries; djxl-original.icc is djxl's --orig_icc_out.
        if (File.Exists(Path.Combine(folder, "original.icc")))
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(folder, "original.icc")), file.Icc);
        }

        Assert.Equal(File.ReadAllBytes(Path.Combine(cache, "reference", "conformance", name[12..], "djxl-original.icc")), file.Icc);
    }

    [Fact]
    public void ProbeReadsTheHeaders()
    {
        RasterInfo info = Raster.Probe(Path.Combine(Corpus, "duck-alpha-lossless.jxl"));
        Assert.Equal(JxlFormat.Info, info.Format);
        Assert.Equal((256, 192, 1), (info.Width, info.Height, info.FrameCount));
        Assert.Equal(Rgba8.Layout, info.Layout);
        Assert.Equal(256L * 192 * 4, info.EstimatedBytes);

        Assert.Equal(Rgba16.Layout.ChannelType, Raster.Probe(Path.Combine(Corpus, "duck-16bit-lossless.jxl")).Layout.ChannelType);
        Assert.Equal(Gray8.Layout, Raster.Probe(Path.Combine(Corpus, "duck-gray-d1.jxl")).Layout);
        Assert.Equal(RgbF32.Layout, Raster.Probe(Path.Combine(Corpus, "duck-float-lossless.jxl")).Layout);
        Assert.Equal(GrayAlpha8.Layout, Raster.Probe(Path.Combine(Corpus, "duck-gray-alpha-lossless.jxl")).Layout);

        RasterInfo sideways = Raster.Probe(Path.Combine(Corpus, "jpeg-exif-orientation-6.jxl"));
        Assert.Equal(ImageOrientation.Rotate90, sideways.Orientation);
        Assert.True(sideways.HasMetadata);

        RasterInfo animation = Raster.Probe(Path.Combine(Corpus, "pan-anim-lossless.jxl"));
        Assert.True(animation.FrameCount > 1);
        Assert.Equal(0, animation.LoopCount);
    }

    [Fact]
    public void DetailsDescribeTheContainer()
    {
        JxlDetails brob = JxlDetails.From(JxlInfoText.Read(File.ReadAllBytes(Path.Combine(Corpus, "jpeg-exif-tags-brob.jxl"))));
        Assert.True(brob.IsContainer);
        Assert.True(brob.HasJpegReconstruction);
        Assert.Equal(8, brob.BitsPerSample);

        JxlDetails level = JxlDetails.From(JxlInfoText.Read(File.ReadAllBytes(Path.Combine(Corpus, "egret-d1-level10.jxl"))));
        Assert.Equal(10, level.Level);
        Assert.True(level.IsXybEncoded);

        JxlDetails bare = JxlDetails.From(JxlInfoText.Read(File.ReadAllBytes(Path.Combine(Corpus, "duck-12bit-lossless.jxl"))));
        Assert.False(bare.IsContainer);
        Assert.Equal((12, 0, false), (bare.BitsPerSample, bare.ExponentBits, bare.IsXybEncoded));

        // 32-bit float needs level 10, so cjxl writes the container with a jxll box.
        JxlDetails single = JxlDetails.From(JxlInfoText.Read(File.ReadAllBytes(Path.Combine(Corpus, "duck-float-lossless.jxl"))));
        Assert.Equal((32, 8, 10), (single.BitsPerSample, single.ExponentBits, single.Level));
    }

    [Fact]
    public void BrotliExifIsOpened()
    {
        JxlFile plain = JxlInfoText.Read(File.ReadAllBytes(Path.Combine(Corpus, "jpeg-exif-tags.jxl")));
        JxlFile brob = JxlInfoText.Read(File.ReadAllBytes(Path.Combine(Corpus, "jpeg-exif-tags-brob.jxl")));
        Assert.NotNull(plain.Container.Exif);
        Assert.Equal(plain.Container.Exif, brob.Container.Exif);
        Assert.Contains(brob.Container.Boxes, b => b is { Type: "brob", CompressedType: "Exif" });
    }

    [Fact]
    public void BrokenMetadataBoxesAreDroppedWithAProblem()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Corpus, "jpeg-exif-tags.jxl"));
        JxlFile good = JxlInfoText.Read(data);

        // The Exif box's TIFF header offset points past its end.
        JxlBox exif = good.Container.Boxes.Single(b => b.Type == "Exif");
        byte[] badOffset = (byte[])data.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(badOffset.AsSpan((int)exif.Offset + 8), 0x7FFFFFFF);
        JxlFile dropped = JxlInfoText.Read(badOffset);
        Assert.Null(dropped.Container.Exif);
        Assert.Contains(dropped.Container.Problems, p => p.Id == "Jxl.ExifDropped");
        Assert.Equal(JxlInfoText.Of(good).Info, JxlInfoText.Of(dropped).Info);

        // A brob box whose Brotli data is not Brotli.
        byte[] brob = Box("brob", [.. "xml "u8, .. new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }]);
        JxlFile broken = JxlInfoText.Read([.. data, .. brob]);
        Assert.Null(broken.Container.Xmp);
        Assert.Contains(broken.Container.Problems, p => p.Id == "Jxl.BrobDropped");

        // A brob box holding XMP opens to it.
        byte[] xmp = "<x:xmpmeta xmlns:x='adobe:ns:meta/'/>"u8.ToArray();
        using MemoryStream packed = new();
        using (BrotliStream brotli = new(packed, CompressionLevel.Optimal, leaveOpen: true))
        {
            brotli.Write(xmp);
        }

        JxlFile opened = JxlInfoText.Read([.. data, .. Box("brob", [.. "xml "u8, .. packed.ToArray()])]);
        Assert.Equal(xmp, opened.Container.Xmp);
    }

    [Fact]
    public void BrokenContainersFail()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Corpus, "jpeg-exif-orientation-6.jxl"));
        JxlFile good = JxlInfoText.Read(data);
        JxlBox[] parts = [.. good.Container.Boxes.Where(b => b.Type == "jxlp")];
        Assert.True(parts.Length >= 2);

        // The second codestream part claims to be the first.
        byte[] reordered = (byte[])data.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(reordered.AsSpan((int)parts[1].Offset + 8), 0);
        Assert.Throws<CorruptDataException>(() => JxlInfoText.Read(reordered));

        // The file ends inside the codestream.
        Assert.Throws<CorruptDataException>(() => JxlInfoText.Read(data[..(int)(parts[1].Offset + 40)]));

        // No ftyp box after the signature.
        byte[] noFtyp = (byte[])data.Clone();
        noFtyp[16] = (byte)'x';
        Assert.Throws<CorruptDataException>(() => JxlInfoText.Read(noFtyp));

        // A bare codestream cut inside the image header.
        byte[] bare = File.ReadAllBytes(Path.Combine(Corpus, "duck-d1.jxl"));
        Assert.Throws<CorruptDataException>(() => JxlInfoText.Read(bare[..3]));
    }

    [Fact]
    public void SizesAreCheckedAgainstTheLimits()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Corpus, "duck-d1.jxl"));
        EgamiContext small = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxWidth = 100 } };
        Assert.Throws<LimitExceededException>(() => Raster.Probe(new MemoryStream(data), small));
        EgamiContext fewFrames = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxFrames = 2 } };
        Assert.Throws<LimitExceededException>(() => Raster.Probe(Path.Combine(Corpus, "pan-anim-lossless.jxl"), fewFrames));
    }

    [Fact]
    public void LoadSaysWhatItDoesNotDecodeYet()
    {
        Assert.Throws<UnsupportedFormatException>(() => Raster.Load(Path.Combine(Corpus, "jpeg-duck-444.jxl")));
    }

    private static byte[] Box(string type, byte[] content)
    {
        byte[] box = new byte[8 + content.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        System.Text.Encoding.Latin1.GetBytes(type, box.AsSpan(4));
        content.CopyTo(box, 8);
        return box;
    }

    private void Compare(string name, byte[] data, string jxlinfo)
    {
        JxlFile file = JxlInfoText.Read(data);
        JxlInfoText ours = JxlInfoText.Of(file);
        JxlInfoText theirs = JxlInfoText.Parse(jxlinfo);
        Assert.Equal(theirs.Boxes, ours.Boxes);
        Assert.Equal(theirs.Info, ours.Info);
        Assert.True(file.FramesKnown);
        Assert.Equal(theirs.Frames, ours.Frames);
        output.WriteLine($"{name}: {file.Frames.Count} frames{(file.Icc is null ? string.Empty : $", {file.Icc.Length}-byte ICC profile")}{(file.Tocs.Any(t => t.Permutation is not null) ? ", permuted TOC" : string.Empty)}");
    }
}
