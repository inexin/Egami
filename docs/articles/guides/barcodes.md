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

## Barcodes

A `Barcode` is one symbol: its `BarcodeFormat`, its `Modules`, the `QuietZone` its standard asks for in modules (4 for QR, 1 for
Data Matrix, 2 for PDF417, none for Aztec and 10 at each end of a linear symbol) and the `HumanReadableText` printed under a
linear symbol. `Barcode.FromModules` makes one from modules you already have.

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
