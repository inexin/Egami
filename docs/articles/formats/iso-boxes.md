# ISO BMFF boxes

MP4, QuickTime, HEIF, HEIC and AVIF files are made of boxes (ISO/IEC 14496-12). Egami reads AVIF and HEIC itself; for other
work on these files, such as reading an MP4's tracks or a tag Egami does not keep, `IsoBoxReader` walks the boxes for you. It
reads one level at a time from bytes in memory, allocates nothing, and checks every size: a box that runs past its parent, or
one too small for its header, raises a `CorruptDataException` that names the format you gave and the box's offset.

[!code-csharp[](../../../tests/Egami.DocSamples/IsoBoxes.cs#walk)]

`IsoBox` gives each box's type, where it starts, where its contents start and where it ends. Box types are `FourCC` values:
`FourCC.Parse("moov")` makes one, and `ToString` prints it, with bytes outside printable ASCII escaped (`\xa9nam`).
`Children(box, skip)` walks the boxes inside one; `skip` passes the fields a box has before its children, such as a full box's
version and flags (4 bytes) or a sample description's entry count (8 bytes). `Content(box)` gives a box's bytes. Large sizes,
size 0 (to the end of the parent) and uuid boxes, whose extended type is `IsoBox.UserType`, are read too.

## Large files

A file too large to read into memory is walked from a stream: `IsoBoxReader.ReadHeader` reads a box header from its first
`IsoBoxReader.MaxHeaderSize` bytes, so you can skip over media data and load only the boxes you need. A reader made with an
`offset` reports file offsets for the boxes inside the part you loaded.

[!code-csharp[](../../../tests/Egami.DocSamples/IsoBoxes.cs#stream)]

Egami.Video reads MP4 and QuickTime files this way.
