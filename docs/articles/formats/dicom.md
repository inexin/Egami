# DICOM (preview)

DICOM is the format of medical imaging: a file (PS3.10) holds a dataset of tagged data elements (the patient, the study, how
the pixels are stored), then the pixel data, native or compressed with JPEG, JPEG-LS, JPEG 2000, JPEG XL or RLE. DICOM reads
through the Egami.Formats.Extended package, as a preview: every DICOM type is marked `[Experimental]` with the diagnostic id
`EGAMIX001`, so code that uses it suppresses that id (`<NoWarn>$(NoWarn);EGAMIX001</NoWarn>`), and the API may change before
it is stable.

This version reads the dataset and decodes the pixel data of every frame through the display pipeline (rescale, window,
colour), writes Secondary Capture images from rasters, and saves opened files again in another transfer syntax.

| | |
| --- | --- |
| Loads | One frame a load, as a viewer shows it: `Gray8` or `Gray16` for monochrome images, `Rgb8` or `Rgb16` for colour and palette images; modality or stored values as `GrayF32` on request; each frame an image for `Raster.LoadAll` |
| Pixel data | Native (1, 8, 16 and 32 bits allocated, planar or interleaved, either byte order), RLE Lossless, Deflated Image Frame Compression, encapsulated uncompressed, JPEG baseline, extended and lossless, JPEG-LS, JPEG 2000, High-Throughput JPEG 2000 and JPEG XL |
| Reads | PS3.10 files with the 128-byte preamble and DICM, and bare datasets without them; implicit VR little endian, explicit VR little endian, explicit VR big endian and deflated explicit VR little endian datasets; sequences and items of defined and undefined length to 64 levels; every VR of PS3.5 table 6.2-1; encapsulated pixel data as fragments with the Basic and Extended Offset Tables; text in the character sets of PS3.3 C.12.1.1.2, ISO 2022 code extension included |
| Writes | Secondary Capture images from any raster, and opened files in another transfer syntax: Explicit and Implicit VR Little Endian, Deflated Explicit VR Little Endian, RLE Lossless, JPEG Baseline, Extended and Lossless (SV1), JPEG-LS lossless and near-lossless, JPEG XL lossless and lossy |
| `FormatInfo` | `DicomFormat.Info`: name `DICOM`, extensions `.dcm`, `.dicom` and `.dic`, MIME type `application/dicom`; registered with `WithDicom()`, not by `WithExtendedFormats()` |

## The dataset

`DicomFile.Load` reads a file into its file meta information and its dataset. Elements are found by PS3.6 keyword or by tag,
and each one decodes its value by its VR (`DicomElement.VR`, a `DicomVR`).

