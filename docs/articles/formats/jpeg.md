# JPEG

JPEG files load like any other: baseline, extended and progressive, Huffman or arithmetic-coded, gray, YCbCr, RGB, CMYK and
YCCK, any chroma subsampling. The decoded pixels match libjpeg-turbo's `djpeg` exactly.

## Arithmetic, 12-bit and lossless files

Arithmetic-coded files load like Huffman-coded ones. 12-bit files load in their natural 16-bit types (`Gray16`, `Rgb16`, or
`Cmyk16` for four components), each sample scaled so 4095 becomes 65535; ask for an 8-bit type and they are rounded to it.
Lossless JPEG, the kind inside DNG tiles and many DICOM files, decodes to its exact samples at 2 to 16 bits: up to 8 bits as
`Gray8` or `Rgb8`, deeper as `Gray16` or `Rgb16`. Three-component lossless files are RGB unless a JFIF segment or Adobe
transform says YCbCr. Lossless files have no DCT, so `MinimumSize` reduces them after a full decode. `JpegDetails.Precision`,
`Arithmetic` and `Lossless` say what a file is. Hierarchical files are refused with `UnsupportedFormatException`.

Browsers show none of these modes. Egami writes each of them on request (below).

## Threads

A large file decodes on up to `MaxParallelism` threads with the same pixels as one thread. A file with restart markers is split
at the markers already in it; a progressive file's scans run as a pipeline, each refinement scan a few rows behind the scan
before it, and the last pass is split by rows. A sequential file without markers overlaps its entropy decoding with the rest on
a second thread. Damaged data falls back to one thread, so warnings and recovery are as before.

## Scaled decode

