using Egami.Ocr;

namespace Egami.Samples.Ocr;

/// <summary>
/// Tesseract (Apache-2.0) through its program: Egami writes the image as a PNG, runs <c>tesseract</c> with no shell and reads the
/// hOCR it writes. Nothing native is linked; install Tesseract and its language data yourself.
/// </summary>
public sealed class TesseractEngine : IOcrEngine
{
    private readonly CommandOcrEngine _command;

    /// <summary>Initializes a new instance of the <see cref="TesseractEngine"/> class.</summary>
    /// <param name="program">The tesseract program, or <see langword="null"/> for the TESSERACT environment variable or the PATH.</param>
    /// <param name="pageSegmentation">Tesseract's page segmentation mode; 3 (the default) finds the layout itself.</param>
    public TesseractEngine(string? program = null, int pageSegmentation = 3)
    {
        program ??= Environment.GetEnvironmentVariable("TESSERACT") ?? "tesseract";
        _command = new CommandOcrEngine($"\"{program}\" {{image}} {{output}} -l {{langs}} --dpi {{dpi}} --psm {pageSegmentation} hocr", new CommandOcrOptions
        {
            Name = "tesseract",
            Languages = ["eng"],
            Output = OcrOutputFormat.Hocr,
        });
    }

    /// <inheritdoc/>
    public OcrEngineInfo Info => _command.Info;

    /// <inheritdoc/>
    public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
    {
        return _command.RecognizeAsync(request, cancellationToken);
    }
}
