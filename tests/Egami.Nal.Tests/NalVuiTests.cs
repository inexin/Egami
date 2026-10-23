namespace Egami.Nal.Tests;

public class NalVuiTests
{
    [Fact]
    public void ReadsTheSharedFields()
    {
        BitWriter w = new();
        w.Flag(true).U(8, 255).U(16, 4).U(16, 3);
        w.Flag(true).Flag(false);
        w.Flag(true).U(3, 1).Flag(true).Flag(true).U(8, 9).U(8, 16).U(8, 9);
        w.Flag(true).Ue(2).Ue(3);
        w.U(5, 0b10101);
        TestVui vui = TestVui.Read(new NalBitReader(w.TrailingBits().ToArray()));
        Assert.Equal((4, 3), (vui.SarWidth, vui.SarHeight));
        Assert.Equal(1, vui.VideoFormat);
        Assert.True(vui.FullRange);
        Assert.Equal((9, 16, 9), (vui.ColourPrimaries, vui.TransferCharacteristics, vui.MatrixCoefficients));
        Assert.Equal((2, 3), (vui.ChromaSampleLocation, vui.ChromaSampleLocationBottom));
        Assert.Equal(0b10101u, vui.Rest);
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 12, 11)]
    [InlineData(13, 160, 99)]
    [InlineData(14, 4, 3)]
    [InlineData(16, 2, 1)]
    [InlineData(0, 0, 0)]
    [InlineData(17, 0, 0)]
    public void MapsTheAspectRatioTable(int idc, int width, int height)
    {
        byte[] data = new BitWriter().Flag(true).U(8, idc).Flag(false).Flag(false).Flag(false).U(5, 0).TrailingBits().ToArray();
        TestVui vui = TestVui.Read(new NalBitReader(data));
        Assert.Equal((width, height), (vui.SarWidth, vui.SarHeight));
    }

    [Fact]
    public void LeavesAbsentFieldsAtTheirDefaults()
    {
        byte[] data = new BitWriter().U(4, 0).U(5, 0b11111).TrailingBits().ToArray();
        TestVui vui = TestVui.Read(new NalBitReader(data));
        Assert.Equal((0, 0), (vui.SarWidth, vui.SarHeight));
        Assert.Equal(5, vui.VideoFormat);
        Assert.False(vui.FullRange);
        Assert.Equal((2, 2, 2), (vui.ColourPrimaries, vui.TransferCharacteristics, vui.MatrixCoefficients));
        Assert.Equal((-1, -1), (vui.ChromaSampleLocation, vui.ChromaSampleLocationBottom));
        Assert.Equal(0b11111u, vui.Rest);
    }

    [Fact]
    public void LeavesAZeroExtendedRatioUnspecified()
    {
        byte[] data = new BitWriter().Flag(true).U(8, 255).U(16, 0).U(16, 5).U(3, 0).U(5, 0).TrailingBits().ToArray();
        Assert.Equal(0, TestVui.Read(new NalBitReader(data)).SarWidth);
    }

    /// <summary>A codec's VUI: the shared start, then five bits of its own.</summary>
    private sealed class TestVui : NalVui
    {
        internal uint Rest { get; private set; }

        internal static TestVui Read(NalBitReader r)
        {
            TestVui vui = new();
            vui.ReadCommon(r);
            vui.Rest = r.U(5, "rest");
            return vui;
        }
    }
}
