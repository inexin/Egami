# Radiance

Radiance pictures hold three 8-bit mantissas and a shared exponent per pixel (RGBE), or CIE X, Y and Z the same way (XYZE).
`RadianceFormat` reads both, every scan order a resolution line can name (`-Y 512 +X 768` and the other seven), and flat,
old-style repeat-pixel and per-channel run-length scanlines. Pixels load as `RgbF32`, bit for bit what stb_image decodes; XYZE
converts to RGB in Radiance's standard primaries, which `RasterMetadata.Primaries` then names. A `PRIMARIES` line sets the
primaries of an RGBE file the same way; without one, Rec. 709 is assumed.

`RadianceDetails` keeps the header: the `RadiancePixelFormat`, `Exposure`, `ColorCorrection`, `Primaries`, `Software`, `View`, `PixelAspectRatio`, the scan
order as stored and every header line. The reader also measures the brightest value and stores it as
`HdrMetadata.MaxContentLightLevel`, so `ToneMap` fits the image rather than a fixed 1,000 cd/m² peak.

[!code-csharp[](../../../tests/Egami.DocSamples/Hdr.cs#radiance)]

The writer writes RGBE, top row first, with per-channel run-length scanlines (`RadianceWriterOptions.Compress = false` writes them
flat) and an `EXPOSURE` line only when `RadianceWriterOptions.Exposure` is set. Reading a file and writing it again gives the same
floats, and the same bytes once written by Egami.

Pixels are linear light with 1.0 as reference white; the [HDR guide](../guides/hdr.md) explains the scale, and `ToneMap` in
[color management](../guides/color-management.md) turns them into SDR.
