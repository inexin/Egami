# Steps for run

The steps `egami run` chains and workflow files list. Every public `RasterOps` method whose arguments a command line can
spell is a step, named in kebab case (`ContrastStretch` is `contrast-stretch`); its options are the method's parameters.
Positional values fill the parameters in order; `--name value` sets one by name. `egami run --list <step>` prints one.

| Step | What it does |
| --- | --- |
| [adaptive-threshold](#adaptive-threshold) | Thresholds each pixel against the mean of the window around it (Bradley and Roth, 2007): a pixel is dark when its luminance is more than limit below its window's mean, so uneven lighting on a scanned page does not swallow text. |
| [add-noise](#add-noise) | Adds noise to red, green and blue (or a CMYK raster's inks), as ImageMagick's +noise with -attenuate does: film grain, sensor noise, or salt and pepper for testing filters. |
| [alpha-to-gray](#alpha-to-gray) | Makes the image a gray picture of its alpha: red, green and blue become the alpha value and alpha becomes opaque, as ImageMagick's -alpha extract does. |
| [arithmetic](#arithmetic) | Applies op with value to every value of channels, on values scaled 0 to 1 and clamped back to 0..1, as ImageMagick's -evaluate defines each operator. |
| [auto-gamma](#auto-gamma) | Applies the Gamma that moves the image's mean to 0.5, as ImageMagick's -auto-gamma; an image whose mean is 0 or 1 is left alone. |
| [auto-level](#auto-level) | Stretches the darkest value to 0 and the lightest to 1, as ImageMagick's -auto-level. |
| [auto-orient](#auto-orient) | Turns the image the way its EXIF orientation says, so it looks as the camera meant, and sets the orientation to Normal. |
| [auto-threshold](#auto-threshold) | Thresholds at the level method finds in the histogram, as ImageMagick's -auto-threshold; see FindThreshold. |
| [auto-white-balance](#auto-white-balance) | Removes a color cast it measures, by method. |
| [bilateral-blur](#bilateral-blur) | Smooths while keeping edges (Tomasi and Manduchi's bilateral filter): each neighbour counts by its distance, through a Gaussian of spatialSigma, and by how much its color differs, through a Gaussian of rangeSigma, so noise flattens and edges stay. |
| [blend-color](#blend-color) | Blends one color over every pixel by mode: Multiply tints, Screen washes out, Color colorizes. |
| [bloom](#bloom) | Makes bright areas glow, as a camera's bloom: the parts of each channel above threshold, stretched to 0..1 and scaled by intensity, blurred in linear light with Fast and screened onto the image in linear light. |
| [blur](#blur) | Blurs with a Gaussian of standard deviation sigma pixels, computed by method. |
| [box-blur](#box-blur) | Replaces each pixel with the mean of the (2r + 1) square around it. |
| [brightness](#brightness) | Multiplies red, green and blue by amount: 1 changes nothing, 0 gives black. |
| [canny-edges](#canny-edges) | Finds thin edges with Canny's detector, as ImageMagick's -canny 0x{sigma}+{low × 100}%+{high × 100}% does: the luma is smoothed by a Gaussian, Canny's first differences give the gradient, pixels that are not the strongest across their edge are dropped, and hysteresis keeps pixels whose magnitude reaches high times the largest, plus those 8-connected to them that reach low times it. |
| [change-speed](#change-speed) | Plays the animation factor times as fast: every delay is divided by it, rounded to whole milliseconds. |
| [clahe](#clahe) | Contrast-limited adaptive histogram equalization (Zuiderveld, 1994) on L*, as ImageMagick's -clahe: each of tilesX × tilesY tiles gets its own clipped, equalized curve, blended smoothly between tile centers, so local detail comes up without the noise of plain equalization. |
| [colorize](#colorize) | Blends red, green and blue toward color, as ImageMagick's -colorize. |
| [contrast](#contrast) | Scales red, green and blue around mid-gray: 1 changes nothing, 0 gives flat gray. |
| [contrast-stretch](#contrast-stretch) | Clips the darkest black and lightest white fractions of the pixels, then stretches the rest to 0..1, as ImageMagick's -contrast-stretch. |
| [convert-color](#convert-color) | Converts the pixels from the encoding the raster is in (GetColorProfile, or the target of an earlier ConvertColor in the chain) to target, in place, and records the new encoding in the metadata: no profile for sRGB, the target's ICC profile otherwise. |
| [cover](#cover) | Scales the image to cover width × height with its aspect ratio kept, keeping the part at anchor. |
| [crop](#crop) | Keeps one rectangle of the image. |
| [deskew](#deskew) | Straightens a scanned or photographed page: detects the tilt of its text lines (see DetectSkew) and turns the image back by it. |
| [despeckle](#despeckle) | Removes specks while keeping edges, by Crimmins' complementary hulling exactly as ImageMagick's -despeckle runs it: each color channel is nudged, a level at a time, toward neighbours that differ by two levels or more, in 16 passes along four directions. |
| [dilate](#dilate) | The maximum over the (2rx + 1) × (2ry + 1) rectangle around each pixel, as Skia's dilate filter with those radii. |
| [displace](#displace) | Moves each pixel by a displacement map, as Skia's DisplacementMap and ImageMagick's -displace do: pixel (x, y) shows the source at (x + scaleX × (mx − 0.5), y + scaleY × (my − 0.5)), with mx and my the map's chosen channels from 0 to 1, so mid-gray moves nothing. |
| [draw-animation](#draw-animation) | Draws an animation (or a still) onto this one at position. |
| [draw-arithmetic](#draw-arithmetic) | Draws image with the arithmetic composite (SVG's feComposite arithmetic operator, Skia's arithmetic blender): each channel of the premultiplied result is k1·S·D + k2·S + k3·D + k4, with S the image and D this raster, in 0..1. |
| [draw-image](#draw-image) | Draws the part of image inside sourceArea onto this raster with that part's top-left corner at position, combining pixels by mode as DrawImage does for the whole image. |
| [drop-shadow](#drop-shadow) | Adds a drop shadow, as Skia's DropShadow image filter: the image's alpha, moved by (dx, dy), blurred by a Gaussian of sigma and filled with color, with the image drawn over it source-over. |
| [drop-zero-delay](#drop-zero-delay) | Removes frames with no delay, which no viewer shows, as ImageMagick's -layers RemoveZero; when every frame has none, nothing is removed. |
| [duotone](#duotone) | Maps luminance onto the line from shadows to highlights. |
| [edges](#edges) | Replaces each pixel with the strength of the edge through it: the gradient magnitude of op's kernels, scaled so a full black-to-white step gives 1, clamped to 0 to 1. |
| [emboss](#emboss) | Embosses: blurs with a Gaussian of sigma, takes the slope toward the light at angle with a 3 × 3 Sobel stencil, scales it by depth and adds mid gray, so flat areas become 0.5 and slopes facing the light are lighter. |
| [entropy-crop](#entropy-crop) | Crops to the busiest part of the image: the bounding box of the 16 × 16 tiles whose luminance entropy (the Shannon entropy of the tile's 256-level histogram, over 8 bits) reaches threshold. |
| [equalize](#equalize) | Spreads the values evenly by histogram equalization: of L* by default, keeping a* and b* so colors hold, or of each channel as ImageMagick's -equalize. |
| [erode](#erode) | The minimum over the (2rx + 1) × (2ry + 1) rectangle around each pixel, as Skia's erode filter with those radii. |
| [fill-color](#fill-color) | Sets every pixel in rect, or the whole image, to one color. |
| [fit](#fit) | Scales the image to fit inside width × height with its aspect ratio kept, so one side matches the box and the other is no larger. |
| [flatten](#flatten) | Composites the image over background and makes it opaque, as ImageMagick's -alpha remove does. |
| [flip](#flip) | Mirrors the image: horizontal (left to right), vertical (top to bottom) or both. |
| [flip-horizontal](#flip-horizontal) | Mirrors the image left to right. |
| [flip-vertical](#flip-vertical) | Mirrors the image top to bottom. |
| [flood-fill](#flood-fill) | Fills the region connected to seed whose colors lie within tolerance of the seed's own with fill, as ImageMagick's -fuzz with -floodfill does. |
| [gamma](#gamma) | Raises red, green and blue to 1/gamma, as ImageMagick's -gamma: 2.2 brightens. |
| [glow](#glow) | Adds a glow around the image's alpha shape, as an editor's outer glow layer style: the alpha, blurred with Fast, spread, and filled with color, drawn behind the image. |
| [grayscale](#grayscale) | Converts toward gray. |
| [high-contrast](#high-contrast) | Skia's high-contrast accessibility filter. |
| [hue-rotate](#hue-rotate) | Rotates hue around the luma axis. |
| [inner-shadow](#inner-shadow) | Adds a shadow inside the image's alpha shape, as an editor's inner shadow layer style: everything outside the shape, moved by (dx, dy) rounded to whole pixels, blurred with Fast and filled with color, drawn on the image inside the shape only. |
| [invert](#invert) | Replaces red, green and blue with 1 − value, blended by amount. |
| [kuwahara](#kuwahara) | Smooths while keeping edges, with the Kuwahara filter as ImageMagick's -kuwahara defines it: of the four (r + 1) squares that have the pixel as a corner, the one whose luma varies least gives the pixel its value, sampled at that square's middle. |
| [lens-blur](#lens-blur) | Blurs as an out-of-focus lens does: each point spreads into the aperture's shape, a disc or a polygon of 5 to 12 blades, so small lights become discs, unlike a Gaussian's soft spots. |
| [level](#level) | Stretches black..white to outputBlack..outputWhite with a midtone gamma, as ImageMagick's -level and +level. |
| [local-contrast](#local-contrast) | Brings out local detail, as ImageMagick's -local-contrast: L* is pushed away from a wide blur of itself by strength, so large-scale light changes less than detail. |
| [luminance-to-alpha](#luminance-to-alpha) | Sets alpha to the luminance of the stored values, 0.212656 R + 0.715158 G + 0.072186 B (the Rec. |
| [make-transparent](#make-transparent) | Makes pixels within tolerance of color transparent, keeping their color, by ImageMagick's -fuzz distance, so with no softness the result equals ImageMagick's -fuzz with -transparent. |
| [median](#median) | Replaces each channel with its median over the (2r + 1) square around the pixel, as ImageMagick's -statistic Median does: removes salt-and-pepper noise while keeping edges. |
| [merge-duplicates](#merge-duplicates) | Removes each frame equal to the one before it and adds its delay to the frame that stays, as ImageMagick's -layers RemoveDups: a screen recording that sat still for seconds becomes one long frame. |
| [motion-blur](#motion-blur) | Streaks the image along a direction, as a camera moving during the exposure, with ImageMagick's -motion-blur meaning: each pixel averages 2⌈radius⌉ + 1 samples one pixel apart, from itself toward angle, weighted by a Gaussian of sigma from the pixel. |
| [normalize](#normalize) | ContrastStretch at ImageMagick's -normalize points: the darkest 2% of the pixels become black and the lightest 1% white. |
| [offset](#offset) | Moves the image by whole pixels, right by dx and down by dy, with no resampling. |
| [oil-paint](#oil-paint) | Makes the image look painted, as ImageMagick's -paint does: around each pixel, in the (2r + 1) square, pixels are sorted into levels bins by intensity, and the pixel becomes a pixel of the most common bin, unchanged. |
| [opacity](#opacity) | Multiplies alpha by amount. |
| [pad](#pad) | Enlarges the canvas to width × height, placing the image at anchor and filling the new area with background. |
| [pixelate](#pixelate) | Pixelates: the image is cut into square cells, and every pixel of a cell becomes the cell's average, taken on premultiplied values so transparent pixels do not darken it. |
| [posterize](#posterize) | Rounds red, green and blue to levels evenly spaced values each, as ImageMagick's -posterize, optionally dithered: ordered dithering adds a fixed 8 × 8 pattern before rounding, Floyd–Steinberg spreads each value's rounding error to its neighbours. |
| [quad-distort](#quad-distort) | Moves the image's four corners to four points, bending it in perspective: ImageMagick's Perspective distort. |
| [quantize](#quantize) | Reduces each frame to at most MaxColors colors, as a palette format would, but keeps the pixel type: an Rgba16 image stays Rgba16 with at most that many distinct values. |
| [replace-color](#replace-color) | Replaces every pixel within tolerance of from with to, by ImageMagick's -fuzz distance, as ImageMagick's -fill with -opaque does. |
| [resize](#resize) | Resizes to a size in the size syntax (W, xH, WxH, P%, with !, ^, > or <) with a fit mode. |
| [retime](#retime) | Resamples the animation to framesPerSecond, as ffmpeg's fps filter: fixed ticks, each showing the frame visible when it starts, with neighbours that show the same frame merged. |
| [reverse](#reverse) | Plays the animation backwards; each frame keeps its delay and metadata. |
| [rotate](#rotate) | Rotates the image clockwise. |
| [rotational-blur](#rotational-blur) | Spins the image around center, as ImageMagick's -rotational-blur: each pixel averages samples along an arc of degrees centered on it, about one per pixel of arc (at most 256), read bilinearly. |
| [saturation](#saturation) | Mixes each color with its luma: 1 changes nothing, 0 gives gray, above 1 oversaturates. |
| [sepia](#sepia) | Tints toward sepia. |
| [shade](#shade) | Shades the image as ImageMagick's -shade does: the luminance is a height field, and each pixel becomes the Lambertian shading of its surface normal by a distant light at azimuth and elevation, a gray relief; with colored the shading multiplies the image's own colors instead, as +shade does. |
| [sharpen](#sharpen) | Sharpens with an unsharp mask: out = src + amount × (src − Blur(sigma)). |
| [shuffle-channels](#shuffle-channels) | Rearranges the working RGBA channels: each output channel takes the input channel, or the constant, its argument names. |
| [sigmoidal-contrast](#sigmoidal-contrast) | Adds (or removes) contrast with ImageMagick's scaled logistic curve, which never clips. |
| [simulate-color-blindness](#simulate-color-blindness) | Shows how the image looks with a color vision deficiency. |
| [skew](#skew) | Shears the image about its center: vertical lines lean by degreesX (x moves by y × tan) and horizontal lines by degreesY. |
| [solarize](#solarize) | Inverts red, green and blue values above threshold, as ImageMagick's -solarize. |
| [spread](#spread) | Scatters pixels, as ImageMagick's -spread does: each pixel takes the value of a random pixel at most radius away (a whole-pixel offset in the disc; edges repeat the edge pixel). |
| [threshold](#threshold) | Makes a two-color image: pixels above level take above, the others below, with alpha kept. |
| [tile](#tile) | Makes a new width × height canvas filled by repeating the image, or the source rectangle of it, with one copy's top-left corner at origin, as Skia's Tile filter and ImageMagick's tile: fill do. |
| [tint](#tint) | Pushes midtones toward color and keeps black and white, as ImageMagick's -tint. |
| [tone-map](#tone-map) | Maps HDR linear light into 0..1: pixel values are read as linear light with reference white at 1.0 (as RgbaF32, RgbaF16 and RgbF32 rasters hold PQ and HLG images converted to a linear encoding), and the tone curve brings the brightest down to 1 on max(R, G, B), which keeps hue. |
| [trim](#trim) | Removes the border: the rows and columns at the edges whose pixels all lie within tolerance of the border color, by ImageMagick's -fuzz distance, so the rectangle equals ImageMagick's -trim. |
| [vignette](#vignette) | Darkens (or tints) the image toward its corners, outside an ellipse fitted to the image, with a soft edge: the shape of ImageMagick's -vignette, which blurs a drawn ellipse. |
| [white-balance](#white-balance) | Applies WhiteBalance: corrects for a light of temperature kelvin. |
| [zoom-blur](#zoom-blur) | Streaks the image along rays from center, as a zoom during the exposure (Photoshop's radial zoom blur): each pixel averages samples on the ray through it, spanning amount of its distance from the center, half inward and half outward, about one per pixel (at most 256). |
| [save](#save) | Writes the image: save <path> [--format f] [--quality q] [--lossless] [--effort e] [--bit-depth b] [--strip] [--set fmt.key=value]. |

## adaptive-threshold

```text
adaptive-threshold: Thresholds each pixel against the mean of the window around it (Bradley and Roth, 2007): a pixel is dark when its luminance is more than limit below its window's mean, so uneven lighting on a scanned page does not swallow text.

  adaptive-threshold [--limit <number>] [--window <int>] [--below <color>] [--above <color>]
    --limit <number>, default 0.15
        How far below the local mean a pixel must be to count as dark, from 0 to 1.
    --window <int>
        The window's side in pixels, at least 1, or null (the default) for an eighth of the width.
    --below <color>
        The dark color, or null for black.
    --above <color>
        The light color, or null for white.
```

## add-noise

```text
add-noise: Adds noise to red, green and blue (or a CMYK raster's inks), as ImageMagick's +noise with -attenuate does: film grain, sensor noise, or salt and pepper for testing filters.

  add-noise <kind> [--amount <number>] [--monochrome <bool>] [--seed <integer>]
    --kind <gaussian|uniform|impulse|laplacian|multiplicative|poisson>, required
        The kind of noise.
    --amount <number>, default 1
        The strength, as ImageMagick's -attenuate, from 0 (changes nothing) to 10; 1 gives ImageMagick's default.
    --monochrome <bool>, default false
        Whether red, green and blue get the same noise, so it has no color.
    --seed <integer>, default 0
        The seed: change it for different noise.
```

## alpha-to-gray

```text
alpha-to-gray: Makes the image a gray picture of its alpha: red, green and blue become the alpha value and alpha becomes opaque, as ImageMagick's -alpha extract does.

  alpha-to-gray no arguments
```

## arithmetic

```text
arithmetic: Applies op with value to every value of channels, on values scaled 0 to 1 and clamped back to 0..1, as ImageMagick's -evaluate defines each operator.

  arithmetic <op> <value> [--channels <choice>]
    --op <add|subtract|multiply|divide|pow|log|min|max|set>, required
        The operator.
    --value <number>, required
        The constant, on the same 0 to 1 scale for Add, Subtract, Min, Max and Set.
    --channels <none|red|green|blue|alpha|color|all>, default color
        The channels to change.
```

## auto-gamma

```text
auto-gamma: Applies the Gamma that moves the image's mean to 0.5, as ImageMagick's -auto-gamma; an image whose mean is 0 or 1 is left alone.

  auto-gamma [--linked <bool>] [--per-frame <bool>]
    --linked <bool>, default true
        Whether the channels share one gamma from their joint mean (the default) or each gets its own.
    --per-frame <bool>, default false
        Whether each frame is measured alone.
```

## auto-level

```text
auto-level: Stretches the darkest value to 0 and the lightest to 1, as ImageMagick's -auto-level.

  auto-level [--linked <bool>] [--per-frame <bool>]
    --linked <bool>, default true
        Whether red, green and blue share one black and one white point (the default, which keeps colors), or each gets its own, which also removes a cast.
    --per-frame <bool>, default false
        Whether each frame is measured alone, as ImageMagick does, instead of all frames together so an animation does not flicker.
```

## auto-orient

```text
auto-orient: Turns the image the way its EXIF orientation says, so it looks as the camera meant, and sets the orientation to Normal.

  auto-orient no arguments
```

## auto-threshold

```text
auto-threshold: Thresholds at the level method finds in the histogram, as ImageMagick's -auto-threshold; see FindThreshold.

  auto-threshold [--method <choice>] [--mode <choice>] [--below <color>] [--above <color>] [--per-frame <bool>]
    --method <otsu|triangle|kapur>, default otsu
        How the level is found.
    --mode <luminance|channels|saturation|max-chroma>, default luminance
        What is measured.
    --below <color>
        The lower color, or null for black.
    --above <color>
        The upper color, or null for white.
    --per-frame <bool>, default false
        Whether each frame is measured alone.
```

## auto-white-balance

```text
auto-white-balance: Removes a color cast it measures, by method.

  auto-white-balance [--method <choice>] [--per-frame <bool>]
    --method <gray-world|white-patch>, default gray-world
        How the cast is found.
    --per-frame <bool>, default false
        Whether each frame is measured alone.
```

## bilateral-blur

```text
bilateral-blur: Smooths while keeping edges (Tomasi and Manduchi's bilateral filter): each neighbour counts by its distance, through a Gaussian of spatialSigma, and by how much its color differs, through a Gaussian of rangeSigma, so noise flattens and edges stay.

  bilateral-blur <spatial-sigma> <range-sigma> [--radius <int>]
    --spatial-sigma <number>, required
        The reach in pixels, above 0 and at most 50.
    --range-sigma <number>, required
        How different a color may be and still count, above 0, on the 0..1 scale: 0.1 keeps most edges.
    --radius <int>
        The window's radius, or null for ⌈2 · spatialSigma⌉ as ImageMagick's width; at most 100.
```

## blend-color

```text
blend-color: Blends one color over every pixel by mode: Multiply tints, Screen washes out, Color colorizes.

  blend-color <color> <mode> [--opacity <number>] [--color-space <choice>]
    --color <color>, required
        The color, with straight alpha.
    --mode <source-over|clear|source|destination|destination-over|source-in|destination-in|source-out|destination-out|source-atop|destination-atop|xor|plus|modulate|multiply|screen|overlay|darken|lighten|color-dodge|color-burn|hard-light|soft-light|difference|exclusion|add|subtract|hue|saturation|color|luminosity>, required
        How the color combines with each pixel.
    --opacity <number>, default 1
        Multiplies the color's alpha before the mode: 1 uses it as it is.
    --color-space <encoded|linear>, default encoded
        Where pixels are blended.
```

## bloom

```text
bloom: Makes bright areas glow, as a camera's bloom: the parts of each channel above threshold, stretched to 0..1 and scaled by intensity, blurred in linear light with Fast and screened onto the image in linear light.

  bloom [--threshold <number>] [--sigma <number>] [--intensity <number>]
    --threshold <number>, default 0.8
        The stored value above which a channel blooms, from 0 to under 1.
    --sigma <number>, default 10
        The bloom's blur in pixels, from 0 to 1000.
    --intensity <number>, default 1
        How strongly the bloom is added, from 0 (nothing) to 100; values are clamped to 1 before the blur.
```

## blur

```text
blur: Blurs with a Gaussian of standard deviation sigma pixels, computed by method.

  blur <sigma> <color-space> <method>
    --sigma <number>, required
        The standard deviation in pixels; 0 changes nothing.
    --color-space <encoded|linear>, required
        Where pixels are blended.
    --method <exact|fast>, required
        How the blur is computed.

  blur <sigma> [--color-space <choice>]
    --sigma <number>, required
        The standard deviation in pixels; 0 changes nothing.
    --color-space <encoded|linear>, default encoded
        Where pixels are blended.
```

## box-blur

```text
box-blur: Replaces each pixel with the mean of the (2r + 1) square around it.

  box-blur <radius>
    --radius <int>, required
        The radius in pixels; 0 changes nothing.
```

## brightness

```text
brightness: Multiplies red, green and blue by amount: 1 changes nothing, 0 gives black.

  brightness <amount>
    --amount <number>, required
        The factor, 0 or more.
```

## canny-edges

```text
canny-edges: Finds thin edges with Canny's detector, as ImageMagick's -canny 0x{sigma}+{low × 100}%+{high × 100}% does: the luma is smoothed by a Gaussian, Canny's first differences give the gradient, pixels that are not the strongest across their edge are dropped, and hysteresis keeps pixels whose magnitude reaches high times the largest, plus those 8-connected to them that reach low times it.

  canny-edges [--sigma <number>] [--low <number>] [--high <number>]
    --sigma <number>, default 1
        The Gaussian's standard deviation in pixels, 0 (no smoothing) to 100.
    --low <number>, default 0.1
        The low threshold, as a fraction 0 to 1 of the largest gradient; at most high.
    --high <number>, default 0.3
        The high threshold, as a fraction 0 to 1 of the largest gradient.
```

## change-speed

```text
change-speed: Plays the animation factor times as fast: every delay is divided by it, rounded to whole milliseconds.

  change-speed <factor>
    --factor <number>, required
        The speed-up, more than 0: 2 plays twice as fast, 0.5 at half speed.
```

## clahe

```text
clahe: Contrast-limited adaptive histogram equalization (Zuiderveld, 1994) on L*, as ImageMagick's -clahe: each of tilesX × tilesY tiles gets its own clipped, equalized curve, blended smoothly between tile centers, so local detail comes up without the noise of plain equalization.

  clahe [--tiles-x <int>] [--tiles-y <int>] [--clip-limit <number>] [--bins <int>]
    --tiles-x <int>, default 8
        Tiles across, from 1 to 256.
    --tiles-y <int>, default 8
        Tiles down, from 1 to 256.
    --clip-limit <number>, default 2
        How far a tile's histogram may rise, as a multiple of its mean bin (2 is mild, 4 strong); 0 does not clip.
    --bins <int>, default 256
        The histogram bins, from 2 to 4,096.
```

## colorize

```text
colorize: Blends red, green and blue toward color, as ImageMagick's -colorize.

  colorize <color> <amount>
    --color <color>, required
        The color.
    --amount <number>, required
        The blend, from 0 to 1.
```

## contrast

```text
contrast: Scales red, green and blue around mid-gray: 1 changes nothing, 0 gives flat gray.

  contrast <amount>
    --amount <number>, required
        The factor, 0 or more.
```

## contrast-stretch

```text
contrast-stretch: Clips the darkest black and lightest white fractions of the pixels, then stretches the rest to 0..1, as ImageMagick's -contrast-stretch.

  contrast-stretch <black> <white> [--linked <bool>] [--per-frame <bool>]
    --black <number>, required
        The fraction of pixels that becomes black, from 0 to 1.
    --white <number>, required
        The fraction that becomes white, from 0 to 1; with black less than 1.
    --linked <bool>, default true
        Whether the channels share one pair of points (the default) or each gets its own.
    --per-frame <bool>, default false
        Whether each frame is measured alone.
```

## convert-color

```text
convert-color: Converts the pixels from the encoding the raster is in (GetColorProfile, or the target of an earlier ConvertColor in the chain) to target, in place, and records the new encoding in the metadata: no profile for sRGB, the target's ICC profile otherwise.

  convert-color <target>
    --target <profile>, required
        The encoding to convert to.
```

## cover

```text
cover: Scales the image to cover width × height with its aspect ratio kept, keeping the part at anchor.

  cover <width> <height> [--anchor <choice>] [--filter <choice>] [--color-space <choice>]
    --width <int>, required
        The output width, at least 1.
    --height <int>, required
        The output height, at least 1.
    --anchor <center|top-left|top|top-right|left|right|bottom-left|bottom|bottom-right>, default center
        Which part of the image to keep.
    --filter <bicubic|nearest|box|bilinear|mitchell|lanczos3|lanczos2|hermite|gaussian|spline>, default bicubic
        The resampling kernel.
    --color-space <encoded|linear>, default encoded
        Where pixels are blended.
```

## crop

```text
crop: Keeps one rectangle of the image.

  crop <x> <y> <width> <height>
    --x <int>, required
        The left edge of the rectangle.
    --y <int>, required
        The top edge of the rectangle.
    --width <int>, required
        The width of the rectangle, at least 1.
    --height <int>, required
        The height of the rectangle, at least 1.

  crop <rect>
    --rect <x,y,w,h>, required
        The rectangle to keep.
```

## deskew

```text
deskew: Straightens a scanned or photographed page: detects the tilt of its text lines (see DetectSkew) and turns the image back by it.

  deskew [--max-angle <number>] [--background <color>] [--keep-size <bool>] [--filter <choice>]
    --max-angle <number>, default 15
        The largest tilt looked for, in degrees, more than 0 and at most 45.
    --background <color>
        The color of the uncovered corners, or null (the default) for the top-left pixel's, the border color Trim would use.
    --keep-size <bool>, default true
        Whether the page keeps its size (the default, as a scanner user expects), or the canvas grows to the turned bounds as Rotate does.
    --filter <bicubic|nearest|box|bilinear|mitchell|lanczos3|lanczos2|hermite|gaussian|spline>, default bicubic
        The resampling kernel for the turn.
```

## despeckle

```text
despeckle: Removes specks while keeping edges, by Crimmins' complementary hulling exactly as ImageMagick's -despeckle runs it: each color channel is nudged, a level at a time, toward neighbours that differ by two levels or more, in 16 passes along four directions.

  despeckle no arguments
```

## dilate

```text
dilate: The maximum over the (2rx + 1) × (2ry + 1) rectangle around each pixel, as Skia's dilate filter with those radii.

  dilate <radius-x> <radius-y>
    --radius-x <int>, required
        The horizontal radius in pixels, 0 to 1000.
    --radius-y <int>, required
        The vertical radius in pixels, 0 to 1000.

  dilate <radius>
    --radius <int>, required
        The radius in pixels, 0 (changes nothing) to 1000.
```

## displace

```text
displace: Moves each pixel by a displacement map, as Skia's DisplacementMap and ImageMagick's -displace do: pixel (x, y) shows the source at (x + scaleX × (mx − 0.5), y + scaleY × (my − 0.5)), with mx and my the map's chosen channels from 0 to 1, so mid-gray moves nothing.

  displace <map> <scale-x> <scale-y> [--x-channel <choice>] [--y-channel <choice>]
    --map <image>, required
        The displacement map, the image's size (Resize it first if it is not).
    --scale-x <number>, required
        The horizontal move, in pixels, for a channel going from 0 to 1.
    --scale-y <number>, required
        The vertical move, in pixels, for a channel going from 0 to 1.
    --x-channel <red|green|blue|alpha|luminance>, default red
        The map channel that moves pixels horizontally.
    --y-channel <red|green|blue|alpha|luminance>, default green
        The map channel that moves pixels vertically.
```

## draw-animation

```text
draw-animation: Draws an animation (or a still) onto this one at position.

  draw-animation <overlay> <position>
    --overlay <image>, required
        The animation or still to draw.
    --position <x,y>, required
        Where its top-left corner goes; it may lie partly or wholly outside.
```

## draw-arithmetic

```text
draw-arithmetic: Draws image with the arithmetic composite (SVG's feComposite arithmetic operator, Skia's arithmetic blender): each channel of the premultiplied result is k1·S·D + k2·S + k3·D + k4, with S the image and D this raster, in 0..1.

  draw-arithmetic <image> <position> <k1> <k2> <k3> <k4> [--clamp <bool>]
    --image <image>, required
        The image to draw.
    --position <x,y>, required
        Where its top-left corner goes; may be negative.
    --k1 <number>, required
        The weight of the product S·D.
    --k2 <number>, required
        The weight of S.
    --k3 <number>, required
        The weight of D.
    --k4 <number>, required
        The constant added.
    --clamp <bool>, default true
        Whether each result is kept a valid premultiplied pixel: every channel in 0..1 and color no higher than alpha.
```

## draw-image

```text
draw-image: Draws the part of image inside sourceArea onto this raster with that part's top-left corner at position, combining pixels by mode as DrawImage does for the whole image.

  draw-image <image> <source-area> <position> <mode> [--opacity <number>] [--color-space <choice>]
    --image <image>, required
        The image to draw from.
    --source-area <x,y,w,h>, required
        The part of the image to draw, clipped to the image.
    --position <x,y>, required
        Where the area's top-left corner goes; may be negative.
    --mode <source-over|clear|source|destination|destination-over|source-in|destination-in|source-out|destination-out|source-atop|destination-atop|xor|plus|modulate|multiply|screen|overlay|darken|lighten|color-dodge|color-burn|hard-light|soft-light|difference|exclusion|add|subtract|hue|saturation|color|luminosity>, required
        How each pixel combines with the one under it.
    --opacity <number>, default 1
        Multiplies the image's alpha before the mode: 1 draws it as it is.
    --color-space <encoded|linear>, default encoded
        Where pixels are blended.

  draw-image <image> <position> <mode> [--opacity <number>] [--color-space <choice>]
    --image <image>, required
        The image to draw, of any pixel type.
    --position <x,y>, required
        Where its top-left corner goes; may be negative.
    --mode <source-over|clear|source|destination|destination-over|source-in|destination-in|source-out|destination-out|source-atop|destination-atop|xor|plus|modulate|multiply|screen|overlay|darken|lighten|color-dodge|color-burn|hard-light|soft-light|difference|exclusion|add|subtract|hue|saturation|color|luminosity>, required
        How each pixel combines with the one under it.
    --opacity <number>, default 1
        Multiplies the image's alpha before the mode: 1 draws it as it is.
    --color-space <encoded|linear>, default encoded
        Where pixels are blended.

  draw-image <image> <source-area> <position> [--opacity <number>]
    --image <image>, required
        The image to draw from.
    --source-area <x,y,w,h>, required
        The part of the image to draw, clipped to the image.
    --position <x,y>, required
        Where the area's top-left corner goes; may be negative.
    --opacity <number>, default 1
        Multiplies the image's alpha: 1 draws it as it is, 0 draws nothing.

  draw-image <image> <position> [--opacity <number>]
    --image <image>, required
        The image to draw.
    --position <x,y>, required
        Where its top-left corner goes; may be negative.
    --opacity <number>, default 1
        Multiplies the image's alpha: 1 draws it as it is, 0 draws nothing.
```

## drop-shadow

```text
drop-shadow: Adds a drop shadow, as Skia's DropShadow image filter: the image's alpha, moved by (dx, dy), blurred by a Gaussian of sigma and filled with color, with the image drawn over it source-over.

  drop-shadow <dx> <dy> <sigma> <color>
    --dx <number>, required
        How far the shadow moves right, in pixels (negative moves it left), at most 10000 either way.
    --dy <number>, required
        How far the shadow moves down, in pixels, at most 10000 either way.
    --sigma <number>, required
        The shadow's blur in pixels, from 0 (a hard shadow) to 1000.
    --color <color>, required
        The shadow's color, with its alpha as the shadow's opacity.
```

## drop-zero-delay

```text
drop-zero-delay: Removes frames with no delay, which no viewer shows, as ImageMagick's -layers RemoveZero; when every frame has none, nothing is removed.

  drop-zero-delay no arguments
```

## duotone

```text
duotone: Maps luminance onto the line from shadows to highlights.

  duotone <shadows> <highlights> [--weights <choice>]
    --shadows <color>, required
        The color for black.
    --highlights <color>, required
        The color for white.
    --weights <rec709|rec601>, default rec709
        The luma weights.
```

## edges

```text
edges: Replaces each pixel with the strength of the edge through it: the gradient magnitude of op's kernels, scaled so a full black-to-white step gives 1, clamped to 0 to 1.

  edges [--op <choice>] [--grayscale <bool>]
    --op <sobel|scharr|prewitt|roberts-cross|kirsch|robinson|laplacian3x3|laplacian5x5|laplacian-of-gaussian>, default sobel
        The kernels; Sobel by default.
    --grayscale <bool>, default true
        Whether to find edges in the luma (gray result) rather than per channel.
```

## emboss

```text
emboss: Embosses: blurs with a Gaussian of sigma, takes the slope toward the light at angle with a 3 × 3 Sobel stencil, scales it by depth and adds mid gray, so flat areas become 0.5 and slopes facing the light are lighter.

  emboss [--angle <number>] [--depth <number>] [--sigma <number>] [--grayscale <bool>]
    --angle <number>, default 135
        Where the light comes from, in degrees counterclockwise from the right with y up, as -shade's azimuth; 135 is the top left.
    --depth <number>, default 1
        The strength of the relief, from 0 (flat gray) to 100; 1 maps a slope of one level per pixel to one level.
    --sigma <number>, default 1
        The blur before the slope is taken, in pixels, from 0 (none) to 100.
    --grayscale <bool>, default true
        Whether the height is the Rec.
```

## entropy-crop

```text
entropy-crop: Crops to the busiest part of the image: the bounding box of the 16 × 16 tiles whose luminance entropy (the Shannon entropy of the tile's 256-level histogram, over 8 bits) reaches threshold.

  entropy-crop [--threshold <number>]
    --threshold <number>, default 0.5
        The entropy a tile needs, from 0 (every tile) to 1 (only uniformly random noise).
```

## equalize

```text
equalize: Spreads the values evenly by histogram equalization: of L* by default, keeping a* and b* so colors hold, or of each channel as ImageMagick's -equalize.

  equalize [--mode <choice>] [--per-frame <bool>]
    --mode <luminance|channels>, default luminance
        What is equalized.
    --per-frame <bool>, default false
        Whether each frame is measured alone.
```

## erode

```text
erode: The minimum over the (2rx + 1) × (2ry + 1) rectangle around each pixel, as Skia's erode filter with those radii.

  erode <radius-x> <radius-y>
    --radius-x <int>, required
        The horizontal radius in pixels, 0 to 1000.
    --radius-y <int>, required
        The vertical radius in pixels, 0 to 1000.

  erode <radius>
    --radius <int>, required
        The radius in pixels, 0 (changes nothing) to 1000.
```

## fill-color

```text
fill-color: Sets every pixel in rect, or the whole image, to one color.

  fill-color <color> [--rect <x,y,w,h>]
    --color <color>, required
        The color.
    --rect <x,y,w,h>
        The area to fill, which must lie inside the image, or null for the whole image.
```

## fit

```text
fit: Scales the image to fit inside width × height with its aspect ratio kept, so one side matches the box and the other is no larger.

  fit <width> <height> [--filter <choice>] [--color-space <choice>] [--enlarge <bool>]
    --width <int>, required
        The width of the box, at least 1.
    --height <int>, required
        The height of the box, at least 1.
    --filter <bicubic|nearest|box|bilinear|mitchell|lanczos3|lanczos2|hermite|gaussian|spline>, default bicubic
        The resampling kernel.
    --color-space <encoded|linear>, default encoded
        Where pixels are blended.
    --enlarge <bool>, default true
        Whether an image smaller than the box is enlarged.
```

## flatten

```text
flatten: Composites the image over background and makes it opaque, as ImageMagick's -alpha remove does.

  flatten <background> [--color-space <choice>]
    --background <color>, required
        The color to show through transparent pixels.
    --color-space <encoded|linear>, default encoded
        Where pixels are blended.
```

## flip

```text
flip: Mirrors the image: horizontal (left to right), vertical (top to bottom) or both.

  flip <direction>
    --direction <horizontal|vertical|both>, required
        Which way to mirror: horizontal (left to right), vertical (top to bottom) or both.
```

## flip-horizontal

```text
flip-horizontal: Mirrors the image left to right.

  flip-horizontal no arguments
```

## flip-vertical

```text
flip-vertical: Mirrors the image top to bottom.

  flip-vertical no arguments
```

## flood-fill

```text
flood-fill: Fills the region connected to seed whose colors lie within tolerance of the seed's own with fill, as ImageMagick's -fuzz with -floodfill does.

  flood-fill <seed> <fill> [--tolerance <number>] [--connectivity <choice>]
    --seed <x,y>, required
        Where the fill starts.
    --fill <color>, required
        The color to fill with.
    --tolerance <number>, default 0
        The color distance, from 0 (the seed's exact color) to 1 (every color).
    --connectivity <four|eight>, default four
        Whether the fill spreads to the four side neighbours (the default) or also the diagonal ones.
```

## gamma

```text
gamma: Raises red, green and blue to 1/gamma, as ImageMagick's -gamma: 2.2 brightens.

  gamma <gamma>
    --gamma <number>, required
        The gamma, above 0.
```

## glow

```text
glow: Adds a glow around the image's alpha shape, as an editor's outer glow layer style: the alpha, blurred with Fast, spread, and filled with color, drawn behind the image.

  glow <sigma> <color> [--spread <number>] [--inner <bool>]
    --sigma <number>, required
        The glow's blur in pixels, from 0 to 1000.
    --color <color>, required
        The glow's color, with its alpha as the glow's opacity.
    --spread <number>, default 0
        How much the glow is hardened before it fades, from 0 (a plain blur) to just under 1 (a hard edge): the blurred alpha is divided by 1 − spread.
    --inner <bool>, default false
        Whether the glow is inside the shape instead of around it.
```

## grayscale

```text
grayscale: Converts toward gray.

  grayscale [--mode <choice>] [--amount <number>] [--color-space <choice>]
    --mode <rec709|rec601>, default rec709
        The luma weights.
    --amount <number>, default 1
        0 changes nothing, 1 gives full gray.
    --color-space <encoded|linear>, default encoded
        Where the luma is taken.
```

## high-contrast

```text
high-contrast: Skia's high-contrast accessibility filter.

  high-contrast [--grayscale <bool>] [--invert <choice>] [--contrast <number>]
    --grayscale <bool>, default false
        Whether colors become gray first.
    --invert <none|brightness|lightness>, default none
        How to invert.
    --contrast <number>, default 0
        The contrast, from −1 to 1.
```

## hue-rotate

```text
hue-rotate: Rotates hue around the luma axis.

  hue-rotate <degrees>
    --degrees <number>, required
        The angle; 0 changes nothing.
```

## inner-shadow

```text
inner-shadow: Adds a shadow inside the image's alpha shape, as an editor's inner shadow layer style: everything outside the shape, moved by (dx, dy) rounded to whole pixels, blurred with Fast and filled with color, drawn on the image inside the shape only.

  inner-shadow <dx> <dy> <sigma> <color>
    --dx <number>, required
        How far the shadow moves right, in pixels (negative moves it left), at most 10000 either way.
    --dy <number>, required
        How far the shadow moves down, in pixels, at most 10000 either way.
    --sigma <number>, required
        The shadow's blur in pixels, from 0 (a hard shadow) to 1000.
    --color <color>, required
        The shadow's color, with its alpha as the shadow's opacity.
```

## invert

```text
invert: Replaces red, green and blue with 1 − value, blended by amount.

  invert [--amount <number>]
    --amount <number>, default 1
        0 changes nothing, 1 inverts fully.
```

## kuwahara

```text
kuwahara: Smooths while keeping edges, with the Kuwahara filter as ImageMagick's -kuwahara defines it: of the four (r + 1) squares that have the pixel as a corner, the one whose luma varies least gives the pixel its value, sampled at that square's middle.

  kuwahara [--radius <int>] [--sigma <number>]
    --radius <int>, default 3
        The radius in pixels, from 1 to 100.
    --sigma <number>, default 0
        The blur before, in pixels, from 0 (none) to 100.
```

## lens-blur

```text
lens-blur: Blurs as an out-of-focus lens does: each point spreads into the aperture's shape, a disc or a polygon of 5 to 12 blades, so small lights become discs, unlike a Gaussian's soft spots.

  lens-blur <radius>
    --radius <number>, required
        The aperture's radius in pixels, from 0 (changes nothing) to 128.
```

## level

```text
level: Stretches black..white to outputBlack..outputWhite with a midtone gamma, as ImageMagick's -level and +level.

  level [--black <number>] [--white <number>] [--gamma <number>] [--output-black <number>] [--output-white <number>]
    --black <number>, default 0
        The input that becomes outputBlack, from 0 to 1.
    --white <number>, default 1
        The input that becomes outputWhite, from 0 to 1.
    --gamma <number>, default 1
        The midtone gamma, above 0: above 1 brightens.
    --output-black <number>, default 0
        The output for black, from 0 to 1.
    --output-white <number>, default 1
        The output for white, from 0 to 1.
```

## local-contrast

```text
local-contrast: Brings out local detail, as ImageMagick's -local-contrast: L* is pushed away from a wide blur of itself by strength, so large-scale light changes less than detail.

  local-contrast [--radius <number>] [--strength <number>]
    --radius <number>, default 0.1
        The blur radius as a fraction of 20% of the longer side, above 0 and at most 1; the default 0.1 blurs over 2%.
    --strength <number>, default 0.125
        How strongly detail is raised, 0 or more; the default 0.125 is ImageMagick's 12.5%.
```

## luminance-to-alpha

```text
luminance-to-alpha: Sets alpha to the luminance of the stored values, 0.212656 R + 0.715158 G + 0.072186 B (the Rec.

  luminance-to-alpha no arguments
```

## make-transparent

```text
make-transparent: Makes pixels within tolerance of color transparent, keeping their color, by ImageMagick's -fuzz distance, so with no softness the result equals ImageMagick's -fuzz with -transparent.

  make-transparent <color> [--tolerance <number>] [--softness <number>]
    --color <color>, required
        The color to remove, with its alpha (opaque for most keys).
    --tolerance <number>, default 0
        The color distance that becomes fully transparent, from 0 (the exact color) to 1 (every color).
    --softness <number>, default 0
        The band of distance beyond the tolerance over which alpha returns to full; 0 for a hard edge.
```

## median

```text
median: Replaces each channel with its median over the (2r + 1) square around the pixel, as ImageMagick's -statistic Median does: removes salt-and-pepper noise while keeping edges.

  median <radius> [--include-alpha <bool>]
    --radius <int>, required
        The radius in pixels, from 0 (changes nothing) to 100.
    --include-alpha <bool>, default false
        Whether alpha gets its own median too; by default it is kept.
```

## merge-duplicates

```text
merge-duplicates: Removes each frame equal to the one before it and adds its delay to the frame that stays, as ImageMagick's -layers RemoveDups: a screen recording that sat still for seconds becomes one long frame.

  merge-duplicates [--tolerance <number>]
    --tolerance <number>, default 0
        How far a pixel may differ and still count as equal, by ImageMagick's -fuzz distance, from 0 (the default, exact) to 1.
```

## motion-blur

```text
motion-blur: Streaks the image along a direction, as a camera moving during the exposure, with ImageMagick's -motion-blur meaning: each pixel averages 2⌈radius⌉ + 1 samples one pixel apart, from itself toward angle, weighted by a Gaussian of sigma from the pixel.

  motion-blur <radius> <sigma> <angle> [--centered <bool>]
    --radius <number>, required
        Half the streak's length in pixels, from 0 (changes nothing) to 127.
    --sigma <number>, required
        How fast the weights fall along the streak, above 0.
    --angle <number>, required
        The direction in degrees, clockwise from the x axis (y points down).
    --centered <bool>, default false
        Whether the streak runs both ways from the pixel, instead of only toward angle.
```

## normalize

```text
normalize: ContrastStretch at ImageMagick's -normalize points: the darkest 2% of the pixels become black and the lightest 1% white.

  normalize [--linked <bool>] [--per-frame <bool>]
    --linked <bool>, default true
        Whether the channels share one pair of points (the default) or each gets its own.
    --per-frame <bool>, default false
        Whether each frame is measured alone.
```

## offset

```text
offset: Moves the image by whole pixels, right by dx and down by dy, with no resampling.

  offset <dx> <dy> [--edges <choice>] [--background <color>]
    --dx <int>, required
        The horizontal move, in pixels.
    --dy <int>, required
        The vertical move, in pixels.
    --edges <clamp|mirror|wrap|transparent>, default wrap
        What fills the uncovered strips.
    --background <color>
        The color of uncovered strips under Transparent.
```

## oil-paint

```text
oil-paint: Makes the image look painted, as ImageMagick's -paint does: around each pixel, in the (2r + 1) square, pixels are sorted into levels bins by intensity, and the pixel becomes a pixel of the most common bin, unchanged.

  oil-paint [--radius <int>] [--levels <int>]
    --radius <int>, default 3
        The brush radius in pixels, from 1 to 50.
    --levels <int>, default 20
        The number of intensity bins, from 2 to 256.
```

## opacity

```text
opacity: Multiplies alpha by amount.

  opacity <amount>
    --amount <number>, required
        0 makes the image transparent, 1 changes nothing.
```

## pad

```text
pad: Enlarges the canvas to width × height, placing the image at anchor and filling the new area with background.

  pad <width> <height> [--background <color>] [--anchor <choice>]
    --width <int>, required
        The new width, at least the image's width.
    --height <int>, required
        The new height, at least the image's height.
    --background <color>
        The color of the new area.
    --anchor <center|top-left|top|top-right|left|right|bottom-left|bottom|bottom-right>, default center
        Where the image sits on the new canvas.
```

## pixelate

```text
pixelate: Pixelates: the image is cut into square cells, and every pixel of a cell becomes the cell's average, taken on premultiplied values so transparent pixels do not darken it.

  pixelate <cell-size> [--origin <x,y>]
    --cell-size <int>, required
        The side of a cell in pixels, from 1 (changes nothing) to 4096.
    --origin <x,y>
        Where a cell's top-left corner falls, in the raster's coordinates; the grid repeats from there both ways.
```

## posterize

```text
posterize: Rounds red, green and blue to levels evenly spaced values each, as ImageMagick's -posterize, optionally dithered: ordered dithering adds a fixed 8 × 8 pattern before rounding, Floyd–Steinberg spreads each value's rounding error to its neighbours.

  posterize <levels> [--dither <choice>]
    --levels <int>, required
        Values per channel, from 2 to 256.
    --dither <floyd-steinberg|ordered|none|atkinson|burkes|jarvis-judice-ninke|sierra3|sierra2|sierra-lite|stevenson-arce|stucki|riemersma|bayer2x2|bayer4x4|bayer16x16|ordered3x3>, default none
        The dithering.
```

## quad-distort

```text
quad-distort: Moves the image's four corners to four points, bending it in perspective: ImageMagick's Perspective distort.

  quad-distort <top-left> <top-right> <bottom-right> <bottom-left>
    --top-left <x,y>, required
        Where the top-left corner goes.
    --top-right <x,y>, required
        Where the top-right corner goes.
    --bottom-right <x,y>, required
        Where the bottom-right corner goes.
    --bottom-left <x,y>, required
        Where the bottom-left corner goes.
```

## quantize

```text
quantize: Reduces each frame to at most MaxColors colors, as a palette format would, but keeps the pixel type: an Rgba16 image stays Rgba16 with at most that many distinct values.

  quantize no arguments
```

## replace-color

```text
replace-color: Replaces every pixel within tolerance of from with to, by ImageMagick's -fuzz distance, as ImageMagick's -fill with -opaque does.

  replace-color <from> <to> [--tolerance <number>]
    --from <color>, required
        The color to replace, with its alpha.
    --to <color>, required
        The color to put in its place.
    --tolerance <number>, default 0
        The color distance, from 0 (the exact color) to 1 (every color).
```

## resize

```text
resize: Resizes to a size in the size syntax (W, xH, WxH, P%, with !, ^, > or <) with a fit mode.

  resize <size> [--fit <choice>] [--gravity <choice>] [--filter <choice>] [--linear <bool>] [--background <color>]
    --size <size>, required
        The size: W, xH, WxH or P%, optionally followed by ! (ignore the aspect ratio), ^ (fill the box), > (only shrink) or < (only enlarge).
    --fit <contain|cover|fill|pad>, default contain
        How the image fills a WxH box: inside it (contain), filling and cropped (cover), stretched (fill) or inside and padded (pad).
    --gravity <center|top-left|top|top-right|left|right|bottom-left|bottom|bottom-right>, default center
        Which part stays for cover, and where the image sits for pad.
    --filter <bicubic|nearest|box|bilinear|mitchell|lanczos3|lanczos2|hermite|gaussian|spline>, default bicubic
        The resampling kernel; bicubic is Catmull-Rom.
    --linear <bool>, default false
        Blend pixels in linear light rather than as encoded.
    --background <color>, default transparent
        The color pad fills with.
```

## retime

```text
retime: Resamples the animation to framesPerSecond, as ffmpeg's fps filter: fixed ticks, each showing the frame visible when it starts, with neighbours that show the same frame merged.

  retime <frames-per-second>
    --frames-per-second <number>, required
        The rate, more than 0 and at most 1,000.
```

## reverse

```text
reverse: Plays the animation backwards; each frame keeps its delay and metadata.

  reverse no arguments
```

## rotate

```text
rotate: Rotates the image clockwise.

  rotate <degrees> [--filter <choice>] [--background <color>]
    --degrees <number>, required
        The angle, clockwise.
    --filter <bicubic|nearest|box|bilinear|mitchell|lanczos3|lanczos2|hermite|gaussian|spline>, default bicubic
        The resampling kernel for angles that are not multiples of 90°.
    --background <color>
        The color of the canvas outside the rotated image.
```

## rotational-blur

```text
rotational-blur: Spins the image around center, as ImageMagick's -rotational-blur: each pixel averages samples along an arc of degrees centered on it, about one per pixel of arc (at most 256), read bilinearly.

  rotational-blur <degrees> [--center <x,y>]
    --degrees <number>, required
        The arc in degrees, from 0 (changes nothing) to 360.
    --center <x,y>
        The center in pixels, pixel centers at +0.5, or null for the image's center.
```

## saturation

```text
saturation: Mixes each color with its luma: 1 changes nothing, 0 gives gray, above 1 oversaturates.

  saturation <amount>
    --amount <number>, required
        The factor, 0 or more.
```

## sepia

```text
sepia: Tints toward sepia.

  sepia [--amount <number>]
    --amount <number>, default 1
        0 changes nothing, 1 gives full sepia.
```

## shade

```text
shade: Shades the image as ImageMagick's -shade does: the luminance is a height field, and each pixel becomes the Lambertian shading of its surface normal by a distant light at azimuth and elevation, a gray relief; with colored the shading multiplies the image's own colors instead, as +shade does.

  shade [--azimuth <number>] [--elevation <number>] [--colored <bool>]
    --azimuth <number>, default 30
        The light's direction in the image plane, in degrees counterclockwise from the right with y up, as ImageMagick's.
    --elevation <number>, default 30
        The light's angle above the image plane, in degrees, from 0 to 90; flat areas get sin(elevation).
    --colored <bool>, default false
        Whether the shading multiplies the colors (+shade) rather than replacing them with gray (-shade).
```

## sharpen

```text
sharpen: Sharpens with an unsharp mask: out = src + amount × (src − Blur(sigma)).

  sharpen [--amount <number>] [--sigma <number>]
    --amount <number>, default 1
        How strongly to sharpen; 0 changes nothing.
    --sigma <number>, default 1
        The blur radius that sets which details are sharpened, in pixels.
```

## shuffle-channels

```text
shuffle-channels: Rearranges the working RGBA channels: each output channel takes the input channel, or the constant, its argument names.

  shuffle-channels <red> <green> <blue> <alpha>
    --red <red|green|blue|alpha|zero|one>, required
        Where the output's red comes from.
    --green <red|green|blue|alpha|zero|one>, required
        Where the output's green comes from.
    --blue <red|green|blue|alpha|zero|one>, required
        Where the output's blue comes from.
    --alpha <red|green|blue|alpha|zero|one>, required
        Where the output's alpha comes from.
```

## sigmoidal-contrast

```text
sigmoidal-contrast: Adds (or removes) contrast with ImageMagick's scaled logistic curve, which never clips.

  sigmoidal-contrast <contrast> [--midpoint <number>] [--increase <bool>]
    --contrast <number>, required
        How steep the curve is, 0 or more: 3 is gentle, 10 strong.
    --midpoint <number>, default 0.5
        Where the curve is steepest, from 0 to 1.
    --increase <bool>, default true
        Whether contrast increases (the default) or decreases.
```

## simulate-color-blindness

```text
simulate-color-blindness: Shows how the image looks with a color vision deficiency.

  simulate-color-blindness <kind> [--severity <number>]
    --kind <protanopia|deuteranopia|tritanopia|achromatopsia>, required
        The deficiency.
    --severity <number>, default 1
        From 0 (normal vision) to 1.
```

## skew

```text
skew: Shears the image about its center: vertical lines lean by degreesX (x moves by y × tan) and horizontal lines by degreesY.

  skew <degrees-x> <degrees-y>
    --degrees-x <number>, required
        The horizontal shear angle, strictly between −90 and 90.
    --degrees-y <number>, required
        The vertical shear angle, strictly between −90 and 90.
```

## solarize

```text
solarize: Inverts red, green and blue values above threshold, as ImageMagick's -solarize.

  solarize [--threshold <number>]
    --threshold <number>, default 0.5
        The level, from 0 to 1.
```

## spread

```text
spread: Scatters pixels, as ImageMagick's -spread does: each pixel takes the value of a random pixel at most radius away (a whole-pixel offset in the disc; edges repeat the edge pixel).

  spread <radius> [--seed <integer>]
    --radius <number>, required
        The farthest a value moves, in pixels, from 0 to 1000; below 1 nothing moves.
    --seed <integer>, default 0
        Picks the pattern; the same seed gives the same result.
```

## threshold

```text
threshold: Makes a two-color image: pixels above level take above, the others below, with alpha kept.

  threshold [--level <number>] [--mode <choice>] [--below <color>] [--above <color>]
    --level <number>, default 0.5
        The level, from 0 to 1.
    --mode <luminance|channels|saturation|max-chroma>, default luminance
        What is measured.
    --below <color>
        The lower color, or null for black.
    --above <color>
        The upper color, or null for white.
```

## tile

```text
tile: Makes a new width × height canvas filled by repeating the image, or the source rectangle of it, with one copy's top-left corner at origin, as Skia's Tile filter and ImageMagick's tile: fill do.

  tile <width> <height> [--origin <x,y>] [--source <x,y,w,h>]
    --width <int>, required
        The canvas width, at least 1.
    --height <int>, required
        The canvas height, at least 1.
    --origin <x,y>
        Where one copy's top-left corner goes; copies repeat from there in every direction.
    --source <x,y,w,h>
        The rectangle to repeat, inside the image, or null (the default) for the whole image.
```

## tint

```text
tint: Pushes midtones toward color and keeps black and white, as ImageMagick's -tint.

  tint <color> [--amount <number>]
    --color <color>, required
        The tint.
    --amount <number>, default 1
        The strength, from 0 to 1.
```

## tone-map

```text
tone-map: Maps HDR linear light into 0..1: pixel values are read as linear light with reference white at 1.0 (as RgbaF32, RgbaF16 and RgbF32 rasters hold PQ and HLG images converted to a linear encoding), and the tone curve brings the brightest down to 1 on max(R, G, B), which keeps hue.

  tone-map [--method <choice>] [--source-peak-nits <number>]
    --method <bt2390|reinhard|clip>, default bt2390
        The curve.
    --source-peak-nits <number>
        The brightest light in the image, in cd/m² (reference white is 203), or null to take it from the metadata's Hdr (MaxCLL, then the mastering peak), else 1,000.
```

## trim

```text
trim: Removes the border: the rows and columns at the edges whose pixels all lie within tolerance of the border color, by ImageMagick's -fuzz distance, so the rectangle equals ImageMagick's -trim.

  trim [--tolerance <number>] [--color <color>]
    --tolerance <number>, default 0
        The color distance still counted as border, from 0 (the exact color) to 1 (every color).
    --color <color>
        The border color, or null (the default) for the top-left pixel's.
```

## vignette

```text
vignette: Darkens (or tints) the image toward its corners, outside an ellipse fitted to the image, with a soft edge: the shape of ImageMagick's -vignette, which blurs a drawn ellipse.

  vignette [--radius <number>] [--softness <number>] [--color <color>] [--center <x,y>]
    --radius <number>, default 1
        The ellipse's size as a fraction of the half-diagonal: at 1 the ellipse passes through the corners, at 0.7 it is about as wide as the image.
    --softness <number>, default 0.5
        The share of the radius over which the edge fades, from 0 (hard) to 1: the fade is a Gaussian-blurred edge whose ±2σ spans softness × radius.
    --color <color>
        The color toward the corners, with its alpha as its strength; black and opaque when null.
    --center <x,y>
        The ellipse's center in the raster's pixel coordinates (0, 0 is the top-left corner of the image); the image's middle when null.
```

## white-balance

```text
white-balance: Applies WhiteBalance: corrects for a light of temperature kelvin.

  white-balance <temperature> [--tint <number>]
    --temperature <number>, required
        The light's correlated color temperature, from 1,500 to 15,000 kelvin; 6504 changes almost nothing.
    --tint <number>, default 0
        The light's offset from a black body's color, from −1 to 1; positive for a greener light.

  white-balance <neutral>
    --neutral <color>, required
        A color that should be gray, such as a gray card's pixels.
```

## zoom-blur

```text
zoom-blur: Streaks the image along rays from center, as a zoom during the exposure (Photoshop's radial zoom blur): each pixel averages samples on the ray through it, spanning amount of its distance from the center, half inward and half outward, about one per pixel (at most 256).

  zoom-blur <amount> [--center <x,y>]
    --amount <number>, required
        The streak's length as a fraction of the distance from the center, from 0 (changes nothing) to 1.
    --center <x,y>
        The center in pixels, pixel centers at +0.5, or null for the image's center.
```

## save

```text
save [path] [--format f] [--quality q] [--lossless] [--effort e] [--bit-depth b] [--strip] [--set fmt.key=value]
```

Writes the image as it is at that point. A plan may save more than once; a save without a path writes to `-o`.
