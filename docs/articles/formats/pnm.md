# Netpbm (PBM, PGM, PPM, PAM, PFM)

Egami reads and writes the Netpbm formats: P1 to P6, plain and binary, at maxval 1 to 65,535, P7 PAM with alpha, and the PF and Pf
float maps.

A file loads as `Gray8`, `Gray16`, `GrayAlpha8`, `Rgb8`, `Rgba8` or `Rgba16`, and a float map as `RgbF32` or `GrayF32`.

[!code-csharp[](../../../tests/Egami.DocSamples/Formats.cs#pnm)]

Maxvals other than 255 and 65,535 are scaled exactly, as round(v × 255 / maxval). A PAM with an unknown tuple type is read by its
depth, with a warning. A stream with several images reads the first and warns. `#` comments become `Comment` text entries and
are written back. Saving to a .pbm, .pgm, .ppm, .pam or .pfm path writes that variant unless `PnmWriterOptions.Kind` names one; .pnm
chooses from the pixel type.

PFM float maps load as `RgbF32` (PF) or `GrayF32` (Pf), marked as linear light, with their scale and byte order in
`PnmDetails`; values outside 0..1 are kept. Writing uses PFM for float types without alpha, or when `PnmKind.FloatMap` is
asked for: little-endian, scale −1, as Netpbm and ImageMagick write it.
