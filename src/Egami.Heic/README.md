# Egami.Heic

Egami.Heic reads HEIC, the HEIF files with HEVC-coded images that iPhones and most Android phones save photos in.

- Register it once with `WithHeic()`; nothing registers it on its own. Without it, core Egami still detects HEIC files and
  `Raster.Probe` gives their size, depth and orientation, and `Raster.Load` says that this package is needed.
- Grids, alpha, color profiles, orientation, Exif and XMP come from core Egami's HEIF reader, shared with AVIF.

Work in progress: this version reads HEIC containers; its managed HEVC decoder is not finished yet.

## Patents

Egami.Heic decodes HEVC (H.265), a format covered by patents. Egami's authors grant no patent rights and are not lawyers.
Whether you need a patent licence depends on your country and on how you use and distribute this package; commercial products
may need one from the HEVC patent pools or holders. The core Egami package contains no HEVC code and does not depend on this
package.

Egami is MIT-licensed, fully managed C# for .NET 10: no native binaries, no P/Invoke and no third-party dependencies, trimming
and Native AOT clean. Documentation: [inexin.github.io/Egami](https://inexin.github.io/Egami/). Source and changelog:
[github.com/inexin/Egami](https://github.com/inexin/Egami).
