using System.Diagnostics;
using System.Globalization;

using Egami.Formats.Jxl;

namespace Egami.Tests.Formats.Jxl;

/// <summary>
/// Recompresses every JPEG in EGAMI_INTEROP_DIR/jxl-jpeg/src to out/NAME.jxl and rebuilds it to out/NAME.egami.jpg, for
/// tools/check-jxl-recompress.py to check with djxl against the originals and cjxl's files; out/results.txt lists each
/// file's sizes, time and outcome. Runs only when EGAMI_INTEROP_DIR is set.
/// </summary>
public class JxlRecompressInteropFiles
{
    [Fact]
    public void WriteFiles()
    {
        string? root = Environment.GetEnvironmentVariable("EGAMI_INTEROP_DIR");
        Assert.SkipUnless(root is not null, "EGAMI_INTEROP_DIR is not set.");
        string source = Path.Combine(root!, "jxl-jpeg", "src");
        string output = Path.Combine(root!, "jxl-jpeg", "out");
        Directory.CreateDirectory(output);
        List<string> lines = [];
        foreach (string path in Directory.EnumerateFiles(source, "*.jpg").Order())
        {
            string name = Path.GetFileNameWithoutExtension(path);
            byte[] jpeg = File.ReadAllBytes(path);
            string jxlPath = Path.Combine(output, name + ".jxl");
            File.Delete(jxlPath);
            try
            {
                // Warm, then timed.
                using MemoryStream jxl = new();
                JxlImage.RecompressJpeg(new MemoryStream(jpeg), jxl);
                Stopwatch sw = Stopwatch.StartNew();
                jxl.SetLength(0);
                JxlImage.RecompressJpeg(new MemoryStream(jpeg), jxl);
                double ms = sw.Elapsed.TotalMilliseconds;
                File.WriteAllBytes(jxlPath, jxl.ToArray());
                jxl.Position = 0;
                using MemoryStream back = new();
                string same;
                try
                {
                    JxlImage.ReconstructJpeg(jxl, back);
                    File.WriteAllBytes(Path.Combine(output, name + ".egami.jpg"), back.ToArray());
                    same = back.ToArray().AsSpan().SequenceEqual(jpeg) ? "same" : "differs";
                }
                catch (Exception e)
                {
                    same = "rebuild-failed:" + e.GetType().Name;
                }

                lines.Add(string.Create(CultureInfo.InvariantCulture, $"{name} {jpeg.Length} {jxl.Length} {ms:F1} {same}"));
            }
            catch (Exception e) when (e is UnsupportedFormatException or CorruptDataException)
            {
                lines.Add($"{name} {jpeg.Length} 0 0 refused:{e.GetType().Name}");
            }
            catch (Exception e)
            {
                lines.Add($"{name} {jpeg.Length} 0 0 crashed:{(e is AggregateException a ? a.InnerExceptions[0] : e).GetType().Name}");
            }
        }

        File.WriteAllLines(Path.Combine(output, "results.txt"), lines);
    }
}
