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

Browsers show none of these modes. Egami writes 12-bit files on request (below), but no arithmetic or lossless ones.

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
and `AutoOrient` applies it; see [resizing and geometry](resizing-and-geometry.md#orientation).

## Writing

[!code-csharp[](../../../tests/Egami.DocSamples/AnimationAndMetadata.cs#jpeg-write)]

| Option | Default | Does |
| --- | --- | --- |
| `Quality` | 75 | 1 to 100, the same scale as libjpeg's `cjpeg` |
| `Subsampling` | `Subsample420` | `Subsample422` or `Subsample444` keep more color detail |
| `OptimizeHuffman` | `false` | Tables built for the image: a few percent smaller |
| `ColorType` | `Auto` | `Gray` writes one gray component whatever the pixel type; `Cmyk` and `Ycck` write four (`Auto` picks `Cmyk` for CMYK pixels) |
| `Progressive` | `false` | Scans that show a coarse image first and refine it, with tables optimized per scan |
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

## What the file says

[!code-csharp[](../../../tests/Egami.DocSamples/AnimationAndMetadata.cs#jpeg-details)]

EXIF, XMP (including extended XMP) and ICC profiles are read and written back. A JPEG saved again is re-encoded: pixels lose a
little each time, so keep a lossless master if you edit repeatedly.

## Details

The components, color space and estimated quality are in `JpegDetails`. `JpegReaderOptions.FastUpsampling` trades a little chroma
sharpness for speed; `JpegColorType.Gray` writes one channel.

CMYK and YCCK files load as RGB by the plain formula, R = (1 − C)(1 − K). `Raster.Load<Cmyk8>` returns the file's ink amounts
instead, without the RGB step; print-accurate color needs the embedded ICC profile, which Egami keeps but does not apply yet.

[!code-csharp[](../../../tests/Egami.DocSamples/ApiExamples.cs#jpeg-details)]

## Run it

[`samples/Jpeg`](https://github.com/inexin/Egami/tree/main/samples/Jpeg) is a console app that does what this guide shows on the bundled
images: `dotnet run --project samples/Jpeg` writes its results to `./output`.
