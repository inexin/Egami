# Egami branding

One icon family for every Egami package: a square tile with its top left and bottom right corners rounded, in the package's
own color, with a white line glyph. The wordmark is lowercase Bricolage Grotesque (SIL Open Font License), drawn as outlines.

- `icons/<Package>.svg`: the icon on a 64 by 64 grid.
- `nuget/<Package>.png`: the 128 pixel NuGet icon. `Directory.Build.targets` packs it as the package icon.
- `favicon/egami.ico` (16, 32 and 48 pixels) and `favicon/egami.svg`: the favicon for web pages.
- `lockups/<Package>-on-light.svg` and `-on-dark.svg`: the icon with the wordmark, for light and dark backgrounds.

| Package | Color |
| --- | --- |
| Egami | `#ff5f45` |
| Egami.Drawing | `#f29d12` |
| Egami.Fonts | `#22a95f` |
| Egami.Svg | `#0fa0b6` |
| Egami.Css | `#3767ee` |
| Egami.Metafiles | `#8752e6` |
| Egami.Heic | `#e5397a` |
| Egami.Formats.Extended | `#8a9a1b` |
| Egami.Video | `#e0458f` |
| Egami.Barcodes | `#3d4656` |
| Egami.Cli | `#4f5bd5` |
| Egami.Video.Cli | `#b03ea8` |
| Egami.Cli.Host | `#5f7183` |

A new package gets a new bright color and a glyph drawn with the same 5-unit round stroke, and its PNG goes in `nuget/`.
