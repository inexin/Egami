# CCITT fax

Egami decodes raw CCITT fax data: modified Huffman and Group 3 (ITU-T T.4, one- and two-dimensional) and Group 4 (ITU-T T.6),
the bi-level coding of fax machines, of many scanned PDFs and of TIFF compressions 2, 3 and 4. Raw data has no header, so its
parameters come from outside: in a PDF they are the CCITTFaxDecode filter's DecodeParms dictionary, and `CcittFaxParameters`
holds the same eight values with the same defaults. CCITT data is never written here; inside TIFF it is read and written by the
TIFF format. Every CCITT stream of a 4,000-file PDF corpus, byte-aligned Group 4 among them, and of a matrix of every parameter
combination decodes bit for bit as PDFium and pdf.js decode it.

## As a PDF filter

`CcittFax.Decode` gives what the filter gives: one bit a pixel, most significant first, each row padded to whole bytes, black 0
and white 1 unless `BlackIs1` is set. The PDF library then applies the image's /Decode array, /ImageMask and color space, as it
does after every other filter.

[!code-csharp[](../../../tests/Egami.DocSamples/Ccitt.cs#ccitt-filter)]

| Parameter | Default | Meaning |
| --- | --- | --- |
| `K` | 0 | Below 0 Group 4; 0 one-dimensional Group 3; above 0 mixed Group 3, a bit after each EOL picking the next row's coding |
| `EndOfLine` | false | EOLs are required before each row; they are accepted where present either way |
| `EncodedByteAlign` | false | Group 4 rows start on byte boundaries; Group 3 EOLs end on one, after fill bits |
| `Columns` | 1728 | Row width in pixels |
| `Rows` | 0 | Row count; 0 decodes until the data or its end-of-block code ends |
| `EndOfBlock` | true | The data ends with RTC (Group 3) or EOFB (Group 4); decoding stops there either way |
| `BlackIs1` | false | The output's bit sense: 1 for black instead of 0 |
| `DamagedRowsBeforeError` | 0 | With EOLs and K 0 or above, how many damaged rows are skipped to the next EOL |

## As an image

`CcittFormat` loads the data as `Gray8`, black 0 and white 255, as 1-bit TIFF and JBIG2 load, whatever `BlackIs1` says. The
parameters go in `CcittReaderOptions`. With `Rows` given, `Raster.Probe` reads nothing and `RasterDecoder` decodes a row at a
time, holding only the current row and the one above it. The format has no signature, so detection never picks it.

[!code-csharp[](../../../tests/Egami.DocSamples/Ccitt.cs#ccitt-load)]

## Damaged data

A row that does not decode ends decoding: the rows before it are kept, the rest are white, and the warning `Ccitt.Damaged` goes
to `EgamiContext.Diagnostics` (or to `RasterMetadata.Warnings` for a load). With EOLs and K 0 or above, up to
`DamagedRowsBeforeError` damaged rows are first skipped to the next EOL, each repeating the row before it (`Ccitt.RowsSkipped`).
Data that ends before `Rows` rows warns `Ccitt.Truncated`. Only data in which not one row decodes throws
`CorruptDataException`; a strict load throws on any of these. PDFium and pdf.js carry on decoding after damage instead, so the
later rows of a damaged stream differ from theirs.

`Columns` and `Rows` are checked against `SafetyLimits` before memory is taken, and with `Rows` 0 the output grows only up to
`MaxPixels`, so a stream of EOLs cannot use up memory.
