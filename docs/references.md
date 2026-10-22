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

## Approved packages

Core Egami references no packages. A satellite package (Egami.Drawing, Egami.Fonts, Egami.Svg and the others) may reference
a package listed here: managed code under an MIT, BSD or Apache-2.0 licence, approved by name by the maintainer, with its
licence and minimum version, set in `Directory.Packages.props`. Raise a minimum version on purpose, with a full test run.

| Package | Licence | Minimum | Used by | Approved |
| --- | --- | --- | --- | --- |
| System.CommandLine | MIT (.NET Foundation) | 2.0.13 | Egami.Cli, the egami tool (an app, not a library): argument parsing, help, response files | CLI design Q3, 2026-10-16 |
| ModelContextProtocol.Core | Apache-2.0 (Model Context Protocol project; brings Microsoft.Extensions.AI.Abstractions, Microsoft.Extensions.DependencyInjection.Abstractions and Microsoft.Extensions.Logging.Abstractions, all MIT) | 2.3.0 | Egami.Cli's `egami mcp` mode: the MCP protocol over standard input and output | MCP design Q1, 2026-10-22 |
| Microsoft.CodeAnalysis.CSharp | MIT (.NET Foundation) | 4.14.0 | Egami.Cli's build-time source generator only; ships nothing | Follows from Q3 and Q15 (no reflection) |

Egap's format add-ons follow the same rule and keep their own list in Egap's docs/references.md.

## Egami.Video

Egami.Video is written from specifications. Permissively licensed code may be studied for ideas; where bit-exact output needs
it, code is translated closely from a listed BSD library, with its notice in THIRD-PARTY-NOTICES.md in the same commit. GPL and
LGPL code is never read.

### Specifications (read freely)

| Spec | Used for |
| --- | --- |
| EBML, IETF RFC 8794 | EBML reader and writer |
| Matroska, IETF RFC 9559 | Matroska reader and writer |
| Matroska codec mapping (draft-ietf-cellar-codec) | `V_MJPEG`, `V_MS/VFW/FOURCC`, audio codec ids |
| WebM container guidelines | What WebM allows inside Matroska |
| Microsoft AVI RIFF file reference | AVI reader and writer |
| OpenDML AVI File Format Extensions 1.02 | AVI files over 1 GB |
| ITU-T T.81 (JPEG), Annex K.3 | Standard Huffman tables for frames without DHT |
| VP9 Bitstream and Decoding Process Specification v0.7 | VP9 decoder, superframes, header field names |
| WebM codec mapping: VP9 CodecPrivate feature records | V_VP9 CodecPrivate |
| FLAC, IETF RFC 9639 | FLAC decoder, encoder and .flac files |
| Microsoft WAVEFORMATEXTENSIBLE; EBU Tech 3306 (RF64); Sony Wave64 GUIDs | WAV, RF64 and Wave64 |
| ITU-R BS.775 | Downmix levels |
| Ogg, IETF RFC 3533; Ogg Opus, IETF RFC 7845 | Ogg pages, the Opus mapping, OpusHead and OpusTags |
| Opus, IETF RFC 6716 and RFC 8251 | Opus decoding and its test vectors |
| Vorbis I specification (Xiph.Org) | The Ogg Vorbis mapping and Vorbis headers |

### Reference code (ideas only)

| Library | License |
| --- | --- |
| mkvalidator (Matroska foundation-source) | BSD-3 |
| libwebp, libvpx (the VP8 encoder: mode decision, loop filter deltas), dav1d, libaom | BSD |

### Translated code (notice in THIRD-PARTY-NOTICES.md)

| Library | License | Used for |
| --- | --- | --- |
| libvpx 1.14.1 | BSD-3, Google patent grant | The VP9 decoder (`src/Egami.Video/Codecs/Vp9`), so frames equal libvpx's sample for sample |
| libopus 1.5.2 | BSD-3, royalty-free IETF patent licenses | The Opus decoder (`src/Egami.Video/Audio/Codecs/Opus`): RFC 6716 makes this decoder normative, and the RFC 8251 test vectors check it |

### Tools run as black boxes only (never read)

| Tool | License | Use |
| --- | --- | --- |
| FFmpeg, ffprobe | LGPL/GPL | Make test clips, decode and probe for comparison, in Docker |
| libvpx vpxenc and vpxdec | BSD-3 | Make VP8 and VP9 clips and give the expected frames, and decode the VP8 encoder's streams, in Docker (`tools/vp8ref`, `tools/video/vp9ref`) |
| MKVToolNix (mkvinfo) | GPL-2.0 | Check our Matroska files, in Docker |
| libopus opus_demo and opus_compare, opus-tools | BSD-3 | Decode the Opus test vectors and make Ogg Opus clips, in Docker (`tools/video/opusref`) |
| flac, SoX | GPL-2.0 | Make FLAC clips, check our FLAC files with flac -t, time resampling, in Docker (`tools/video/audioref`) |
