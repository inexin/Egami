# The .egdr format

An Egami drawing file holds a recorded `Drawing`: the calls a `DrawingRecorder` kept, and the images, paths and nested drawings
they use. `Drawing.Save` writes it and `Drawing.Load` reads it; this page is the format's specification, version 1.0, for
anyone writing a reader or writer of their own. See [Drawing](drawing.md#recorded-drawings) for the API.

## Layout

All numbers are little-endian. `u8`, `u16` and `u32` are unsigned integers of 1, 2 and 4 bytes, `i32` a signed 4-byte integer,
`f32` an IEEE 754 single, and `bool` a `u8` that is 0 or 1.

A file is a 12-byte header followed by chunks:

| Bytes | Field | Value |
| --- | --- | --- |
| 0 to 7 | Signature | `89 45 47 44 52 0D 0A 1A` (`\x89EGDR\r\n\x1A`, after PNG's: it catches text-mode transfers and stops `type`) |
| 8 to 9 | Major version | `u16`, 1. A reader rejects a major version it does not know. |
| 10 to 11 | Minor version | `u16`, 0. A reader accepts any minor version of its major version. |

Each chunk is a 4-byte ASCII type, a `u32` payload length, and the payload. A reader skips the payload of a chunk type it does
not know, which is how later minor versions add to the format. The chunk types of version 1.0:

| Type | Payload | Defines |
| --- | --- | --- |
| `IMAG` | A PNG file | The next image, numbered from 0 |
| `PATH` | A path, below | The next path, numbered from 0 |
| `DRAW` | A drawing, below | The next drawing, numbered from 0 |
| `DEND` | Empty | The end. The file's drawing is the last `DRAW` chunk before it. |

A chunk refers to images, paths and drawings by number, and only to ones defined by earlier chunks, so a file is read in one pass
and cannot hold a cycle. A writer writes each image, path and drawing once, however often it is used. Bytes after `DEND` are
ignored.

## Paths

| Field | Type |
| --- | --- |
| Fill rule | `u8`: 0 nonzero, 1 even-odd |
| Verb count | `u32` |
| Verbs | `u8` each: 0 move, 1 line, 2 quadratic, 3 cubic, 4 close |
| Point count | `u32`, the sum of the verbs' points: 1 for a move or a line, 2 for a quadratic, 3 for a cubic, 0 for a close |
| Points | `f32` x and y each, finite |

A line, quadratic or cubic must follow a move, or another segment, in its contour: after a close, a new contour starts with a
move.

## Drawings

| Field | Type |
| --- | --- |
| Bounds | Rectangle |
| Call count | `u32` |
| Calls | One after another, each an opcode `u8` and its operands |

The payload must end exactly after the last call.

A rectangle is four `f32`: x, y, width and height. A matrix is six `f32`: M11, M12, M21, M22, M31, M32, as .NET's `Matrix3x2`,
which maps a row vector (x, y, 1). A color is four `f32`: red, green, blue and straight alpha, encoded (sRGB) values from 0 to 1.
A point is two `f32`. A round rectangle is a rectangle and the x and y radii of its corners: top-left, top-right, bottom-right,
bottom-left. An enum is a `u8` with the value Egami's enum of that name has. Every number must be finite.

| Opcode | Call | Operands |
| --- | --- | --- |
| 1 | Save | None |
| 2 | Restore (also ends a layer) | None |
| 3 | SaveLayer | Opacity `f32`, BlendMode, isolated `bool`, has-bounds `bool`, bounds rectangle if it has them |
| 4 | SetMatrix | Matrix |
| 5 | ClipRect | Rectangle, ClipOp, antialias `bool` |
| 6 | ClipRoundRect | Round rectangle, ClipOp, antialias `bool` |
| 7 | ClipPath | Path number `u32`, ClipOp, antialias `bool` |
| 8 | DrawPaint | Paint |
| 9 | DrawRect | Rectangle, has-stroke `bool`, stroke if it has one, paint |
| 10 | DrawRoundRect | Round rectangle, has-stroke `bool`, stroke, paint |
| 11 | DrawOval | Rectangle, has-stroke `bool`, stroke, paint |
| 12 | DrawPath | Path number `u32`, has-stroke `bool`, stroke, paint |
| 13 | DrawPoints | PointMode, count `u32`, points, stroke, paint |
| 14 | DrawImage | Image number `u32`, source rectangle (inside the image), destination rectangle, sampling, has-paint `bool`, paint |
| 15 | DrawLattice | Image number, lattice, destination rectangle, sampling, has-paint `bool`, paint |
| 16 | DrawAtlas | Image number, count `u32`, that many sprite transforms (four `f32`: ScaleCos, ScaleSin, Tx, Ty), that many source rectangles, has-colors `bool`, that many colors if it has them, BlendMode, sampling, has-paint `bool`, paint |
| 17 | DrawMesh | Mesh, paint, BlendMode, stops |
| 18 | DrawPatch | Patch, paint, BlendMode, stops |
| 19 | DrawDrawing | Drawing number `u32`, matrix, has-paint `bool`, paint, cache-as-image `bool` |

Coordinates are those the recorder's hooks received: local coordinates under the matrix last set. Replaying a drawing sets each
recorded matrix after the matrix the drawing is replayed under. Text is written as `DrawPath` of its outlines, filled, since a
file holds no fonts.

A **stroke** is width `f32` (0 for a hairline), LineCap, LineJoin, miter limit `f32`, has-dash `bool`, and for a dash an
interval count `u32`, the intervals as `f32` and the phase `f32`. Path effects never reach a file: the canvas applies them
before the hook.

**Sampling** is ResampleFilter, ColorSpace, EdgeMode and the background color as four `u8` (red, green, blue, alpha).

**Stops** are a count `u32`, then each stop's offset `f32` (0 to 1) and color.

A **lattice** is the x division count `u32` and the divisions as `i32`, the same for y, has-bounds `bool` and the bounds as four
`i32` (x, y, width, height), has-kinds `bool` and one LatticeCellKind per cell, has-colors `bool` and one color per cell. There
are (x divisions + 1) · (y divisions + 1) cells, row by row.

A **mesh** is MeshMode, the vertex count `u32`, the positions, a flags `u8` (1 colors, 2 texture coordinates, 4 values,
8 indices), then for each flag set in that order: a color per vertex, a point per vertex, an `f32` per vertex, and an index
count `u32` with that many `i32` indices, each below the vertex count. Colors and values are not both present, and a mesh with
values has at least one stop.

A **patch** is PatchKind (0 Coons, 1 tensor), its 12 or 16 points, a flags `u8` (1 colors, 2 texture coordinates, 4 values),
then four corner colors, four corner points and four corner `f32` values for the flags set, in that order.

## Paints

A paint is its kind `u8`, the fields every paint has, then its own:

| Field | Type |
| --- | --- |
| BlendMode | `u8` |
| Opacity | `f32`, 0 to 1 |
| Flags | `u8`: 1 antialias, 2 dither, 4 has a mask blur |
| Local matrix | Matrix |
| Mask blur | Sigma `f32` and BlurStyle, when flag 4 is set |

| Kind | Paint | Own fields |
| --- | --- | --- |
| 1 | SolidPaint | Color |
| 2 | LinearGradient | Start point, end point, gradient fields |
| 3 | RadialGradient | Center point, radius `f32`, gradient fields |
| 4 | ConicalGradient | Start point, start radius `f32`, end point, end radius `f32`, gradient fields |
| 5 | SweepGradient | Center point, start angle `f32`, end angle `f32`, gradient fields |
| 6 | ImagePaint | Image number `u32`, TileMode x, TileMode y, sampling |
| 7 | NoisePaint | NoiseType, base frequency x and y `f32`, octaves `i32`, seed `i32`, has-tile `bool`, tile width and height `i32` |
| 8 | BlendPaint | Destination paint, source paint, BlendMode |
| 9 | ColorMatrixPaint | Source paint, the 20 matrix values `f32`, row by row |
| 10 | DrawingPaint | Drawing number `u32`, TileMode x, TileMode y, tile rectangle |

The gradient fields are the stops (at least one), TileMode, a flags `u8` (1 extend start, 2 extend end, 4 premultiplied),
InterpolationSpace and HueMethod. A simplex noise uses its x frequency.

A `CustomPaint` runs C# and has no kind: saving a drawing that uses one throws, and so does saving a layer with a filter, a
backdrop or a soft mask, naming the call.

## Reading untrusted files

Egami's reader checks every count against the bytes left before it allocates, puts every value through the constructors that
check it, reads images with the PNG reader under the context's `SafetyLimits`, counts what it decodes toward
`MaxTotalBytes`, and limits drawings to 64 levels of nesting and 50 million calls with nested drawings expanded, so a small file
cannot ask for an exponential replay. Any failure is an `EgamiException`. The reader has a fuzz target, `egdr`.
