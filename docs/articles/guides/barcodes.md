# Barcodes

Egami.Barcodes writes and reads barcodes. It draws them through Egami.Drawing's canvas, so a barcode stays vector on a PDF page
or in SVG output, and reads them from rasters. It is fully managed.

The package is in development and not yet released. This guide grows as barcode types are added.

## Modules

Every symbol is a `BarcodeModules`: a grid of dark and light modules for a 2D code, or a row of bars and spaces for a linear
one. `Width` and `Height` are in modules, the indexer says whether a module is dark, and a linear symbol also lists its bar and
space widths in `Runs`. A grid holds the symbol only, without its quiet zone.

`BarcodeModules.Create` and `BarcodeModules.CreateLinear` make one from modules you already have, for example from another
encoder.

[!code-csharp[](../../../tests/Egami.DocSamples/Barcodes.cs#barcode-modules)]

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

## Barcodes

A `Barcode` is one symbol: its `BarcodeFormat`, its `Modules`, the `QuietZone` its standard asks for in modules (4 for QR, 1 for
Data Matrix, 2 for PDF417, none for Aztec; at each end of a linear symbol, 10 for Code 128, Code 39 and ITF, 11 for EAN-13, 7
for EAN-8 and 9 for UPC) and the `HumanReadableText` printed under a linear symbol. `Barcode.FromModules` makes one from modules you already have.

[!code-csharp[](../../../tests/Egami.DocSamples/Barcodes.cs#barcode-from-modules)]

## Drawing on a canvas

`DrawBarcode`, an extension method in `CanvasBarcodeExtensions`, draws on any canvas: a raster, a PDF or SVG page, a recording. The whole symbol is one filled path, a rectangle for
each run of dark modules in a row, so a QR code is a few hundred rectangles rather than one per module, and neighbouring
rectangles leave no seams.

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