[!code-csharp[](../../../tests/Egami.DocSamples/Dicom.cs#dicom-load)]

| Value | Read with |
| --- | --- |
| Text (AE, AS, CS, LO, LT, PN, SH, ST, UC, UI, UR, UT) | `GetString`, or `GetStrings` for each value of a multi-valued string |
| Numbers (DS, IS, US, SS, UL, SL, UV, SV, FL, FD, and OW, OL, OV, OF, OD) | `GetNumbers`, `GetIntegers`; `DicomDataset.GetNumber` for the first |
| Dates and times (DA, TM, DT) | `GetDate`, `GetTime`, `GetDateTime` |
| Person names (PN) | `GetPersonName`: a `DicomPersonName` with its alphabetic, ideographic and phonetic groups and the five name components |
| Tags (AT) | `GetTags` |
| Sequences (SQ) | `Items`, each a `DicomDataset` |
| Bytes (OB, UN, private tags) | `RawValue`, as the file stores them |

[!code-csharp[](../../../tests/Egami.DocSamples/Dicom.cs#dicom-values)]

Implicit VR files do not state the VR; it comes from PS3.6, with the choices PS3.6 leaves (US or SS, OB or OW) made from Pixel
Representation and the element. Private elements read as UN (their creators as LO), and an unknown element of undefined length
reads as the sequence PS3.5 section 6.2.2 says it is.

## Tags and the dictionary

`DicomTag` is a group and an element. Its keyword, name and retired flag come from the data dictionary of PS3.6, generated from
the standard's own XML by `tools/make-dicom-dictionary.py` and embedded in the package; `DicomFile.DictionaryEdition` names the
edition. Repeating groups such as the overlays (60xx) match every group they cover. `DicomTransferSyntax` names a file's
transfer syntax from PS3.6's UID registry.

[!code-csharp[](../../../tests/Egami.DocSamples/Dicom.cs#dicom-tags)]

## Character sets

Text in SH, LO, ST, LT, PN, UC and UT decodes by the dataset's Specific Character Set (0008,0005), which an item inherits
unless it has its own: ISO-IR 6 (read as Latin-1 so that undeclared 8-bit text is kept), Latin-1 to Latin-5 and Latin-9,
Cyrillic, Arabic, Greek, Hebrew, Thai, JIS X 0201, UTF-8 (ISO_IR 192), GB18030 and GBK, and with ISO 2022 code extension JIS X
0208 and JIS X 0212, KS X 1001 and GB 2312 between escape sequences, reset at each delimiter as PS3.5 section 6.1.2.5.3 says.
An unknown term reads as Latin-1 with the `Dicom.CharacterSet` warning.

## Compressed pixel data

Encapsulated pixel data (any transfer syntax but the four native ones) is kept as its fragments, without the item headers,
with the Basic Offset Table and, when the dataset has one, the Extended Offset Table (7FE0,0001) and its lengths (7FE0,0002).

[!code-csharp[](../../../tests/Egami.DocSamples/Dicom.cs#dicom-fragments)]

## Pixels

Registered with `WithDicom()`, `Raster.Load` decodes the first frame and takes it through what PS3.3 C.11 says a display does,
so the image looks as it would in a viewer. `DicomDetails` in the metadata says what was applied.

[!code-csharp[](../../../tests/Egami.DocSamples/Dicom.cs#dicom-pixels)]

For a monochrome image, in order:

1. Stored values: the Bits Stored under the High Bit, sign-extended when Pixel Representation is 1; other bits (old overlays)
   are dropped. A JPEG, JPEG-LS or JPEG 2000 codestream that codes signed data as unsigned is sign-extended the same way.
2. Modality values: the Modality LUT Sequence, or Rescale Slope and Intercept (for CT, Hounsfield units).
3. The VOI transform: the VOI LUT Sequence, or the window (Window Center and Width with the VOI LUT Function LINEAR,
   LINEAR_EXACT or SIGMOID of PS3.3 C.11.2.1.2). A file with neither is stretched from its frame's smallest to its largest
   modality value. The output fills `Gray8` when Bits Stored is 8 or less, `Gray16` otherwise.
4. Inversion: MONOCHROME1 images are inverted, unless a Presentation LUT Shape says IDENTITY; INVERSE inverts any.

Enhanced multi-frame files take each frame's rescale (Pixel Value Transformation) and window (Frame VOI LUT) from its
Per-Frame Functional Groups, then the Shared ones, then the dataset.

`DicomReaderOptions` changes what a load gives: a window of your own, which of several windows or VOI LUTs, the window over
the VOI LUT, or the numbers behind the picture.

[!code-csharp[](../../../tests/Egami.DocSamples/Dicom.cs#dicom-window)]

[!code-csharp[](../../../tests/Egami.DocSamples/Dicom.cs#dicom-values-option)]

| `DicomValues` | Monochrome | Colour and palette |
| --- | --- | --- |
| `Display` (default) | `Gray8` or `Gray16`, as above | `Rgb8` or `Rgb16`, YBR converted to RGB, palette indices through the colour tables |
| `Modality` | `GrayF32` modality values, such as Hounsfield units | As `Display` |
| `Stored` | `GrayF32` stored values with their sign (exact to 2^24; unsigned 32-bit values above 2^31 − 1 are clamped) | `Rgb8` or `Rgb16` as stored, YBR not converted; palette indices as `GrayF32` |

Colour: RGB is scaled to 8 or 16 bits; YBR_FULL and YBR_FULL_422 convert by the full-range equations of PS3.3 C.7.6.3.1.2,
YBR_PARTIAL_422 and YBR_PARTIAL_420 by the BT.601 studio-range ones; YBR_RCT and YBR_ICT are already RGB once the JPEG 2000
decoder undoes its component transform, and JPEG XL decodes to RGB. PALETTE COLOR goes through the red, green and blue tables,
segmented ones included, to `Rgb8` for 8-bit tables and `Rgb16` for 16-bit ones.

## Frames

Each frame of a multi-frame file is an image of its own: `Raster.Probe` gives their number as `ImageCount`, `Raster.LoadAll`
loads them all, and `DicomReaderOptions.Frame` picks one. `DicomFile.LoadFrame` decodes frames of a file already read, so a
long series is read once. Encapsulated frames are found by the Extended Offset Table, else the Basic Offset Table, else one
fragment a frame when the counts match, else from where each codestream starts. Cine timing (Frame Time, the Frame Time
Vector, and the recommended or cine frame rate) is in `DicomDetails`.

[!code-csharp[](../../../tests/Egami.DocSamples/Dicom.cs#dicom-frames)]

## Writing

A raster saves as a Secondary Capture image (PS3.3 A.8.1) with `DicomFormat.Info`: gray pixel types as MONOCHROME2, any other
as RGB, at 8 bits for 8-bit pixel types and 16 for wider and float ones; alpha is dropped. The writer makes new Study, Series and
SOP Instance UIDs and leaves the type 2 elements of the Patient, Study and Series modules empty; `DicomWriterOptions.Attributes`
gives any element by PS3.6 keyword, its value as text (several separated by `\`), numbers and tags parsed by the element's VR.
Text that is not ASCII is written in UTF-8 with Specific Character Set `ISO_IR 192`. The elements that describe the pixels (the
Image Pixel module, Pixel Data, SOP Class UID) are the writer's own; giving one throws `ArgumentException`.

[!code-csharp[](../../../tests/Egami.DocSamples/Dicom.cs#dicom-save)]

`DicomFile.Save` writes an opened file again in the transfer syntax `DicomWriterOptions.TransferSyntax` names, keeping every
element, private ones and sequences included; only group lengths, which would no longer be right, are left out. Pixel data
already in that transfer syntax, or native in a little-endian file written native, is copied as it is. Any other is decoded
strictly (a damaged frame throws) and coded again; the Photometric Interpretation and Planar Configuration follow (YBR_FULL_422
data decodes to YBR_FULL; JPEG 2000 YBR_RCT and YBR_ICT and JPEG XL to RGB).

[!code-csharp[](../../../tests/Egami.DocSamples/Dicom.cs#dicom-transcode)]

| Transfer syntax | Holds |
| --- | --- |
| Explicit VR Little Endian (default), Implicit VR Little Endian, Deflated Explicit VR Little Endian | Native pixel data at the file's Bits Allocated, values under its High Bit |
| RLE Lossless | 8, 16 and 32 bits allocated, up to 15 segments (a new PackBits encoder, PS3.5 annex G) |
| JPEG Lossless (SV1), JPEG-LS Lossless | 2 to 16 bits stored, signed values as the two's complement of their stored bits |
| JPEG XL Lossless | Up to 16 bits stored, gray, palette or RGB; YBR data throws, since DICOM reads JPEG XL as RGB |
| JPEG-LS Near-Lossless | As JPEG-LS Lossless, each sample within `NearLossless` of its value; not for palette indices |
| JPEG Baseline, JPEG Extended, JPEG XL | Unsigned gray and colour of up to 8 bits (12 for JPEG Extended, written with Bits Stored 12); colour JPEG as YBR_FULL_422 |

Encapsulated frames are one fragment each, with the Basic Offset Table by default; `DicomOffsetTable.Extended` writes the
Extended Offset Table instead (it is used anyway past 4 GiB), and `DicomOffsetTable.None` neither. A lossy transfer syntax
sets Lossy Image Compression to 01, adds its ratio and method (`ISO_10918_1`, `ISO_14495_1`, `ISO_18181_1`) to any earlier
ones, and gives the image a new SOP Instance UID, as PS3.3 C.7.6.1.1.5 asks. The file meta information is written anew:
version, Media Storage SOP Class and Instance UIDs from the dataset, the transfer syntax, Egami's Implementation Class UID
(`DicomUid.ImplementationClassUid`) and version name; other elements of the old one are kept. `DicomUid.Generate` makes UIDs
from random UUIDs (PS3.5 B.2). Explicit VR Big Endian is retired and not written.

## Detection and probing

Registered with `WithDicom()`, DICOM files are found by the DICM prefix at byte 128, and `Raster.Probe` gives the size, the
number of frames and the pixel layout a load gives. Bare datasets are
not detected; `DicomFile.Load` reads them, and `Raster.Probe` with `DicomFormat.Info` names the format.

[!code-csharp[](../../../tests/Egami.DocSamples/Dicom.cs#dicom-probe)]

## Damaged and hostile data

The file is read whole into memory, within `SafetyLimits.MaxTotalBytes`, and each element counts against the same budget, so
a file of millions of empty elements cannot use more memory than the limit allows. Every length is checked against the data
around it; sequences nest at most 64 levels deep (deeper throws `CorruptDataException`); a deflated dataset inflates within the
limit.

A lenient read (the default) keeps every element read before damage and says what happened in `DicomFile.Warnings`:

| Id | Means |
| --- | --- |
| `Dicom.Truncated` | A value, item or sequence says it is longer than the data; it was cut there |
| `Dicom.MissingDelimiter` | An item or sequence of undefined length ended without its delimiter |
| `Dicom.UndefinedLength`, `Dicom.BadItem`, `Dicom.BadFragment`, `Dicom.StrayDelimiter`, `Dicom.TrailingBytes` | Structure that breaks PS3.5; the rest was skipped |
| `Dicom.Deflate` | The deflated dataset, or a deflated frame, is damaged; what inflated was read |
| `Dicom.PixelDataShort` | Native pixel data ends before the frame does; the rest is 0 |
| `Dicom.RleHeader`, `Dicom.RleSegments`, `Dicom.RleShort` | An RLE frame's header or segments are damaged; what decoded was kept |
| `Dicom.FrameSize` | A frame's codestream is not Rows by Columns; it was cropped or padded |
| `Dicom.OffsetTable` | An offset table does not point at fragment starts; frames were found from the codestreams |
| `Dicom.Codestream`, `Dicom.JpegLsDamaged` | A codec recovered from damage in a frame |
| `Dicom.WindowInvalid`, `Dicom.PaletteMissing` | The window width or the palette tables cannot be used; the frame's range or the indices are shown |

A strict read (`ReaderOptions.Recovery = RecoveryMode.Strict`) throws `CorruptDataException` for those. Irregularities that
lose nothing are noted in both modes: `Dicom.NoPreamble`, `Dicom.ImplicitMeta`, `Dicom.NoMeta` (strict throws), `Dicom.NoTransferSyntax`,
`Dicom.TransferSyntaxMismatch` (the dataset is read in the VR encoding it is really in), `Dicom.ImplicitElement`,
`Dicom.UnknownVR`, `Dicom.OddLength`, `Dicom.TagOrder`, `Dicom.ZlibHeader` and `Dicom.CharacterSet`.

## Not supported yet

Writing JPEG 2000, High-Throughput JPEG 2000, JPEG XL JPEG recompression and Deflated Image Frame Compression, multi-frame
Secondary Capture from animations, and editing a dataset's elements before a save are not written yet. MPEG-2, H.264, HEVC and JPIP transfer syntaxes, Float and Double Float Pixel
Data, 2-sample, ARGB and CMYK images and retired 12-bit packing throw `NotSupportedException`. Networking (DIMSE, DICOMweb), DICOMDIR media, structured reports, presentation states, segmentation and RT
objects, overlay burn-in and anonymization are not planned.
