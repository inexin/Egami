using System.Runtime.InteropServices.WindowsRuntime;

using Egami.Ocr;
using Egami.Pixels;

using Windows.Globalization;
using Windows.Graphics.Imaging;

using WinOcr = Windows.Media.Ocr;

namespace Egami.Samples.OcrWindows;

/// <summary>
/// Windows' own OCR (Windows.Media.Ocr, Windows 10 and 11) as an <see cref="IOcrEngine"/>: the image goes in as a BGRA software
/// bitmap and the lines and words come back. It reads the languages whose OCR pack is installed, and gives no confidence.
/// </summary>
public sealed class WindowsOcrEngine : IOcrEngine
{
    private readonly WinOcr.OcrEngine _engine;

    /// <summary>Initializes a new instance of the <see cref="WindowsOcrEngine"/> class.</summary>
    /// <param name="language">A BCP-47 tag such as "en-US", or <see langword="null"/> for the user's profile languages.</param>
    public WindowsOcrEngine(string? language = null)
    {
        _engine = (language is null ? WinOcr.OcrEngine.TryCreateFromUserProfileLanguages() : WinOcr.OcrEngine.TryCreateFromLanguage(new Language(language)))
            ?? throw new InvalidOperationException($"Windows has no OCR pack for {language ?? "the user's languages"}.");
        Info = new OcrEngineInfo("windows-ocr")
        {
            Languages = [_engine.RecognizerLanguage.LanguageTag],
            MaxImageDimension = (int)WinOcr.OcrEngine.MaxImageDimension,
            IsThreadSafe = false,
        };
    }

    /// <inheritdoc/>
    public OcrEngineInfo Info { get; }

    /// <inheritdoc/>
    public async Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Raster<Rgba8> image = request.Image;
        byte[] bgra = new byte[image.Width * image.Height * 4];
        for (int y = 0; y < image.Height; y++)
        {
            Span<Rgba8> row = image.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                int i = ((y * image.Width) + x) * 4;
                (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]) = (row[x].B, row[x].G, row[x].R, 255);
            }
        }

        using SoftwareBitmap bitmap = new(BitmapPixelFormat.Bgra8, image.Width, image.Height, BitmapAlphaMode.Ignore);
        bitmap.CopyFromBuffer(bgra.AsBuffer());
        WinOcr.OcrResult found = await _engine.RecognizeAsync(bitmap).AsTask(cancellationToken);

        // Windows gives each word an upright rectangle in the image, and the page's skew as TextAngle; the boxes are kept upright.
        List<OcrLine> lines = [];
        foreach (WinOcr.OcrLine line in found.Lines)
        {
            List<OcrWord> words = [];
            foreach (WinOcr.OcrWord word in line.Words)
            {
                Windows.Foundation.Rect box = word.BoundingRect;
                words.Add(new OcrWord(word.Text, OcrQuad.FromBounds((float)box.X, (float)box.Y, (float)box.Width, (float)box.Height), Language: Info.Languages[0]));
            }

            lines.Add(new OcrLine(words));
        }

        return new OcrResult(lines) { ImageSize = new Size(image.Width, image.Height) };
    }
}
