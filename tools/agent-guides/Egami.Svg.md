# Egami.Svg: guide for coding agents

Egami.Svg reads, edits, saves and draws SVG 1.1 and SVG 2 (and SVGZ): an editable element tree, drawn through Egami.Drawing's
canvas onto rasters, PDF pages or SVG, with styles, text, filters and color fonts. It can also make `Raster.Load` read SVG files.

Namespaces: `Egami.Svg`, with `Egami`, `Egami.Pixels` and `Egami.Drawing`.

{{shared}}

## Entry points

- `SvgDocument.Load(path)` and `SvgDocument.Parse(text)`, with `SvgLoadOptions` (resolver, DPI, strict mode, context).
- `document.Rasterize<TPixel>(new SvgRenderOptions { Width = ... })`: a raster; set a width or a height and the other keeps the
  aspect ratio.
- `document.Root`, `FindById`, `SvgElement.Attributes` and `Children`: the editable tree; `document.Save` writes it back.
- `EgamiContext.Default.WithFormat(SvgFormat.Info)`: SVG as an Egami format, so `Raster.Load` and `Raster.Probe` take .svg files.

## Samples

SVG to PNG:

{{sample Svg.cs#svg-to-png}}

Load and read the warnings:

{{sample Svg.cs#load}}

Edit the tree:

{{sample Svg.cs#edit}}

Let the file open what it refers to:

{{sample Svg.cs#resolver}}

`Raster.Load` for SVG files:

{{sample Svg.cs#svg-format}}

## Common mistakes

- Expecting external images, style sheets and fonts to load. Nothing outside the file is opened unless
  `SvgLoadOptions.Resolver` allows it (`SvgResolvers.SameDirectory(folder)` for example); `data:` URIs always work. Keep it that
  way for files from users.
- Calling `Raster.Load` on an .svg without registering `SvgFormat.Info` in the context: core Egami does not read SVG.
- Expecting bad markup to throw. Loading is lenient and reports what it skipped in `document.Warnings`; set
  `Strict = true` to get an `SvgException` with the line and column.
- Text in fonts the machine lacks falls back to other fonts: give it the fonts the SVG names with `SvgLoadOptions.Fonts = new SvgFontOptions { Collection = fonts }`.

## More

- SVG guide: https://inexin.github.io/Egami/v1/articles/guides/svg.html
- Every page in one list: https://inexin.github.io/Egami/v1/llms.txt
- API reference: https://inexin.github.io/Egami/v1/api/Egami.Svg.html
