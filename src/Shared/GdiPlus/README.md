# Shared GDI+ semantics

Internal C# source with the GDI+ behaviour that Egami.Metafiles' EMF+ player and Egami.SystemDrawing share, compiled into each
package as linked source rather than shipped as a package of its own (step 2.13, question 7). EMF+ records are serialized GDI+
calls, so the player and the System.Drawing object model need the same pens, brushes, regions, string formats and image
attributes. Neither package depends on the other.

| File | What it holds |
| --- | --- |
| `GdiPlusPen.cs` | The pen model (`GdiPlusPen`, `GdiPlusCustomCap`) and `GdiPlusPens`: dash patterns scaled by the width, dash caps kept inside the dash, compound lines, inset alignment, anchor and adjustable-arrow caps, the one-pixel minimum width |
| `GdiPlusBrushes.cs` | Wrap modes, hatch tiles and their scale, blend factors and preset colors as gradient stops (gamma-corrected brushes mix with Egami.Drawing's `GradientInterpolation.GdiPlusGamma`), path gradients as Gouraud meshes |
| `GdiPlusHatches.cs` | The 8 by 8 tiles of the 53 hatch styles, measured from what GDI+ draws |
| `GdiPlusRegions.cs` | Region combine modes over paths, with null for the infinite region |
| `GdiPlusStringFormat.cs` | The string format model, its flag values, the hotkey prefix and tab stop positions |
| `GdiPlusImageAttributes.cs` | Image attributes per color adjust type: color and gray matrices, gamma, threshold, color key, remap table, output channel, wrap mode and clamp color |
| `GdiPlusData.cs` | The serialized forms in [MS-EMFPLUS] 2.2 both need: paths and point types, points, rectangles and region trees |

Rules for the folder:

- Everything is `internal`, in the namespace `Egami.GdiPlus`, and every type name starts with `GdiPlus` (Egami.Metafiles has
  its own GDI objects named `Gdi...`). A package that compiles the folder adds it to its project file with
  `<Compile Include="..\Shared\GdiPlus\**\*.cs" Link="Shared\GdiPlus\%(RecursiveDir)%(Filename)%(Extension)" />` and a global
  using that makes `Path` mean `Egami.Drawing.Path` (see `src/Egami.Metafiles/GlobalUsings.cs`).
- Behaviour comes from Microsoft's public documentation and [MS-EMFPLUS], and from black-box measurements of GDI+ in
  egami-compare. GDI+ itself is never disassembled.
- The tests are in `tests/Egami.GdiPlus.Tests`, which compiles the folder on its own. Any change here must keep Egami.Metafiles'
  tests and its GDI+ gates in egami-compare (`MetafileGdiPlusGateTests`) at the same numbers, unless the change is a measured
  fix for both packages.
- A test project that sees the internals of both packages sees two copies of each type; give it an `extern alias` for one.