[!code-csharp[](../../../tests/Egami.DocSamples/AnimationAndMetadata.cs#scaled-decode)]

`ReaderOptions.MinimumSize` picks the smallest of 1/1, 1/2, 1/4 and 1/8 scale that still covers the size you need, and decodes
at that scale straight from the DCT. For a thumbnail from a 12-megapixel photo it is many times faster than decoding in full and
resizing. Other formats ignore it.

## Orientation

Cameras and phones store pixels as the sensor saw them and say in EXIF how to turn them. `Raster.Probe` reports the orientation,
and `AutoOrient` applies it; see [resizing and geometry](../guides/resizing-and-geometry.md#orientation). To turn the file itself upright
without re-encoding it, use `JpegTransform` with `AutoOrient` (see [lossless rotation](#lossless-rotation-flips-and-crops)).

## Writing

[!code-csharp[](../../../tests/Egami.DocSamples/AnimationAndMetadata.cs#jpeg-write)]

| Option | Default | Does |
| --- | --- | --- |
| `Quality` | 75 | 1 to 100, the same scale as libjpeg's `cjpeg` |
| `Subsampling` | `Subsample420` | `Subsample422` or `Subsample444` keep more color detail |
| `OptimizeHuffman` | `false` | Tables built for the image: a few percent smaller |
| `ColorType` | `Auto` | `Gray` writes one gray component whatever the pixel type; `Cmyk` and `Ycck` write four (`Auto` picks `Cmyk` for CMYK pixels) |
| `Progressive` | `false` | Scans that show a coarse image first and refine it, with tables optimized per scan |
| `Arithmetic` | `false` | Arithmetic coding instead of Huffman: smaller files, same pixels, no browser support |
| `Lossless` | none | A `JpegLosslessOptions` writes lossless JPEG: the exact samples, no DCT |
| `Precision` | 8 | 12 keeps more of 16-bit and float pixels |
| `RestartInterval` | 0 | Restart markers every so many blocks, for damaged-stream recovery |
| `Background` | none | Composites alpha onto this color first; without it alpha is ignored |

### Progressive

[!code-csharp[](../../../tests/Egami.DocSamples/AnimationAndMetadata.cs#jpeg-progressive)]

`Progressive` writes the scan script libjpeg's `cjpeg -progressive` writes: ten scans for color, six for gray, spectral
selection and successive approximation, each scan with its own optimized Huffman tables. The file decodes to exactly the pixels
the baseline file at the same quality gives, and is the same file `cjpeg -progressive` writes. The encoder holds every quantized
coefficient until the last scan, about 2 bytes per sample (72 MB for a 24-megapixel 4:2:0 photo), counted against
`SafetyLimits.MaxTotalBytes`; a streamed encode buffers them. Re-saving a progressive file writes baseline unless you set it.

### CMYK and YCCK

[!code-csharp[](../../../tests/Egami.DocSamples/AnimationAndMetadata.cs#jpeg-cmyk)]

`Cmyk8` and `Cmyk16` pixels save as four CMYK components with an Adobe marker, the values inverted as Photoshop writes them and
every reader expects, and no JFIF segment (JFIF covers gray and YCbCr only). `Ycck` converts cyan, magenta and yellow to YCbCr
as libjpeg does and subsamples their chroma by `Subsampling`, keeping black at full size; it is usually smaller. Other pixel types
written as `Cmyk` or `Ycck` are converted with the plain formula. djpeg, ImageMagick and Pillow read the files to the same inks.

### 12-bit

[!code-csharp[](../../../tests/Egami.DocSamples/AnimationAndMetadata.cs#jpeg-12bit)]

`Precision = 12` writes 12-bit samples (an extended, SOF1 file, or SOF2 when progressive) from any pixel type, narrowing 16-bit
samples to 12 bits; it is worth it for 16-bit and float pixels. The Huffman tables are always optimized, as libjpeg-turbo does
at 12 bits, and quantization tables pass 255 at low quality, in 16-bit entries. libjpeg-turbo's djpeg reads them; browsers,
Pillow and many ImageMagick builds do not.

### Arithmetic coding

[!code-csharp[](../../../tests/Egami.DocSamples/AnimationAndMetadata.cs#jpeg-arithmetic)]

`Arithmetic` codes the scans with T.81's adaptive arithmetic coder instead of Huffman codes: a sequential (SOF9) file, or a
progressive (SOF10) one with `Progressive`, at 8 or 12 bits, in every color type, subsampling and restart interval. The
quantized coefficients are the same, so the file decodes to exactly the pixels of the Huffman file with the same options, and
is about 4 to 8 % smaller than with optimized Huffman tables; it is no larger than `cjpeg -arithmetic`'s file at the same
settings (libjpeg adds a 12-byte DAC segment Egami leaves out). A sequential file needs no tables, so it streams in one pass at
either precision and ignores `OptimizeHuffman`. libjpeg-turbo, ImageMagick and Egami read these files; browsers do not, and
Pillow fails on some. They suit archives, not the web. An Ultra HDR gain map is left out with a `Save.GainMapDropped` warning,
and `GainMap` with `Arithmetic` throws `NotSupportedException`. Re-saving an arithmetic file writes Huffman codes unless you set it.

### Lossless

[!code-csharp[](../../../tests/Egami.DocSamples/AnimationAndMetadata.cs#jpeg-lossless)]

`Lossless` writes lossless JPEG (T.81 annex H, the kind DNG raw tiles and DICOM use): no DCT and no quantization, each sample
predicted from its neighbors and the difference coded, so the file loads back to exactly the samples written. The components are
the pixels' own: gray, RGB behind an Adobe marker, or CMYK, never subsampled or converted to YCbCr. `Quality`, `Subsampling`,
`OptimizeHuffman` and `Precision` do not apply; `Progressive` or `ColorType = Ycck` with it throws `ArgumentException`.

| `JpegLosslessOptions` | Default | Does |
| --- | --- | --- |
| `Predictor` | automatic | 1 to 7, T.81's predictors (1 left, 2 above, 3 above left, 4 to 7 combinations); automatic tries each on rows spread through the image and keeps the smallest |
| `PointTransform` | 0 | Drops that many low bits from every sample first: smaller, no longer lossless (reported as `Save.DepthReduced`) |
| `Precision` | the pixel type's | 2 to 16 bits; 8 for 8-bit pixel types, 16 for 16-bit and float ones. Fewer bits round the samples (reported) |

The Huffman tables are always made for the image, so every sample is held until the end, about 2 bytes each, counted against
`SafetyLimits.MaxTotalBytes`. `RestartInterval` is rounded up to whole rows of pixels, as T.81 asks of lossless files. With
`Arithmetic` the file is SOF11, up to a few percent smaller (6 % on an 8-bit photo, the same at 16 bits); only Egami and Thomas
Richter's libjpeg read SOF11, while libjpeg-turbo, DNG and DICOM readers read the Huffman-coded SOF3. libjpeg-turbo's `djpeg`
decodes Egami's SOF3 files to exactly the samples written, and at the same predictor they are within 0.2 % of `cjpeg
-lossless`'s size. No browser reads either. An Ultra HDR gain map is left out with a `Save.GainMapDropped` warning, and
`GainMap` with `Lossless` throws `NotSupportedException`.

## Lossless rotation, flips and crops

[!code-csharp[](../../../tests/Egami.DocSamples/AnimationAndMetadata.cs#jpeg-transform)]

`JpegTransform` turns, mirrors, crops and drops the color of a JPEG without decoding it to pixels, as `jpegtran` does: the
quantized DCT coefficients move and change sign block by block, and are coded again, so the result has exactly the quality the
file had. Quantization tables and chroma subsampling stay as they were. It takes every mode the reader takes except lossless
JPEG: baseline, progressive, arithmetic, 12-bit, gray, CMYK and YCCK; the result is Huffman-coded unless `Arithmetic` is set,
so an existing file can also be repacked smaller with no change to its pixels (`jpegtran -arithmetic`). The result decodes to
the same pixels as the file `jpegtran` makes with the same switches, on every subsampling and edge case.

| Option | Default | Does |
| --- | --- | --- |
| `Operation` | `None` | `FlipHorizontal`, `FlipVertical`, `Rotate90`, `Rotate180`, `Rotate270`, `Transpose`, `Transverse`, or `AutoOrient`: what the EXIF orientation needs, then the tag is reset to normal |
| `Crop` | none | The area to keep, after the operation; its top-left corner moves to the MCU grid (8 or 16 pixels), as `jpegtran -crop` does |
| `Grayscale` | `false` | Keeps only luma: a gray file with no loss. YCbCr files only |
| `Edges` | `Trim` | What happens at a mirrored edge that is not a whole number of MCUs (below) |
| `Progressive` | `false` | Writes progressive scans, as `JpegWriterOptions.Progressive` does |
| `OptimizeHuffman` | `true` | Tables built for the result, a few percent smaller than the standard ones |
| `Arithmetic` | `false` | Arithmetic coding, as `JpegWriterOptions.Arithmetic` writes: smaller, same pixels |
| `CopyMetadata` | `true` | Copies EXIF, ICC, XMP, IPTC, comments and other segments |

A block of 8 × 8 pixels moves as a whole, so an edge that is not a whole number of MCUs (8 pixels, or 16 with subsampled chroma)
cannot be mirrored to the other side intact. `JpegTransformEdges.Trim` drops that partial MCU from each mirrored edge, so the image loses up to 15
pixels there, as `jpegtran -trim` does; `Strict` throws instead, as `-perfect` does; `Keep` leaves it in place unmirrored, as
`jpegtran` does by default. Edges that are not mirrored keep every pixel, and `Transpose` never loses any.

EXIF's pixel dimensions follow the result, and its thumbnail gets the same rotation, flip and gray conversion; after a crop the
thumbnail is dropped, since it would still show the whole picture. An MPF index (the extra images some cameras append) is
dropped too. Other segments are copied byte for byte. The result's size and any warnings about a damaged source are in the
returned `JpegTransformResult`.

The whole image's coefficients are held while it runs, about 2 bytes per sample, counted against `SafetyLimits.MaxTotalBytes`.
A 12-megapixel photo turns in 170 ms with optimized tables (jpegtran `-optimize`: 305 ms), 130 ms with the standard ones
(jpegtran: 246 ms) and 260 ms written progressive (jpegtran: 458 ms).

## What the file says

[!code-csharp[](../../../tests/Egami.DocSamples/AnimationAndMetadata.cs#jpeg-details)]

EXIF, XMP (including extended XMP) and ICC profiles are read and written back. A JPEG saved again is re-encoded: pixels lose a
little each time, so keep a lossless master if you edit repeatedly, or use `JpegTransform` for rotations, flips and crops.

## Details

The components, color space and estimated quality are in `JpegDetails`. `JpegReaderOptions.FastUpsampling` trades a little chroma
sharpness for speed; `JpegColorType.Gray` writes one channel.

CMYK and YCCK files load as RGB by the plain formula, R = (1 − C)(1 − K). `Raster.Load<Cmyk8>` returns the file's ink amounts
instead, without the RGB step; print-accurate color needs the embedded ICC profile, which Egami keeps but does not apply yet.

[!code-csharp[](../../../tests/Egami.DocSamples/ApiExamples.cs#jpeg-details)]

## Multi-Picture JPEG files

Phones store more than one image in a JPEG: a gain map for HDR, large previews, the second view of a stereo pair. The CIPA
Multi-Picture Format (MPF) lists them in an APP2 segment. `JpegDetails.MultiPicture` gives that index as `MpfData`: an
`MpfImage` for each image with its `MpfImageType`, flags, length, and offset from the start of the file (the same numbers
ExifTool prints). `Raster.Probe` counts them in `RasterInfo.ImageCount`, and `Raster.LoadAll` loads each JPEG one, the primary
first. A damaged index never fails a load: the primary loads with a `Jpeg.MpfDamaged` warning.

[!code-csharp[](../../../tests/Egami.DocSamples/Hdr.cs#mpf)]

The gain maps of Ultra HDR photos are in the [HDR guide](../guides/hdr.md#gain-maps).

## Recipes

Short answers to common tasks in this area. Each one compiles and runs in Egami's test run.

### Read a CMYK JPEG's inks

[!code-csharp[](../../../tests/Egami.DocSamples/PixelTypes.cs#cmyk-inks)]

## Run it

[`samples/Jpeg`](https://github.com/inexin/Egami/tree/main/samples/Jpeg) is a console app that does what this guide shows on the bundled
images: `dotnet run --project samples/Jpeg` writes its results to `./output`.
