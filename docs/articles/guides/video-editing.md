# Video editing

Egami.Video's editing library, in `Egami.Video.Editing`, joins clips with transitions, draws clips over clips, keys out green
screens, lays clips side by side and runs Egami operations on the result, encoding it all again in one pass. An edit is a
description: `VideoEdit` holds an `EditCanvas`, a main `EditTrack` of `Clip`s with a `Transition` between each two, `Layer`s
drawn over it and `EditEffect`s. Nothing is decoded until it renders, and then only the frames in use are held. The
`egami-video` commands `join`, `fade`, `overlay`, `pip`, `stack` and `edit` build the same edits (see
[the command line](../video-cli/index.md#editing)).

## Joining clips

Clips play one after another; each `TrackItem` on the track is a clip and the transition into it. They may differ in size, frame rate, pixel format and sound: the canvas sets the output's size,
frame rate and audio format (from the first clip when there is no canvas), each clip is fitted with `ClipFit` (`Contain`
with bars by default, `Cover`, `Stretch` or `None`) over the canvas `Background`, each output frame shows the source frame
nearest its time, and the sound is resampled and remixed; a clip without sound adds silence. `Clip.Trim` cuts a clip on the
exact frame and sample, and `FadeIn` and `FadeOut` fade the picture from and to a colour and the sound from and to silence.
`Render` writes the result with a `VideoWriter`, whose settings `EditRenderOptions.Writer` carries; the sound goes into Opus
unless another audio codec is set.

[!code-csharp[](../../../tests/Egami.DocSamples/Editing.cs#edit-join)]

`Plan` lays the timeline out in whole frames before anything is decoded and returns an `EditPlan`: every `PlannedClip` and
`PlannedLayer` with its frames, the length, and warnings about what it fixed up. An edit that cannot render, such as one with
a trim past a clip's end or a transition longer than a clip, throws with every reason. `ToJson` and `VideoEdit.FromJson`
save and load an edit as the JSON file the command line's `edit` runs.

[!code-csharp[](../../../tests/Egami.DocSamples/Editing.cs#edit-plan)]

## Transitions

A `Transition` overlaps the two clips it joins by its `Duration`, so the edit gets shorter by it, and crossfades their sound
over the same time (`AudioCurve`, equal power by default). `TransitionKind` has ten built-in kinds: `Fade` (a cross-dissolve),
`FadeThroughColor`, `Dissolve`, `Wipe` and `Slide` in a `TransitionDirection`, `CircleOpen`, `CircleClose`, `Clock`, `Zoom`,
`Pixelize` and `Blur`. A wipe can have a soft edge (`Softness`), and any transition can ease its progress (`Easing`). The
fades and wipes match FFmpeg's `xfade` to within its rounding and one pixel of the edge.

[!code-csharp[](../../../tests/Egami.DocSamples/Editing.cs#edit-transitions)]

An app's own transition implements `ITransition`, which draws one frame from the two clips' frames and the progress:

[!code-csharp[](../../../tests/Egami.DocSamples/Editing.cs#edit-custom-transition)]

[!code-csharp[](../../../tests/Egami.DocSamples/Editing.cs#edit-blinds)]

## Layers

A `Layer` draws a clip over the main track: placed in a rectangle (`Place(Rect)`) or at an `Anchor` with a margin and a scale,
with an opacity and an Egami `BlendMode`, inside a time window (`Between`), with fades. `Animate` moves its `LayerProperty`
values (`X`, `Y`, `Scale`, `Rotation` and `Opacity`) through `Keyframe`s, timed from the layer's start. A layer ends with its
clip, or holds the last frame (`HoldLastFrame`). Its sound is mixed in unless its clip is muted.

[!code-csharp[](../../../tests/Egami.DocSamples/Editing.cs#edit-layers)]

`Layer.Key` runs a `ChromaKey` on the layer's frames: Egami's chroma key takes out the pixels near the screen colour in the
CbCr plane, so uneven lighting on the screen matters little, and despill takes the screen's colour out of hair and edges.

[!code-csharp[](../../../tests/Egami.DocSamples/Editing.cs#edit-chroma-key)]

## Stacks

`Stack.Horizontal`, `Stack.Vertical` and `Stack.Grid` (the three `StackLayout`s) make one clip of several shown at once, like FFmpeg's `hstack`,
`vstack` and `xstack`. `StackOptions` sets a gap, the background, how grid cells fit, whether the stack ends with its
shortest clip, and whether the sound is the first clip's or every clip's mixed. A stack is a clip, so it can be trimmed,
faded and joined with a transition.

[!code-csharp[](../../../tests/Egami.DocSamples/Editing.cs#edit-stack)]

## Effects

`EditEffect.Apply` runs Egami operations or any `IRasterOperation` on each frame, and `EditEffect.Of` runs an app's own
`IFrameEffect`, which gets the frame's time and its progress through the effect's window in a `FrameEffectContext`. Effects
attach to the whole edit (`VideoEdit.Effects`), to a clip (`Clip.WithEffect`) or to a layer (`Layer.WithEffect`), and
`Between` limits one to a time window. An effect keeps the frame size.

[!code-csharp[](../../../tests/Egami.DocSamples/Editing.cs#edit-effects)]

[!code-csharp[](../../../tests/Egami.DocSamples/Editing.cs#edit-darken)]
