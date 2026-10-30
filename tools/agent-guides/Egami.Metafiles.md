# Egami.Metafiles: guide for coding agents

Egami.Metafiles plays Windows metafiles: WMF, EMF and EMF+ (and the gzip forms WMZ and EMZ), onto Egami.Drawing's canvas. They
stay vectors in PDF and SVG output and rasterize on any OS, at a DPI or a size, or stretched to a box as Word does.

Namespaces: `Egami.Metafiles`, with `Egami`, `Egami.Pixels`, `Egami.Drawing` and `Egami.Fonts`.

{{shared}}

## Entry points

- `Metafile.Load(path)` (also streams and bytes): `Kind` and `Size` (in points) without drawing.
- `metafile.Rasterize<TPixel>(MetafileRenderOptions)`: a raster at a `Dpi`, or a `Width` or `Height`, over a `Background`.
- `metafile.Draw(canvas, rect)`: stretched to a box on any canvas, raster or PDF page.
- `MetafileRenderOptions.Fonts`: the fonts its text is set in.

## Samples

Metafile to PNG:

{{sample Metafiles.cs#metafile-to-png}}

Draw on a canvas:

{{sample Metafiles.cs#metafile-draw}}

Fonts that are not installed:

{{sample Metafiles.cs#metafile-substitutes}}

## Common mistakes

- Rendering on a server and getting the wrong fonts. Text uses the installed fonts by default, and metafiles name Arial, Calibri
  and Times New Roman, which Linux machines lack: add Egami.Fonts.Substitutes as in the sample.
- Leaving the background transparent and saving JPEG. Set `Background` when the output has no alpha.
- Rasterizing a metafile to put it into a PDF or SVG. `Draw` it on that canvas instead, so it stays vector.

## More

- Windows metafiles guide: https://inexin.github.io/Egami/v1/articles/guides/metafiles.html
- Every page in one list: https://inexin.github.io/Egami/v1/llms.txt
- API reference: https://inexin.github.io/Egami/v1/api/Egami.Metafiles.html
