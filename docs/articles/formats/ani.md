# ANI

Egami reads and writes Windows animated cursors (.ani), written from Microsoft's description of the RIFF `ACON` form.
`AniFormat.Info` is registered in `EgamiContext.Default`. An animated cursor stores a set of frames, each a whole cursor or icon,
and a list of steps that says which frame shows and for how long; Egami's [ICO and CUR](ico.md) reader decodes the frames, so a
frame loads exactly as the same cursor would, and its CUR writer stores the frames when Egami writes one.

## Reading animated cursors

`Raster.Load` returns one animated raster with a frame per step: the `seq ` list is played out, so a frame it shows twice is in
the raster twice, and each frame's `Delay` is its step's rate in jiffies (1/60 s), or the header's default rate. Animated cursors
always loop, so `LoopCount` is 0. Every frame replaces the one before it (`FrameDisposal.ClearToBackground`, `FrameBlend.Source`),
so the frames save straight to GIF, APNG or WebP. Each frame's hotspot is in its `IcoDetails`, and can move from frame to frame.

All frames of a raster share one size, but a frame's cursor can hold several. Load picks one for the whole animation the way it
picks an icon's entry: the largest, or with `ReaderOptions.MinimumSize` the smallest size at least that big. A frame without that
size is resized from its nearest larger image, hotspot and all, with the `Ani.SizeMissing` warning. `Raster.LoadAll` returns the
animation once for every size, largest first.

[!code-csharp[](../../../tests/Egami.DocSamples/Ani.cs#ani-load)]

`Raster.Probe` reads the chunks and each frame's directory without decoding a pixel: `RasterInfo.FrameCount` is the number of
steps and `ImageCount` the number of sizes. `AniDetails`, the raster's format details, gives the title and artist from the file's
`INFO` list (also added to the raster's text entries as `Title` and `Author`, the keys PNG and TGA use), the default delay, how
many frames the file stores, the frame each step shows and every size.

[!code-csharp[](../../../tests/Egami.DocSamples/Ani.cs#ani-details)]

Old files sometimes store raw bitmaps instead of cursors (the header's icon flag is clear): whole BMP files, BMP data without
the file header, or bare 24 or 32-bit rows at the header's size. Egami reads all three; their frames have no hotspot, and
`AniDetails.RawBitmaps` is true. A frame that is a cursor in a file that says otherwise is read as a cursor, with the
`Ani.IconInRawFile` warning.

## Writing animated cursors

`Raster.Save` with a `.ani` path, or `AniFormat.Info`, writes an animated raster as a cursor: each frame is a step. A frame with
the same pixels and hotspot as an earlier one is stored once, and a `seq ` list says which stored frame each step shows, so a
spinner that comes back to its first picture costs nothing extra. Every stored frame is a cursor written by Egami's
[CUR writer](ico.md), 32-bit BMP entries below 256 × 256 and PNG at 256, which is what Windows expects.

`AniWriterOptions` has the same words as `IcoWriterOptions`. `Sizes` gives every stored frame each size, from the frame resized
with `Filter`; `AniWriterOptions.CursorSizes` (32, 48, 64, 96 and 128) are the sizes Windows draws cursors at as the display
scale grows. Without `Sizes` the frames keep the raster's size, fitted down to 256 with the `Save.IcoFitted` warning when larger.
With several sizes and no `Encoding`, every entry of every frame has one encoding: 32-bit BMP while each entry starts within the
frame's first 64 KiB, else PNG, as for the five cursor sizes. Windows loads no animated cursor with an entry past that point,
and given BMP and PNG entries in one frame it may draw a BMP entry of another size. `Encoding = IcoEntryEncoding.Bmp` at sizes
that cannot fit is written as asked, with the `Save.AniTooLarge` warning.
`Hotspot` sets every frame's hotspot at the raster's size, scaled to each entry; without it each frame keeps its own
`IcoDetails.Hotspot`, so a cursor loaded from ANI or CUR saves with its hotspots. `Title` and `Artist` go into the `INFO` list,
or come from a loaded cursor's `AniDetails`, else from the raster's `Title` and `Author` text entries, unless `StripMetadata`
is set; so a PNG's title carries into the cursor.

[!code-csharp[](../../../tests/Egami.DocSamples/Ani.cs#ani-save)]

Delays are rounded to the nearest jiffy. A frame shown for less than 20 ms is written as 100 ms, as browsers play such GIF frames,
with the `Save.AniDelayRaised` warning. The most common delay becomes the header's default rate, and a `rate` list is written
only when the delays differ. Animated cursors always loop, so a `LoopCount` other than 0 is not written, with the
`Save.AniLoops` warning. Raw-bitmap frames are read but never written. The same raster and options give the same bytes, and
`egami convert spinner.gif spinner.ani --set ani.sizes=32,48` turns a GIF into a cursor from the command line.

## Damaged and hostile data

Every count, index and size is checked before it is used; CVE-2007-0038 came from a reader that trusted the `anih` header's size.
The steps are checked against `SafetyLimits.MaxFrames` and every frame's size against the other limits before its pixels are
allocated, and each stored frame is decoded once however often it shows. In lenient mode, the default, damage becomes a warning
and the parts that can be used load: an `anih` chunk that is not 36 bytes is read for its first 36 (`Ani.HeaderSize`), a step
that names a frame the file does not store is left out (`Ani.SequenceIndex`), a rate of 0 uses the default rate
(`Ani.ZeroRate`), and a frame that cannot be read is left out with the steps that show it (`Ani.FrameDamaged`), as is the cut-off
end of a truncated file. In strict mode each of these throws `CorruptDataException`; a file with no step left to show throws in
both. Odd-sized chunks without RIFF's pad byte, which some writers leave out, are followed. The reader is fuzzed through whole
files.
