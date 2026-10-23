using System.Diagnostics;

using Egami.Formats.Heic;
using Egami.Formats.Heif;
using Egami.Heic.Hevc;
using Egami.Nal;

namespace Egami.Heic.Tests;

/// <summary>A rough timing of the HEVC decoder on a whole iPhone photo (48 grid tiles), with EGAMI_CORPUS=heic.</summary>
public class HevcSpeedProbe(ITestOutputHelper output)
{
    [Fact]
    public void DecodeAPhonePhoto()
    {
        if (HevcTestFiles.Cache() is not string cache)
        {
            return;
        }

        string path = Path.Combine(cache, "phones", "kashiwa-sogo", "IMG_0303.HEIC");
        HeifFile heif = HeifFile.Parse(File.ReadAllBytes(path), "HEIC");
        List<List<HevcNal>> tiles = [];
        foreach (HeifItem item in heif.Items.Where(i => i.Type == HeicFile.Hvc1 && heif.Property(i, HeicFile.HvcC) is not null && i.Hidden))
        {
            HevcConfig config = HevcConfig.Read(heif, heif.Property(item, HeicFile.HvcC)!);
            List<HevcNal> nals = [.. config.NalUnits.Select(n => HevcNal.Parse(n.Span))];
            nals.AddRange(NalFraming.SplitLengthPrefixed(heif.ItemData(item), config.LengthSize).Select(n => HevcNal.Parse(n.Span)));
            tiles.Add(nals);
        }

        for (int round = 0; round < 3; round++)
        {
            Stopwatch watch = Stopwatch.StartNew();
            foreach (List<HevcNal> nals in tiles)
            {
                HevcDecoder.Decode(nals, SafetyLimits.Default, out _, 1, TestContext.Current.CancellationToken);
            }

            output.WriteLine($"{tiles.Count} tiles: {watch.Elapsed.TotalMilliseconds:F0} ms");
        }
    }
}
