# Your own format

Egami's formats are plug-ins like any other: a format you write loads, probes, streams and saves through the same calls as PNG or
JPEG, and stays inside the same safety limits. Register it on an `EgamiContext` and every `Load`, `Probe` and `Save` with that
context can use it.

A format is a `FormatInfo`: a name, extensions, a signature check, a reader and a writer.

[!code-csharp[](../../../tests/Egami.DocSamples/Extending.cs#custom-format)]

[!code-csharp[](../../../tests/Egami.DocSamples/Extending.cs#use-custom-format)]

Readers read only through `EgamiInput`, which keeps them inside the safety limits:

| Member | Use |
| --- | --- |
| `Peek`, `Read`, `ReadExactly`, `Skip`, `ReadByte`, `ReadUInt16/32…` | Read the data; a short read throws `CorruptDataException` |
| `CreateRaster<TPixel>(width, height)` | The raster you decode into, size-checked before any allocation |
| `AddFrame(raster)` | Another frame for animated formats, counted against `MaxFrames` and the memory budget |
| `RentScratch(bytes)` | Working memory counted against the same budget |
| `CheckFrameSize`, `CheckMemory`, `CheckFrameCount`, `CheckMetadataBytes` | Check a number from the header before acting on it |
| `Warn(raster, id, text)` | Note something a lenient read recovered from, with a stable id such as `Pcx.PaletteMissing`; `Warn(metadata, id, text)` does the same for an open `RowReader`'s metadata |
| `Corrupt(offset, problem)` | The exception to throw for data that breaks the format's rules |

Respect `ReaderOptions.Recovery`: in `Lenient` mode keep what decoded and warn; in `Strict` mode throw. The writer gets the
raster and a stream; options come as your own `WriterOptions` subclass.

## Streaming

Two optional interfaces let a format stream. A reader that also implements `IRowFormatReader` returns a `RowReader<TNatural>`
from `Open`: the open image hands out rows as `RasterDecoder` asks for them (top-down, never going back within a frame) and
moves through frames with `NextFrame`. The decoder applies `Region`, `MinimumSize` and the pixel conversion after it. A writer
that also implements `IRowFormatWriter` returns a `RowEncoder<TPixel>` from `Start`, which takes rows (or frames, for an
animation) one at a time; `Save`, `SaveAsync`, `RasterEncoder` and `Transcode` then all write through it. When streaming,
`EncodeStart.Whole` is `null`: an option that needs every pixel before the first byte should call `EncodeStart.Refuse`.
An open image checks the header's size with `CheckFrameSize` before it rents its row buffers, since it creates no raster. An
encoder calls `EncodeStart.ReportLosses` with the `SaveKeeps` flags for what it writes, so a save that drops alpha, depth,
frames or metadata says so in the Warnings, as Egami's own writers do.

[!code-csharp[](../../../tests/Egami.DocSamples/Extending.cs#row-format)]

A format without them still works everywhere: the decoder reads the whole image with `Read` and serves rows from it, and the
encoder gathers the image and calls `Write` at the end.

Writing your own operation or pixel type is covered in [Custom operations and formats](../guides/extending.md).

## Converting without decoding

A writer that can turn a file of another format into its own without decoding it to pixels, as DNG keeps a camera's sensor
data, also implements `IFormatConverter`. `Transcode` offers it the input when there are no operations and the reader options
ask for nothing; it decides by the input's format, before reading anything, and returns `false` to have the file decoded as
usual.

[!code-csharp[](../../../tests/Egami.DocSamples/Extending.cs#format-converter)]

## Files with several images

A format whose files hold several images, as TIFF holds pages, also implements `IMultiImageReader`. LoadAll asks it for one
`ReaderOptions` per image, usually a subclass of your own that names the image, and loads each as Load would with those options. A writer that writes several images to one file implements
`IMultiImageWriter`, which SaveAll hands every raster.

[!code-csharp[](../../../tests/Egami.DocSamples/Extending.cs#multi-image-format)]
