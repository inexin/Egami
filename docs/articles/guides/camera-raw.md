# Developing camera RAW

A camera RAW file holds what the sensor recorded: one colour per photosite, linear light, the camera's own colour space and
noise. Developing it is a series of choices that a camera makes for its JPEGs: white balance, exposure, contrast, noise
reduction and lens corrections. Egami.Formats.Extended makes them as it loads, so `Raster.Load` returns a finished image, and
`RawReaderOptions` lets you make them differently. The [Camera RAW format page](../formats/raw.md) lists every format, option
and detail; this guide is about choosing them.

Register the format once with `WithRaw()` or `WithExtendedFormats()` ([Extended formats](../formats/extended.md)).

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#raw-load)]

## Pick what to load

A full develop of a 24-megapixel file takes about a second; much of the time you need less.

| Need | Load | Cost |
| --- | --- | --- |
| A finished photo | The default develop | About 1 s for 24 MP on 8 cores |
| A gallery, contact sheet or culling view | `ReaderOptions.MinimumSize`: the smallest embedded preview, else a half-size develop, that covers it | Milliseconds for a preview |
| A quick look at the real data | `RawOutput.HalfSize`: no demosaicing | About a quarter of a develop |
| The camera's own rendering | `RawOutput.Preview`: the embedded JPEG, as the camera made it | Milliseconds |
| A pipeline of your own | `RawOutput.Mosaic`: the stored sensor values, with the levels and pattern in `RawDetails` | The decode alone |

Canon CR3 files give their previews and metadata only: their sensor data uses a codec Egami does not decode yet, so load them
with `RawOutput.Preview`.

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#raw-thumbnails)]

## Colour and white balance

As shot, the default, uses the white balance the camera recorded, which is right far more often than an automatic guess.
`RawWhiteBalance.Auto` assumes the scene averages to gray, which suits mixed light and scanned film; `Custom` takes a
temperature in kelvin and a tint, the numbers a photo editor's sliders show. Colours come from the file's camera profile, or
for maker formats from Egami's camera table, measured from Adobe DNG Converter; `RawDetails.WhiteBalance`, `Temperature` and
`Tint` say what the develop used, so a batch can repeat it exactly.

Render straight into a wide-gamut space with `ConvertColorTo` (Display P3, Adobe RGB, Rec. 2020 or ProPhoto): the develop
writes into it directly, so saturated colours are not clipped to sRGB on the way.

## Exposure, highlights and contrast

`Exposure` is in stops, on top of the camera's own baseline exposure. When a bright sky clips, `RawHighlightMode.Blend` keeps it
from turning a colour, and `Rebuild` brings back detail from the channels that did not clip, which shows best with a negative
exposure.

By default the develop is scene-linear: the output's transfer function is the only curve, which suits editing and compositing
but looks flat next to a camera's JPEG. `DefaultToneCurve = true` adds the contrast curve of Adobe Camera Raw's default
rendering for files whose profile has none (all maker formats): midtones lift and highlights roll off. A DNG whose profile has
its own curve keeps that one.

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#raw-develop)]

## Noise and lenses

Colour noise reduction is on at 25, which removes the coloured blotches of high-ISO shots without softening detail; luminance
noise reduction is off, since it trades fine detail for smoothness. Raise it to 20 or 30 for ISO 6400 and up.

Lens corrections are on. A DNG that carries its own corrections uses them. For other files, Egami looks for a lens profile in
the folders `LensProfiles` names, a system Lensfun install and the folders Adobe's software installs its profiles in; Egami
ships no profiles of its own. `AutoChromaticAberration` measures colour fringing from the image itself when no profile covers
the lens; it costs about as much as the demosaic, so it is off by default.

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#raw-lens)]

## Batches from the command line

The `egami` tool develops RAW inputs in every command, and the `--raw-*` options set the develop:

```text
egami convert *.NEF -o "jpeg/{name}.jpg" --quality 90 --raw-tone-curve --raw-exposure +0.3
egami thumbnail *.CR3 --size 512 --raw-output preview
```

## Memory

A develop works in 32-bit floats: a 24-megapixel file needs well under 1 GB while it develops, a 100-megapixel one more than the 2 GB
`MaxTotalBytes` allows by default, so raise the limit for medium-format files, or load a preview or a half-size develop.
