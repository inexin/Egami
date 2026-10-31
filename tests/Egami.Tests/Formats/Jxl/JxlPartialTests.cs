using System.Text.Json;

using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Tests.Formats.Jxl;

/// <summary>
/// JPEG XL extras PR D: a file decoded while its bytes arrive. Each cut of tools/make-jxl-partial-references.py shows a preview
/// exactly when djxl's progressive flush does, within one level at 8 bits of djxl's picture; the rows that come out once all
/// bytes have arrived are a whole-file load's, whatever the chunk size.
/// </summary>
public class JxlPartialTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "jxl");
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "jxl", "partial");

    private static readonly Lazy<JsonElement> Json = new(() => JsonDocument.Parse(File.ReadAllText(Path.Combine(References, "partial.json"))).RootElement);

    public static TheoryData<string, string> Cuts()
    {
        TheoryData<string, string> data = [];
        foreach (JsonProperty file in Json.Value.EnumerateObject())
        {
            foreach (JsonProperty cut in file.Value.EnumerateObject())
            {
                data.Add(file.Name, cut.Name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cuts))]
    public void PreviewMatchesDjxl(string name, string pct)
    {
        JsonElement cut = Json.Value.GetProperty(name).GetProperty(pct);
        byte[] data = File.ReadAllBytes(Path.Combine(Images, name + ".jxl"));
        using RasterDecoder decoder = RasterDecoder.OpenIncremental();
        decoder.Append(data.AsSpan(0, cut.GetProperty("bytes").GetInt32()));
        Assert.True(decoder.HeaderRead);
        using Raster<Rgba8> preview = Raster.Create<Rgba8>(decoder.Info.Width, decoder.Info.Height);
        bool shown = decoder.RenderPreview(preview);
        Assert.Equal(cut.GetProperty("djxl").GetBoolean(), shown);
        if (!shown)
        {
            return;
        }

        using Raster<Rgba8> djxl = Raster.Load<Rgba8>(Path.Combine(References, $"{name}.cut{pct}.png"));
        int worst = 0;
        for (int y = 0; y < djxl.Height; y++)
        {
            Span<Rgba8> a = preview.Row(y), b = djxl.Row(y);
            for (int x = 0; x < djxl.Width; x++)
            {
                worst = Math.Max(worst, Math.Max(Math.Abs(a[x].R - b[x].R), Math.Max(Math.Abs(a[x].G - b[x].G), Math.Abs(a[x].B - b[x].B))));
            }
        }

        Assert.True(worst <= 1, $"{name} at {pct} %: off by {worst} levels from djxl.");
    }

    [Theory]
    [InlineData("duck-d1", 1)]
    [InlineData("duck-d1-progressive", 4096)]
    [InlineData("duck-lossless-e7", 65536)]
    [InlineData("duck-alpha-d1", 777)]
    public void RowsAreAWholeLoad(string name, int chunk)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Images, name + ".jxl"));
        using Raster<Rgba16> whole = Raster.Load<Rgba16>(new MemoryStream(data));
        using RasterDecoder decoder = RasterDecoder.OpenIncremental();
        using Raster<Rgba16> rows = Raster.Create<Rgba16>(whole.Width, whole.Height);
        int read = 0;
        for (int at = 0; at < data.Length; at += chunk)
        {
            decoder.Append(data.AsSpan(at, Math.Min(chunk, data.Length - at)));
            if (decoder.HeaderRead && at % 16384 < chunk)
            {
                // Rows wait for the end; a preview on the way must not move the decode on.
                decoder.RenderPreview(rows);
                read += decoder.ReadRows(rows);
            }
        }

        decoder.EndOfInput();
        Assert.Equal(0, read);
        Assert.Equal(whole.Height, decoder.ReadRows(rows));
        for (int y = 0; y < whole.Height; y++)
        {
            Assert.True(whole.Row(y).SequenceEqual(rows.Row(y)), $"Row {y} differs.");
        }
    }
}
