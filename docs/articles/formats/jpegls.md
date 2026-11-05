# JPEG-LS

JPEG-LS (ITU-T T.87, ISO/IEC 14495-1) codes 2 to 16-bit samples losslessly, or near-losslessly with every sample within a
set error, by predicting each sample from its neighbours and coding the error with Golomb codes. It is quick, and it is one of
the transfer syntaxes of DICOM, so medical images often carry it. It reads and writes through the Egami.Formats.Extended
package: register it with `WithJpegLs()` or `WithExtendedFormats()` ([Extended formats](extended.md)).

| | |
| --- | --- |
| Reads | The whole of T.87: lossless and near-lossless coding, 2 to 16 bits, 1 or more components, interleave none, line and sample, one scan or one per component, restart markers, preset coding parameters, mapping tables (palettes), subsampled components, sizes from an LSE segment or a DNL segment, and HP's colour transforms |
| Writes | Gray, gray and alpha, RGB and RGBA at 2 to 16 bits, lossless or near-lossless, in any interleave mode, with restart markers and HP's colour transforms |
| Natural pixel type | `Gray8`, `GrayAlpha8`, `Rgb8` or `Rgba8` for 8 bits and fewer; `Gray16`, `GrayAlpha16`, `Rgb16` or `Rgba16` above; a palette loads as its colours |
| `FormatInfo` | `JpegLsFormat.Info`: name `JPEG-LS`, extension `.jls`, MIME types `image/jls` and `image/x-jls` |

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#jpegls)]

JPEG-LS and JPEG both start with the SOI marker, so `WithJpegLs()` puts JPEG-LS ahead of the context's formats, where its
detection looks for the JPEG-LS frame header; JPEG files still go to the JPEG reader.

## Reading

Samples of other depths than 8 and 16 bits are scaled to the pixel's full range, so a 12-bit file loads as `Gray16` with 4095
at 65535. `JpegLsDetails` in `RasterMetadata.FormatDetails` keeps the stored depth, NEAR, the interleave mode, the colour
transform, the number of components and whether a palette gave the colours. When one scan holds every component at full size
(sampling 1 × 1), rows decode as `RasterDecoder` asks for them; files with one scan per component or with subsampled
components are decoded whole first, and subsampled components are repeated up to full size. Files of more than four
components load their first four, with a warning.

A lenient read of a damaged file keeps what decoded. Inside a file with restart markers, a restart interval that cannot be
decoded is left black and decoding goes on at the next marker (`JpegLs.IntervalDamaged`); without them the rows from the
damage on are left transparent black (`JpegLs.Damaged`). A file that ends early keeps its rows with `JpegLs.RowsMissing`. JPEG-LS
has no check sums, so damage that still decodes gives wrong samples without a warning. A strict read throws
`CorruptDataException` instead.

## Writing

`JpegLsWriterOptions` sets NEAR, the interleave mode, the colour transform, the bits of a sample and the restart interval.
Without them a raster read from JPEG-LS is written with its own depth and NEAR again, and others losslessly at 8 or 16 bits.

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#jpegls-options)]

- NEAR 0 is lossless. NEAR 1 to 3 make photographs much smaller, with no sample more than NEAR from its original.
- Line interleave, the default, writes rows as they come. Sample interleave does too and is a little slower; interleave
  none writes one scan per component and keeps the image until the end.
- The HP transforms code RGB as differences, which shrinks most photographs. HP's software, CharLS and Egami read them; other
  decoders give the coded components as they are. They apply to lossless, interleaved RGB only and are ignored otherwise.

`JpegLsWriter.Write` codes samples that do not come from a raster, such as the pixel data of a DICOM file, 1 to 4 components
of 2 to 16 bits, side by side in a row:

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#jpegls-samples)]

Files Egami writes decode exactly in CharLS, and lossless files are the size CharLS writes.

## Not supported

JPEG-LS part 2 (ITU-T T.870: arithmetic coding, prediction and error-mapping extensions) is not read, and DICOM files
themselves are not read here.
