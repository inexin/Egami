# Test fonts

Fonts for Egami.Fonts' tests, each under its own license (in LICENSES), none of them MIT. They are test data only and ship in no
package. fontTools' reading of each is in tests/reference/fonts, written by `python tools/make-font-references.py`.

| File | Source | License |
| --- | --- | --- |
| NotoSans-Regular.ttf, NotoSans-Bold.ttf, NotoSans-Italic.ttf, NotoSerif-Regular.ttf | notofonts.github.io, hinted TrueType | SIL OFL 1.1 (LICENSES/Noto-OFL.txt) |
| SourceSans3-Regular.otf | adobe-fonts/source-sans, release branch | SIL OFL 1.1 (LICENSES/SourceSans3-OFL.md) |
| SourceSerif4-Regular.otf | adobe-fonts/source-serif, release branch | SIL OFL 1.1 (LICENSES/SourceSerif4-OFL.md) |
| Roboto-Regular.ttf | googlefonts/roboto-2, src/hinted | Apache 2.0 (LICENSES/Roboto-Apache-2.0.txt) |
| DejaVuSans.ttf, DejaVuSansMono.ttf | dejavu-fonts 2.37 | Bitstream Vera and Arev licenses (LICENSES/DejaVu-LICENSE.txt) |
| derived/NotoSansSerif.ttc | Noto Sans and Noto Serif as one collection | SIL OFL 1.1 |
| derived/SourceSans3.cff | Source Sans 3's CFF table, bare | SIL OFL 1.1 |
| derived/NotoSansCJK-subset.otf, .cff | Noto Sans CJK SC cut to ASCII and 200 hanzi, CID-keyed | SIL OFL 1.1 (LICENSES/NotoCJK-OFL.txt) |
| derived/NotoSans-symbol.ttf, -mac.ttf, -format12.ttf, -kern.ttf | Noto Sans cut down, with a symbol, Mac Roman or format 12 cmap, or a kern table | SIL OFL 1.1 |

The derived fonts are modified versions under the OFL; their names keep the Reserved Font Names only as file names in this test
folder and they are never distributed as fonts. The full Noto Sans CJK SC (16 MB) is fetched to .cache/fonts for the subset and the
speed gates, and not committed.
