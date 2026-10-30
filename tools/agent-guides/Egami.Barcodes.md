# Egami.Barcodes: guide for coding agents

Egami.Barcodes writes and reads barcodes: 1D (Code 128, GS1-128, Code 39, ITF, EAN and UPC and others) and 2D (QR, Data
Matrix, PDF417, Aztec). It draws them onto Egami.Drawing's canvas, so they stay vectors in PDF and SVG output, or onto rasters,
and reads them from rasters.

Namespaces: `Egami.Barcodes`, with `Egami`, `Egami.Pixels` and `Egami.Drawing`.

{{shared}}

## Entry points

- `Barcode.Create(format, data)`, or one method per symbology such as `Barcode.Qr`, `Barcode.Code128` and `Barcode.DataMatrix`,
  with its options (`QrOptions` and others); `Gs1.Parse` for GS1 element strings.
- `barcode.ToRaster(pixelsPerModule)`: black on white, quiet zone included.
- `canvas.DrawBarcode(barcode, rect, paint)`, and `barcode.ToPath()`: as vectors.
- `raster.ReadBarcodes(options)` and `raster.ReadBarcode(options)`: find and decode; `BarcodeReaderOptions.Formats` limits what
  is looked for.

## Samples

From a table of formats and data:

{{sample Barcodes.cs#barcode-create}}

QR codes:

{{sample Barcodes.cs#barcode-qr}}

On a canvas:

{{sample Barcodes.cs#barcode-draw}}

Reading:

{{sample Barcodes.cs#barcode-read}}

## Common mistakes

- Making a small raster and scaling it up, or scaling a barcode image to a size that is not a whole number of pixels per
  module. Pass `pixelsPerModule` to `ToRaster`, or draw with `DrawBarcode`, where modules snap to whole pixels on rasters.
- Cutting off the quiet zone. `ToRaster` and `DrawBarcode` keep it inside the size you give; don't crop it away.
- Reading for every format when you know which one to expect: set `BarcodeReaderOptions.Formats`, so the reader skips the
  others.
- Putting GS1 data into a plain Code 128 or QR string. Parse it with `Gs1.Parse` so the FNC1 characters are right.

## More

- Barcodes guide: https://inexin.github.io/Egami/v1/articles/guides/barcodes.html
- Reading guide: https://inexin.github.io/Egami/v1/articles/guides/barcode-reading.html
- Every page in one list: https://inexin.github.io/Egami/v1/llms.txt
- API reference: https://inexin.github.io/Egami/v1/api/Egami.Barcodes.html
