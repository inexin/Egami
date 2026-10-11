# Metadata

`raster.Metadata` holds what a file says beside its pixels. It is kept apart from the pixels, survives operations unless an
operation makes it wrong, and is written back by every format that can carry it.

| Item | Read from | Written to |
| --- | --- | --- |
| `Exif` (`ExifData`) | JPEG, PNG | JPEG, PNG |
| `Xmp` | JPEG (with extended XMP), PNG, GIF | JPEG, PNG, GIF |
| `IccProfile` | JPEG, PNG, GIF, BMP (V5) | JPEG, PNG, GIF, BMP |
| `Text` | PNG text chunks, GIF comments, PNM comments, TGA author and comments | The same |
| `Resolution` | PNG, JPEG, BMP | PNG, JPEG, BMP |
| `ColorSpace`, `Gamma` | PNG, QOI, TGA | PNG, QOI |
| `LoopCount`, frame `Delay` | GIF, APNG | GIF, APNG |
| `FormatDetails` | Every format: what only it has | The same format, for round trips |
| `Warnings` | Every reader: what a lenient read recovered from | Never |

[!code-csharp[](../../../tests/Egami.DocSamples/AnimationAndMetadata.cs#metadata)]

## EXIF

[!code-csharp[](../../../tests/Egami.DocSamples/AnimationAndMetadata.cs#exif)]

Common values have typed properties (`Orientation`, `DateTaken`, `CameraMake`, `CameraModel`, `Location`); any tag can be read
with `TryGet` and written with `Set`. A block you do not edit is saved back byte for byte; an edited one is laid out again, in
tag order. A damaged block keeps its raw bytes and sets `IsDamaged`, and the pixels still load. `ExifData.Parse` reads a block on
its own, capped by `SafetyLimits.MaxMetadataBytes`.

## What operations do to metadata

- Rotating, flipping and `AutoOrient` set the EXIF orientation to `Normal`.
- Resizing and cropping, including your own operations that change the size, update the EXIF pixel dimensions and drop the
  embedded thumbnail, which no longer matches.
- Color operations change nothing in the metadata.

## Removing metadata

[!code-csharp[](../../../tests/Egami.DocSamples/AnimationAndMetadata.cs#strip)]

## Typed EXIF values

[!code-csharp[](../../../tests/Egami.DocSamples/ApiExamples.cs#exif-values)]

EXIF stored somewhere else, such as a database, parses on its own:

[!code-csharp[](../../../tests/Egami.DocSamples/ApiExamples.cs#exif-parse)]

## IPTC

`IptcData` holds IPTC-IIM datasets, as news agencies, stock libraries and Lightroom write them. Each `IptcEntry` has an
`IptcKey` (record and dataset, such as 2:25 for keywords), and typed getters cover the common ones. Values longer than the
IIM specification allows are refused on `Set` and `Add`; long text belongs in XMP. Text that is not ASCII switches the block
to UTF-8.

[!code-csharp[](../../../tests/Egami.DocSamples/ApiExamples.cs#iptc-values)]

IPTC stored somewhere else parses on its own:

[!code-csharp[](../../../tests/Egami.DocSamples/ApiExamples.cs#iptc-parse)]

## Color space

`RasterMetadata.ColorSpace` says what the file declared about its colors. Egami keeps the declaration and the profile bytes;
it does not convert between color spaces.

[!code-csharp[](../../../tests/Egami.DocSamples/ApiExamples.cs#color-space-hint)]

## Run it

[`samples/Metadata`](https://github.com/inexin/Egami/tree/main/samples/Metadata) is a console app that does what this guide shows on the bundled
images: `dotnet run --project samples/Metadata` writes its results to `./output`.
