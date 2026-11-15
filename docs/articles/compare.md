# Egami and other .NET libraries

This page compares Egami with the five imaging libraries .NET developers most often weigh: ImageSharp, SkiaSharp,
Magick.NET, NetVips and System.Drawing.Common. Egami is MIT-licensed, fully managed and runs anywhere .NET 10 runs, with
formats and features that otherwise take several of these libraries. Another library is the better choice when you need
.NET Framework, GPU drawing, ImageMagick's format list or libvips's low-memory pipeline; [When another library fits
better](#when-another-library-fits-better) says which.

Facts about the other libraries come from their own NuGet pages, licence files and documentation, linked in
[Sources](#sources), as of 14 November 2026. A cell says "not documented" when we could not find the answer there; it does
not mean the library lacks the feature. If something here is wrong or out of date, please open an issue.

## At a glance

| | Egami | ImageSharp | SkiaSharp | Magick.NET | NetVips | System.Drawing.Common |
| --- | --- | --- | --- | --- | --- | --- |
| Version compared | 1.5 | 4.1.3 | 4.156.0 | 15.1.0 (Q16) | 3.2.0 | 10.0 |
| Licence | MIT | Six Labors Split License 1.0 (see below) | MIT | Apache-2.0 | MIT; libvips is LGPL-2.1-or-later | MIT |
| Native code | None | None | Google's Skia | ImageMagick | libvips | Windows GDI+ |
| Operating systems | Every OS .NET 10 runs on | Every OS .NET runs on (fully managed) | Windows, macOS, Linux, Android, iOS, Mac Catalyst, tvOS, Tizen, WebAssembly | Windows, Linux (glibc and musl), macOS on arm64 | Windows, Linux (glibc and musl), macOS | Windows only |
| Oldest .NET | .NET 10 | .NET 8 | .NET Framework 4.6.2, .NET Standard 2.0 | .NET Standard 2.0 | .NET Framework 4.6.2 | .NET Framework 4.6.2 |
| Trimming and Native AOT | Supported | Not documented | Not documented | Not documented | Not documented | Not documented |
| GPU drawing | No | Through ImageSharp.Drawing's WebGPU targets | OpenGL, Metal, Vulkan, Direct3D | Not documented | Not documented | No |

## Licence and deployment

- **Egami** is MIT. It has no native files and no third-party dependencies, so one build runs on every OS and CPU .NET 10
  supports, and publishes trimmed or as Native AOT. Optional packages keep patent-encumbered or niche formats out of the core:
  HEIC is in Egami.Heic, and camera RAW, DDS and other extra formats are in Egami.Formats.Extended.
- **ImageSharp** is fully managed. Its [Six Labors Split License](https://github.com/SixLabors/ImageSharp/blob/main/LICENSE)
  gives Apache-2.0 terms to open source and source-available software, to transitive dependencies, to non-profits, and to
  for-profit users with less than USD 1 million annual gross revenue. Everyone else needs a paid [Six Labors Commercial
  License](https://sixlabors.com/pricing/), and the pricing page says a licence key is needed to build the software.
- **SkiaSharp** is MIT and wraps Google's Skia. It ships a native library per platform; on Linux you pick a native assets
  package, with a NoDependencies variant for minimal containers.
- **Magick.NET** is Apache-2.0 and wraps ImageMagick 7, with native files for each supported platform. PDF, PostScript and
  EPS need Ghostscript installed separately, and the docs say some formats are left out of the Linux and macOS builds where a
  licence or other issue prevents them.
- **NetVips** is MIT, but the libvips library it needs is LGPL-2.1-or-later, and the NetVips.Native packages list
  LGPL-3.0-or-later. Check what the LGPL asks of you before you ship it.
- **System.Drawing.Common** is MIT and wraps Windows GDI+. [Since .NET 7 it runs on Windows
  only](https://learn.microsoft.com/dotnet/core/compatibility/core-libraries/6.0/system-drawing-common-windows-only). Egami's
  [System.Drawing drop-in](guides/system-drawing.md) offers the same API on every OS.

## Formats

The table covers the common formats. Egami reads more than 40 formats in all; [Formats](formats/index.md) lists every one with
its options. RW means read and write, R read only, W write only, and "–" means the format is not in the library's
documented set of built-in codecs.

| Format | Egami | ImageSharp | SkiaSharp | Magick.NET | NetVips | System.Drawing |
| --- | --- | --- | --- | --- | --- | --- |
| JPEG | RW | RW | RW | RW | RW | RW |
| PNG and APNG | RW | RW | RW | RW | RW | RW (PNG) |
| GIF, animated | RW | RW | R | RW | RW | RW |
| WebP, animated | RW | RW | RW | RW ¹ | RW | – |
| TIFF | RW | RW | – | RW | RW | RW |
| BMP | RW | RW | R | RW | – | RW |
| ICO and CUR | RW | RW | R (ICO) | R | – | R (ICO) |
| WBMP | RW (Egami.Formats.Extended) | – | R | RW | – | – |
| TGA | RW | RW | – | RW | – | – |
| QOI | RW | RW | – | RW | – | – |
| Netpbm (PBM, PGM, PPM) | RW | RW | – | RW | – ² | – |
| AVIF | RW | – | – ³ | RW ¹ | RW | – |
| HEIC | RW (Egami.Heic) | – | – ³ | RW ¹ | – ² | – |
| JPEG XL | RW | – | – ³ | RW ¹ | – ² | – |
| JPEG 2000 | R | – | – | RW ¹ | – ² | – |
| OpenEXR | RW | RW | – | RW ¹ | – ² | – |
| Radiance HDR | RW | – | – | RW | – ² | – |
| PSD and PSB | R, layers included | – | – | RW | – | – |
| SVG | RW and draws (Egami.Svg) | – | R (separate Svg.Skia package) | R ¹ | R | – |
| Camera RAW | R, and writes DNG (Egami.Formats.Extended) | – | – ³ | R ¹ | – ² | – |
| DDS | RW (Egami.Formats.Extended) | – | – | RW | – | – |
| EMF and WMF | R (Egami.Metafiles) | – | – | R ¹ | – | R |
| PDF | Read, render and write with [Egap.Pdf](https://github.com/inexin/Egap) | – | Writes PDF documents | RW with Ghostscript | – ² | – |

The SkiaSharp column is the build SkiaSharp 4.156.0 ships: the codecs in Skia's source at the revision it pins, with the
build options SkiaSharp's native build scripts set. The NetVips column is the NetVips.Native 8.18.7 binaries, from the build
script and library list of the project that makes them.

¹ ImageMagick needs a delegate library for this format, and Magick.NET's documentation does not say which delegates its
packages include.

² libvips can handle this format with an extra library or build option, but the NetVips.Native binaries are built without
it. A libvips you build yourself can add it.

³ Skia has decoders for AVIF, JPEG XL and camera RAW, and decodes HEIF through Android's own decoder, but SkiaSharp's native
builds leave them off.

## Features

| Area | Egami | ImageSharp | SkiaSharp | Magick.NET | NetVips | System.Drawing |
| --- | --- | --- | --- | --- | --- | --- |
| Resize, crop, rotate, filters, blend modes | Yes | Yes | Yes | Yes | Yes | Resize, crop and rotate through `Graphics` |
| ICC color conversion | Yes, CMYK too | Yes | ICC color spaces | Yes | Yes | Embedded profiles on load |
| EXIF, IPTC and XMP | Read and write | Read and write | Not documented | EXIF, IPTC and ICC profiles | EXIF auto-rotate | EXIF property items |
| HDR, gain maps and Ultra HDR | Yes | Not documented | Not documented | Not documented | Ultra HDR | No |
| Vector drawing (paths, gradients, clips) | Egami.Drawing | ImageSharp.Drawing | Yes | Drawables API | Basic shapes and flood fill | Yes, through `Graphics` |
| Text with complex-script shaping | Egami.Fonts | SixLabors.Fonts | With SkiaSharp.HarfBuzz | Not documented | Not documented | GDI+ text |
| Animation | GIF, APNG, WebP, AVIF, JPEG XL | GIF, APNG, WebP | Decodes GIF and WebP frames | GIF, APNG, MNG | GIF, WebP | GIF frames |
| Low-memory streaming of large images | Row streaming and `Raster.Transcode` | Not documented | Not documented | Memory limits with disk paging | Yes, its core design | No |
| Barcodes | Egami.Barcodes | – | – | – | – | – |
| Video frames and audio | Egami.Video | – | – | Video through FFmpeg, installed separately | – | – |
| Command-line tool | `egami` | – | – | ImageMagick's `magick` (installed separately) | libvips's `vips` tools | – |

## Speed

Egami aims to match or beat the other libraries on the same work. [Benchmarks](benchmarks.md) has 260 timed comparisons
against ImageSharp 3.1.12, SkiaSharp and Magick.NET, with the dates of each run: Egami is faster in 234, within 5% in 16
and more than 5% slower in 10. [Performance](performance.md) has the tips that keep your own code fast.

## When another library fits better

- **You target .NET Framework or an older .NET.** Egami needs .NET 10. SkiaSharp, Magick.NET, NetVips and
  System.Drawing.Common run on .NET Framework 4.6.2, and ImageSharp 4 on .NET 8.
- **You draw a UI on the GPU.** SkiaSharp renders through OpenGL, Metal, Vulkan and Direct3D and is built into many .NET UI
  frameworks. Egami draws on the CPU.
- **You need ImageMagick's format list or its exact output.** Magick.NET reads and writes over 100 formats, PDF and PostScript
  through Ghostscript, and matches the `magick` command's results. For PDF without Ghostscript, Egami's sister library
  [Egap.Pdf](https://github.com/inexin/Egap) reads, renders, edits and writes PDF in the same managed code.
- **You process very large images on little memory.** libvips, through NetVips, runs whole pipelines a region at a time.
  Egami streams decoding and transcoding, but most of its operations work on a whole raster.
- **You have Windows code built on System.Drawing.** It keeps working there. To run the same code on Linux or macOS, try
  Egami's [System.Drawing drop-in](guides/system-drawing.md) and then [migrate](guides/system-drawing-migration.md) piece by
  piece.

## Sources

Checked on 14 November 2026.

- ImageSharp: [NuGet](https://www.nuget.org/packages/SixLabors.ImageSharp), [licence](https://github.com/SixLabors/ImageSharp/blob/main/LICENSE),
  [pricing](https://sixlabors.com/pricing/), [formats](https://docs.sixlabors.com/articles/imagesharp/imageformats.html),
  [releases](https://github.com/SixLabors/ImageSharp/releases), [IPTC](https://docs.sixlabors.com/api/ImageSharp/SixLabors.ImageSharp.Metadata.Profiles.Iptc.html)
  and [XMP](https://docs.sixlabors.com/api/ImageSharp/SixLabors.ImageSharp.Metadata.Profiles.Xmp.html) profiles,
  [ImageSharp.Drawing](https://docs.sixlabors.com/articles/imagesharp.drawing/gettingstarted.html).
- SkiaSharp: [NuGet](https://www.nuget.org/packages/SkiaSharp), [repository](https://github.com/mono/SkiaSharp),
  [SKPixmap.Encode](https://learn.microsoft.com/dotnet/api/skiasharp.skpixmap.encode),
  [SKColorSpace](https://learn.microsoft.com/dotnet/api/skiasharp.skcolorspace),
  [SKDocument.CreatePdf](https://learn.microsoft.com/dotnet/api/skiasharp.skdocument.createpdf),
  [SkiaSharp.HarfBuzz](https://www.nuget.org/packages/SkiaSharp.HarfBuzz), [Svg.Skia](https://github.com/wieslawsoltes/Svg.Skia),
  SkiaSharp 4.156.0's [native build options](https://github.com/mono/SkiaSharp/blob/v4.156.0/scripts/infra/native/shared/native-shared.cake)
  and the [Skia revision](https://github.com/mono/skia/tree/d1717dc22ef2203b7fc9bf0e01078b37b1a4af6b) it pins:
  [codec build targets](https://github.com/mono/skia/blob/d1717dc22ef2203b7fc9bf0e01078b37b1a4af6b/BUILD.gn),
  [default options](https://github.com/mono/skia/blob/d1717dc22ef2203b7fc9bf0e01078b37b1a4af6b/gn/skia.gni).
- Magick.NET: [NuGet](https://www.nuget.org/packages/Magick.NET-Q16-AnyCPU), [repository](https://github.com/dlemstra/Magick.NET),
  [docs](https://github.com/dlemstra/Magick.NET/blob/main/docs/Readme.md),
  [cross-platform notes](https://github.com/dlemstra/Magick.NET/blob/main/docs/CrossPlatform.md),
  [ImageMagick formats](https://imagemagick.org/formats/).
- NetVips: [NuGet](https://www.nuget.org/packages/NetVips), [NetVips.Native](https://www.nuget.org/packages/NetVips.Native),
  [repository](https://github.com/kleisauke/net-vips), [libvips](https://github.com/libvips/libvips),
  [libvips operations](https://www.libvips.org/API/current/function-list.html),
  [multipage and animated images](https://www.libvips.org/API/current/multipage-and-animated-images.html),
  NetVips.Native's [build script](https://github.com/kleisauke/libvips-packaging/blob/main/build/posix.sh),
  [library versions](https://github.com/kleisauke/libvips-packaging/blob/main/versions.properties) and
  [bundled libraries](https://github.com/kleisauke/libvips-packaging/blob/main/THIRD-PARTY-NOTICES.md).
- System.Drawing.Common: [NuGet](https://www.nuget.org/packages/System.Drawing.Common),
  [Windows-only change](https://learn.microsoft.com/dotnet/core/compatibility/core-libraries/6.0/system-drawing-common-windows-only),
  [GDI+ encoders and decoders](https://learn.microsoft.com/windows/win32/gdiplus/-gdiplus-using-image-encoders-and-decoders-use),
  [Image.FromFile](https://learn.microsoft.com/dotnet/api/system.drawing.image.fromfile).
