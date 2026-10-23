using System.Numerics;
using System.Text;

using Egami.Pixels;

namespace Egami.Ocr;

/// <summary>
/// A text recognition (OCR) engine: an image in, words with their boxes out. Egami ships no engine; plug one in, such as
/// <see cref="CommandOcrEngine"/> for any program that writes hOCR or ALTO, or your own class around an OS or cloud service.
/// </summary>
/// <remarks>
/// Implementations must not change or dispose <see cref="OcrRequest.Image"/>. A call may run on any thread; when
/// <see cref="OcrEngineInfo.IsThreadSafe"/> is <see langword="true"/>, callers may run several at once.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Ocr.cs" region="engine"/>
/// </example>
public interface IOcrEngine
{
    /// <summary>Gets what the engine can do: its name, languages, largest image and whether calls may overlap.</summary>
    OcrEngineInfo Info { get; }

    /// <summary>Recognizes the text in one image.</summary>
    /// <param name="request">The image and how to read it.</param>
    /// <param name="cancellationToken">Cancels the recognition.</param>
    /// <returns>The lines and words found, in pixels of <see cref="OcrRequest.Image"/> with the origin at the top left.</returns>
    Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default);
}

/// <summary>What an <see cref="IOcrEngine"/> can do. Callers such as a PDF driver read it to size and schedule their requests.</summary>
/// <param name="Name">The engine's name, for reports and diagnostics, such as "tesseract".</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Ocr.cs" region="engine"/>
/// </example>
public sealed record OcrEngineInfo(string Name)
{
    /// <summary>Gets the languages the engine reads, as the engine names them (such as "eng" or "en-US"), or empty if not known.</summary>
    public IReadOnlyList<string> Languages { get; init; } = [];

    /// <summary>
    /// Gets the largest width or height, in pixels, the engine accepts, or <see langword="null"/> for no limit. Callers scale
    /// larger images down and the boxes back up.
    /// </summary>
    public int? MaxImageDimension { get; init; }

    /// <summary>Gets a value indicating whether the engine finds the page orientation and reports it in <see cref="OcrResult.Rotation"/>.</summary>
    public bool FindsOrientation { get; init; }

    /// <summary>Gets a value indicating whether several calls to the engine may run at once. Defaults to <see langword="true"/>.</summary>
    public bool IsThreadSafe { get; init; } = true;
}

/// <summary>One image to recognize, with how to read it.</summary>
/// <param name="Image">The image. The engine reads it and must not change or dispose it.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Ocr.cs" region="recognize"/>
/// </example>
public sealed record OcrRequest(Raster<Rgba8> Image)
{
    private readonly double _dpi = 300;

    /// <summary>
    /// Gets the image resolution in pixels per inch, which engines use to judge text size. Defaults to 300. Must be positive.
    /// </summary>
    public double Dpi
    {
        get => _dpi;
        init => _dpi = double.IsFinite(value) && value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The resolution must be positive.");
    }

    /// <summary>Gets the languages to read, as the engine names them (such as "eng" and "nor"), most likely first; empty for the engine's default.</summary>
    public IReadOnlyList<string> Languages { get; init; } = [];

    /// <summary>Gets the page number, from 0, when the image is one page of a document; for engines that report or log it.</summary>
    public int PageIndex { get; init; }
}

/// <summary>The text an engine found in one image: lines of words, in reading order.</summary>
/// <param name="Lines">The lines, in reading order.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Ocr.cs" region="recognize"/>
/// </example>
public sealed record OcrResult(IReadOnlyList<OcrLine> Lines)
{
    /// <summary>Gets an empty result, for an image with no text.</summary>
    public static OcrResult Empty { get; } = new([]);

    /// <summary>
    /// Gets the clockwise turn, in degrees (0, 90, 180 or 270), that would put the image upright, as the engine found it; 0 when
    /// it is upright or the engine does not look. Boxes are always in the image as given, not turned.
    /// </summary>
    public int Rotation { get; init; }

    /// <summary>
    /// Gets the size of the image the boxes refer to, or <see cref="Size"/> zero if not known. hOCR and ALTO files give it per page.
    /// </summary>
    public Size ImageSize { get; init; }

    /// <summary>Gets every word, line by line.</summary>
    public IEnumerable<OcrWord> Words => Lines.SelectMany(line => line.Words);

