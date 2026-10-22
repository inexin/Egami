# Reading barcodes

Egami.Barcodes reads every symbology it writes: QR Code, Data Matrix (DMRE too), PDF417 (compact too), Aztec Code (runes too),
Code 128 and GS1-128, Code 39, ITF and ITF-14, EAN-13, EAN-8, UPC-A and UPC-E with their add-ons. It reads rendered barcodes,
scans and photos: turned, in perspective, blurred, noisy, light on dark or mirrored, several in one image.

## Reading an image

`ReadBarcodes`, an extension method in `BarcodeReader`, finds every barcode in a raster of any pixel type and returns a
`BarcodeResult` for each: its `Format`, its `Text`, the `Bytes` it holds, the `Corners` it was found at, its `Eci`, its GS1
element strings in `Gs1`, and how many codewords error correction restored. `ReadBarcode` returns the first one, or `null`.

[!code-csharp[](../../../tests/Egami.DocSamples/Barcodes.cs#barcode-read)]

Text is decoded with the symbol's ECI; without one, as ISO 8859-1, or as UTF-8 or Shift JIS when the bytes clearly are. GS1
data comes back in its human-readable form, such as `(01)09501101530003(10)AB12`. For EAN and UPC, `Text` holds the main
digits and `AddOn` the digits of an EAN-2 or EAN-5 add-on, or `null`.

`BarcodeReaderOptions` narrows or widens the search. `Formats` names the symbologies to look for, as `BarcodeFormat` flags
(`BarcodeFormat.Qr | BarcodeFormat.DataMatrix`, or `Linear`, `Matrix` and `All`); fewer read faster.
`Effort`, 0 to 10, decides how many binarizations, scan lines and rotations are tried, never what counts as a valid read;
from effort 5 light symbols on a dark background are tried too (`TryInverted`). `Region` limits the search to part of the
image. `PureBarcode` reads an image that holds one upright symbol and its quiet zone, such as a rendered barcode, without
searching.

[!code-csharp[](../../../tests/Egami.DocSamples/Barcodes.cs#barcode-read-options)]

## Camera frames

A camera frame's 8-bit luminance (the Y plane of a YUV frame) reads in place, without a copy: `BarcodeReader.ReadBarcodes`
and `ReadBarcode` take the bytes with the width, height and stride. The buffer is only read, and only during the call.

[!code-csharp[](../../../tests/Egami.DocSamples/Barcodes.cs#barcode-read-luminance)]

The Y plane is read where it is, so a frame from a camera API needs no conversion or copy.

## How each symbology is found

Linear barcodes are found along scan lines: rows, columns from effort 3 and diagonals from effort 7, each read in both
directions with its edges placed between pixels, so low-resolution and blurred photos read. From effort 5 rows are also read
through the local threshold, which keeps bars under glare apart. A linear barcode counts once two scan lines agree on it.
ITF, whose check is weakest, is read from 4 digits on.

QR Codes are found by their three finder patterns, at any rotation and in perspective; from version 2 the alignment pattern
corrects the perspective (each one near the expected place is tried, nearest first), and a mirrored symbol reads too.

Data Matrix symbols are found by their L-shaped finder, square and rectangular (DMRE too), at any rotation and in
perspective, on a label lighter than its background or not; the clock tracks count the modules.

PDF417 symbols are found by their start and stop patterns at any rotation and in perspective; a compact symbol, which has
no stop pattern, is read from its start pattern on, and one cut off before its start pattern from its stop pattern back.
Rows and columns that cannot be read are filled in by error correction.

Aztec Codes are found by their bullseye at any rotation and in perspective, compact, full-range and runes; on larger
full-range symbols the reference grid places the modules out to the edge.

## Many symbols, inverted and mirrored

Every symbol in the image comes back once, in reading order: top to bottom, then left to right. A linear read inside a 2D
symbol is dropped as part of it. `Inverted` tells which symbols were light on dark, `Mirrored` which were read from behind
or flipped. Large images are searched on several threads, up to the `MaxParallelism` of the options' `Context`; the
results are the same at any thread count.

[!code-csharp[](../../../tests/Egami.DocSamples/Barcodes.cs#barcode-read-many)]

## GS1 data, ECI and add-ons

GS1 symbols (GS1-128, GS1 QR Code, GS1 Data Matrix) come back with their element strings in `Gs1`, each with its AI, title and
value, and `Text` in the human-readable form with the AIs in parentheses. A symbol that names an Extended Channel
Interpretation has it in `Eci`, and its text is decoded with that character set. `Bytes` holds what the symbol carries before
any character set is applied.

[!code-csharp[](../../../tests/Egami.DocSamples/Barcodes.cs#barcode-read-gs1)]

## What reads well

- Two pixels or more per module. Linear barcodes read down to about one pixel per narrow bar, as their edges are placed between
  pixels.
- The quiet zone the symbology asks for, or most of it: 10 modules beside Code 128 and Code 39, 4 around a QR code.
- Contrast between bars and background; uneven light is fine, as each part of the image gets its own threshold.
- A sharp image. Blur that merges neighbouring modules cannot be undone, although error correction fills in damaged parts of
  2D symbols.

## Speed

The reader allocates per call only and can run on many threads at once. On one thread it reads the ZXing test images in
about the time zxing-cpp takes (0.9 times over 817 images, each read for its own symbology). These settle how long a call takes:

- `Formats`: only the symbologies you expect. Each detector skipped saves its time.
- `Effort`: 5 is the default. Lower efforts try fewer scan lines and rotations and no inverted image; higher ones try more.
- `Region`: only the part of the image where the symbol is.
- `PureBarcode` for a rendered symbol with nothing else in the image: it reads without searching.
- Images of a quarter of a megapixel or more are searched on several threads, up to the `Context`'s `MaxParallelism`.
