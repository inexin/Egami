# Egami.Fuzz

Fuzzing harnesses for Egami's readers, run with [SharpFuzz](https://github.com/Metalnem/sharpfuzz) and AFL++.
A reader must either load an input or throw an `EgamiException`; any other exception is a crash, and AFL++ also
reports hangs. CI runs each target for 30 minutes every night (`.github/workflows/fuzz.yml`).

Targets:

| Target | Input | Seeds |
|---|---|---|
| `png` | PNG files: loaded leniently and strictly, converted, probed | `tests/images/pngsuite/*.png` |
| `jpeg` | JPEG files: probed and loaded leniently, strictly, with fast upsampling and at reduced scale, then auto-oriented and saved as JPEG | `tests/images/jpeg/*.jpg` |
| `exif` | Raw EXIF (TIFF) blocks: parsed, every value read, edited and rebuilt; parsing must never throw at all | `tests/images/fuzz/exif-seeds/*.tiff` |
| `icc` | ICC profiles: parsed (must never throw), then made into a ColorProfile (may fail only with an EgamiException) | `tests/images/icc/*/*.icc` |
| `jpeg-write` | Bytes that pick a pixel type, size, pixels and JPEG writer options (CMYK, YCCK, 12-bit, progressive, restart intervals, background): the file must read back at its size without warnings, a progressive file must decode to the baseline file's pixels, and a streamed encode must write what Save writes | `tests/images/fuzz/jpeg-write-seeds/*` |
| `jpeg-transform` | JPEG files, transformed losslessly with the operation, edges, crop, gray conversion, coding and metadata copying picked from the input's hash: the result must load at the size reported, a horizontal flip done twice with kept edges must restore a plain copy's pixels, and four cores must write what one writes | `tests/images/jpeg/transform/*.jpg tests/images/jpeg/*.jpg` |
| `webp` | WebP files, stills and animations: probed and loaded leniently, strictly, with the background color, fast upsampling and frame ranges, from a stream that cannot seek and as planes, then written back lossless (which must round-trip exactly) and lossy | `tests/images/webp/*.webp` |
| `webpcodec` | The first byte picks: a raw VP8 frame, a raw VP8L stream, or the rest as pixels for both encoders, whose lossless round trip must be exact | `tests/images/fuzz/webpcodec-seeds/*` |
| `heic` | HEIC files with Egami.Heic: probed and loaded leniently and strictly, from a stream that cannot seek, a region and the HDR rendition of a gain map, as YUV planes, and every image item on its own | `tests/images/fuzz/heic-seeds/*` (small corpus files) |
| `pcx`, `sgi`, `sun`, `xbm`, `xpm`, `wbmp`, `farbfeld`, `dds`, `raw` | Egami.Formats.Extended's formats: probed and loaded leniently and strictly, as Rgba8 from a stream that cannot seek and row by row through `RasterDecoder`, which must agree with the load; what loads is saved back and must load again at its size; DDS also loads every surface and mip with LoadAll; RAW loads every output and develops through every demosaic, highlight, white balance and noise path | `tests/images/fuzz/<target>-seeds/*` |
| `heicbox` | The bytes after a heic ftyp box, so mutations reach the HEIF box reader, then as `heic` | `tests/images/fuzz/heicbox-seeds/*` |
| `hevc` | An Annex B HEVC stream: its first picture decoded on one thread and on four, which must give the same samples | `tests/images/fuzz/hevc-seeds/*` (the corpus items' parameter sets and slices) |
| `avif-write` | Bytes that pick a size, bit depth, chroma format, quantizer, effort and screen content tools, then planes for the AV1 encoder: Egami's decoder must give exactly the encoder's reconstruction, and the source when lossless; one input in four also writes a lossless AVIF with alpha and a grid, which must load back exactly | `tests/images/fuzz/avif-write-seeds/*` |
| `cube` | .cube LUT files (Adobe 1D and 3D, Resolve's 1D shaper with a 3D table): read with low limits, written back and read again to the same values, and run as a filter with every interpolation | `tests/images/fuzz/cube-seeds/*` |
| `svgpath` | SVG path data: read strictly, with `TryParseSvg` and leniently, which must agree; written back absolute, relative and compact, which must read again to the same path; small paths then measured, dashed, outlined and simplified | `tests/images/fuzz/svgpath-seeds/*` |
| `stream` | Any format through `RasterDecoder`, whole and incremental, with a region, a reduction, a band height and the sizes the bytes arrive in all picked from the input's hash, and previews drawn on the way; a strict incremental decode must give the whole decode's rows | `tests/images/fuzz/stream-seeds/*` |
| `font` | TrueType, OpenType and collection data (Egami.Fonts): probed, every face loaded leniently and strictly, names, metrics, the first glyphs' outlines, bounds and kerning read, text shaped and measured | `tests/images/fuzz/font-seeds/*` |
| `cff` | Bare CFF streams as PDF embeds them: loaded, then read as `font` does | `tests/images/fuzz/cff-seeds/*` |
| `fontdata` | What `font` or `cff` loads has every glyph name, CID, cmap subtable and CFF encoding read (Phase 2.7.3); each name and CID must find a glyph no later than its own, and each listed mapping must look up the same glyph | `tests/images/fuzz/fontdata-seeds/*` |
| `subset` | What `font` or `cff` loads is subsetted (numbering, cmap and prefix picked by the last byte), re-loaded, and every kept glyph's outline must be unchanged | `tests/images/fuzz/font-seeds/*` |
| `woff2` | WOFF and WOFF2 files (the glyf, loca and hmtx transforms, Brotli and zlib streams): read as `font` does | `tests/images/fuzz/woff2-seeds/*` |
| `gvar` | Variable TrueType fonts: every face at its default, its first named instances and three positions the bytes pick, read as `font` does there (gvar, avar 1 and 2, HVAR, MVAR, GDEF deltas) | `tests/images/fuzz/gvar-seeds/*` |
| `cff2` | Variable CFF2 fonts, as `gvar` (blend and vsindex in the charstrings) | `tests/images/fuzz/cff2-seeds/*` |
| `colr` | Color fonts: palettes, every glyph's COLR, sbix, CBDT and SVG data read, and the first color glyphs drawn upright and turned, at two positions | `tests/images/fuzz/colr-seeds/*` |
| `instance` | Variable fonts subsetted at positions the bytes pick: the static instance must re-load as a static font and read as `font` does | `tests/images/fuzz/instance-seeds/*` |
| `shaping` | Fonts that load shape texts in ten scripts (Latin ligatures and marks, Arabic, Devanagari, Khmer, Myanmar, Thai, Tibetan, Hangul, emoji, mixed bidi) run by run and through `TextShaper.Shape`, with features, directions and cluster levels the bytes pick, and the GSUB closure of each text's glyphs is taken; nothing may throw and every cluster must stay in its run | `tests/images/fuzz/shaping-seeds/*` |
| `css` | CSS text (Egami.Css): parsed as a style sheet and a declaration list, every selector matched against a small fixed tree, cascaded and computed with var(), and every declaration value run through each value parser; nothing may throw | `tests/images/fuzz/css-seeds/*` |
| `svg` | SVG documents (Egami.Svg): loaded leniently under small limits with one embedded font and no system fonts, drawn at up to 128 pixels and into a box, saved and loaded again, and loaded strictly; only a lenient load may fail, with an `EgamiException` | `tests/images/fuzz/svg-seeds/*` (small files from resvg's suite, Apache 2.0 or MIT) |
| `wmf`, `emf` | WMF and EMF files (Egami.Metafiles): loaded under small limits, probed through MetafileFormat, drawn at up to 128 pixels with both playbacks, aliased and antialiased, and onto a box with text as paths; only the load may fail, with an `EgamiException` | `tests/images/metafiles/synthetic/*.wmf`, `gdi-*.emf` |
| `emfplus` | A stream of EMF+ records, wrapped in EMF comments of up to 8 KB behind a fixed header, then as `emf` | `tests/images/fuzz/emfplus-seeds/*` (the records of the synthetic EMF+ files) |
| `bidi` | The bytes as UTF-16 text: resolved in every direction, reordered over line cuts the bytes pick (runs must cover each line exactly), split into graphemes and words (which must tile the text) and shaped with an embedded Arabic font | `tests/images/fuzz/bidi-seeds/*` |
| `paragraph` | Eight option bytes (alignment, justification, line breaking, fill, writing mode, line limits, width, spacing, spans), then UTF-16 text with U+FFFC as placeholders: laid out with the embedded Arabic and CJK subsets, flowed at changing widths, hit tested, caret-walked, selected, decorated, outlined and drawn on a small canvas; nothing may throw, lines must tile the text (or end at the hidden range) and every coordinate must be finite | `tests/images/fuzz/paragraph-seeds/*` |
| `ani` | ANI animated cursors (icon, cursor and raw-bitmap frames): probed and loaded leniently and strictly, every size, a smaller size, a frame range and through `RasterDecoder`; Probe's size and image count must match what loads, and what loads saves as ANI and loads back strictly with the same steps | `tests/images/fuzz/ani-seeds/*` |
| `radiance` | Radiance pictures: probed and loaded leniently and strictly, from a stream that cannot seek; what loads must write back and write again to the same bytes | `tests/images/radiance/*.hdr` |
| `exr` | OpenEXR files: probed and loaded leniently and strictly, every part, the first layers in the display window and the first channels alone, then written back as ZIP and PIZ | `tests/images/exr/*.exr` |
| `gainmap` | JPEGs with an MPF index and a gain map: every MP image, the HDR rendition at full capacity and at a headroom of 2, the map applied, a crop and turn saved as JPEG, which must keep the map | `tests/images/gainmap/*.jpg tests/images/mpf/stereo*.jpg tests/images/mpf/preview.jpg` |
| `ccitt` | Six bytes pick PDF's CCITTFaxDecode parameters (K, EndOfLine, EncodedByteAlign, EndOfBlock, BlackIs1, Columns, Rows, DamagedRowsBeforeError), the rest is the data: decoded by `CcittFax.Decode`, loaded leniently and strictly and read row by row; the three must agree on every pixel and the row count | `tests/images/fuzz/ccitt-seeds/*` |
| `zstd` | Zstandard frames (TIFF ZSTD): decoded into a large output and into one of exactly the size written, which must give the same bytes; only an `EgamiException` may escape | `tests/images/zstd/*.zst` |
| `zstd-write` | A level byte, then data for the Zstandard encoder: the frame must decode back to the data exactly | `tests/images/fuzz/zstd-write-seeds/*` |
| `xz` | xz streams (TIFF LZMA): decoded into a large output and into one of exactly the size written, which must give the same bytes; only an `EgamiException` may escape | `tests/images/xz/*.xz` |

## Running locally (Linux or WSL)

```sh
sudo apt-get install afl++
dotnet tool install --global SharpFuzz.CommandLine
dotnet publish tests/Egami.Fuzz -c Release -o out/fuzz
sharpfuzz out/fuzz/Egami.dll
mkdir -p findings
mkdir -p seeds && cp tests/images/pngsuite/*.png seeds/
AFL_SKIP_BIN_CHECK=1 afl-fuzz -i seeds -o findings -t 5000 -m none -- out/fuzz/Egami.Fuzz png
```

## AFL++ in Docker (any OS)

The Dockerfile here has the .NET SDK, AFL++ and SharpFuzz. `tools/fuzz-afl.py` starts one container per target instance;
each copies the sources, publishes and instruments the harness and fuzzes for the given hours, with findings in `.cache/afl`.

```sh
docker build -t egami-fuzz -f tests/Egami.Fuzz/Dockerfile tests/Egami.Fuzz
python tools/fuzz-afl.py 3 jpeg:6 exif       # 3 hours: six JPEG instances sharing a queue, one EXIF instance
```

## Without AFL++ (any OS)

A simple mutation fuzzer runs in process: random edits of the seeds for the given seconds, with crashes and inputs that take
over 10 seconds saved to the findings directory. It is not coverage-guided, so it complements AFL++ rather than replacing it.

```sh
dotnet run --project tests/Egami.Fuzz -c Release -- mutate jpeg 3600 seeds-dir findings-dir
```

## Reproducing a crash

```sh
dotnet run --project tests/Egami.Fuzz -c Release -- png path/to/crash
```

Every crash found becomes a regression test: add the file under `tests/images/fuzz/` and a test that loads it.
