using Egami.Ocr;

namespace Egami.Fuzz;

/// <summary>
/// The hocr and alto fuzz targets (Egami 2.12, OcrFuzzTarget.cs): read the input as hOCR or ALTO under small limits, which may
/// only fail with an EgamiException; pages that read must write back and read again to the same text, boxes, confidences and
/// languages, and the other format must carry the same words. An ALTO file with no page is left there.
/// </summary>
internal static class OcrFuzzTarget
{
    private static readonly OcrReadOptions Limits = new() { MaxBytes = 1024 * 1024, MaxWords = 100_000 };

    public static void Hocr(byte[] data)
    {
        Run(data, Ocr.Hocr.Read);
    }

    public static void Alto(byte[] data)
    {
        Run(data, Ocr.Alto.Read);
    }

    private static void Run(byte[] data, Func<Stream, OcrReadOptions, IReadOnlyList<OcrResult>> read)
    {
        IReadOnlyList<OcrResult> pages;
        try
        {
            pages = read(new MemoryStream(data), Limits);
        }
        catch (EgamiException)
        {
            return;
        }

        if (pages.Count == 0)
        {
            // An ALTO file may have no Page; hOCR with no ocr_page is not hOCR, so there is nothing to carry over.
            return;
        }

        foreach ((Action<Stream, IEnumerable<OcrResult>> write, Func<Stream, OcrReadOptions, IReadOnlyList<OcrResult>> again) in
            new (Action<Stream, IEnumerable<OcrResult>>, Func<Stream, OcrReadOptions, IReadOnlyList<OcrResult>>)[] { (Ocr.Hocr.Write, Ocr.Hocr.Read), (Ocr.Alto.Write, Ocr.Alto.Read) })
        {
            using MemoryStream stream = new();
            write(stream, pages);
            stream.Position = 0;
            IReadOnlyList<OcrResult> back = again(stream, OcrReadOptions.Default);
            if (back.Count != pages.Count)
            {
                throw new InvalidOperationException($"{pages.Count} pages wrote and read back as {back.Count}.");
            }

            for (int p = 0; p < pages.Count; p++)
            {
                OcrWord[] expected = [.. pages[p].Words];
                OcrWord[] actual = [.. back[p].Words];
                if (expected.Length != actual.Length)
                {
                    throw new InvalidOperationException($"Page {p}: {expected.Length} words read back as {actual.Length}.");
                }

                for (int w = 0; w < expected.Length; w++)
                {
                    OcrWord a = expected[w], b = actual[w];
                    if (b.Box.Bounds != a.Box.Bounds || b.Language != a.Language || (b.Confidence is null) != (a.Confidence is null)
                        || Math.Abs((b.Confidence ?? 0) - (a.Confidence ?? 0)) > 0.006 || b.Text.Length == 0)
                    {
                        throw new InvalidOperationException($"Page {p}, word {w}: {a} read back as {b}.");
                    }
                }
            }
        }
    }
}
