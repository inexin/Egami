# Barcodes

Egami.Barcodes writes and reads barcodes. It draws them through Egami.Drawing's canvas, so a barcode stays vector on a PDF page
or in SVG output, and reads them from rasters. It is fully managed.

It writes and reads QR Code, Data Matrix, PDF417 and Aztec Code, and the linear Code 128 (with GS1-128), Code 39, ITF and
ITF-14, EAN-13, EAN-8, UPC-A and UPC-E. Each has its own factory on `Barcode` with typed options; `Barcode.Create` takes a
format chosen at run time with the default options.

[!code-csharp[](../../../tests/Egami.DocSamples/Barcodes.cs#barcode-create)]

The package is in development and not yet released.

## QR Code

`Barcode.Qr` writes a QR Code from text, bytes or GS1 element strings. Text is split into numeric, alphanumeric, byte and
Kanji segments that give the fewest bits. Text in ISO 8859-1 is written as it is, text that Shift JIS holds is written in
Shift JIS (its kanji in Kanji mode), and anything else as UTF-8 with an ECI that tells readers so.

By default the symbol is the smallest that holds the data at level M, and the level is then raised to Q or H while the
symbol stays the same size, so spare room goes to error correction. `QrOptions` fixes the level, the version (1 to 40) or the
mask instead.

[!code-csharp[](../../../tests/Egami.DocSamples/Barcodes.cs#barcode-qr)]

## Data Matrix

`Barcode.DataMatrix` writes Data Matrix ECC 200 from text, bytes or GS1 element strings. The six encodation modes (ASCII,
C40, Text, X12, EDIFACT and Base 256) are switched for the fewest codewords, and the symbol is the smallest that holds them.
Text in ISO 8859-1 is written as it is, anything else as UTF-8 with an ECI.

Symbols are square by default. `DataMatrixOptions.Shape` allows rectangular symbols, or the smallest of either, and
`AllowDmre` adds the rectangular sizes of ISO/IEC 21471 (DMRE), which older readers may not read.

[!code-csharp[](../../../tests/Egami.DocSamples/Barcodes.cs#barcode-datamatrix)]

## PDF417

`Barcode.Pdf417` writes PDF417 from text or bytes, as on boarding passes and driving licences. Text, numeric and byte
compaction are chosen run by run. The error correction level follows the standard's recommended minimum for the amount of
data, and the columns are picked so the symbol is about three times as wide as tall; `Pdf417Options` sets either, or asks for
compact PDF417, which leaves out the right row indicators for a narrower symbol. Rows are 3 modules tall.

[!code-csharp[](../../../tests/Egami.DocSamples/Barcodes.cs#barcode-pdf417)]

## Aztec Code

`Barcode.Aztec` writes Aztec Code from text or bytes, as on rail and airline tickets. The text is written in the Upper,
Lower, Mixed, Punctuation and Digit modes and Binary Shift, switched for the fewest bits. The symbol is the smallest compact
(up to 4 layers) or full-range (up to 32 layers) one that leaves at least 23 % of its codewords, plus 3, for error correction;
`AztecOptions` raises that share or fixes the form and layers. Aztec Code finds itself by its bullseye, so it needs no quiet
zone. `Barcode.AztecRune` makes an Aztec Rune, an 11 x 11 core that holds one value from 0 to 255.

[!code-csharp[](../../../tests/Egami.DocSamples/Barcodes.cs#barcode-aztec)]

## Linear barcodes

### Code 128

`Barcode.Code128` writes any text of characters 0 to 255. It picks the code sets A, B and C, and the changes and shifts
between them, that give the shortest symbol, so runs of digits go in pairs. `Code128Options.Height` sets the bar height in
modules when the barcode is drawn at a module size; drawn into a rectangle, the bars fill it.

[!code-csharp[](../../../tests/Egami.DocSamples/Barcodes.cs#barcode-code128)]

### GS1-128 and GS1 element strings

`Gs1.Parse` reads GS1 element strings written with each Application Identifier in parentheses, such as
`(01)09501101530003(17)261231`, and checks each one against the GS1 General Specifications: the AI is defined, the
characters and lengths fit it, and check digits, dates and times are right. A bad field throws a `FormatException` that says
which AI is wrong and how; `Gs1.TryParse` returns `false` instead. Each `Gs1ElementString` has its `Ai`, `Value` and GS1's
`Title` for the AI. A parenthesis inside a value is written `\(`.

`Barcode.Gs1_128` encodes element strings as Code 128 with FNC1 first, and an FNC1 after each field whose AI has no
predefined length, unless it is the last.

[!code-csharp[](../../../tests/Egami.DocSamples/Barcodes.cs#barcode-gs1)]

The checks come from the GS1 Barcode Syntax Dictionary. Checks that need outside lists (country and currency codes, company
prefixes) and the rules about which AIs go together are not applied.

### Code 39

`Barcode.Code39` writes digits, capital letters, space and `- . $ / + %`. Text with other ASCII characters, such as
lowercase letters, is written as Full ASCII, which spells them as pairs; `Code39Options.FullAscii` forces it on or off. A
reader has to be set to Full ASCII to read such text back. `Code39Options.CheckDigit` adds the mod 43 check character, and
`WideRatio` makes wide elements 2 (the default) or 3 times as wide as narrow ones.

[!code-csharp[](../../../tests/Egami.DocSamples/Barcodes.cs#barcode-code39)]

### ITF and ITF-14

`Barcode.Itf` writes Interleaved 2 of 5 from digits; an odd number of digits gets a leading zero. `Barcode.Itf14` writes a
GTIN-14 for cartons: 13 digits get their check digit added, 14 are checked, and bearer bars 5 modules thick frame the symbol
and its quiet zone. `ItfOptions` sets the wide ratio (3 by default), the bearer bars and the height.

[!code-csharp[](../../../tests/Egami.DocSamples/Barcodes.cs#barcode-itf)]

### EAN and UPC

`Barcode.Ean13`, `Barcode.Ean8`, `Barcode.UpcA` and `Barcode.UpcE` write retail barcodes. The check digit is added when it is
left out and checked when it is given; a wrong one throws. `Barcode.UpcE` also takes a UPC-A number and writes its
zero-suppressed form, or throws when the number has too few zeros. `EanUpcOptions.AddOn` adds a 2-digit or 5-digit add-on to
the right, such as a magazine's issue or a book's price.

With a font, the digits are drawn as on a product: EAN-13's first digit left of the symbol and the others between the guard
bars, which reach down into the text line, and an add-on's digits above its bars. Bar heights default to the standards'
nominal heights.

[!code-csharp[](../../../tests/Egami.DocSamples/Barcodes.cs#barcode-ean)]

## Reading barcodes

`ReadBarcodes`, an extension method in `BarcodeReader`, finds every barcode in a raster of any pixel type and returns a
`BarcodeResult` for each: its `Format`, its `Text`, the `Bytes` it holds, the `Corners` it was found at, its `Eci`, its GS1
element strings in `Gs1`, and how many codewords error correction restored. `ReadBarcode` returns the first one, or `null`.

[!code-csharp[](../../../tests/Egami.DocSamples/Barcodes.cs#barcode-read)]

Text is decoded with the symbol's ECI; without one, as ISO 8859-1, or as UTF-8 or Shift JIS when the bytes clearly are. GS1
data comes back in its human-readable form, such as `(01)09501101530003(10)AB12`, and an EAN or UPC add-on after the main
digits and a space.

`BarcodeReaderOptions` narrows or widens the search. `Formats` names the symbologies to look for; fewer read faster.
`Effort`, 0 to 10, decides how many binarizations, scan lines and rotations are tried, never what counts as a valid read;
from effort 5 light symbols on a dark background are tried too (`TryInverted`). `Region` limits the search to part of the
image. `PureBarcode` reads an image that holds one upright symbol and its quiet zone, such as a rendered barcode, without
searching.

[!code-csharp[](../../../tests/Egami.DocSamples/Barcodes.cs#barcode-read-options)]

Linear barcodes are found along scan lines: rows, columns from effort 3 and diagonals from effort 7, each read in both
directions with its edges placed between pixels, so low-resolution and blurred photos read. A linear barcode counts once two
scan lines agree on it.

QR Codes are found by their three finder patterns, at any rotation and in perspective; from version 2 the alignment pattern
corrects the perspective, and a mirrored symbol reads too.

Data Matrix symbols are found by their L-shaped finder, square and rectangular (DMRE too), at any rotation and in
perspective, on a label lighter than its background or not; the clock tracks count the modules.

## Drawing on a canvas

`DrawBarcode`, an extension method in `CanvasBarcodeExtensions`, draws on any canvas: a raster, a PDF or SVG page, a
recording. The whole symbol is one filled path, a rectangle for each run of dark modules in a row, so a QR code is a few
hundred rectangles rather than one per module, and neighbouring rectangles leave no seams.

Given a rectangle, the barcode is fitted into it and centered, with its quiet zone kept clear inside it. 2D symbols keep square
modules; linear bars fill the rectangle's height. On a raster canvas with no rotation or skew, the module size snaps to whole
pixels, the largest that still fits, so every edge is sharp.

[!code-csharp[](../../../tests/Egami.DocSamples/Barcodes.cs#barcode-draw)]

Given a point and a module size instead, the barcode is drawn at that size, the X dimension of the standards, with the top-left
corner of its quiet zone at the point. Linear bars are then `BarcodeModules.Height` modules tall.

`BarcodeDrawOptions` changes how it is drawn:

- `QuietZone` keeps the quiet zone clear; it is on by default. Turn it off only when the space around the symbol is blank anyway.
- `BarWidthReduction` draws each dark bar or module narrower by that much, half from each side, to make up for ink spreading on
  a printer. It changes drawing only, never the modules.
- `Font` draws the human-readable text under the bars, centered, in that font and size. Without a font no text is drawn;
  Egami.Barcodes ships no font of its own.

[!code-csharp[](../../../tests/Egami.DocSamples/Barcodes.cs#barcode-print)]

## Paths and rasters

`ToPath` returns the symbol as one path, with the quiet zone's top-left corner at the origin, ready for `Path.ToSvg` or your own
drawing. It holds no text.

[!code-csharp[](../../../tests/Egami.DocSamples/Barcodes.cs#barcode-path)]

`ToRaster` renders black on white into a `Raster<Gray8>`, each module a square of whole pixels, with the quiet zone and, with a
font, the text.

[!code-csharp[](../../../tests/Egami.DocSamples/Barcodes.cs#barcode-raster)]

## Barcodes from modules

A `Barcode` is one symbol: its `BarcodeFormat`, its `Modules`, the `QuietZone` its standard asks for in modules (4 for QR, 1 for
Data Matrix, 2 for PDF417, none for Aztec; at each end of a linear symbol, 10 for Code 128, Code 39 and ITF, 11 for EAN-13, 7
for EAN-8 and 9 for UPC) and the `HumanReadableText` printed under a linear symbol. `Barcode.FromModules` makes one from
modules you already have, such as from another encoder.

[!code-csharp[](../../../tests/Egami.DocSamples/Barcodes.cs#barcode-from-modules)]

## Modules

Every symbol is a `BarcodeModules`: a grid of dark and light modules for a 2D code, or a row of bars and spaces for a linear
one. `Width` and `Height` are in modules, the indexer says whether a module is dark, and a linear symbol also lists its bar and
space widths in `Runs`. A grid holds the symbol only, without its quiet zone.

`BarcodeModules.Create` and `BarcodeModules.CreateLinear` make one from modules you already have, for example from another
encoder.

[!code-csharp[](../../../tests/Egami.DocSamples/Barcodes.cs#barcode-modules)]
