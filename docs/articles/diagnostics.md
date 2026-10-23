# Diagnostics

Egami can tell an app what it recovered from, what it lost on purpose and what it decided on the caller's behalf, through one
optional handler on `EgamiContext`. Egami has no logging dependency: the handler is a plain delegate, so one line sends the events
to `ILogger`, Serilog or the console.

[!code-csharp[](../../tests/Egami.DocSamples/Diagnostics.cs#logger)]

Set it once at startup; every call that passes no context then reports to the log.

## The handler

`EgamiContext.Diagnostics` is an `Action<EgamiDiagnostic>`, `null` by default. With no handler, nothing is built and nothing is
allocated. Each `EgamiDiagnostic` carries:

| Member | What it holds |
| --- | --- |
| `Level` | `Warning`, `Info` or `Debug`, mapping onto the log levels Warning, Information and Debug |
| `Source` | The part of Egami that found it: `Png`, `Tiff`, `Exif`, `Limits` and so on (the id up to its first dot) |
| `Id` | A stable id such as `Png.ChecksumMismatch`, to filter and count on without parsing text |
| `Message` | What happened and what Egami did, as a sentence |
| `Offset` | The byte offset in the input, when known |
| `Frame` | The frame or page index, when known |

Ids are listed below and are never renamed or reused once shipped, like the public API.

- **Levels.** `EgamiContext.DiagnosticLevel` is the lowest level delivered, `Warning` by default. Events below it are never even
  formatted.

  [!code-csharp[](../../tests/Egami.DocSamples/Diagnostics.cs#level)]

- **Threads.** The handler runs inline, on whichever thread found the event, possibly on several decode threads at once, so it
  must be quick and thread-safe. Egami takes no lock around it.
- **When Warnings arrive.** The Warnings a load raises arrive as the call ends, in the order of `Metadata.Warnings`, on the
  calling thread; a `RasterDecoder` session sends them when it is disposed. A reader that waits for more input can take a warning
  back and read it again, so nothing is sent until it is final. Info and Debug events arrive as they happen. A load that throws
  sends no Warnings: the exception says what went wrong.
- **Errors.** An exception from the handler fails the call with that exception, which turns warnings into errors. Nothing the call
  allocated is leaked.

  [!code-csharp[](../../tests/Egami.DocSamples/Diagnostics.cs#errors)]

A handler that throws turns every warning into an error; `RecoveryMode.Strict` does the same for damage alone, without a handler.

- **Repeats.** One id is delivered at most 10 times per call, then once more as "The same problem came up N more times".
  `Metadata.Warnings` holds the same capped list, so the two always agree. A hostile file cannot flood a log.
- **Per file.** Contexts are cheap records, so a context per file, whose handler's closure carries the path, scopes the events to
  that file.

  [!code-csharp[](../../tests/Egami.DocSamples/Diagnostics.cs#per-file)]

## Metadata.Warnings

Every Warning a load raises is also in `RasterMetadata.Warnings`, an `IReadOnlyList<EgamiDiagnostic>`, with or without a handler.
Info and Debug events never are. Metadata parsed on first use (XMP, IPTC, ICC) adds its warnings then, and sends them to the handler
of the context the raster was loaded with.

[!code-csharp[](../../tests/Egami.DocSamples/Diagnostics.cs#warnings)]

## Reporting from your own code

A format plug-in notes a recovery with `EgamiInput.Warn(raster, id, message)`, which lists it in `Metadata.Warnings` and sends it to
the handler as Egami's own readers do; `EgamiInput.Diagnostics` takes its Info and Debug events. Code with no `EgamiInput`, such
as a video demuxer, starts a `DiagnosticReporter` for the call with `EgamiContext.StartDiagnostics()`, which applies the same level
filter and cap; disposing it sends the counts of capped ids. Use your own source name in ids (`Avi.IndexRebuilt`), never one of
Egami's.

[!code-csharp[](../../tests/Egami.DocSamples/Diagnostics.cs#plugin)]

## Logging libraries

`ILogger` takes the mapping in the first example. Serilog's static `Log` is the same one line:

[!code-csharp[](../../tests/Egami.DocSamples/Diagnostics.cs#serilog)]

Memory leaks are reported by `PixelMemoryUsage.LeakDetected` instead, because they belong to a memory source rather than a call and
are found on the finalizer thread after the call has ended; see [Memory and threading](guides/memory-and-threading.md#finding-leaks).

## Catalog

<!-- tests/Egami.Tests/DiagnosticsCatalogTests.cs checks this table against the ids in src/Egami, one to one. -->

| Id | Level | Meaning |
| --- | --- | --- |
| `Ani.ChunkPastEnd` | Warning | An animated cursor's chunk, or a frame in its `fram` list, runs past the end of the file or the list; the bytes that are there were read |
| `Ani.ChunkRepeated` | Warning | An animated cursor has a second `anih` header or `fram` list; the first was used |
| `Ani.FrameCount` | Warning | An animated cursor's header gives a frame count its `fram` list contradicts; the frames in the list were used |
| `Ani.FrameDamaged` | Warning | A frame of an animated cursor cannot be read or decoded; the steps that show it were left out |
| `Ani.HeaderSize` | Warning | An animated cursor's `anih` header is not 36 bytes, or says it is not; its first 36 bytes were read |
| `Ani.IconInRawFile` | Warning | An animated cursor's header says its frames are raw bitmaps, but a frame is an icon; it was read as one |
| `Ani.ListSize` | Warning | An animated cursor's `rate` or `seq ` chunk is not a whole number of 4-byte entries; the extra bytes were ignored |
| `Ani.RateCount` | Warning | An animated cursor's `rate` chunk has a different number of entries than it has steps; steps without one use the default rate |
| `Ani.RiffSize` | Warning | An animated cursor's RIFF header gives a size past the end of the file; the chunks that are there were read |
| `Ani.SequenceFlag` | Warning | An animated cursor's header flag for a `seq ` chunk disagrees with whether there is one; the chunk, if any, was used |
| `Ani.SequenceIndex` | Warning | A step of an animated cursor shows a frame the file does not store; the step was left out |
| `Ani.SizeMissing` | Warning | A frame of an animated cursor has no image at the size loaded; its nearest image was resized, hotspot and all |
| `Ani.StepCount` | Warning | An animated cursor's header gives a step count its `seq ` chunk or frames contradict; theirs was used |
| `Ani.ZeroRate` | Warning | An animated cursor's default rate, or a step's rate, is 0; the step uses the default rate, or one jiffy (1/60 s) when that is 0 too |
| `Avif.BaseLayerOnly` | Warning | An AVIF image is coded in layers whose later ones are inter frames, which Egami does not decode yet; its first layer was loaded |
| `Avif.ExifDamaged` | Warning | An AVIF Exif item's TIFF header offset points past its end; the item was skipped |
| `Avif.FramesDropped` | Warning | A frame of an AVIF sequence after the first cannot be decoded; a lenient load keeps the frames before it |
| `Avif.GainMapDropped` | Warning | An AVIF gain map (tmap item) has metadata or an image that cannot be read, or a version Egami does not know; the image loads as SDR only |
| `Avif.MatrixApproximated` | Warning | An AVIF image's matrix coefficients are ones libavif does not convert (reserved, constant luminance, or YCgCo at limited range); the nearest matrix it has was used |
| `Avif.MatrixUnknown` | Warning | An AVIF image's matrix coefficients have no YuvMatrix; YuvRaster.Load marks the planes BT.601 |
| `Avif.SequenceFirstFrame` | Warning | No longer reported: AVIF sequences decode every frame, inter frames included (Egami 1.1) |
| `Bmp.DataOffsetInHeaders` | Warning | The pixel data offset points into the headers; the pixels are read from where the headers end |
| `Bmp.EmbeddedSizeMismatch` | Warning | An embedded PNG or JPEG is not the size the BMP header gives |
| `Bmp.IndexOutOfRange` | Warning | Pixels use indices past the palette; they are drawn black |
| `Bmp.PlanesInvalid` | Warning | The header gives a plane count other than 1; it was ignored |
| `Bmp.ProfileLinked` | Warning | The color profile is linked by file name, which Egami does not open |
| `Bmp.ProfileTruncated` | Warning | The file ends before the embedded ICC profile; it was dropped |
| `Bmp.RowsMissing` | Warning | The pixel data ends or is damaged; the rows not decoded are black |
| `Bmp.RunPastRow` | Warning | RLE runs go past the end of a row; the extra pixels were dropped |
| `Ccitt.Damaged` | Warning | A row of CCITT data is damaged and could not be skipped; the rows before it were kept and the rest are white |
| `Ccitt.RowsSkipped` | Warning | Damaged CCITT rows were skipped to the next EOL, as DamagedRowsBeforeError allows; each repeats the row before it |
| `Ccitt.Truncated` | Warning | The CCITT data ends before the rows its parameters give; the rest are white |
| `Color.ConvertedOnLoad` | Info | `ReaderOptions.ConvertColorTo` converted the pixels; from which encoding to which |
| `Decode.FramesSelected` | Info | `ReaderOptions.Frames` chose some frames; how many were read |
| `Decode.ScaleChosen` | Info | For `ReaderOptions.MinimumSize`, the 1/N scale the reader decodes at and the box factor after it |
| `Detect.Format` | Info | The format detected, and how |
| `Exif.Damaged` | Warning | The EXIF block would not parse in full; it is kept as bytes |
| `Exr.BlocksDamaged` | Warning | Blocks of an OpenEXR part cannot be read or decompressed; their pixels are zero |
| `Exr.ChunkTableRebuilt` | Warning | An OpenEXR part's chunk offset table is incomplete or damaged; it was rebuilt by walking the chunks |
| `Font.BadColorGlyph` | Warning | A glyph's color data (COLR, sbix, CBDT or SVG) is damaged, cyclic or deeper than 64 paints; the part that cannot be drawn is left out, or the glyph draws as its outline (Egami.Fonts) |
| `Font.BadContainer` | Warning | A WOFF or WOFF2 file's header or blocks disagree with its data (a wrong length, a misplaced metadata block); the tables are still read (Egami.Fonts) |
| `Font.BadDirectory` | Warning | A font's table directory lists a table outside the file; it was dropped or cut short (Egami.Fonts) |
| `Font.BadGlyph` | Warning | A glyph's data cannot be read; it draws empty (Egami.Fonts) |
| `Font.BadTable` | Warning | A font table is damaged; its documented default is used (Egami.Fonts) |
| `Font.FileSkipped` | Warning | A file in a font folder cannot be indexed; it is left out of the collection (Egami.Fonts) |
| `Font.MissingTable` | Warning | A font lacks a table; its documented default is used (Egami.Fonts) |
| `Font.ShapingLimit` | Warning | A font's layout tables drove shaping past its operation or length limits, as a hostile font can; the text is shaped as far as the limits allow (Egami.Fonts) |
| `Gif.BlockTruncated` | Warning | The file ends inside a block |
| `Gif.ColorTableMissing` | Warning | A frame has no color table; it is drawn transparent |
| `Gif.ControlExtensionShort` | Warning | A graphic control extension is shorter than 4 bytes |
| `Gif.DisposalUndefined` | Warning | A frame's disposal method is undefined; it is read as none |
| `Gif.FrameOutsideScreen` | Warning | A frame sticks out of the logical screen |
| `Gif.ImageDataShort` | Warning | A frame's image data ends early |
| `Gif.ImageDataTruncated` | Warning | The file ends inside a frame's image data |
| `Gif.ImageTruncated` | Warning | The file ends inside a frame's header |
| `Gif.IndexOutOfRange` | Warning | A frame uses indices past its color table; they are drawn transparent |
| `Gif.LzwCodeSizeOne` | Warning | An LZW minimum code size of 1, outside the specification |
| `Gif.TrailerMissing` | Warning | The file ends without a trailer |
| `Gif.UnknownBlock` | Warning | An unknown block where a block introducer was expected |
| `Gif.XmpTrailerMissing` | Warning | The XMP application extension lacks its magic trailer |
| `Heic.ExifDamaged` | Warning | A HEIC Exif item's TIFF header offset points past its end; the item was skipped |
| `Heic.FramesDropped` | Warning | A frame of a HEIC image sequence after the first could not be decoded; the frames before it were loaded |
| `Heic.GainMapDropped` | Warning | A HEIC gain map (tmap item) has metadata or an image that cannot be read, or a version Egami does not know; the image loads as SDR only |
| `Heic.MatrixApproximated` | Warning | A HEIC image's matrix coefficients are reserved or constant luminance, which heif-dec does not convert; BT.601's were used |
| `Heic.MatrixUnknown` | Warning | A HEIC image's matrix coefficients have no YuvMatrix; YuvRaster.Load marks the planes BT.601 |
| `Heic.SequenceFirstFrame` | Warning | A HEIC image sequence has inter frames, which Egami.Heic does not decode; its first frame was loaded |
| `Heif.ItemCount` | Warning | A HEIF (AVIF) iinf box's entry count differs from the infe boxes it holds; the boxes found are read |
| `Heif.ItemEntryVersion` | Warning | A HEIF (AVIF) infe box has an unknown version; the item was skipped |
| `Heif.OldItemEntry` | Warning | A HEIF (AVIF) infe box of version 0 or 1 has no item type; the item was skipped |
| `Heif.PrimaryItemMissing` | Warning | A HEIF (AVIF) pitm box names an item that has no iinf entry |
| `Heif.PropertiesOfUnknownItem` | Warning | A HEIF (AVIF) ipma box associates properties with an item that has no iinf entry; they were ignored |
| `Heif.ReferenceFromUnknownItem` | Warning | A HEIF (AVIF) iref box has a reference from an item that has no iinf entry; it was ignored |
| `Heif.SecondMeta` | Warning | A HEIF (AVIF) file has a second meta box at the top level; it was ignored |
| `Heif.TrailingData` | Warning | A HEIF (AVIF) file has data after its last whole top-level box that does not form a box; it was ignored |
| `Heif.UnknownLocatedItem` | Warning | A HEIF (AVIF) iloc box locates an item that has no iinf entry; it was ignored |
| `Icc.Unusable` | Warning | The embedded ICC profile is damaged or of a kind Egami cannot use; colors are read as if it were absent |
| `Ico.DirectoryMismatch` | Warning | An icon's directory gives a size its entry's own header contradicts; the header's was used |
| `Ico.EntryDamaged` | Warning | The entry Load picked could not be decoded; the next best was loaded |
| `Ico.EntrySkipped` | Warning | An icon entry lies outside the file, overlaps another or is neither PNG nor BMP; it was skipped |
| `Ico.MaskMissing` | Warning | A BMP entry ends before its AND mask; the rows without one are opaque |
| `Iptc.Damaged` | Warning | The IPTC datasets would not parse in full; the block is kept as bytes |
| `Iptc.ResourceBlockDamaged` | Warning | The Photoshop resource block that holds IPTC is damaged; it is kept as bytes |
| `Iptc.ValueTooLong` | Warning | An IPTC value is longer than the IIM specification allows; it was kept |
| `Jbig2.ColourExtension` | Warning | A region uses the colour extension (T.88 Amendment 3); it was decoded as bilevel |
| `Jbig2.ColourPaletteSkipped` | Warning | A colour palette segment was skipped; the page decodes as bilevel |
| `Jbig2.RegionClipped` | Warning | A region lies partly outside its page; the part outside was dropped |
| `Jbig2.RegionDamaged` | Warning | A region segment is damaged; it was skipped and the rest of the page decoded |
| `Jbig2.RegionDataShort` | Warning | A region's arithmetic-coded data ends early; the rest of it is what the decoder makes of 1-bits |
| `Jbig2.ReservedSegment` | Warning | A segment has a type T.88 reserves; it was skipped |
| `Jbig2.Truncated` | Warning | The data ends inside a segment; what it holds was decoded |
| `Jpeg.ArithmeticDamaged` | Warning | Arithmetic-coded data is damaged; the rest of the image is gray |
| `Jpeg.DataEnded` | Warning | The entropy-coded data ends early; the rest is gray or has less detail |
| `Jpeg.EncodingChosen` | Info | The color space, chroma sampling and quality a JPEG save used, with `ColorType` left at Auto |
| `Jpeg.EoiMissing` | Warning | The file ends before its end-of-image marker; the scans so far were decoded |
| `Jpeg.ExtendedXmpIncomplete` | Warning | The extended XMP packet is missing parts; it was dropped |
| `Jpeg.ExtendedXmpOverLimit` | Warning | The extended XMP packet is over the metadata limit; it was dropped |
| `Jpeg.GainMapDropped` | Warning | A gain map was found but its metadata or image cannot be read; the SDR image loads without it |
| `Jpeg.IccIncomplete` | Warning | Parts of the ICC profile are missing; it was dropped |
| `Jpeg.MpfDamaged` | Warning | The Multi-Picture Format index is damaged; only the primary image is listed and loaded |
| `Jpeg.PhotoshopOverLimit` | Warning | The Photoshop resource block is over the metadata limit; it was dropped |
| `Jpeg.RestartResync` | Warning | A restart marker is missing or out of order; decoding picked up at the next one |
| `Jpeg.SegmentTruncated` | Warning | A segment between scans is cut off |
| `Jpeg.StrayBytes` | Warning | Stray bytes between segments after a scan were skipped |
| `Jpeg.ThumbnailDropped` | Warning | A lossless transform could not turn the EXIF thumbnail with the image (it is not a JPEG, or is damaged); it was left out |
| `Jpeg2000.ChannelsDropped` | Warning | Channels that are neither color nor alpha (by the channel definitions, or beyond the color space's count) were not loaded |
| `Jpeg2000.ExtraCodestreams` | Warning | A JPX file holds several codestreams; only the first was read |
| `Jpeg2000.MissingTiles` | Warning | Tiles have no data in the codestream; they were left black |
| `Jpeg2000.SignedSamples` | Warning | Signed samples were offset by half their range to load as unsigned |
| `Jpeg2000.Truncated` | Warning | The codestream ends before its last packet; what it holds was decoded |
| `Jxl.BoxDropped` | Warning | A JPEG XL metadata box (Exif, xml, jumb, jhgm, jbrd, jxli or brob) is over SafetyLimits.MaxMetadataBytes, or opens to more; it was skipped |
| `Jxl.BrobDropped` | Warning | A JPEG XL brob box is too small, compresses a box that must not be compressed, or holds broken Brotli data; it was dropped |
| `Jxl.ExifDropped` | Warning | A JPEG XL Exif box's TIFF header offset is missing or points past the box; the box was dropped |
| `Jxl.GainMapDropped` | Warning | A JPEG XL jhgm gain map box is damaged, or its metadata or map cannot be read; the SDR image loads without it |
| `Jxl.LevelInvalid` | Warning | A JPEG XL jxll box holds a level other than 5 or 10; it is ignored |
| `Jxl.RegionGroups` | Debug | How many of a JPEG XL frame's pass groups ReaderOptions.Region needed; the others were not decoded |
| `Jxl.TrailingData` | Warning | A JPEG XL file ends with bytes after its last box that do not make a whole box; they were ignored |
| `Limits.Exceeded` | Debug | A safety limit is about to fail the load: its name, the limit and the value found |
| `Metafile.BadBitmap` | Warning | A bitmap in a record or a pattern brush cannot be decoded; it is not drawn, or the brush draws black (Egami.Metafiles) |
| `Metafile.BadChecksum` | Warning | A placeable WMF header's checksum does not match; the header is used anyway, as Windows does (Egami.Metafiles) |
| `Metafile.BadHeader` | Warning | A header field cannot be used (0 units per inch, a description outside the header); a default is used or the field ignored (Egami.Metafiles) |
| `Metafile.BadObject` | Warning | An EMF+ object split over several records is broken (no size, interrupted, larger than the file); it is skipped (Egami.Metafiles) |
| `Metafile.BadRecord` | Warning | A record's fields do not fit its size, or describe geometry that cannot be drawn; that record is skipped and playback goes on (Egami.Metafiles) |
| `Metafile.FontSubstituted` | Warning | A text record names a face that is not installed; a metric-compatible or generic face is used with the record's own advances, or, with no font at all, the text is not drawn (design question 8) (Egami.Metafiles) |
| `Metafile.GlyphIndices` | Warning | Text given as glyph indices of a face that is not installed is skipped, since the indices would show wrong letters in another face (design question 9) (Egami.Metafiles) |
| `Metafile.NoEndRecord` | Warning | The file ends without its end-of-file record; the records before the end are played (Egami.Metafiles) |
| `Metafile.NoPlaceableHeader` | Warning | A WMF has no placeable header; it is sized by its window extent at 96 DPI (design question 10) (Egami.Metafiles) |
| `Metafile.RasterOperation` | Warning | A mix mode or raster operation has no Egami equivalent; it is drawn as a copy (design question 7) (Egami.Metafiles) |
| `Metafile.SkippedRecord` | Warning | Records Egami.Metafiles does not draw (flood fills, OpenGL, escapes, unknown types); they are skipped, once reported per kind (Egami.Metafiles) |
| `Metafile.TextApproximated` | Warning | An EMF+ DrawString has no layout width; its text is aligned from its origin instead (Egami.Metafiles) |
| `Metafile.TooDeep` | Warning | A metafile inside a metafile is nested deeper than 8 levels; it is not drawn (design question 12) (Egami.Metafiles) |
| `Metafile.TruncatedRecord` | Warning | A record claims more bytes than the file or its EMF+ comment holds; reading stops there and the records before it are played (Egami.Metafiles) |
| `Operation.ChainPlanned` | Info | The planner changed a chain: a crop fused into a resize, color steps fused, a no-op draw dropped |
| `Operation.GainMapDropped` | Warning | A step changed pixel values, or moved them in a way the gain map cannot follow; the gain map was dropped and the image saves as SDR only |
| `Parallel.Bands` | Debug | How many bands and threads a parallel decode or encode uses |
| `Png.ActlAfterData` | Warning | The acTL chunk comes after the image data; the file is read as a still PNG |
| `Png.ActlFrameCount` | Warning | The acTL chunk gives an impossible frame count; the file is read as a still PNG |
| `Png.ActlLength` | Warning | The acTL chunk has the wrong length; the file is read as a still PNG |
| `Png.ActlRepeated` | Warning | A second acTL chunk was ignored |
| `Png.AdlerMismatch` | Warning | The image data's Adler-32 checksum is wrong |
| `Png.AnimationTruncated` | Warning | The file ends inside the animation; the frames before are kept |
| `Png.ChecksumMismatch` | Warning | An ancillary chunk's CRC is wrong; the chunk was ignored |
| `Png.ChunkMalformed` | Warning | An ancillary chunk is malformed; it was ignored |
| `Png.ChunkOutOfOrder` | Warning | A chunk breaks an ordering rule; it was ignored |
| `Png.ChunkOutOfPlace` | Warning | A chunk appears where it may not; it was ignored |
| `Png.ChunkTruncated` | Warning | The file ends inside a chunk after the image data |
| `Png.ColorTypeChosen` | Info | The color type, bit depth and palette a PNG save chose, with `ColorType` left unset |
| `Png.DamageAfterLastRow` | Warning | The compressed data is damaged after the last row |
| `Png.DataAfterLastRow` | Warning | The image data has extra bytes after the last row |
| `Png.DataEmpty` | Warning | The image data ends before any row; the image is transparent black |
| `Png.DataTruncated` | Warning | The file ends inside the image data |
| `Png.ExifDuplicate` | Warning | EXIF is given twice (eXIf and a raw profile); the first is used |
| `Png.FctlUnknownOps` | Warning | An fcTL chunk has unknown disposal or blend values; they are read as 0 |
| `Png.FdatMissing` | Warning | An fcTL chunk is not followed by fdAT; the animation ends there |
| `Png.FramesExtra` | Warning | There are more frames than acTL gives; the rest were ignored |
| `Png.FramesMissing` | Warning | There are fewer frames than acTL gives |
| `Png.IendData` | Warning | The IEND chunk has data |
| `Png.IendMissing` | Warning | The file ends without an IEND chunk |
| `Png.IndexOutOfRange` | Warning | Pixels use palette indices past the palette; they were made black |
| `Png.PaletteTooLong` | Warning | The palette has more entries than the bit depth can index |
| `Png.RawProfileInvalid` | Warning | A "Raw profile type" text chunk cannot be decoded; it is kept as text |
| `Png.RowsMissing` | Warning | The image data ends early; the rows not decoded are transparent black |
| `Png.SequenceError` | Warning | An APNG sequence number is out of order |
| `Png.TrnsInvalid` | Warning | The tRNS chunk does not fit the color type; it was ignored |
| `Png.XmpDuplicate` | Warning | XMP is given twice (iTXt and a raw profile); the iTXt chunk is used |
| `Pnm.DataAfterImage` | Warning | More data follows the image; only the first image is read |
| `Pnm.RowsMissing` | Warning | The pixel data ends or is damaged; the rows not decoded are black |
| `Pnm.SamplesClamped` | Warning | Samples above the maxval were clamped |
| `Pnm.TupleTypeMismatch` | Warning | The PAM tuple type does not fit the depth; the image is read by its depth |
| `Pnm.UnknownHeaderLine` | Warning | An unknown PAM header line was ignored |
| `Psd.ChannelsDropped` | Warning | A multichannel file has no color model; its first channel was loaded as gray and the rest were not |
| `Psd.DataTruncated` | Warning | The file ends inside the flattened image; the missing pixels are 0 |
| `Psd.LayersDamaged` | Warning | A layer record is damaged; the records after it were not read, which the flattened image does not need |
| `Psd.NoComposite` | Warning | The file was saved without Maximize Compatibility; its stored placeholder was loaded, not the layers composited |
| `Psd.ResourcesDamaged` | Warning | An image resource is damaged; the resources after it were skipped |
| `Psd.RowShort` | Warning | A PackBits row decodes short; the rest of it is 0 |
| `Qoi.DataAfterEnd` | Warning | Data follows the last pixel where the end marker should be |
| `Qoi.EndMarkerMissing` | Warning | The file ends without the QOI end marker |
| `Qoi.RowsMissing` | Warning | The file ends early; the rows not decoded are transparent black |
| `Radiance.RowsMissing` | Warning | The file ends early; the scanlines not decoded are black |
| `Save.AlphaDropped` | Warning | The format, as written with these options, has no alpha; translucent pixels were written opaque |
| `Save.AniDelayRaised` | Warning | ANI frames shown for less than 20 ms were written as 100 ms, as browsers play such GIF frames |
| `Save.AniLoops` | Warning | The raster's LoopCount was not 0; ANI animations always loop, so it was not written |
| `Save.ColorsQuantized` | Warning | The image has more colors than the palette holds; it was reduced |
| `Save.ConvertedToRgb` | Warning | The format holds no CMYK; the inks were converted to RGB |
| `Save.DepthReduced` | Warning | Samples deeper than the format holds were rounded, or floating-point samples clipped to integers |
| `Save.FramesDropped` | Warning | The format holds one image here; only the first frame was written |
| `Save.GainMapDropped` | Warning | The format holds no gain map, or the map no longer fits the image; the SDR base was written without it |
| `Save.HdrClipped` | Warning | An HDR raster with light above reference white was saved to a format that holds 0 to 1; the highlights were clipped and ToneMap would have kept them |
| `Save.IcoFitted` | Warning | A raster larger than an icon entry's 256 × 256 was fitted to 256 |
| `Save.MetadataDropped` | Warning | Metadata the format cannot hold, or that is too large for it, was not written |
| `Streaming.BufferGrown` | Debug | An incremental decode's buffer grew past its first size to hold bytes not yet decoded |
| `Streaming.ReadWhole` | Debug | A stream that cannot seek was read into memory whole, to find every image |
| `Streaming.Replayed` | Debug | A transcode of a stream that cannot seek kept the bytes it read to plan, and read them again |
| `Svg.Approximated` | Warning | A blend mode or call SVG cannot express was written as the nearest SVG can draw, or left out (Egami.Svg's SvgCanvas) |
| `Svg.BadAttribute` | Warning | An attribute's value cannot be read (a length, number, list, viewBox or keyword); it is ignored, as browsers ignore it (Egami.Svg) |
| `Svg.BadPathData` | Warning | Path data breaks the grammar; the path is drawn up to the error, as SVG says (Egami.Svg) |
| `Svg.BadReference` | Warning | A reference (href, url(#id) in fill, stroke, clip-path, mask, marker) names no element of the right kind, or forms a cycle; what refers to it draws as SVG says for a missing reference (Egami.Svg) |
| `Svg.BadStyle` | Warning | A property's value cannot be used where it is applied; the inherited or initial value is used (Egami.Svg) |
| `Svg.BadStyleSheet` | Warning | A style sheet or style attribute has a CSS syntax error; the declaration or rule is dropped and the rest applies, as CSS says (Egami.Svg) |
| `Svg.DefaultSize` | Warning | The root has no width, height or viewBox to size it; it is drawn at 100 by 100 pixels, as resvg does (Egami.Svg) |
| `Svg.FallbackImage` | Info | A drawing call SVG cannot express was drawn by Egami and embedded as a PNG image (Egami.Svg's SvgCanvas) |
| `Svg.FontNotEmbedded` | Warning | A font's license forbids embedding a subset, or it could not be subset; its text names the font only (Egami.Svg's SvgCanvas) |
| `Svg.FontNotFound` | Warning | A font-family names a family no font has, or no font is available at all; the next family, the serif family or no text is drawn (Egami.Svg) |
| `Svg.FontNotLoaded` | Warning | An @font-face rule has no single family, or none of its sources could be read; the family falls back (Egami.Svg) |
| `Svg.ImageNotLoaded` | Warning | An image was not loaded: no resolver allows its URI, the resolver refused it, or it cannot be decoded; it draws nothing (Egami.Svg) |
| `Svg.NoNamespace` | Warning | The root svg element has no namespace; its elements are read as SVG anyway (Egami.Svg) |
| `Svg.Unsupported` | Warning | An element Egami.Svg does not draw (foreignObject, and text until stage C); it is skipped (Egami.Svg) |
| `Svg.UnsupportedColor` | Warning | A color uses CSS Color 4 syntax, which SVG here does not read (question 5); the initial value is used (Egami.Svg) |
| `Text.LcdFallback` | Warning | LCD text edging could not be drawn (no pixel geometry, a canvas without 8-bit RGB, a layer, or a paint other than solid source-over); the text was drawn with grayscale antialiasing, reported once per canvas (Egami.Drawing) |
| `Tga.IndexOutOfRange` | Warning | Pixels use indices outside the color map; they are drawn black |
| `Tga.RowsMissing` | Warning | The pixel data ends or is damaged; the rows not decoded are black |
| `Tga.RunPastImage` | Warning | An RLE packet goes past the end of the image; the rest of it was ignored |
| `Tiff.ByteCountsMissing` | Warning | A page has no usable byte counts; they were worked out |
| `Tiff.ExtraSamplesDropped` | Warning | Extra samples that are not alpha were dropped |
| `Tiff.Float64Narrowed` | Warning | 64-bit floating-point samples were read as 32-bit |
| `Tiff.IfdDamaged` | Warning | An IFD in the chain is damaged; the pages before it were read |
| `Tiff.IfdLoop` | Warning | The IFD chain loops; it was read up to there |
| `Tiff.IfdOutside` | Warning | The next IFD lies outside the file; the pages before it were read |
| `Tiff.InksNotCmyk` | Warning | A page has inks that are not CMYK; only the first was read, as gray |
| `Tiff.OffsetsMissing` | Warning | A page has fewer strip or tile offsets than it needs; the rest is black |
| `Tiff.PhotometricMissing` | Warning | A page has no PhotometricInterpretation; it was read as RGB or gray |
| `Tiff.PredictorIgnored` | Warning | A predictor with a compression it means nothing to was ignored |
| `Tiff.RowsPerStripZero` | Warning | RowsPerStrip is 0; the page was read as one strip |
| `Tiff.SignedSamples` | Warning | Signed samples were offset so 0 is the middle value |
| `Tiff.SubIfdDamaged` | Warning | An EXIF, GPS or other sub-IFD is damaged; it was skipped |
| `Tiff.TileSizeOdd` | Warning | Tiles are not multiples of 16, as TIFF 6.0 requires; they were read as they are |
| `Tiff.UnitsShort` | Warning | Strips or tiles decode to fewer bytes than their rows; the rest is zero |
| `Webp.AlphaDamaged` | Warning | The alpha data is short or damaged; the missing rows are opaque or transparent |
| `Webp.AlphaEmpty` | Warning | The ALPH chunk is empty; the image is read as opaque |
| `Webp.AlphaRepeated` | Warning | A second ALPH chunk was ignored |
| `Webp.AnimationFlagWithoutFrames` | Warning | The header says animated, but the file holds a still image |
| `Webp.AnimInvalid` | Warning | The ANIM chunk has the wrong size; it was ignored |
| `Webp.AnmfShort` | Warning | An ANMF chunk is too short for its header; the frame was skipped |
| `Webp.ChunkBetweenFrames` | Warning | A chunk lies between the frames of an animation; it was ignored |
| `Webp.ChunkHeaderTruncated` | Warning | The file ends inside a chunk header |
| `Webp.ChunkPastRiff` | Warning | A chunk's size runs past the RIFF data |
| `Webp.ChunkTruncated` | Warning | The file ends inside a chunk |
| `Webp.EncodingChosen` | Info | Lossless or lossy, and the quality, a WebP save used with `Lossless` left unset |
| `Webp.ExifRepeated` | Warning | A second EXIF chunk was ignored |
| `Webp.FileTruncated` | Warning | The file ends early, or before the end the RIFF size gives |
| `Webp.FrameChunkPastAnmf` | Warning | A frame's image chunk runs past its ANMF chunk |
| `Webp.FrameOutsideCanvas` | Warning | A frame reaches past the canvas; it was clipped |
| `Webp.FramesWithoutAnimationFlag` | Warning | The file holds ANMF frames, but the header does not say it is animated |
| `Webp.IccAfterImage` | Warning | The ICCP chunk comes after the image data; it was read anyway |
| `Webp.LossyDataEnded` | Warning | The lossy image data ends early; the rest is decoded from zeros |
| `Webp.MetadataOverLimit` | Warning | A metadata chunk is over the metadata limit; it was dropped |
| `Webp.RiffSizeMismatch` | Warning | The RIFF size does not match the file |
| `Webp.RowsMissing` | Warning | The lossless data is damaged; the rows from there on are transparent black |
| `Webp.SecondImage` | Warning | A second image chunk follows the image; it was ignored |
| `Webp.TrailingBytes` | Warning | Bytes after the last chunk are too few for another; they were ignored |
| `Webp.XmpRepeated` | Warning | A second XMP chunk was ignored |
| `Xmp.Damaged` | Warning | The XMP packet is not well-formed XML; it is kept as bytes |
