# JPEG

JPEG files load like any other: baseline and progressive, gray, YCbCr, RGB, CMYK and YCCK, any chroma subsampling. The decoded
pixels match libjpeg-turbo's `djpeg` exactly.

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
| `ColorType` | `Auto` | `Gray` writes one gray component whatever the pixel type |
| `RestartInterval` | 0 | Restart markers every so many blocks, for damaged-stream recovery |
| `Background` | none | Composites alpha onto this color first; without it alpha is ignored |

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
