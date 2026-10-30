## Egami's vocabulary

Egami has its own names. Use them, not names from other imaging libraries: Egami has no `Image` type, no `Mutate` or `Clone`
and no `Configuration`.

- `Raster` and `Raster<TPixel>` hold an image; `TPixel` is a pixel type such as `Rgba8`, `Rgb8`, `Gray8`, `Bgra8` or `RgbaF32`
  (namespace `Egami.Pixels`). `Raster.Load` returns the type that fits the file; `Raster.Load<Rgba8>` converts while decoding.
- `Raster.Probe` reads a file's size, format and orientation without decoding it.
- `Apply(ops => ...)` changes a raster in place through a chain of operations; `Derive(ops => ...)` returns a new raster.
  `As<TPixel>()` converts to a new raster and `Copy()` duplicates one.
- `Save(path)` picks the writer from the extension; `Save(stream, format, options)` names it. Loading always detects the format
  from the bytes, never from the extension.
- `EgamiContext` holds the settings, `SafetyLimits`, thread count and registered formats a call uses. It is immutable: change
  it with `with`, and pass it to `Load` or `Probe`, or make it the default once at startup with `EgamiContext.SetDefault`.
- Every `Raster` owns pooled pixel memory: dispose it, best with `using`. `Derive`, `As` and `Load` return new rasters that you
  dispose too.
