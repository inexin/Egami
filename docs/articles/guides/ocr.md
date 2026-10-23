# Text recognition (OCR)

Egami does not recognize text itself. It gives every OCR engine one shape, so code that needs text from an image works with
whichever engine you plug in: a program such as Tesseract, the OS's own OCR or a cloud service. Egami ships no engine and links no native code. It also reads and writes
hOCR and ALTO, the two files engines write. Types live in `Egami.Ocr`.

## The engine shape

[!code-csharp[](../../../tests/Egami.DocSamples/Ocr.cs#engine)]

An `IOcrEngine` has one method, `RecognizeAsync`, and an `OcrEngineInfo` that says what it can do: its `Name`, the `Languages`
it reads, the `MaxImageDimension` it takes (callers scale larger images down and the boxes back up), whether it `FindsOrientation`
and whether calls may overlap (`IsThreadSafe`, the default; a caller runs one page at a time when it is `false`).

[!code-csharp[](../../../tests/Egami.DocSamples/Ocr.cs#recognize)]

An `OcrRequest` carries a `Raster<Rgba8>` that the engine must not change or dispose, its `Dpi` (300 by default; engines use it
to judge text size), the `Languages` to read as the engine names them, most likely first, and the `PageIndex` when the image is a
page of a document. The `OcrResult` has `Lines` of `OcrWord`s in reading order, each with its `Text`, its `Box`, a `Confidence`
from 0 to 1 when the engine gives one, and a `Language`. `Text` joins words with spaces and lines with line feeds; `Words`
lists them all. `Rotation` is the clockwise quarter turn that would put the image upright, for engines that look, and `ImageSize`
the size of the image the boxes refer to.

All engines are async. A network engine that blocks a thread while it waits can deadlock a caller with a synchronization context,
so there is no synchronous form; a console app can simply `await`.

## Boxes are quads

[!code-csharp[](../../../tests/Egami.DocSamples/Ocr.cs#quads)]

An `OcrQuad` holds a word's four corners in image pixels with the origin at the top left, named as the word reads: a word that runs
up the page has its `TopLeft` at the bottom left of its box. Rotated and skewed scans keep each word's shape, which a PDF text layer
needs for selection that matches the picture. `FromBounds` makes the quad of an upright word, or of a word turned by a quarter turn;
`Bounds` gives the whole-pixel rectangle around it, `Angle` the reading direction in degrees counterclockwise, and `Transform`
maps the corners through a matrix, such as back from an image the engine saw at a smaller size.

## Any OCR program: CommandOcrEngine

[!code-csharp[](../../../tests/Egami.DocSamples/Ocr.cs#command)]

`CommandOcrEngine` runs a program for each image and reads the hOCR or ALTO it writes, so any engine with a command line plugs in
without code. The command is split into words at spaces, with quotes around a word that holds spaces, and run without a shell:
an image path or a language can never become a command. Words may hold placeholders:

| Placeholder | Becomes |
|---|---|
| `{image}` | The image, written as a PNG file in the run's own temporary folder |
| `{output}` | A path without extension for the output file; the engine reads it, or the one file the program wrote with an extension added |
| `{langs}` | The request's languages joined by `+` (Tesseract's form), or `CommandOcrOptions.Languages` when the request names none |
| `{dpi}` | The request's resolution as a whole number |
| `{page}` | The request's page index |

Without `{output}`, the engine reads the program's standard output, so `tesseract {image} - -l {langs} hocr` works as well.
`CommandOcrOptions` sets the `Output` format (`OcrOutputFormat.Auto`, the default, tells hOCR from ALTO by the content), a
`Timeout` (5 minutes; the program and its children are stopped), the `WorkingDirectory`, the engine `Name` and the `Read` limits
for what the program writes. A program that cannot start, exits with an error, times out or writes no hOCR or ALTO throws
`OcrEngineException`, with its `ExitCode` and the end of its `ErrorOutput`. Each run has its own temporary folder, deleted
afterwards, so runs may overlap.

## hOCR and ALTO files

[!code-csharp[](../../../tests/Egami.DocSamples/Ocr.cs#hocr)]

`Hocr` reads and writes hOCR, the HTML that Tesseract, OCRopus and kraken write: `ocr_page` pages, `ocr_line` lines (and
`ocrx_line`, `ocr_header`, `ocr_caption` and `ocr_textfloat`) and `ocrx_word` words with `bbox`, `x_wconf` (confidence as a
percentage), `textangle` and `lang`. It reads HTML the way browsers do, not only XHTML, so missing end tags and unquoted
attributes are fine. A line with `textangle` 90, 180 or 270 gives turned quads. A document with neither a page nor a word throws
`CorruptDataException`.

[!code-csharp[](../../../tests/Egami.DocSamples/Ocr.cs#alto)]

`Alto` reads ALTO 1 to 4, the XML of library digitization that ABBYY, Tesseract and Transkribus write: `Page`, `TextLine` and
`String` with `HPOS`, `VPOS`, `WIDTH`, `HEIGHT`, `WC` and `LANG`, a `HYP` hyphen joined to the word before it. Positions in 1/10 mm
(`mm10`) or 1/1200 inch (`inch1200`) become pixels at `OcrReadOptions.Dpi`. It writes ALTO 4 in pixels.

Both readers take untrusted files: `OcrReadOptions` limits the size (`MaxBytes`, 256 MB) and the words (`MaxWords`, 4 million),
breaking either throws `LimitExceededException`, and no DTD, schema or other file is ever fetched. Both writers replace
characters XML cannot hold with U+FFFD.

## Sample engines

Egami's samples hold three adapters you can copy, built with the rest of the repository but never packed:

- [`samples/Ocr`](https://github.com/inexin/Egami/tree/main/samples/Ocr) has Tesseract, through its program and `CommandOcrEngine`,
  and Azure AI Document Intelligence's Read model, through its REST API with `HttpClient` and no SDK. It draws a page of text,
  reads it back and writes text, hOCR and ALTO: `dotnet run --project samples/Ocr -- tesseract` (or `azure`, with
  `AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT` and `AZURE_DOCUMENT_INTELLIGENCE_KEY` set).
- [`samples/OcrWindows`](https://github.com/inexin/Egami/tree/main/samples/OcrWindows) has Windows' own OCR (Windows.Media.Ocr)
  for Windows 10 and 11: `dotnet run --project samples/OcrWindows -- scan.png en-US`.

Engines differ in what they report. Tesseract gives confidences and no page orientation in hOCR; Windows OCR gives upright word
rectangles and no confidence; Document Intelligence gives polygons, confidences and the page's angle.
