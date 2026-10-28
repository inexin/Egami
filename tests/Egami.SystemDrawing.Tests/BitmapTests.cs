using System.Runtime.InteropServices;

namespace Egami.SystemDrawing.Tests;

/// <summary>
/// Bitmaps in every pixel format, checked against what GDI+ gives on Windows (egami-compare, sysdrawing BitmapProbes): the
/// expected values here are copied from those measurements.
/// </summary>
public class BitmapTests
{
    private static readonly Color[] Colors =
    [
        Color.FromArgb(255, 255, 0, 0), Color.FromArgb(255, 10, 20, 30), Color.FromArgb(128, 200, 100, 50), Color.FromArgb(0, 1, 2, 3),
        Color.FromArgb(255, 255, 255, 255), Color.FromArgb(255, 0, 0, 0), Color.FromArgb(255, 255, 128, 0), Color.FromArgb(1, 0, 0, 0),
        Color.FromArgb(254, 127, 129, 3), Color.FromArgb(64, 255, 255, 255), Color.FromArgb(255, 7, 8, 9), Color.FromArgb(200, 33, 66, 99),
    ];

    /// <summary>What GetPixel returns after SetPixel of each of <see cref="Colors"/>, per format, as GDI+ returns it.</summary>
    private static readonly Dictionary<PixelFormat, uint[]> GdiPlusReadBack = new()
    {
        [PixelFormat.Format16bppRgb555] = [0xFFFF0000u, 0xFF081018u, 0xFFCE6331u, 0xFF000000u, 0xFFFFFFFFu, 0xFF000000u, 0xFFFF8400u, 0xFF000000u, 0xFF7B8400u, 0xFFFFFFFFu, 0xFF000808u, 0xFF214263u],
        [PixelFormat.Format16bppRgb565] = [0xFFFF0000u, 0xFF081418u, 0xFFCE6531u, 0xFF000000u, 0xFFFFFFFFu, 0xFF000000u, 0xFFFF8200u, 0xFF000000u, 0xFF7B8200u, 0xFFFFFFFFu, 0xFF000808u, 0xFF214163u],
        [PixelFormat.Format16bppArgb1555] = [0xFFFF0000u, 0xFF081018u, 0xFFCE6331u, 0x00000000u, 0xFFFFFFFFu, 0xFF000000u, 0xFFFF8400u, 0x00000000u, 0xFF7B8400u, 0x00FFFFFFu, 0xFF000808u, 0xFF214263u],
        [PixelFormat.Format24bppRgb] = [0xFFFF0000u, 0xFF0A141Eu, 0xFFC86432u, 0xFF010203u, 0xFFFFFFFFu, 0xFF000000u, 0xFFFF8000u, 0xFF000000u, 0xFF7F8103u, 0xFFFFFFFFu, 0xFF070809u, 0xFF214263u],
        [PixelFormat.Format32bppRgb] = [0xFFFF0000u, 0xFF0A141Eu, 0xFFC86432u, 0xFF010203u, 0xFFFFFFFFu, 0xFF000000u, 0xFFFF8000u, 0xFF000000u, 0xFF7F8103u, 0xFFFFFFFFu, 0xFF070809u, 0xFF214263u],
        [PixelFormat.Format32bppArgb] = [0xFFFF0000u, 0xFF0A141Eu, 0x80C86432u, 0x00010203u, 0xFFFFFFFFu, 0xFF000000u, 0xFFFF8000u, 0x01000000u, 0xFE7F8103u, 0x40FFFFFFu, 0xFF070809u, 0xC8214263u],
        [PixelFormat.Format32bppPArgb] = [0xFFFF0000u, 0xFF0A141Eu, 0x80C76331u, 0x00000000u, 0xFFFFFFFFu, 0xFF000000u, 0xFFFF8000u, 0x01000000u, 0xFE7F8003u, 0x40FFFFFFu, 0xFF070809u, 0xC8214263u],
        [PixelFormat.Format48bppRgb] = [0xFFFF0000u, 0xFF0A141Eu, 0xFFC86432u, 0xFF010203u, 0xFFFFFFFFu, 0xFF000000u, 0xFFFF8000u, 0xFF000000u, 0xFF7F8103u, 0xFFFFFFFFu, 0xFF070809u, 0xFF214263u],
        [PixelFormat.Format64bppArgb] = [0xFFFF0000u, 0xFF0A141Eu, 0x7FC86432u, 0x00010203u, 0xFFFFFFFFu, 0xFF000000u, 0xFFFF8000u, 0x00000000u, 0xFD7F8103u, 0x3FFFFFFFu, 0xFF070809u, 0xC7214263u],
        [PixelFormat.Format64bppPArgb] = [0xFFFF0000u, 0xFF0A141Eu, 0x7FC86432u, 0x00000000u, 0xFFFFFFFFu, 0xFF000000u, 0xFFFF8000u, 0x00000000u, 0xFD7F8102u, 0x3FFFFFFFu, 0xFF070809u, 0xC7214263u],
    };

