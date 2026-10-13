# SVG

Egami.Svg reads SVG 1.1 and SVG 2 files into an editable element tree, saves them back, and draws them through Egami.Drawing's
canvas, so the same document renders to a raster, to a PDF page or back out as SVG. It is fully managed and opens nothing
outside the file unless you allow it.

Egami.Svg is a preview package (0.1.0-preview.1): reading, saving, styles, drawing, filters, text, color fonts, SVG output and
`Raster.Load` support are in, and its API may still change before 1.0.

## Loading

`SvgDocument.Load` reads an .svg or .svgz file or stream (gzip is detected by its first bytes), and `SvgDocument.Parse` reads
markup from a string. Loading is lenient, as browsers and resvg read SVG: what cannot be used is skipped, listed in
`SvgDocument.Warnings` and sent to the context's diagnostics as `Svg.*` events.

[!code-csharp[](../../../tests/Egami.DocSamples/Svg.cs#load)]

`SvgLoadOptions.Strict` throws an `SvgException` with the line and column instead, for checking files you produce:

[!code-csharp[](../../../tests/Egami.DocSamples/Svg.cs#strict)]

## The element tree

The document is a tree of `SvgElement`s, like XDocument's: each has a `Name` and `Namespace`, an `SvgAttributeCollection` of
`SvgAttribute`s and an `SvgContentList` of `SvgContent` items in file order (child elements, `SvgTextContent` for text and
CDATA, `SvgComment` and `SvgProcessingInstruction`). Elements and attributes Egami does not draw, such as Inkscape's and
Illustrator's, are kept. `SvgNamespaces` names the SVG, XLink, XML and xmlns namespaces. `FindById` finds an element by its
`id`.

[!code-csharp[](../../../tests/Egami.DocSamples/Svg.cs#edit)]

`Save` writes the tree back as SVG, with everything read from the file (unknown elements, foreign namespaces, comments, the
DOCTYPE), so a load and save without edits keeps the document's meaning; entities come out expanded, and attribute order and
whitespace inside tags may change. `SvgSaveOptions` sets indentation (only where whitespace means nothing, never inside text),
the decimals of numbers and paths set in code, and gzip for .svgz.

[!code-csharp[](../../../tests/Egami.DocSamples/Svg.cs#save)]

Every edit marks the document for re-resolving on its next draw. The tree is not safe for writes from several threads at once,
like XDocument.

## Drawing

`Rasterize` draws the document into a new raster at its own size (`SvgDocument.Size`: the root's width and height, a
percentage or missing side taken of the viewBox, or 100 by 100 when there is none, as resvg sizes documents), scaled, or at a
target width or height; `SvgRenderOptions` also sets the background and the canvas options.

[!code-csharp[](../../../tests/Egami.DocSamples/Svg.cs#rasterize)]

`Draw` draws on any Egami.Drawing canvas: a raster canvas, a PDF page, a `PictureRecorder`. It calls only the public canvas
API, so every backend receives the same calls; a box fits the document in by the root's preserveAspectRatio, and an id draws
one element, such as a `symbol` of a sprite sheet. Drawing never throws on content: what cannot be drawn was reported when
the document was read.

[!code-csharp[](../../../tests/Egami.DocSamples/Svg.cs#draw)]

Rendering follows resvg, the closest static SVG renderer, where SVG leaves a choice open, and is checked against resvg's
test suite. Styles come from Egami.Css: style sheets in `style` elements (with media queries), the `style` attribute and
presentation attributes, with selectors up to Selectors level 4.

Paint servers are linear and radial gradients, patterns and the mesh gradients Inkscape writes (`meshgradient`, SVG 2's
draft): each patch is a Coons patch with a color at each corner, blended bilinearly or, with `type="bicubic"`, smoothly
across patches. Outside its patches a mesh paints nothing. A mesh that names another with `href` takes its rows and keeps
its own x and y, as Inkscape reads it.

## Text

Text is laid out when the document is read: characters are placed by the x, y, dx, dy and rotate lists, shaped in their
fonts (bidi, ligatures, kerning, fallback for characters a font lacks), spaced by letter-spacing, word-spacing and
textLength, anchored, put on a textPath or set vertically, and shifted to their baselines, as resvg lays text out. Each run
of glyphs becomes an `SvgTextRun` drawn with `DrawGlyphRun`, so a PDF page or an SVG canvas keeps real text;
`SvgRenderOptions.TextAsPaths` draws the outlines instead, for a plotter or a cutter.

Wrapped text from SVG 2 is laid out by Egami.Fonts' paragraph layout: `inline-size` fills lines of that length from the
text's x and y, aligned by text-anchor, and `shape-inside` flows the text into shapes (less `shape-subtract` and its
`shape-margin`, inside `shape-padding`), filling one shape after another as Inkscape's flowed text does. `white-space` keeps
spaces and line breaks for pre, pre-wrap and pre-line.

Fonts come from `SvgFontOptions`: the caller's collection first, then the document's `@font-face` rules (data URIs, or files
the load options' resolver allows), then the fonts installed on the machine unless
`UseSystemFonts` is off, then the serif family. A server that renders files it did not write should pass its own fonts and
turn system fonts off, so the output does not depend on the machine.

[!code-csharp[](../../../tests/Egami.DocSamples/Svg.cs#fonts)]

Color fonts with an OpenType SVG table, such as many emoji fonts, draw in color through `SvgGlyphRenderer`, an
`IColorGlyphRenderer` that Egami.Drawing asks when `CanvasOptions.ColorGlyphs` holds it: each glyph's document is read once
per font. A glyph that also has a COLR version 1 form draws that instead; COLR version 0, sbix and CBDT come after SVG.
`Rasterize` uses the renderer already; set it yourself on any other raster canvas.

[!code-csharp[](../../../tests/Egami.DocSamples/Svg.cs#svg-glyphs)]

## Loading SVG as a raster

`SvgFormat.Info` makes SVG an Egami format: register it with `EgamiContext.WithFormat`, and `Raster.Load` reads .svg and
.svgz files drawn at their own size, `Raster.Probe` gives that size without drawing, and `RasterDecoder` and the rest of
Egami's loading take them like any image. It is found by content, not by extension: an `svg` root element after any XML
declaration, comments and DOCTYPE, or gzip data that starts so. `SvgDecodeOptions.Scale` draws at a multiple of the size,
for icons on high-density screens, and `Recovery = Strict` throws on what a lenient load skips; a lenient load keeps the
`Svg.*` warnings in the raster's `Metadata.Warnings`. The context's safety limits apply, and nothing outside the file is
opened, so images it links are not drawn; text uses the machine's fonts. The format never writes: save SVG with
`SvgDocument.Save` or `SvgCanvas`. For a resolver, your own fonts, a target size or a background, load an `SvgDocument` and
call `Rasterize`.

[!code-csharp[](../../../tests/Egami.DocSamples/Svg.cs#svg-format)]

## The render tree

Drawing works from the render tree, `SvgDocument.RenderTree`: the elements with their styles applied, units in pixels, `use`
copies expanded, paint servers, clip paths, masks and markers linked, and shapes turned into Egami.Drawing paths. It is built
when the document is read and again after an edit, and it is immutable, so a document can be drawn on many threads at once.
Its nodes are `SvgNode`s: `SvgGroup` for containers, `SvgPath` for shapes (fill, stroke and markers, in `SvgPaintOrder`),
`SvgImage` for images, each with its transform, opacity, blend mode, `SvgClip`, `SvgMask` and `SvgFilter`, and a link back to
its `SvgElement`.

[!code-csharp[](../../../tests/Egami.DocSamples/Svg.cs#render-tree)]

## Untrusted files

An SVG file is untrusted input. A load honors the context's `SafetyLimits`: the file, after gzip, is bounded by
`MaxTotalBytes` (a gzip bomb stops there), entity expansion by `MaxMetadataBytes` (so a billion-laughs DTD stops), the number
of elements grows with `MaxTotalBytes`, and elements nest at most 256 deep. Internal DTD entities are expanded, because
Illustrator writes them; external entities and DTDs are never opened. Images, style sheets and fonts the file refers to open
only through `SvgLoadOptions.Resolver`; `SvgResolvers.SameDirectory` allows files in one folder and nothing else, and `data:`
URIs always work. Egami never opens a network connection itself.

[!code-csharp[](../../../tests/Egami.DocSamples/Svg.cs#resolver)]

## Writing SVG

`SvgCanvas` is a `Canvas` that writes SVG: anything drawn on it, by your code or by a `Picture`, becomes a vector file that
browsers, Inkscape and Egami.Svg read. Disposing it writes the file to the stream, which stays open. Each call becomes a
`path`, `use`, `image` or `text` element with the canvas transform; equal gradients, clips, masks and patterns are written
once in `defs` and shared, an image or a picture drawn many times is stored once, and clips add no nesting (each clip path's
shape is clipped by the one before, and a chain longer than 16 is written as one intersected shape, since readers limit how
deep references nest), so a deep save/restore stack stays flat. Gradients mixed in linear light, or between stops of
different alpha, get extra stops mixed as Egami mixes them, so every viewer draws them alike. Layers become groups with `opacity`, CSS `mix-blend-mode` and `isolation`, and soft masks
become `mask` elements.

[!code-csharp[](../../../tests/Egami.DocSamples/Svg.cs#svg-canvas)]

`SvgWriteOptions` sets the number of decimals (`Precision`), `Compact` output, the `SvgImageFormat` drawn images are embedded
in (PNG, or JPEG for opaque images) and the resolution of fallback images. What SVG cannot express is drawn by Egami into a
PNG over the area it covers, at `FallbackScale` pixels per unit, and reported as `Svg.FallbackImage` to the options' context:
sweep gradients, noise, blend and color matrix paints, custom paints, gradients mixed in Oklab or Oklch, clamped images
drawn past their edges, two-point conical gradients whose start circle is not inside the end circle, inner and outer mask
blurs, meshes and patches, and layers with a filter, a backdrop or a mask transfer function. Blend modes CSS lacks (the
Porter-Duff modes other than source-over and plus) are drawn as source-over and reported as `Svg.Approximated`.

Text is written as glyph outlines by default (`SvgTextMode.Outlines`): each glyph's outline is in `defs` once, in font units,
and every use of it scales it, so the file looks the same everywhere. `SvgTextMode.Text` writes `text` elements with the font's
family, size, weight and style and each cluster's position, so the text stays searchable and lines up with Egami's layout;
with `EmbedSubset` each font's glyphs used are embedded as an OpenType subset in an `@font-face` rule (unless the font's license
forbids it, reported as `Svg.FontNotEmbedded`). Synthetic bold and italic are drawn as Egami draws them, as a stroke and a slant.

[!code-csharp[](../../../tests/Egami.DocSamples/Svg.cs#svg-canvas-text)]
