using System.IO.Compression;

using Egami.Metafiles.Reading;

namespace Egami.Metafiles.Tests;

/// <summary>Stage A: the WMF, EMF and EMF+ containers of the synthetic files, and broken ones.</summary>
public class ContainerTests
{
    public static TheoryData<string> Synthetic => [.. TestFiles.Synthetic()];

    [Theory]
    [MemberData(nameof(Synthetic))]
    public void EverySyntheticFileLoadsCleanly(string name)
    {
        Metafile metafile = Metafile.Load(TestFiles.SyntheticPath(name));
        MetafileKind expected = name switch
        {
            _ when name.EndsWith(".plus.emf", StringComparison.Ordinal) => MetafileKind.EmfPlusOnly,
            _ when name.EndsWith(".dual.emf", StringComparison.Ordinal) => MetafileKind.EmfPlusDual,
            _ when name.EndsWith(".emf", StringComparison.Ordinal) => MetafileKind.Emf,
            _ => MetafileKind.Wmf,
        };
        Assert.Equal(expected, metafile.Kind);
        Assert.True(metafile.Size.X > 0 && metafile.Size.Y > 0);
        MetafileContent c = metafile.Content;
        if (expected == MetafileKind.Wmf)
        {
            Assert.Equal(MetafileReader.WmfEof, c.Records[^1].Type);
            bool plain = name.EndsWith(".plain.wmf", StringComparison.Ordinal);
            Assert.Equal(plain, c.PlaceableBox is null);
            Assert.Equal(plain ? ["Metafile.NoPlaceableHeader"] : [], metafile.Warnings.Select(w => w.Id));
            Assert.Equal(new(150, 112.5f), metafile.Size); // 200 by 150 units at 96 per inch, or a 200 by 150 window at 96 DPI
        }
        else
        {
            Assert.Empty(metafile.Warnings);
            Assert.Equal(MetafileReader.EmrHeader, c.Records[0].Type);
            Assert.Equal(MetafileReader.EmrEof, c.Records[^1].Type);
            Assert.Equal(c.HeaderRecordCount, c.Records.Count);
            Assert.Equal(expected != MetafileKind.Emf, c.PlusRecords.Count > 0);
            Assert.StartsWith(expected == MetafileKind.Emf ? "Egami generator" : "", metafile.Description ?? "");
        }
    }

    [Fact]
    public void EmzAndWmzInflate()
    {
        foreach (string name in (string[])["gdi-shapes-basic.emf", "gdi-shapes-basic.wmf"])
        {
            byte[] raw = File.ReadAllBytes(TestFiles.SyntheticPath(name));
            using MemoryStream gz = new();
            using (GZipStream zip = new(gz, CompressionLevel.Fastest, leaveOpen: true))
            {
                zip.Write(raw);
            }

            gz.Position = 0;
            Metafile packed = Metafile.Load(gz);
            Metafile plain = Metafile.Load(new MemoryStream(raw));
            Assert.Equal(plain.Kind, packed.Kind);
            Assert.Equal(plain.Frame, packed.Frame);
            Assert.Equal(raw, packed.Content.Data);
        }
    }

    [Fact]
    public void BytesLoadAsTheStreamDoes()
    {
        byte[] raw = File.ReadAllBytes(TestFiles.SyntheticPath("gdi-shapes-basic.emf"));
        using MemoryStream gz = new();
        using (GZipStream zip = new(gz, CompressionLevel.Fastest, leaveOpen: true))
        {
            zip.Write(raw);
        }

        Metafile fromStream = Metafile.Load(new MemoryStream(raw));
        byte[] padded = [0, .. raw, 0];
        foreach (ReadOnlyMemory<byte> data in new ReadOnlyMemory<byte>[] { raw, gz.ToArray(), padded.AsMemory(1, raw.Length) })
        {
            Metafile fromBytes = Metafile.Load(data);
            Assert.Equal(fromStream.Kind, fromBytes.Kind);
            Assert.Equal(fromStream.Frame, fromBytes.Frame);
            Assert.Equal(raw, fromBytes.Content.Data);
        }

        MetafileLoadOptions small = new() { Context = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxTotalBytes = 100 } } };
        Assert.Throws<LimitExceededException>(() => Metafile.Load(raw, small));
        Assert.Throws<LimitExceededException>(() => Metafile.Load(gz.ToArray(), small));
        Assert.Throws<CorruptDataException>(() => Metafile.Load(ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void ContinuedEmfPlusObjectsAreJoined()
    {
        // The image scenarios hold bitmaps larger than one EMF+ record, so GDI+ splits them with the continue flag.
        Metafile metafile = Metafile.Load(TestFiles.SyntheticPath("plus-images.plus.emf"));
        PlusRecord[] objects = [.. metafile.Content.PlusRecords.Where(r => r.Type == MetafileReader.PlusObject)];
        Assert.NotEmpty(objects);
        Assert.All(objects, o => Assert.Equal(0, o.Flags & 0x8000));
    }

    [Fact]
    public void NotAMetafileThrows()
    {
        Assert.Throws<CorruptDataException>(() => Metafile.Load(new MemoryStream("GIF89a not a metafile at all"u8.ToArray())));
        Assert.Throws<CorruptDataException>(() => Metafile.Load(new MemoryStream([])));
    }

    [Theory]
    [InlineData("gdi-shapes-basic.emf")]
    [InlineData("gdi-shapes-basic.wmf")]
    [InlineData("plus-shapes.dual.emf")]
    public void TruncatedFilesKeepTheirRecordsAndWarn(string name)
    {
        byte[] raw = File.ReadAllBytes(TestFiles.SyntheticPath(name));
        Metafile whole = Metafile.Load(new MemoryStream(raw));
        for (int cut = raw.Length - 1; cut > raw.Length * 2 / 3; cut -= 7)
        {
            Metafile part = Metafile.Load(new MemoryStream(raw[..cut]));
            Assert.Contains(part.Warnings, w => w.Id is "Metafile.TruncatedRecord" or "Metafile.NoEndRecord");
            Assert.True(part.Content.Records.Count < whole.Content.Records.Count);
            Assert.Equal(whole.Frame, part.Frame);
        }
    }

    [Fact]
    public void OversizedRecordsStopTheWalk()
    {
        byte[] raw = File.ReadAllBytes(TestFiles.SyntheticPath("gdi-shapes-basic.emf"));
        Metafile whole = Metafile.Load(new MemoryStream(raw));
        MetaRecord second = whole.Content.Records[1];
        BitConverter.TryWriteBytes(raw.AsSpan(second.Offset + 4), 0x7FFFFFF0);
        Metafile broken = Metafile.Load(new MemoryStream(raw));
        Assert.Single(broken.Content.Records);
        Assert.Contains(broken.Warnings, w => w.Id == "Metafile.TruncatedRecord");
    }

    [Fact]
    public void FileLimitApplies()
    {
        byte[] raw = File.ReadAllBytes(TestFiles.SyntheticPath("gdi-shapes-basic.emf"));
        MetafileLoadOptions small = new() { Context = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxTotalBytes = 100 } } };
        Assert.Throws<LimitExceededException>(() => Metafile.Load(new MemoryStream(raw), small));
    }
}