    /// <summary>The bytes GDI+ stores for <see cref="Colors"/> in a few formats.</summary>
    private static readonly Dictionary<PixelFormat, string> GdiPlusStored = new()
    {
        [PixelFormat.Format16bppRgb555] = "007C430486650000FF7F0000007E0000003EFF7F21000C11",
        [PixelFormat.Format16bppArgb1555] = "00FC438486E50000FFFF008000FE000000BEFF7F21800C91",
        [PixelFormat.Format32bppRgb] = "0000FFFF1E140AFF3264C88003020100FFFFFFFF000000FF0080FFFF0000000103817FFEFFFFFF40090807FF634221C8",
        [PixelFormat.Format32bppPArgb] = "0000FFFF1E140AFF1932648000000000FFFFFFFF000000FF0080FFFF0000000103807FFE40404040090807FF4E341AC8",
        [PixelFormat.Format48bppRgb] = "0000000000206A0039001900050114047C120700050002000020002000200000000000000000E806002000000000000007000607CB06002000200020160014001100FE03BE017D00",
    };

    public static TheoryData<PixelFormat> DirectFormats() => [.. GdiPlusReadBack.Keys];

    [Theory]
    [MemberData(nameof(DirectFormats))]
    public void SetPixelThenGetPixelGivesWhatGdiPlusGives(PixelFormat format)
    {
        using Bitmap bitmap = new(Colors.Length, 1, format);
        for (int i = 0; i < Colors.Length; i++)
        {
            bitmap.SetPixel(i, 0, Colors[i]);
        }

        Assert.Equal(GdiPlusReadBack[format], Enumerable.Range(0, Colors.Length).Select(i => (uint)bitmap.GetPixel(i, 0).ToArgb()));
        if (GdiPlusStored.TryGetValue(format, out string? stored))
        {
            Assert.Equal(stored, Convert.ToHexString(Row(bitmap, format)));
        }
    }

    [Fact]
    public void StridesArePaddedToFourBytes()
    {
        int[] expected48 = [8, 12, 20, 24, 32, 36, 44, 48, 56];
        int[] expected24 = [4, 8, 12, 12, 16, 20, 24, 24, 28];
        int[] expected1 = [4, 4, 4, 4, 4, 4, 4, 4, 4];
        for (int width = 1; width <= 9; width++)
        {
            Assert.Equal(expected48[width - 1], Stride(width, PixelFormat.Format48bppRgb));
            Assert.Equal(expected24[width - 1], Stride(width, PixelFormat.Format24bppRgb));
            Assert.Equal(expected1[width - 1], Stride(width, PixelFormat.Format1bppIndexed));
        }

        static int Stride(int width, PixelFormat format)
        {
            using Bitmap b = new(width, 2, format);
            BitmapData data = b.LockBits(new Rectangle(0, 0, width, 2), ImageLockMode.ReadOnly, format);
            b.UnlockBits(data);
            return data.Stride;
        }
    }

    [Fact]
    public void NewBitmapsHaveGdiPlusDefaults()
    {
        using Bitmap b = new(5, 3);
        Assert.Equal(PixelFormat.Format32bppArgb, b.PixelFormat);
        Assert.Equal((96f, 96f), (b.HorizontalResolution, b.VerticalResolution));
        Assert.Equal(ImageFormat.MemoryBmp, b.RawFormat);
        Assert.Equal((int)ImageFlags.HasAlpha, b.Flags);
        Assert.Equal(Color.FromArgb(0), b.GetPixel(0, 0));
        Assert.Equal([FrameDimension.Page.Guid], b.FrameDimensionsList);
        GraphicsUnit unit = GraphicsUnit.Inch;
        Assert.Equal(new RectangleF(0, 0, 5, 3), b.GetBounds(ref unit));
        Assert.Equal(GraphicsUnit.Pixel, unit);

        using Bitmap indexed = new(1, 1, PixelFormat.Format8bppIndexed);
        Assert.Equal(256, indexed.Palette.Entries.Length);
        Assert.Equal((int)PaletteFlags.Halftone, indexed.Palette.Flags);
        Assert.Equal(Color.FromArgb(0), indexed.Palette.Entries[16]);
        Assert.Equal(Color.FromArgb(unchecked((int)0xFF000033)), indexed.Palette.Entries[41]);
        Assert.Throws<InvalidOperationException>(() => indexed.SetPixel(0, 0, Color.Red));
        using Bitmap gray = new(1, 1, PixelFormat.Format16bppGrayScale);
        Assert.Throws<ArgumentException>(() => gray.GetPixel(0, 0));
    }

