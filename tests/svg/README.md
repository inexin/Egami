# SVG test files

Inputs for Egami.Svg's tests (Phase 2.10). They are test data only and ship in no package. Reference renders of these
files live in egami-compare (`references/svg`), which reads them from this folder.

| Folder | Source | License |
| --- | --- | --- |
| resvg/tests, resvg/resources | resvg's regression suite, `crates/resvg/tests` at linebender/resvg 77b12f4 (0.48.1): 1,742 SVG files in seven chapters and the images they use | Apache 2.0 or MIT, as resvg (resvg/LICENSE-MIT) |
| resvg/fonts | The fonts that suite renders with, unmodified | Each font's own license file beside it (SIL OFL 1.1, Apache 2.0, MIT) |
| corpus | 200 real files (icons, emoji, Inkscape and Illustrator drawings, charts, maps, flags), built by `tools/fetch-svg-corpus.py` | Per file in corpus/corpus.json: public domain, CC0, MIT, SIL OFL 1.1 or CC BY (author and source recorded there) |

The corpus keeps each file byte for byte as published. Its CC BY files are by the authors named in corpus.json and come
from the listed pages: Twemoji (jdecked/twemoji, CC BY 4.0), Font Awesome Free (FortAwesome/Font-Awesome, CC BY 4.0) and
Wikimedia Commons uploads.
