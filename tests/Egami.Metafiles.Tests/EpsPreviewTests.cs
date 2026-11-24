using System.Buffers.Binary;
using System.Globalization;
using System.Text;

using Egami.Formats;
using Egami.Formats.Eps;
using Egami.Pixels;

namespace Egami.Metafiles.Tests;

/// <summary>EPS metafile previews: core Egami hands them to MetafileFormat, which draws them at the bounding box size.</summary>
public class EpsPreviewTests
{
    private static readonly EgamiContext s_context = EgamiContext.Default.WithFormat(MetafileFormat.Info);

    /// <summary>A DOS binary EPS of a PostScript header with the bounding box given and the metafile as its preview.</summary>
    private static byte[] Eps(byte[] wmf, double width, double height)
    {
        string box = string.Create(CultureInfo.InvariantCulture, $"0 0 {Math.Ceiling(width)} {Math.Ceiling(height)}");
        string hiRes = string.Create(CultureInfo.InvariantCulture, $"0 0 {width} {height}");
        byte[] ps = Encoding.ASCII.GetBytes($"%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: {box}\n%%HiResBoundingBox: {hiRes}\n%%EndComments\nshowpage\n");
        byte[] file = new byte[30 + ps.Length + wmf.Length];
        Span<byte> header = file;
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0xC6D3D0C5);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 30);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)ps.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], (uint)(30 + ps.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], (uint)wmf.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header[28..], 0xFFFF);
        ps.CopyTo(file, 30);
        wmf.CopyTo(file, 30 + ps.Length);
        return file;
    }

    [Theory]
    [InlineData("gdi-shapes-basic.wmf")]
    [InlineData("gdi-shapes-basic.plain.wmf")]
    [InlineData("gdi-brush-hatch.plain.wmf")]
    public void TheMetafilePreviewMatchesRasterize(string name)
    {
        byte[] wmf = File.ReadAllBytes(TestFiles.SyntheticPath(name));
        Metafile metafile = Metafile.Load(wmf);

        // The bounding box is the metafile's own size, so the preview is drawn as Rasterize draws it at that size.
        byte[] eps = Eps(wmf, metafile.Size.X, metafile.Size.Y);
        RasterInfo info = Raster.Probe(eps, s_context);
        Assert.Same(EpsFormat.Info, info.Format);

        using Raster loaded = Raster.Load(eps, s_context);
        Raster<Rgba8> raster = Assert.IsType<Raster<Rgba8>>(loaded);
        Assert.Equal((info.Width, info.Height), (raster.Width, raster.Height));
        Assert.Equal(EpsPreviewKind.Wmf, Assert.IsType<EpsDetails>(raster.Metadata.FormatDetails).Preview);
        Assert.Equal("EPS", raster.Metadata.FormatName);
        Assert.InRange(raster.Width, Math.Floor(metafile.Size.X * 96 / 72 * 0.99), Math.Ceiling(metafile.Size.X * 96 / 72 * 1.01));
        Assert.InRange(raster.Height, Math.Floor(metafile.Size.Y * 96 / 72 * 0.99), Math.Ceiling(metafile.Size.Y * 96 / 72 * 1.01));

        using Raster<Rgba8> direct = metafile.Rasterize<Rgba8>(new MetafileRenderOptions { Width = raster.Width, Height = raster.Height });
        for (int y = 0; y < raster.Height; y++)
        {
            Assert.True(direct.Row(y).SequenceEqual(raster.Row(y)), $"Row {y} differs.");
        }
    }

    [Fact]
    public void TheBoundingBoxSetsTheSize()
    {
        byte[] wmf = File.ReadAllBytes(TestFiles.SyntheticPath("gdi-shapes-basic.plain.wmf"));
        Metafile metafile = Metafile.Load(wmf);
        byte[] eps = Eps(wmf, metafile.Size.X * 2, metafile.Size.Y * 2);
        RasterInfo info = Raster.Probe(eps, s_context);
        RasterInfo own = Raster.Probe(wmf, s_context);
        Assert.InRange(info.Width, (2 * own.Width) - 2, (2 * own.Width) + 2);

        RasterInfo sharp = Raster.Probe(eps, s_context, new MetafileDecodeOptions { Dpi = 192 });
        Assert.InRange(sharp.Width, (2 * info.Width) - 2, (2 * info.Width) + 2);
    }
}