    [Fact]
    public void BadArgumentsThrowWhatGdiPlusThrows()
    {
        Assert.Throws<ArgumentException>(() => new Bitmap(0, 1));
        Assert.Throws<ArgumentException>(() => new Bitmap(1, 1, PixelFormat.Undefined));
        using Bitmap b = new(2, 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => b.SetPixel(2, 0, Color.Red));
        Assert.Throws<ArgumentException>(() => b.LockBits(new Rectangle(1, 1, 2, 2), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb));
        Assert.Throws<ExternalException>(() => b.Clone(new Rectangle(10, 0, 1, 1), PixelFormat.Format32bppArgb));
        Assert.Throws<ArgumentException>(() => b.SetResolution(0, 72));
        b.Dispose();
        Assert.Throws<ArgumentException>(() => b.Width);
    }

    [Fact]
    public void ALockInTheBitmapsFormatIsItsMemoryAndOthersAreCopiesWrittenBack()
    {
        using Bitmap b = new(3, 2, PixelFormat.Format24bppRgb);
        BitmapData own = b.LockBits(new Rectangle(1, 0, 2, 2), ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        Assert.Equal(12, own.Stride);
        Marshal.WriteByte(own.Scan0, 2, 200);
        Assert.Throws<ExternalException>(() => b.GetPixel(0, 0));
        Assert.Throws<ExternalException>(() => b.LockBits(new Rectangle(0, 0, 1, 1), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb));
        b.UnlockBits(own);
        Assert.Equal(Color.FromArgb(255, 200, 0, 0), b.GetPixel(1, 0));
        Assert.Throws<ExternalException>(() => b.UnlockBits(own));

        // GDI+ measured: writing 32-bit ARGB into a 24-bit bitmap drops alpha on unlock.
        BitmapData copy = b.LockBits(new Rectangle(1, 1, 2, 1), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        Assert.Equal(8, copy.Stride);
        Marshal.Copy(new byte[] { 10, 20, 30, 255, 40, 50, 60, 128 }, 0, copy.Scan0, 8);
        b.UnlockBits(copy);
        Assert.Equal(Color.FromArgb(255, 30, 20, 10), b.GetPixel(1, 1));
        Assert.Equal(Color.FromArgb(255, 60, 50, 40), b.GetPixel(2, 1));
    }

    [Fact]
    public void AUserBufferReceivesTheLockedPixels()
    {
        using Bitmap b = new(2, 2);
        b.SetPixel(1, 1, Color.FromArgb(255, 1, 2, 3));
        byte[] buffer = new byte[32];
        GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            BitmapData mine = new() { Scan0 = handle.AddrOfPinnedObject(), Stride = 16, Width = 2, Height = 2, PixelFormat = PixelFormat.Format32bppArgb };
            BitmapData got = b.LockBits(new Rectangle(0, 0, 2, 2), ImageLockMode.ReadWrite | ImageLockMode.UserInputBuffer, PixelFormat.Format32bppArgb, mine);
            Assert.Same(mine, got);
            Assert.Equal("030201FF", Convert.ToHexString(buffer, 20, 4));
            buffer[0] = 99;
            buffer[3] = 255;
            b.UnlockBits(got);
            Assert.Equal(Color.FromArgb(255, 0, 0, 99), b.GetPixel(0, 0));
        }
        finally
        {
            handle.Free();
        }
    }

    [Fact]
    public void ClonesToIndexedFormatsUseGdiPlusHalftonePalettes()
    {
        using Bitmap source = new(8, 1);
        for (int i = 0; i < 8; i++)
        {
            source.SetPixel(i, 0, Colors[i]);
        }

        // GDI+ measured: the 216-color cube plus 8 system colors; (10, 20, 30) maps to black through the 16-level table.
        using Bitmap eight = source.Clone(new Rectangle(0, 0, 8, 1), PixelFormat.Format8bppIndexed);
        Assert.Equal(224, eight.Palette.Entries.Length);
        Assert.Equal("B4009D00D700C600", Convert.ToHexString(Row(eight, PixelFormat.Format8bppIndexed)));
        using Bitmap four = source.Clone(new Rectangle(0, 0, 8, 1), PixelFormat.Format4bppIndexed);
        Assert.Equal("0000FFFF000000FF008080FF000000FFFFFFFFFF000000FF00FFFFFF000000FF", Convert.ToHexString(Row(four, PixelFormat.Format32bppArgb)));

        // A lock in 8bppIndexed maps to the default palette, VGA colors first.
        Assert.Equal("0900C5000F00EE00", Convert.ToHexString(Row(source, PixelFormat.Format8bppIndexed)));
    }

    [Fact]
    public void ConversionsBetweenFormatsMatchGdiPlus()
    {
        using Bitmap source = new(8, 1);
        for (int i = 0; i < 8; i++)
        {
            source.SetPixel(i, 0, Colors[i]);
        }

        using Bitmap premultiplied = source.Clone(new Rectangle(0, 0, 8, 1), PixelFormat.Format32bppPArgb);
        Assert.Equal("0000FF1E140A193264000000FFFFFF0000000080FF000000", Convert.ToHexString(Row(premultiplied, PixelFormat.Format24bppRgb)));
        Assert.Equal("00000000002000206A00390019000020FC00FE03471210100000000000000000002000200020002000000000000000200000E806002000200000000000002000", Convert.ToHexString(Row(premultiplied, PixelFormat.Format64bppArgb)));
        using Bitmap linear = source.Clone(new Rectangle(0, 0, 8, 1), PixelFormat.Format64bppPArgb);
        Assert.Equal("0000FFFF1E140AFF3264C87F00000000FFFFFFFF000000FF0080FFFF00000000", Convert.ToHexString(Row(linear, PixelFormat.Format32bppArgb)));
    }

    [Fact]
    public void MakeTransparentKeysTheBottomLeftColorAndRoundTripsThroughPremultipliedColor()
    {
        using Bitmap b = new(8, 1);
        for (int i = 0; i < 8; i++)
        {
            b.SetPixel(i, 0, Colors[i]);
        }

        b.MakeTransparent();
        Assert.Equal(PixelFormat.Format32bppArgb, b.PixelFormat);
        Assert.Equal("000000001E140AFF3163C78000000000FFFFFFFF000000FF0080FFFF00000001", Convert.ToHexString(Row(b, PixelFormat.Format32bppArgb)));
    }

    [Theory]
    [InlineData(RotateFlipType.Rotate90FlipNone, "1x8", "0000FFFF")]
    [InlineData(RotateFlipType.Rotate180FlipNone, "8x1", "000000010080FFFF000000FFFFFFFFFF030201003264C8801E140AFF0000FFFF")]
    [InlineData(RotateFlipType.Rotate270FlipNone, "1x8", "00000001")]
    [InlineData(RotateFlipType.Rotate180FlipX, "8x1", "0000FFFF1E140AFF3264C88003020100FFFFFFFF000000FF0080FFFF00000001")]
    public void RotateFlipMovesPixels(RotateFlipType type, string size, string firstRow)
    {
        using Bitmap b = new(8, 1);
        for (int i = 0; i < 8; i++)
        {
            b.SetPixel(i, 0, Colors[i]);
        }

        b.RotateFlip(type);
        Assert.Equal(size, $"{b.Width}x{b.Height}");
        Assert.Equal(firstRow, Convert.ToHexString(Row(b, PixelFormat.Format32bppArgb)));
    }

    [Fact]
    public void BitmapsOverCallerMemoryReadAndWriteInPlace()
    {
        byte[] memory = new byte[8 * 2];
        GCHandle handle = GCHandle.Alloc(memory, GCHandleType.Pinned);
        try
        {
            using Bitmap b = new(2, 2, 8, PixelFormat.Format32bppArgb, handle.AddrOfPinnedObject());
            b.SetPixel(1, 1, Color.FromArgb(4, 3, 2, 1));
            Assert.Equal("01020304", Convert.ToHexString(memory, 12, 4));
        }
        finally
        {
            handle.Free();
        }
    }

    [Fact]
    public void WindowsHandlesThrowPlatformNotSupported()
    {
        using Bitmap b = new(1, 1);
        PlatformNotSupportedException e = Assert.Throws<PlatformNotSupportedException>(() => b.GetHbitmap());
        Assert.Contains("LockBits", e.Message);
        Assert.Throws<PlatformNotSupportedException>(() => b.GetHicon());
        Assert.Throws<PlatformNotSupportedException>(() => Image.FromHbitmap(IntPtr.Zero));
        Assert.Throws<PlatformNotSupportedException>(() => Icon.FromHandle(IntPtr.Zero));
    }

    private static byte[] Row(Bitmap b, PixelFormat format)
    {
        BitmapData data = b.LockBits(new Rectangle(0, 0, b.Width, 1), ImageLockMode.ReadOnly, format);
        try
        {
            byte[] row = new byte[((b.Width * Image.GetPixelFormatSize(format)) + 7) / 8];
            Marshal.Copy(data.Scan0, row, 0, row.Length);
            return row;
        }
        finally
        {
            b.UnlockBits(data);
        }
    }
}
