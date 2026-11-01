# Camera RAW

Camera RAW files hold the sensor data a camera recorded, before any processing. Egami reads DNG (.dng), the open RAW format
Adobe publishes, which phones, Leica, Pentax, Ricoh, Hasselblad and Adobe DNG Converter write, and the makers' own formats:
Canon CR2, Nikon NEF and NRW, Pentax PEF, Sony ARW and SR2, Olympus ORF, Panasonic RW2 and Fujifilm RAF. It develops them into
an image on load. Of Canon's CR3 it reads the previews and the metadata ([Canon CR3](#canon-cr3)).
[Developing camera RAW](../guides/camera-raw.md) is about choosing the develop. It reads through the Egami.Formats.Extended
package: register it with `WithRaw()` or `WithExtendedFormats()`
([Extended formats](extended.md)). RAW is detected ahead of TIFF, since a DNG file (like most maker formats) is a TIFF file;
without it, a DNG opens as TIFF and only its preview pages load.

| | |
| --- | --- |
| Reads | DNG 1.0 to 1.7: colour filter arrays of any pattern and LinearRaw, uncompressed 1 to 32-bit integers and 16, 24 or 32-bit floats, lossless JPEG, Deflate with the integer and floating-point predictors, lossy JPEG and JPEG XL, in strips or tiles; linearization tables, black level patterns and deltas, white levels, active area and default crop; opcode lists 1 to 3; camera profiles (dual-illuminant colour matrices, ForwardMatrix, HueSatMap, LookTable, tone curve); embedded previews. The maker formats of [Maker formats](#maker-formats) |
| Writes | DNG: any RAW file Egami reads, with its sensor data unchanged (`RawFormat.ConvertToDng`), and any raster (`Save` with `RawFormat.Info`): a RAW mosaic as a colour filter array DNG, anything else as LinearRaw |
| Natural pixel type | `Rgb16` in sRGB for a develop, or in the RGB space `ConvertColorTo` names; for `RawOutput.Mosaic`, `Gray16` (CFA) or `Rgb16` (LinearRaw), `GrayF32` or `RgbF32` for float data; for a preview, the JPEG's `Rgb8` |
| `FormatInfo` | `RawFormat.Info`: name `RAW`, extensions `.dng`, `.cr2`, `.nef`, `.nrw`, `.pef`, `.arw`, `.sr2`, `.orf`, `.rw2`, `.raf` and `.cr3`, with each format's MIME type |

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#raw-load)]

## What Load returns

`RawReaderOptions.Output` (a `RawOutput`) picks it:

- `Developed` (the default) runs the whole develop below.
- `HalfSize` develops at half the width and height: each 2 × 2 block of the mosaic is one pixel, so there is no demosaicing and
  the develop is about four times faster.
- `Preview` returns an embedded preview as the camera or converter rendered it, the largest unless `PreviewIndex` picks
  another. A file without one gives a half-size develop.
- `Mosaic` returns the sensor data as stored, nothing scaled, cropped or balanced, for a pipeline of your own; the pattern, the
  black and white levels and the rest are in `RawDetails`.

With `ReaderOptions.MinimumSize` (and no `Region`), Load returns the smallest preview that covers the size, else a half-size
develop if that does, else the full develop. LoadAll gives the developed image, then each preview.

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#raw-outputs)]

## Maker formats

A maker file goes through the same develop as a DNG. What a DNG carries in its tags comes from elsewhere:

- The sensor data is decoded by each maker's codec: Canon's sliced lossless JPEG and sRAW and mRAW, Nikon's Huffman coding and
  curve, Pentax's Huffman coding, Sony's ARW2, ARW1 and lossless JPEG tiles, Olympus's and Panasonic's own codings (Panasonic
  RawFormat 4 to 8), Fujifilm's compressed codec (lossless and lossy, X-Trans and Bayer) and the plain packings. Decoded
  mosaics are bit for bit those Adobe DNG Converter stores (on 22 CC0 sample files), but for defective pixels and clipping.
- White balance, black levels and crops come from the file and its maker note where it has them.
- Colour matrices, and levels and crops a file does not give, come from Egami's camera table: for each camera, what Adobe DNG
  Converter writes in a DNG of a sample file, measured as a black box. A camera that is not in the table develops with a
  generic matrix and a `CameraUnknown` warning.
- Canon sRAW and mRAW files are LinearRaw (three samples a pixel), so `RawOutput.Mosaic` gives `Rgb16`. Fujifilm X-Trans
  sensors (6 × 6 pattern) are demosaiced by Egami's own gradient-weighted method whatever `Demosaic` says. A RAF's
  dynamic-range setting moves the baseline exposure as it does in Adobe's DNGs.

Not read: Sony SRF, Nikon's small (sNEF) and High Efficiency raws, the Sony A100, RAFs from before 2012 whose CFA section is not
a TIFF, and 14-bit packed RAFs. They throw `NotSupportedException`.

## Canon CR3

Canon's CR3 files (EOS R and M bodies, the 250D, 850D and 90D, the 1D X Mark III and recent PowerShots) are an ISO base media
file. Egami reads their three previews, all JPEGs: the full-size one, a 1620 × 1080 (or 1440 × 1080) one and a 160 × 120
thumbnail; and their metadata: EXIF, GPS, and Canon's maker note as bytes in `RawDetails.MakerNote`. `Raster.Probe` gives the
sensor data's size, and `RawOutput.Preview` (with `PreviewIndex`, or `ReaderOptions.MinimumSize`) loads a preview.

The sensor data itself is coded with Canon's CRX codec, which Egami does not decode yet: a develop, a half-size develop or the
mosaic of a CR3 throws `NotSupportedException` saying so.

## The develop

The develop follows the DNG specification's processing order:

1. Opcode list 1 on the stored values; then the linearization table, the black levels (per position of the black level pattern,
   plus the per-row and per-column deltas) and the white level map the active area to 0 (black) to 1 (clipping).
2. Opcode list 2 (gain maps for lens shading, polynomial and table maps, bad-pixel fixes).
3. Luminance noise reduction, when `LuminanceNoiseReduction` (0 to 100) is above 0: wavelet shrinkage of each CFA position's
   samples, with the file's NoiseProfile as the noise model, or one measured from the image.
4. White balance (`RawWhiteBalance`): as shot (the camera's AsShotNeutral or AsShotWhiteXY), automatic (the image's average as
   gray), or custom (`Temperature` in kelvin and `Tint`). The colour matrices of the two calibration illuminants are
   interpolated for the white's temperature.
5. Demosaicing (`RawDemosaicMethod`): `Bilinear`, `Malvar` (Malvar, He and Cutler's gradient-corrected interpolation) or `Ahd`
   (Hirakawa and Parks' adaptive homogeneity-directed method, the default). A pattern other than a 2 × 2 Bayer one gets a
   same-colour neighbourhood average.
6. Opcode list 3 (lens distortion through WarpRectilinear and WarpFisheye, radial vignetting); `LensCorrections = false` skips
   the lens opcodes.
7. Highlights (`RawHighlightMode`): `Clip` cuts every colour where the least amplified one clips, so clipped areas turn white;
   `Blend` lifts what clipping removed evenly across the colours; `Rebuild` estimates clipped colours from unclipped ones with
   the colour of the surroundings, which brings detail back when `Exposure` is lowered.
8. Colour noise reduction (`ColorNoiseReduction`, 0 to 100, default 25): wavelet shrinkage of two colour-difference planes,
   leaving luminance alone.
9. The default crop; camera RGB to XYZ D50 through the ForwardMatrix, or the inverted ColorMatrix and Bradford adaptation; the
   camera profile's HueSatMap, then exposure (`Exposure` plus the file's BaselineExposure unless `ApplyBaselineExposure` is
   off), then its LookTable and tone curve, all in linear ProPhoto RGB (`ApplyProfile = false` leaves them out); and the output
   colour space. A profile without a tone curve (every maker file's) leaves the develop scene-linear; `DefaultToneCurve = true`
   gives it a contrast curve measured from Adobe Camera Raw's default rendering instead (`tools/measure-raw-tone-curve.py`),
   which lifts the midtones (18 % gray comes out at about 38 %) and rolls off the highlights.

The output is sRGB unless `ReaderOptions.ConvertColorTo` names another RGB space (Display P3, Adobe RGB, ProPhoto, Rec. 2020
or a linear one): the develop renders straight into it, so wide-gamut colours are never clipped to sRGB first, and the raster
carries its ICC profile. Other colour models (CMYK, gray) are converted to after the develop. Orientation is not applied:
`RasterInfo.Orientation` and the EXIF Orientation say how to turn the image, and `AutoOrient` does it.

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#raw-develop)]

The develop runs in parallel bands, and its output is the same for any `EgamiContext.MaxParallelism`.

## Lens profiles

A file that carries its own lens corrections (a DNG's warp and vignetting opcodes) is corrected with those. For other files,
`RawReaderOptions.LensProfiles` names folders of lens profiles that Egami reads at run time: Lensfun database files (`.xml`)
and Adobe lens profiles (`.lcp`). Two kinds of folders are searched as well when they are there: a system Lensfun install (such
as `/usr/share/lensfun` on Linux), and the folders Adobe's software (Camera Raw, Lightroom, DNG Converter) installs its lens
profiles in, `%ProgramData%\Adobe\CameraRaw\LensProfiles\1.0` and `%AppData%\Adobe\CameraRaw\LensProfiles\1.0` on Windows and
`/Library/Application Support/Adobe/CameraRaw/LensProfiles/1.0` and its counterpart in your own Library on macOS. Egami only
reads them. The profile whose lens name matches the file's EXIF LensModel (an Adobe profile's name as people read it, or as
cameras write it) corrects vignetting, distortion and lateral chromatic aberration, its calibrations interpolated for the
shot's focal length and aperture, at the calibrated focus distance nearest the shot's; `RawDetails.LensProfile` says which one
did. The corrections measure from the output image (the default crop), the image the profiles were made for.

Adobe's folder holds thousands of profiles, so Egami reads only those whose file name has the lens's focal length, the first
time a lens is developed (about two seconds for a common zoom), and keeps them for the process.

Egami ships no profiles. Lensfun's database is under CC BY-SA 3.0 and Adobe's profiles come with Adobe's software, so you
supply them, or install that software, and their terms apply to them. `LensCorrections = false` turns every lens correction off.

For a file no profile covers, `AutoChromaticAberration = true` measures lateral chromatic aberration from the image itself and
scales red and blue about the centre to line their edges up with green's. It is off by default: the measurement costs about as
much as the demosaic.

## Details and metadata

`RawDetails`, in `RasterMetadata.FormatDetails`, says what the raster is (`Output`, `PreviewIndex`), the camera (`Make`,
`Model`, `UniqueCameraModel`, `DngVersion`), the sensor data (`SensorWidth`, `SensorHeight`, `ActiveArea`, `CropArea`,
`CfaPattern`, `BitsPerSample`, `Compression`, `BlackLevels`, `WhiteLevels`), the colour the develop used (`AsShotNeutral`,
`WhiteBalance`, `Temperature`, `Tint`, `CameraToXyz`, `ProfileName`, `BaselineExposure`), the `Opcodes`, the `Previews` (each a
`RawPreview` of a `RawPreviewKind`), the exposure settings and lens, and the maker note as bytes.

EXIF (with GPS) and XMP come through as for TIFF.

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#raw-details)]

## Limits

- The maker formats above; of CR3, only the previews and metadata.
- Fujifilm GFX 100 files (100 MP) need `MaxTotalBytes` above its 2 GB default for a full develop; their previews, half-size
  develop and mosaic fit.
- Three-colour data only: monochrome DNGs and four-colour (CMYG) sensors are not developed (their mosaic loads).
- Non-square pixels (DefaultScale other than 1) are developed square. The third calibration illuminant of DNG 1.6, profile gain
  table maps, semantic masks and extra camera profiles are not read; opcode TrimBounds and WarpRectilinear2 are skipped with a
  warning.
- Lens corrections that cameras record in their own maker notes (Fujifilm, Micro Four Thirds, Nikon Z, Sony) are not read;
  a lens profile, or `AutoChromaticAberration`, corrects those files instead.
- Without a NoiseProfile (maker formats), luminance noise reduction measures the noise from the image, which for textured
  scenes reads higher than Adobe's calibrated profiles do, so the same setting reduces more. Luminance noise reduction works on
  the mosaic, so Canon sRAW and mRAW files get colour noise reduction only.
- Adobe gives some Canon sRAW and mRAW files a brighter baseline exposure than the camera's normal raws (+0.25 EV for the EOS 5D
  Mark II, +0.34 EV for the EOS 70D, by a rule not yet known); Egami uses the camera's raw one.
- DNG writing does not take CR3 files (Egami does not decode their CRX data for it), writes BigTIFF past 4 GB (which DNG
  readers, Egami's included, may not open), and makes previews from Egami's own develop.

## Writing DNG

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#raw-dng)]

`RawFormat.ConvertToDng` copies the sensor data as Egami decodes it: not demosaiced, scaled or cropped. The file is laid out
as Adobe DNG Converter writes it: a 256-pixel thumbnail and the camera tags in IFD0, the raw data in tiles in a SubIFD, and a
JPEG preview in another. A converted file keeps the source's levels, active area, crop, colour filter pattern, linearization
table and opcode lists, a colour profile from the file or the camera table (both illuminants' ColorMatrix, CameraCalibration
and ForwardMatrix, and the baseline exposure), EXIF, XMP and the maker note, which goes into DNGPrivateData in Adobe's form. A
DNG source keeps every tag it had, its values exactly. A camera with colour data in neither the file nor the table gets the
profile Egami develops it with (the camera treated as sRGB) and a `Raw.DngCameraUnknown` warning through
`EgamiContext.Diagnostics`.

`Raster.Transcode` and `egami convert` take the same route when the output is .dng and there are no operations: the RAW
format's writer is an `IFormatConverter` ([Custom formats](custom.md#converting-without-decoding)). With operations, the
developed image is saved instead, as LinearRaw.

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#raw-dng-rasters)]

`Save` with `RawFormat.Info` writes a `Gray16` or `GrayF32` raster whose `FormatDetails` is a RAW mosaic's `RawDetails` as a
colour filter array DNG, with the details' pattern, levels and areas and the camera table's profile (or, for a camera not in
it, one worked out from `CameraToXyz`). Any other raster becomes LinearRaw: linear RGB in its own primaries (sRGB when it has
no RGB profile), 16-bit or float, black 0, with a ColorMatrix and ForwardMatrix for D50 worked out from the primaries. Its
previews show the raster itself.

| `DngWriterOptions` | Default | Does |
| --- | --- | --- |
| `Compression` | `DngCompression.LosslessJpeg` | Lossless JPEG, as Adobe writes, a Bayer row coded as pairs (other patterns as one component); floating-point data goes in `Deflate` instead. `None`, `Deflate` (with TIFF's integer or floating-point predictor), or DNG 1.7's lossless `JpegXl` |
| `TileSize` | `null` | Tiles near 256 pixels, sized along each axis to leave the least padding, as Adobe sizes them; or a square size, a multiple of 16 |
| `Preview` | `DngPreview.Medium` | A 256-pixel thumbnail and a 1024-pixel JPEG; `Thumbnail`, `Full` (a full-size JPEG) or `None` (the raw data is the first image) |
| `ParallelCompression` | `true` | Codes tiles on several threads; the file is the same |

On the 41 corpus files Egami reads as RAW, a converted DNG loads back with its mosaic, levels, areas and pattern bit for bit,
and develops to the source's develop within a level in 16 bits. Adobe DNG Converter, LibRaw (through Magick.NET), Windows'
Raw Image Extension and Adobe's dng_validate open it. Its colour tags are Adobe's for the same file. With lossless JPEG and
the medium preview it is 0.7 % smaller than Adobe's DNG over the corpus, within 2 % file by file but for a lossy Nikon NEF,
which Adobe stores as the codes before the camera's tone curve, with a linearization table (21 % smaller). On one thread it
converts the corpus in 70 % of the time Adobe DNG Converter takes pinned to one core (one large Fujifilm file takes 3 %
longer than Adobe).

Limits: Egami does not convert CR3 files. A DNG past 4 GB is written as BigTIFF, which Egami's own RAW reader does not open.
`Save` has no context, so it reports no `Raw.DngCameraUnknown` warning; `ConvertToDng` and `Transcode` do. Some maker files'
active area is their crop, as Egami reads them, which dng_validate warns leaves little padding for a develop (Canon EOS 350D,
Panasonic).
