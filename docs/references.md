# Libraries we may learn from

These permissively licensed projects may be studied for ideas. Study the idea, then write your own C#. If code is ever translated closely, its license notice goes into [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) in the same pull request.

Licenses were checked on 2026-10-07 (the HEVC libraries on 2026-10-15, the barcode libraries on 2026-10-19) against each project's own license file or package manifest.

| Library | Language | License | What to learn from it | Egami step |
|---|---|---|---|---|
| [Skia](https://skia.org) / [SkiaSharp](https://github.com/mono/SkiaSharp) | C++ / C# | BSD-3 / MIT | Codec design, resampling and blending | All |
| [StbImageSharp](https://github.com/StbSharp/StbImageSharp) | C# | Public domain or MIT | A managed port of stb_image | 1.1, 1.3, 1.5 |
| [stb](https://github.com/nothings/stb) | C | MIT or public domain | Compact decoders and encoders; stb_image_resize2 for SIMD resampling | All |
| [BigGustave](https://github.com/EliotJones/BigGustave) | C# | Public domain | Small, readable managed PNG reader and writer | 1.1 |
| [Wuffs](https://github.com/google/wuffs) | Wuffs, generated C | MIT or Apache 2.0 | One of the fastest PNG decoders | 1.1, 1.4 |
| [image-rs png](https://github.com/image-rs/image-png) | Rust | MIT or Apache 2.0 | Safe, fast PNG close to how C# would look | 1.1 |
| [image-rs image](https://github.com/image-rs/image) | Rust | MIT or Apache 2.0 | Overall library design | Core |
| [zune-image](https://github.com/etemesi254/zune-image) | Rust | MIT or Apache 2.0 | Very fast JPEG and PNG with SIMD | 1.1, 1.3 |
| [fast_image_resize](https://github.com/Cykooz/fast_image_resize) | Rust | MIT or Apache 2.0 | SIMD resize kernels | 1.2 |
| [libspng](https://github.com/randy408/libspng) | C | BSD-2 | Modern, simple PNG reader and writer | 1.1 |
| [LodePNG](https://github.com/lvandeve/lodepng) | C/C++ | zlib | Complete PNG encoder | 1.1, 1.4 |
| [QOI reference](https://github.com/phoboslab/qoi) | C | MIT | The whole QOI format | 1.5 |
| [Pfim](https://github.com/nickbabcock/Pfim) | C# | MIT | Managed TGA and DDS readers | 1.5 |
| [Go image packages](https://github.com/golang/go/tree/master/src/image) | Go | BSD-3 | Readable PNG, JPEG and GIF codecs | 1.1, 1.3, 1.4 |
| [Pillow](https://github.com/python-pillow/Pillow) | C and Python | HPND | Resampling and color operations | 1.2 |
| [ZXing](https://github.com/zxing/zxing) | Java | Apache 2.0 | Barcode reading and writing across 1D and 2D symbologies | 2.11 |
| [ZXing.Net](https://github.com/micjahn/ZXing.Net) | C# | Apache 2.0 | A managed port of ZXing | 2.11 |
| [zxing-cpp](https://github.com/zxing-cpp/zxing-cpp) | C++ | Apache 2.0 | Fast, robust barcode detection and decoding | 2.11 |
| [BoofCV](https://github.com/lessthanoptimal/BoofCV) | Java | Apache 2.0 | QR code and fiducial detection in hard images | 2.11 |
| [QRCoder](https://github.com/codebude/QRCoder) | C# | MIT | Managed QR code generation | 2.11 |
| [Nayuki QR Code generator](https://github.com/nayuki/QR-Code-generator) | Several | MIT | Small, readable QR code encoder with optimal segment modes | 2.11 |
| [bwip-js](https://github.com/metafloor/bwip-js) | JavaScript | MIT | Barcode writing for many symbologies | 2.11 |
| [libdmtx](https://github.com/dmtx/libdmtx) | C | BSD-2 | Data Matrix encoding and decoding | 2.11 |
| [quirc](https://github.com/dlbeer/quirc) | C | ISC | Compact QR code recognition | 2.11 |
| [zint](https://github.com/zint/zint) (backend library only) | C | BSD-3 | Barcode encoding for many symbologies | 2.11 |
| [GS1 Barcode Syntax Dictionary](https://github.com/gs1/gs1-syntax-dictionary) | Data | Apache 2.0 | Application Identifier formats and checks, embedded as it is | 2.11 |
| [HM](https://vcgit.hhi.fraunhofer.de/jvet/HM), the HEVC reference software | C++ | BSD-3 (grants no patent rights) | Where H.265 is unclear; HM's decoder is the bit-exact reference | 2.9.4 |
| [libhevc](https://android.googlesource.com/platform/external/libhevc) | C | Apache 2.0 | Fast SIMD structure of an HEVC decoder | 2.9.4 |
| [CrabbyAvif](https://github.com/webmproject/CrabbyAvif) | Rust | Apache 2.0 | HEIF item handling, including HEIC | 2.9.4 |

Specifications, textbooks and papers are always fine. Code under GPL, LGPL or AGPL, and code with no license, is never used.

For barcodes, ZBar (LGPL) is never used, and of zint only the backend library (BSD-3) may be studied: its frontends and GUI are
under the GPL and are never used.

For HEIC, libheif and libde265 (LGPL-3), FFmpeg's HEVC decoder and openHEVC (LGPL-2.1) and x265 (GPL-2) are never read; they
only run as black-box tools in Docker to make test files and references. Nokia's HEIF code (non-commercial licence) is never
read.
