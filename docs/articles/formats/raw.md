# Camera RAW

Camera RAW files hold the sensor data a camera recorded, before any processing. Egami reads DNG (.dng), the open RAW format
Adobe publishes, which phones, Leica, Pentax, Ricoh, Hasselblad and Adobe DNG Converter write, and the makers' own formats:
Canon CR2, Nikon NEF and NRW, Pentax PEF, Sony ARW and SR2, Olympus ORF, Panasonic RW2 and Fujifilm RAF. It develops them into
an image on load. It reads through the Egami.Formats.Extended package: register it with `WithRaw()` or `WithExtendedFormats()`
([Extended formats](extended.md)). RAW is detected ahead of TIFF, since a DNG file (like most maker formats) is a TIFF file;
without it, a DNG opens as TIFF and only its preview pages load.

| | |
| --- | --- |
| Reads | DNG 1.0 to 1.7: colour filter arrays of any pattern and LinearRaw, uncompressed 1 to 32-bit integers and 16, 24 or 32-bit floats, lossless JPEG, Deflate with the integer and floating-point predictors, lossy JPEG and JPEG XL, in strips or tiles; linearization tables, black level patterns and deltas, white levels, active area and default crop; opcode lists 1 to 3; camera profiles (dual-illuminant colour matrices, ForwardMatrix, HueSatMap, LookTable, tone curve); embedded previews. The maker formats of [Maker formats](#maker-formats) |
| Writes | Nothing (camera RAW is read-only) |
| Natural pixel type | `Rgb16` in sRGB for a develop, or in the RGB space `ConvertColorTo` names; for `RawOutput.Mosaic`, `Gray16` (CFA) or `Rgb16` (LinearRaw), `GrayF32` or `RgbF32` for float data; for a preview, the JPEG's `Rgb8` |
| `FormatInfo` | `RawFormat.Info`: name `RAW`, extensions `.dng`, `.cr2`, `.nef`, `.nrw`, `.pef`, `.arw`, `.sr2`, `.orf`, `.rw2` and `.raf`, with each format's MIME type |

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

Not read: Canon CR3 (stage C will give its previews and metadata), Sony SRF, Nikon's small (sNEF) and High Efficiency raws, the
Sony A100, RAFs from before 2012 whose CFA section is not a TIFF, and 14-bit packed RAFs. They throw `NotSupportedException`.

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
   colour space.

The output is sRGB unless `ReaderOptions.ConvertColorTo` names another RGB space (Display P3, Adobe RGB, ProPhoto, Rec. 2020
or a linear one): the develop renders straight into it, so wide-gamut colours are never clipped to sRGB first, and the raster
carries its ICC profile. Other colour models (CMYK, gray) are converted to after the develop. Orientation is not applied:
`RasterInfo.Orientation` and the EXIF Orientation say how to turn the image, and `AutoOrient` does it.

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#raw-develop)]

The develop runs in parallel bands, and its output is the same for any `EgamiContext.MaxParallelism`.

## Lens profiles

A file that carries its own lens corrections (a DNG's warp and vignetting opcodes) is corrected with those. For other files,
`RawReaderOptions.LensProfiles` names folders of lens profiles that Egami reads at run time: Lensfun database files (`.xml`)
and Adobe lens profiles (`.lcp`). A system Lensfun install (such as `/usr/share/lensfun` on Linux) is searched too. The
profile whose lens name matches the file's EXIF LensModel corrects vignetting, distortion and lateral chromatic aberration,
its calibrations interpolated for the shot's focal length and aperture; `RawDetails.LensProfile` says which one did.

Egami ships no profiles. Lensfun's database is under CC BY-SA 3.0 and Adobe's profiles come with Adobe's software, so you
supply them and their terms apply to them. `LensCorrections = false` turns every lens correction off.

## Details and metadata

`RawDetails`, in `RasterMetadata.FormatDetails`, says what the raster is (`Output`, `PreviewIndex`), the camera (`Make`,
`Model`, `UniqueCameraModel`, `DngVersion`), the sensor data (`SensorWidth`, `SensorHeight`, `ActiveArea`, `CropArea`,
`CfaPattern`, `BitsPerSample`, `Compression`, `BlackLevels`, `WhiteLevels`), the colour the develop used (`AsShotNeutral`,
`WhiteBalance`, `Temperature`, `Tint`, `CameraToXyz`, `ProfileName`, `BaselineExposure`), the `Opcodes`, the `Previews` (each a
`RawPreview` of a `RawPreviewKind`), the exposure settings and lens, and the maker note as bytes.

EXIF (with GPS) and XMP come through as for TIFF.

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#raw-details)]

## Limits

- The maker formats above; CR3 will give its previews and metadata.
- Fujifilm GFX 100 files (100 MP) need `MaxTotalBytes` above its 2 GB default for a full develop; their previews, half-size
  develop and mosaic fit.
- Three-colour data only: monochrome DNGs and four-colour (CMYG) sensors are not developed (their mosaic loads).
- Non-square pixels (DefaultScale other than 1) are developed square. The third calibration illuminant of DNG 1.6, profile gain
  table maps, semantic masks and extra camera profiles are not read; opcode TrimBounds and WarpRectilinear2 are skipped with a
  warning.
- Egami writes no RAW or DNG files.