    /// <summary>Gets the text: words joined by spaces, lines by line feeds.</summary>
    public string Text
    {
        get
        {
            StringBuilder text = new();
            foreach (OcrLine line in Lines)
            {
                if (text.Length > 0)
                {
                    text.Append('\n');
                }

                text.Append(line.Text);
            }

            return text.ToString();
        }
    }
}

/// <summary>One line of text: its words, in reading order.</summary>
/// <param name="Words">The words, in reading order.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Ocr.cs" region="recognize"/>
/// </example>
public sealed record OcrLine(IReadOnlyList<OcrWord> Words)
{
    /// <summary>Gets the text: the words joined by spaces.</summary>
    public string Text => string.Join(' ', Words.Select(word => word.Text));

    /// <summary>Gets the smallest pixel rectangle that holds every word, or an empty rectangle for a line with no words.</summary>
    public Rect Bounds
    {
        get
        {
            if (Words.Count == 0)
            {
                return default;
            }

            Rect bounds = Words[0].Box.Bounds;
            int left = bounds.X, top = bounds.Y, right = bounds.X + bounds.Width, bottom = bounds.Y + bounds.Height;
            foreach (OcrWord word in Words)
            {
                Rect box = word.Box.Bounds;
                left = Math.Min(left, box.X);
                top = Math.Min(top, box.Y);
                right = Math.Max(right, box.X + box.Width);
                bottom = Math.Max(bottom, box.Y + box.Height);
            }

            return new Rect(left, top, right - left, bottom - top);
        }
    }
}

/// <summary>One recognized word.</summary>
/// <param name="Text">The word's text.</param>
/// <param name="Box">Where the word is, in pixels of the image, its corners in the word's own reading order.</param>
/// <param name="Confidence">How sure the engine is, from 0 to 1, or <see langword="null"/> if it does not say.</param>
/// <param name="Language">The word's language as the engine names it, or <see langword="null"/> if not known.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Ocr.cs" region="recognize"/>
/// </example>
public sealed record OcrWord(string Text, OcrQuad Box, double? Confidence = null, string? Language = null);

/// <summary>
/// The four corners of a word, in pixels with the origin at the top left of the image, named as the word reads: a word that runs
/// up the page has its <see cref="TopLeft"/> at the bottom left of its bounds. Rotated and skewed words keep their shape.
/// </summary>
/// <param name="TopLeft">The corner where the word starts, at the top of its letters.</param>
/// <param name="TopRight">The corner where the word ends, at the top of its letters.</param>
/// <param name="BottomRight">The corner where the word ends, at the bottom of its letters.</param>
/// <param name="BottomLeft">The corner where the word starts, at the bottom of its letters.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Ocr.cs" region="quads"/>
/// </example>
public readonly record struct OcrQuad(Vector2 TopLeft, Vector2 TopRight, Vector2 BottomRight, Vector2 BottomLeft)
{
    /// <summary>Gets the smallest whole-pixel rectangle that holds the four corners.</summary>
    public Rect Bounds
    {
        get
        {
            float left = MathF.Min(MathF.Min(TopLeft.X, TopRight.X), MathF.Min(BottomRight.X, BottomLeft.X));
            float top = MathF.Min(MathF.Min(TopLeft.Y, TopRight.Y), MathF.Min(BottomRight.Y, BottomLeft.Y));
            float right = MathF.Max(MathF.Max(TopLeft.X, TopRight.X), MathF.Max(BottomRight.X, BottomLeft.X));
            float bottom = MathF.Max(MathF.Max(TopLeft.Y, TopRight.Y), MathF.Max(BottomRight.Y, BottomLeft.Y));
            int x = (int)MathF.Floor(left), y = (int)MathF.Floor(top);
            return new Rect(x, y, (int)MathF.Ceiling(right) - x, (int)MathF.Ceiling(bottom) - y);
        }
    }

    /// <summary>
    /// Gets the direction the word reads in, in degrees counterclockwise as seen on screen, from 0 up to 360: 0 for normal text,
    /// 90 for text that runs up the page.
    /// </summary>
    public float Angle
    {
        get
        {
            Vector2 along = TopRight - TopLeft;
            if (along == Vector2.Zero)
            {
                return 0;
            }

            float degrees = MathF.Atan2(-along.Y, along.X) * (180 / MathF.PI);
            return degrees < 0 ? degrees + 360 : degrees;
        }
    }

    /// <summary>Returns the quad of an upright word with these bounds.</summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <returns>The quad.</returns>
    public static OcrQuad FromBounds(float x, float y, float width, float height)
    {
        return new OcrQuad(new Vector2(x, y), new Vector2(x + width, y), new Vector2(x + width, y + height), new Vector2(x, y + height));
    }

    /// <summary>
    /// Returns the quad of a word with these bounds that reads at a quarter turn: 0 for normal text, 90 for text that runs up the
    /// page, 180 for upside-down text and 270 for text that runs down. Other angles give the upright quad.
    /// </summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="angle">The reading direction, in degrees counterclockwise.</param>
    /// <returns>The quad.</returns>
    public static OcrQuad FromBounds(float x, float y, float width, float height, int angle)
    {
        Vector2 topLeft = new(x, y), topRight = new(x + width, y), bottomRight = new(x + width, y + height), bottomLeft = new(x, y + height);
        return (((angle % 360) + 360) % 360) switch
        {
            90 => new OcrQuad(bottomLeft, topLeft, topRight, bottomRight),
            180 => new OcrQuad(bottomRight, bottomLeft, topLeft, topRight),
            270 => new OcrQuad(topRight, bottomRight, bottomLeft, topLeft),
            _ => new OcrQuad(topLeft, topRight, bottomRight, bottomLeft),
        };
    }

    /// <summary>
    /// Returns the quad with each corner mapped through <paramref name="matrix"/>, such as a scale back from an image the engine
    /// saw smaller, or the move from image pixels to page space.
    /// </summary>
    /// <param name="matrix">The transform.</param>
    /// <returns>The mapped quad.</returns>
    public OcrQuad Transform(Matrix3x2 matrix)
    {
        return new OcrQuad(Vector2.Transform(TopLeft, matrix), Vector2.Transform(TopRight, matrix), Vector2.Transform(BottomRight, matrix), Vector2.Transform(BottomLeft, matrix));
    }
}

