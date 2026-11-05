# DPX and Cineon

DPX (SMPTE ST 268, Digital Moving-Picture Exchange, .dpx) is the frame format of film scanning, grading and visual effects:
one file per frame, a 2048-byte header of file, image, film and television fields, then up to eight image elements, most often
10-bit printing-density RGB packed three samples to a 32-bit word. Cineon (.cin) is Kodak's film-scan format (version 4.5)
from which DPX grew. Both read and write through the Egami.Formats.Extended package: register them with `WithDpx()` and
`WithCineon()`, or `WithExtendedFormats()` ([Extended formats](extended.md)).

| | DPX | Cineon |
| --- | --- | --- |
| Reads | Versions 1.0, 2.0 and 3.0 in either byte order; 1, 8, 10, 12 and 16-bit integer and 32 and 64-bit float samples; packed (packing 0) or filled to 32 bits (packing 1 and 2); luma, depth, RGB, RGBA, ABGR, single R, G, B or A, and CbYCrY, CbYACrYA, CbYCr and CbYCrA (4:2:2 and 4:4:4, BT.709 or BT.601); run-length coded elements; every element through `DpxReaderOptions` | 1 or 3 channels of 8, 10, 12 or 16 bits, pixel-interleaved, packed bit to bit or filled to 8, 16 or 32 bits, in either byte order |
| Writes | Version 2.0, gray, RGB or RGBA at 8, 10, 12 or 16 bits (`DpxWriterOptions`), big endian unless asked; the header fields of `DpxDetails` | 10-bit RGB, three samples to a big-endian 32-bit word; the text and film fields of `CineonDetails` |
| Natural pixel type | `Gray8`, `Rgb8` or `Rgba8` at 8 bits and below, their 16-bit forms above; `GrayF32`, `RgbF32` or `RgbaF32` for float elements and when linearized | `Gray16` or `Rgb16` (`Gray8` or `Rgb8` for 8-bit files); `GrayF32` or `RgbF32` when linearized |
| `FormatInfo` | `DpxFormat.Info`: name `DPX`, extension `.dpx`, MIME type `image/x-dpx` | `CineonFormat.Info`: name `Cineon`, extension `.cin`, MIME types `image/cineon` and `image/x-cineon` |

## Code values

Load gives the samples as the file stores them, scaled to the pixel type: a 10-bit code value `c` becomes `c × 65535 / 1023`
in a 16-bit channel, rounded, and the writer turns it back into the same code, so a file that loads and saves at its own depth
keeps every code value. Luma and colour-difference files (CbYCr) are turned into RGB with video range and the BT.709 matrix,
or BT.601 when the colorimetric field says so.

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#dpx-load)]

Cineon has no reader options of its own: a Cineon load takes `DpxReaderOptions` too, and of them `Linearize` applies (a
Cineon file has one image element).

Printing density, the usual transfer of film scans, is logarithmic. `DpxReaderOptions.Linearize` turns it into scene-linear
light with Kodak's conversion, reference white at code 685, reference black at code 95 and 0.002 density a code with a film
gamma of 0.6: code 685 becomes 1.0 and code 95 becomes 0.0. It applies to Cineon and to DPX elements whose transfer is printing
density or logarithmic; other elements load as they are stored. Alpha stays linear from 0 to 1.

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#dpx-linearize)]

## Header fields

What the header says that the pixels do not is kept in `RasterMetadata.FormatDetails`: `DpxDetails` holds the element's
descriptor, depth, packing, byte order, transfer and colorimetric codes, and the file (name, time, creator, project,
copyright), film (manufacturer, type, offset, prefix, count, format, frame position, sequence length, frame rate, frame id,
slate) and television (timecode, user bits, frame rate) fields; `CineonDetails` the channels, depth, packing, orientation,
text and film fields. The writers write them back, so a frame keeps its timecode and slate through a save.

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#dpx-details)]

## Writing

DPX is written 10-bit, big endian and filled three samples to a word (packing 1, method A) unless the details or
`DpxWriterOptions` say otherwise: `BitsPerSample` picks 8, 10, 12 or 16, `BigEndian` the byte order and `Transfer` the
transfer and colorimetric codes. Without one, a raster read from DPX keeps its own, one read from Cineon is printing density,
and any other is user defined. 12-bit samples are filled to 16-bit words. Cineon is always written 10-bit RGB, big endian.

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#dpx-save)]

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#cineon)]

## Elements, orientation and damaged files

A DPX file may hold up to eight image elements, such as a colour and a matte, or left and right eyes. Load gives the first
one Egami reads; `Raster.Probe` counts them in `ImageCount`, LoadAll gives every one, and `DpxReaderOptions.Element` picks one.
Elements in a layout Egami does not read (composite video, the generic 2 to 8-component descriptors and user-defined ones) are skipped. A file whose
orientation is bottom-up or right to left loads upright; the four orientations that swap rows and columns load as stored, with
the warning `Dpx.OrientationIgnored`.

Rows decode from the input as `RasterDecoder` asks for them; a bottom-up or run-length coded element reads whole first. A
lenient read of a file that ends early keeps the rows it has and leaves the rest black, with the warning `Dpx.RowsMissing` or
`Cineon.RowsMissing`, and a broken run with `Dpx.RunDamaged`; a strict read throws `CorruptDataException`.
