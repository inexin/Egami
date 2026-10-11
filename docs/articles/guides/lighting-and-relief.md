# Lighting and relief

Three steps treat an image as a height field and light it. `Lighting` is the SVG 1.1 lighting filters, with Skia's parameters;
`Emboss` and `Shade` are the two quick relief effects ImageMagick users know.

| Operation | Does |
| --- | --- |
| `Lighting(light, model, options)` | SVG's feDiffuseLighting or feSpecularLighting with a distant, point or spot light: returns the light map, or blends it onto the image |
| `Emboss(angle, depth, sigma, grayscale)` | Blurs, takes the slope toward the light, and adds mid gray: flat areas become 0.5 |
| `Shade(azimuth, elevation, colored)` | ImageMagick's -shade (gray relief) or +shade (the shading multiplied onto the colors), identical at 8 bits |

## Lights and surfaces

[!code-csharp[](../../../tests/Egami.DocSamples/Lighting.cs#lights)]

The height is alpha by default, as in SVG and Skia: a logo on a transparent canvas stands up from it. `SurfaceScale` is the
height of a fully opaque (or white) pixel, in pixels, so larger values give steeper slopes. Normals come from SVG's 3 × 3 Sobel
stencils, with SVG's own stencils on the edges and corners of the image.

- `DistantLight(azimuth, elevation)` shines from one direction everywhere. The azimuth is SVG's: degrees clockwise from the x
  axis, with y down, so 225 is the top left.
- `PointLight(position)` and `SpotLight(position, target, exponent, coneAngle)` sit at a point in pixel coordinates, with z
  above the image; pixel centers are at x + 0.5, as Skia evaluates them. A spot light fades as cos(angle)^exponent away from its
  axis and stops at the cone angle, with Skia's soft cone edge.
- `LightingModel.Diffuse(kd)` gives the light's color times kd times N·L, and an opaque map. `LightingModel.Specular(ks,
  shininess)` gives a highlight, ks times (N·H)^shininess, and a map whose alpha is its brightest channel.

Light colors and the light map are not color-managed: they work on stored values, as Skia does in its working space. SVG's
default of computing filters in linear light is not applied; convert the image first if you need it.

[!code-csharp[](../../../tests/Egami.DocSamples/Lighting.cs#lighting)]

## A bevelled logo

With `Blend`, the light map is blended onto the image with a 2.5.1 `BlendMode`, the light map as the source, and the image keeps
its alpha, so the light changes the color and never the shape. Multiply shades the slopes away from the light; Screen adds a
specular highlight on the slopes that face it.

[!code-csharp[](../../../tests/Egami.DocSamples/Lighting.cs#bevel)]

## Embossed text

`Emboss` turns a page or a photo into gray relief: it blurs with `sigma`, takes the slope toward the light with a 3 × 3 Sobel
stencil, scales it by `depth` and adds mid gray, so paper becomes flat gray and strokes rise. The angle is counterclockwise
from the right with y up, as ImageMagick's -shade azimuth (135 is the top left). `grayscale: false` embosses each color
channel on its own; alpha is kept.

[!code-csharp[](../../../tests/Egami.DocSamples/Lighting.cs#emboss)]

ImageMagick's -emboss shares only the name: it sharpens along a diagonal and then equalizes the histogram, so its results
differ from this classic emboss.

## Shade

`Shade` is ImageMagick's -shade: the luminance is the height, and each pixel becomes the Lambertian shading by a light at the
azimuth (counterclockwise from the right, y up) and elevation; flat areas get sin(elevation). `colored: true` multiplies the
shading onto the colors, as +shade does. Results are identical to ImageMagick 7 (Q16 HDRI) at 8 bits; alpha is kept.

[!code-csharp[](../../../tests/Egami.DocSamples/Lighting.cs#shade)]

## Edges and regions

All three keep the size and read one pixel past each output (Emboss also the blur's reach), repeating the edge pixel past the
border. They work inside `Within` and `OnChannels`: inside a region they read the real pixels around it, and a point or spot
light keeps its position in the raster's coordinates.
