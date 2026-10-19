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