/// <summary>Limits for reading hOCR and ALTO files, which may come from an untrusted engine or file.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Ocr.cs" region="hocr"/>
/// </example>
public sealed record OcrReadOptions
{
    private readonly long _maxBytes = 256L * 1024 * 1024;
    private readonly int _maxWords = 4_000_000;
    private readonly double _dpi = 300;

    /// <summary>Gets the default options.</summary>
    public static OcrReadOptions Default { get; } = new();

    /// <summary>Gets the largest file, in bytes. Defaults to 256 MB.</summary>
    public long MaxBytes { get => _maxBytes; init => _maxBytes = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The limit must be at least 1."); }

    /// <summary>Gets the most words in one file, over all pages. Defaults to 4,000,000.</summary>
    public int MaxWords { get => _maxWords; init => _maxWords = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The limit must be at least 1."); }

    /// <summary>
    /// Gets the resolution, in pixels per inch, that turns ALTO positions in 1/10 mm or 1/1200 inch into pixels. Defaults to 300.
    /// </summary>
    public double Dpi { get => _dpi; init => _dpi = double.IsFinite(value) && value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The resolution must be positive."); }
}

/// <summary>Thrown when an OCR engine fails, such as a program that exits with an error, times out or writes no result.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Ocr.cs" region="command"/>
/// </example>
public sealed class OcrEngineException : EgamiException
{
    /// <summary>Initializes a new instance of the <see cref="OcrEngineException"/> class.</summary>
    /// <param name="message">What went wrong, as a sentence.</param>
    /// <param name="exitCode">The program's exit code, or <see langword="null"/> when no program ran or it was stopped.</param>
    /// <param name="errorOutput">The end of what the program wrote to its error output, or <see langword="null"/>.</param>
    /// <param name="innerException">The exception that caused this one, if any.</param>
    public OcrEngineException(string message, int? exitCode = null, string? errorOutput = null, Exception? innerException = null)
        : base(message, innerException)
    {
        ExitCode = exitCode;
        ErrorOutput = errorOutput;
    }

    /// <summary>Gets the program's exit code, or <see langword="null"/> when no program ran or it was stopped.</summary>
    public int? ExitCode { get; }

    /// <summary>Gets the end of what the program wrote to its error output, or <see langword="null"/>.</summary>
    public string? ErrorOutput { get; }
}
