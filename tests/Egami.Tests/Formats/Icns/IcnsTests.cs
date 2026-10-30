using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

using Egami.Formats;
using Egami.Formats.Icns;
using Egami.Pixels;

namespace Egami.Tests.Formats.Icns;

/// <summary>ICNS written and read back, and hand-built families of every element kind the reader decodes.</summary>
public class IcnsTests
{
    [Fact]
    public void SavedIconsRoundTrip()
    {
        using Raster<Rgba8> source = Random(32, 32);
        byte[] data = Save(source);
        Assert.Equal("icns", Encoding.Latin1.GetString(data, 0, 4));
        Assert.Equal((uint)data.Length, BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4)));
        Assert.Equal("TOC ", Encoding.Latin1.GetString(data, 8, 4));
        Assert.Same(IcnsFormat.Info, Raster.Probe(data).Format);
        using Raster<Rgba8> back = Raster.Load<Rgba8>(data);
        Assert.Equal(Bytes(source), Bytes(back));
        IcnsDetails details = Assert.IsType<IcnsDetails>(back.Metadata.FormatDetails);
        Assert.Equal("icp5", details.Type);
        Assert.Equal(IcnsEntryEncoding.Png, details.Encoding);
    }

    [Fact]
    public void MacSizesWriteEveryEntryAndLoadPicksTheLargest()
    {
        using Raster<Rgba8> source = Raster.Create<Rgba8>(300, 200, new Rgba8(9, 9, 9, 255));
        byte[] data = Save(source, new IcnsWriterOptions { Sizes = IcnsWriterOptions.MacSizes });
        RasterInfo info = Raster.Probe(data);
        Assert.Equal(7, info.ImageCount);
        Assert.Equal(1024, info.Width);
        using Raster raster = Raster.Load(data);
        IcnsDetails details = Assert.IsType<IcnsDetails>(raster.Metadata.FormatDetails);
        Assert.Equal(["icp4", "icp5", "icp6", "ic07", "ic08", "ic09", "ic10"], details.Entries.Select(e => e.Type));
        Assert.Equal("ic10", details.Type);
        using RasterList all = Raster.LoadAll(data);
        Assert.Equal([16, 32, 64, 128, 256, 512, 1024], all.Select(r => r.Width));

        // A 300 × 200 raster is fitted inside each square and centred on transparency.
        using Raster<Rgba8> small = Raster.Load<Rgba8>(data, options: new IcnsReaderOptions { Entry = 0 });
        Assert.Equal(0, small.Row(0)[2].A);
        Assert.Equal(255, small.Row(8)[2].A);
        using Raster<Rgba8> enough = Raster.Load<Rgba8>(data, options: new ReaderOptions { MinimumSize = new Size(100, 100) });
        Assert.Equal(128, enough.Width);
    }

    [Fact]
    public void OtherSizesAreFittedToAnEntrySize()
    {
        List<EgamiDiagnostic> warnings = [];
        using Raster<Rgba8> source = Raster.Create<Rgba8>(40, 40, new Rgba8(1, 2, 3, 255), EgamiContext.Default with { Diagnostics = warnings.Add });
        byte[] data = Save(source);
        Assert.Contains(warnings, w => w.Id == "Save.IcnsFitted");
        Assert.Equal(32, Raster.Probe(data).Width);
    }

    [Fact]
    public void BadSizesThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new IcnsWriterOptions { Sizes = [48] });
        Assert.Throws<ArgumentException>(() => new IcnsWriterOptions { Sizes = [16, 16] });
        using Raster<Rgba8> a = Random(16, 16);
        using Raster<Rgba8> b = Random(17, 17);
        using MemoryStream stream = new();
        Assert.Throws<ArgumentException>(() => Raster.SaveAll([a, b], stream, IcnsFormat.Info));
    }

    [Fact]
    public void ReadsRgbWithItsAlphaMask()
    {
        using Raster<Rgba8> expected = Random(32, 32);
        byte[] planes = [.. Packed(expected, 0), .. Packed(expected, 1), .. Packed(expected, 2)];
        byte[] alpha = new byte[32 * 32];
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                alpha[(y * 32) + x] = expected.Row(y)[x].A;
            }
        }

        byte[] data = Family(("il32", planes), ("l8mk", alpha));
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(data, options: new ReaderOptions { Recovery = RecoveryMode.Strict });
        Assert.Equal(Bytes(expected), Bytes(raster));
        Assert.Equal(IcnsEntryEncoding.Rgb, ((IcnsDetails)raster.Metadata.FormatDetails!).Encoding);
        Assert.Single(((IcnsDetails)raster.Metadata.FormatDetails!).Entries);
    }

    [Fact]
    public void ReadsIt32AndArgb()
    {
        using Raster<Rgba8> rgb = Random(128, 128);
        byte[] it32 = [0, 0, 0, 0, .. Packed(rgb, 0), .. Packed(rgb, 1), .. Packed(rgb, 2)];
        using Raster<Rgba8> argb = Random(16, 16);
        byte[] ic04 = [.. "ARGB"u8, .. Packed(argb, 3), .. Packed(argb, 0), .. Packed(argb, 1), .. Packed(argb, 2)];
        byte[] data = Family(("ic04", ic04), ("it32", it32));
        using Raster<Rgba8> large = Raster.Load<Rgba8>(data, options: new ReaderOptions { Recovery = RecoveryMode.Strict });
        Assert.All(Enumerable.Range(0, 128), y => Assert.True(Opaque(rgb.Row(y)).SequenceEqual(large.Row(y).ToArray())));
        using Raster<Rgba8> small = Raster.Load<Rgba8>(data, options: new IcnsReaderOptions { Entry = 0, Recovery = RecoveryMode.Strict });
        Assert.Equal(Bytes(argb), Bytes(small));
    }

    [Fact]
    public void ReadsTheOldIconsWithTheirMask()
    {
        // ics8: index 0 is white and 255 black; ics#: the icon, then the mask.
        byte[] ics8 = new byte[16 * 16];
        byte[] mono = new byte[64];
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                ics8[(y * 16) + x] = (byte)(x < 8 ? 0 : 255);
            }

            mono[(y * 2) + 1] = 0xFF; // the icon's right half is black
            mono[32 + (y * 2)] = (byte)(y < 8 ? 0xFF : 0x00); // only the top half is opaque
            mono[32 + (y * 2) + 1] = (byte)(y < 8 ? 0xFF : 0x00);
        }

        byte[] data = Family(("ics#", mono), ("ics8", ics8));
        using RasterList all = Raster.LoadAll<Rgba8>(data);
        Assert.Equal(2, all.Count);
        foreach (Raster entry in all)
        {
            Raster<Rgba8> raster = (Raster<Rgba8>)entry;
            Assert.Equal(new Rgba8(255, 255, 255, 255), raster.Row(0)[0]);
            Assert.Equal(new Rgba8(0, 0, 0, 255), raster.Row(0)[15]);
            Assert.Equal(0, raster.Row(15)[0].A);
        }

        // The 8-bit icon is the deeper of the two.
        using Raster best = Raster.Load(data);
        Assert.Equal("ics8", ((IcnsDetails)best.Metadata.FormatDetails!).Type);
        Assert.Equal(new Rgba8(0x99, 0xCC, 0x66, 255), FromIndex(0x51));
    }

    [Fact]
    public void DamagedFilesWarnOrThrow()
    {
        using Raster<Rgba8> source = Random(32, 32);
        byte[] data = Save(source, new IcnsWriterOptions { Sizes = [16, 32] });
        byte[] cut = data[..^10];
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(cut);
        Assert.Equal(16, raster.Width);
        Assert.Contains(raster.Metadata.Warnings, w => w.Id == "Icns.Truncated");
        Assert.Contains(raster.Metadata.Warnings, w => w.Id == "Icns.ElementCut");
        Assert.ThrowsAny<EgamiException>(() => Raster.Load(cut, options: new ReaderOptions { Recovery = RecoveryMode.Strict }));
        Assert.Throws<CorruptDataException>(() => Raster.Load(Family(("TOC ", new byte[8]))));
        byte[] shortRle = Family(("il32", [0x7F, 1, 2]));
        Assert.Throws<CorruptDataException>(() => Raster.Load(shortRle));
    }

    private static Rgba8 FromIndex(byte index)
    {
        byte[] icl8 = new byte[32 * 32];
        icl8.AsSpan().Fill(index);
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(Family(("icl8", icl8)));
        return raster.Row(0)[0];
    }

    private static Rgba8[] Opaque(ReadOnlySpan<Rgba8> row) => [.. row.ToArray().Select(p => p with { A = 255 })];

    /// <summary>Packs one channel (0 red to 3 alpha): runs of 3 or more repeat, the rest go in literal runs.</summary>
    private static byte[] Packed(Raster<Rgba8> raster, int channel)
    {
        List<byte> values = [];
        for (int y = 0; y < raster.Height; y++)
        {
            foreach (Rgba8 p in raster.Row(y))
            {
                values.Add(channel switch { 0 => p.R, 1 => p.G, 2 => p.B, _ => p.A });
            }
        }

        List<byte> packed = [];
        int i = 0;
        while (i < values.Count)
        {
            int run = 1;
            while (i + run < values.Count && run < 130 && values[i + run] == values[i])
            {
                run++;
            }

            if (run >= 3)
            {
                packed.Add((byte)(run + 125));
                packed.Add(values[i]);
                i += run;
                continue;
            }

            int start = i;
            while (i < values.Count && i - start < 128 && !(i + 2 < values.Count && values[i] == values[i + 1] && values[i] == values[i + 2]))
            {
                i++;
            }

            packed.Add((byte)(i - start - 1));
            packed.AddRange(values.GetRange(start, i - start));
        }

        return [.. packed];
    }

    private static byte[] Family(params (string Type, byte[] Data)[] elements)
    {
        List<byte> data = [.. "icns"u8, 0, 0, 0, 0];
        foreach ((string type, byte[] body) in elements)
        {
            data.AddRange(Encoding.Latin1.GetBytes(type));
            byte[] length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)(8 + body.Length));
            data.AddRange(length);
            data.AddRange(body);
        }

        byte[] file = [.. data];
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(4), (uint)file.Length);
        return file;
    }

    private static Raster<Rgba8> Random(int width, int height)
    {
        Random random = new((width * 31) + height);
        Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                // Flat stretches as well as noise, so the run-length tests see both kinds of run.
                row[x] = x % 16 < 6 ? new Rgba8(10, 20, 30, 255) : new Rgba8((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            }
        }

        return raster;
    }

    private static byte[] Save<TPixel>(Raster<TPixel> raster, IcnsWriterOptions? options = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using MemoryStream stream = new();
        raster.Save(stream, IcnsFormat.Info, options ?? IcnsWriterOptions.Default);
        return stream.ToArray();
    }

    private static byte[] Bytes<TPixel>(Raster<TPixel> raster)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        List<byte> bytes = [];
        for (int y = 0; y < raster.Height; y++)
        {
            bytes.AddRange(MemoryMarshal.AsBytes(raster.Row(y)).ToArray());
        }

        return [.. bytes];
    }
}
