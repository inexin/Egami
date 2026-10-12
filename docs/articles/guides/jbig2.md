# JBIG2

Egami reads JBIG2 (ITU-T T.88 | ISO/IEC 14492), the bi-level format of scanned documents and of the JBIG2Decode images in PDF,
written from the 2018 edition of T.88 and the PDF specification. `Jbig2Format.Info` is registered in `EgamiContext.Default`.
JBIG2 is never written. Every page of Egami's corpus (made with jbig2enc in each of its modes), of PDFBox's JBIG2 test streams and
of the T.88 Annex H datastream decodes bit for bit as PDFium and jbig2dec decode it.

## Reading files

A standalone file starts with the T.88 file header, so `Raster.Load` recognizes it. Each page loads as `Gray8`: black 0, white
255, as 1-bit TIFF does. `Load` reads the first page, or the one `Jbig2ReaderOptions.Page` names; `LoadAll` reads every page
at its own size, and `RasterInfo.ImageCount` says how many there are without decoding any.

[!code-csharp[](../../../tests/Egami.DocSamples/Jbig2.cs#jbig2-load)]

## Images from a PDF

A PDF holds JBIG2 in T.88's embedded form: segments without a file header, and often a separate JBIG2Globals stream with the
symbol dictionaries several images share. The embedded form has no signature, so load it with the format named.
`Jbig2Globals.Parse` decodes the globals once; the object is immutable and can be shared by every image of the document, on
any thread.

[!code-csharp[](../../../tests/Egami.DocSamples/Jbig2.cs#jbig2-embedded)]

A PDF image's /Decode array and its color space stay the PDF library's business: Egami returns the page as T.88 defines it.

## What is decoded

| Segment | Read |
| --- | --- |
| Generic regions | Templates 0 to 3 and the extended template, adaptive pixels anywhere, typical prediction, skipped pixels; MMR through Egami's CCITT Group 4 decoder |
| Refinement regions | Templates 0 and 1, typical prediction, against the page or an intermediate region |
| Symbol dictionaries, text regions | Arithmetic and Huffman, refinement and aggregation, every reference corner and transposition, standard tables B.1 to B.15 and code table segments |
| Pattern dictionaries, halftone regions | Arithmetic and MMR gray-scale planes, any grid, skipping |
| Pages | Known and unknown height, striped, every combination operator, intermediate regions; end of file and random-access files |
| Extensions | Comments into `Jbig2Details.Comments`; others skipped unless marked necessary, which throws `NotSupportedException` |
| Colour palettes (T.88 Amendment 3) | Skipped with a warning; the page decodes as bilevel |

`Jbig2Details` reports, per page, the stream's `Jbig2Organization`, whether the page used the globals, its striping, default
pixel and `Jbig2CombinationOperator`, the `Jbig2RegionKinds` it is made of and whether it is eventually lossless. The page
information's resolution goes to `RasterMetadata.Resolution`.

[!code-csharp[](../../../tests/Egami.DocSamples/Jbig2.cs#jbig2-details)]

## Damaged and hostile data

JBIG2 decoders are a long-standing attack surface (FORCEDENTRY, in 2021, was an integer overflow in one), so every size is
checked arithmetic and counted against `SafetyLimits` before memory is taken: page and region sizes against `MaxWidth`,
`MaxHeight` and `MaxPixels`; symbols, patterns, tables and every bitmap together against `MaxTotalBytes`. A segment that refers to
a segment that is not before it, in the stream or in the globals, always throws. In lenient mode a damaged region is skipped
with a warning and the rest of the page decodes; strict mode throws `CorruptDataException`.

The ITU's own conformance streams for T.88 mostly break the Recommendation (their text regions refer to no symbol dictionary),
and PDFium and jbig2dec reject them as Egami does; the two that follow it, and the Annex H datastream, decode exactly.
