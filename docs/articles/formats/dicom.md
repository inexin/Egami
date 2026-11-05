# DICOM (preview)

DICOM is the format of medical imaging: a file (PS3.10) holds a dataset of tagged data elements (the patient, the study, how
the pixels are stored), then the pixel data, native or compressed with JPEG, JPEG-LS, JPEG 2000, JPEG XL or RLE. DICOM reads
through the Egami.Formats.Extended package, as a preview: every DICOM type is marked `[Experimental]` with the diagnostic id
`EGAMIX001`, so code that uses it suppresses that id (`<NoWarn>$(NoWarn);EGAMIX001</NoWarn>`), and the API may change before
it is stable.

This version reads the dataset. Decoding the pixel data, the display pipeline (rescale, window, color) and writing come in
later versions; until then `Raster.Load` of a DICOM file throws `NotSupportedException`.

| | |
| --- | --- |
| Reads | PS3.10 files with the 128-byte preamble and DICM, and bare datasets without them; implicit VR little endian, explicit VR little endian, explicit VR big endian and deflated explicit VR little endian datasets; sequences and items of defined and undefined length to 64 levels; every VR of PS3.5 table 6.2-1; encapsulated pixel data as fragments with the Basic and Extended Offset Tables; text in the character sets of PS3.3 C.12.1.1.2, ISO 2022 code extension included |
| Writes | Nothing yet |
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

## Detection and probing

Registered with `WithDicom()`, DICOM files are found by the DICM prefix at byte 128, and `Raster.Probe` gives the size, the
frame count and a pixel layout from Rows, Columns, Number of Frames, Samples per Pixel and Bits Allocated. Bare datasets are
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
| `Dicom.Deflate` | The deflated dataset is damaged; what inflated was read |

A strict read (`ReaderOptions.Recovery = RecoveryMode.Strict`) throws `CorruptDataException` for those. Irregularities that
lose nothing are noted in both modes: `Dicom.NoPreamble`, `Dicom.ImplicitMeta`, `Dicom.NoMeta` (strict throws), `Dicom.NoTransferSyntax`,
`Dicom.TransferSyntaxMismatch` (the dataset is read in the VR encoding it is really in), `Dicom.ImplicitElement`,
`Dicom.UnknownVR`, `Dicom.OddLength`, `Dicom.TagOrder`, `Dicom.ZlibHeader` and `Dicom.CharacterSet`.

## Not supported yet

Pixel data decoding (native, RLE, and the codecs), the display pipeline, multi-frame pages and writing are the next stages of
the DICOM work. Networking (DIMSE, DICOMweb), DICOMDIR media, structured reports, presentation states, segmentation and RT
objects, overlay burn-in and anonymization are not planned.
