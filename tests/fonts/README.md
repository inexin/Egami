# Test fonts

Fonts for Egami.Fonts' tests, each under its own license (in LICENSES). They are test data only and ship in no
package. fontTools' reading of each is in tests/reference/fonts, written by `python tools/make-font-references.py`.

| File | Source | License |
| --- | --- | --- |
| NotoSans-Regular.ttf, NotoSans-Bold.ttf, NotoSans-Italic.ttf, NotoSerif-Regular.ttf | notofonts.github.io, hinted TrueType | SIL OFL 1.1 (LICENSES/Noto-OFL.txt) |
| SourceSans3-Regular.otf | adobe-fonts/source-sans, release branch | SIL OFL 1.1 (LICENSES/SourceSans3-OFL.md) |
| SourceSerif4-Regular.otf | adobe-fonts/source-serif, release branch | SIL OFL 1.1 (LICENSES/SourceSerif4-OFL.md) |
| Roboto-Regular.ttf | googlefonts/roboto-2, src/hinted | Apache 2.0 (LICENSES/Roboto-Apache-2.0.txt) |
| DejaVuSans.ttf, DejaVuSansMono.ttf | dejavu-fonts 2.37 | Bitstream Vera and Arev licenses (LICENSES/DejaVu-LICENSE.txt) |
| shaping/NotoSans{Arabic,Hebrew,Devanagari,Bengali,Tamil,Thai,Lao,Khmer,Myanmar,Sinhala}-Regular.ttf | notofonts releases, unhinted TrueType | SIL OFL 1.1 (LICENSES/Noto-OFL.txt) |
| shaping/Amiri-Regular.ttf | aliftype/amiri 1.003 | SIL OFL 1.1 (LICENSES/Amiri-OFL.txt) |
| shaping/ScheherazadeNew-Regular.ttf | silnrsi/font-scheherazade 4.500 | SIL OFL 1.1 (LICENSES/ScheherazadeNew-OFL.txt) |
| shaping/Mukta-Regular.ttf | google/fonts, ofl/mukta | SIL OFL 1.1 (LICENSES/Mukta-OFL.txt) |
| harfbuzz/in-house, harfbuzz/aots | HarfBuzz 14.5.1's shaping tests and their fonts, without the AAT, Graphite and system-shaper tests | As distributed with HarfBuzz (MIT, LICENSES/HarfBuzz-COPYING.txt); the AOTS fonts and tests are Adobe's, Apache 2.0 (LICENSES/HarfBuzz-AOTS-COPYING.txt) |
| derived/NotoSansSerif.ttc | Noto Sans and Noto Serif as one collection | SIL OFL 1.1 |
| derived/SourceSans3.cff | Source Sans 3's CFF table, bare | SIL OFL 1.1 |
| derived/NotoSansCJK-subset.otf, .cff | Noto Sans CJK SC cut to ASCII and 200 hanzi, CID-keyed | SIL OFL 1.1 (LICENSES/NotoCJK-OFL.txt) |
| derived/NotoSans-symbol.ttf, -mac.ttf, -format12.ttf, -kern.ttf | Noto Sans cut down, with a symbol, Mac Roman or format 12 cmap, or a kern table | SIL OFL 1.1 |
| test_glyphs-glyf_colr_1.ttf, test_glyphs-glyf_colr_1_variable.ttf | googlefonts/color-fonts, fonts/ (the COLR v1 test glyphs, unmodified) | Apache 2.0 (LICENSES/color-fonts-Apache-2.0.txt) |
| derived/RobotoFlex-subset.ttf | Roboto Flex variable (google/fonts ofl/robotoflex) cut to Latin-1, 13 axes | SIL OFL 1.1 (LICENSES/RobotoFlex-OFL.txt) |
| derived/NotoSans-VF-subset.ttf, NotoSerif-VF-subset.ttf | Noto Sans and Noto Serif variable (google/fonts) cut to Latin-1 | SIL OFL 1.1 (LICENSES/Noto-OFL.txt) |
| derived/NotoSans-avar2.ttf | NotoSans-VF-subset with an avar version 2 that narrows wdth as wght grows | SIL OFL 1.1 |
| derived/SourceSerif4-VF-subset.otf, SourceSans3-VF-subset.otf | Source Serif 4 and Source Sans 3 variable CFF2 (adobe-fonts, release branches) cut to Latin-1 | SIL OFL 1.1 (LICENSES/SourceSerif4-OFL.md, SourceSans3-OFL.md) |
| derived/NotoColorEmoji-COLRv1-subset.ttf, -CBDT-subset.ttf | Noto Color Emoji (google/fonts ofl/notocoloremoji, COLR v1; googlefonts/noto-emoji v2.047, CBDT) cut to 25 emoji | SIL OFL 1.1 (LICENSES/NotoColorEmoji-OFL.txt) |
| derived/Twemoji-COLRv0-subset.ttf | Twemoji Mozilla 0.7.0 (COLR v0) cut to 25 emoji | CC-BY 4.0 graphics (LICENSES/Twemoji-CC-BY-4.0.txt, Twemoji-Mozilla-LICENSE.md) |
| derived/Twemoji-sbix.ttf | An sbix font built with fontTools from eight Twemoji 72-pixel PNGs (jdecked/twemoji) | CC-BY 4.0 graphics (LICENSES/Twemoji-CC-BY-4.0.txt) |
| derived/Svg-test.ttf | A three-glyph OpenType SVG font built with fontTools: one gzipped and one plain document | Test data written for Egami |
| derived/*.woff, *.woff2 | 2.7.1 fonts and RobotoFlex-subset as WOFF (fontTools, one with metadata and private blocks) and WOFF2 (fontTools; the collection with Debian's woff2_compress), with each transform | As their sources |

The derived fonts are modified versions under the OFL; their names keep the Reserved Font Names only as file names in this test
folder and they are never distributed as fonts. The full Noto Sans CJK SC (16 MB) is fetched to .cache/fonts for the subset and the
speed gates, and not committed. Phase 2.7.2's downloads (the full variable and emoji fonts and the Twemoji PNGs) go to .cache/fonts
too, with their licenses, and tools/fontref/derive_272.py cuts them down.

The shaping fonts, HarfBuzz's tests and hb-shape's references in tests/reference/shaping come from
`python tools/make-shaping-references.py`, which pins every download by SHA-256 and runs HarfBuzz 14.5.1 in Docker.
