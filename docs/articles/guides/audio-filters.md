# Audio filters

Egami.Video's audio filters change level, time and placement of decoded audio as it streams: gain and gain envelopes, fades
and crossfades, mixing and panning, delay, padding, trimming and new timestamps, and tempo and speed changes. They live in
`Egami.Video.Audio.Filters` and work on the `AudioFrame`s that `AudioReader` and the decoders hand out (see [Audio](audio.md)).

## The filter loop

Every filter is an `IAudioFilter` with the same calls as `Resampler`: `Send` a frame, `TryReceive` the frames that are ready,
`Flush` at the end of the input and `Reset` after a seek. `Latency` says how much input a filter holds back, such as the
lookahead of `Tempo`. Frames of any sample format go in and are converted once to planar float; frames come out as
`SampleFormat.F32Planar` with exact timestamps. The filters that keep the length work on the frame in place, so a steady
stream allocates no sample memory. Times you give a filter, such as where a fade starts or a trim cuts, are the frames' own
timestamps, not counted from the first sample, as in FFmpeg: audio that starts at 1.4 s fades at 1.4 s only if you say so.
Put a `Retime` first to count from zero.

`AudioFilterChain` runs several filters on one input in one pass and is itself a filter. It owns the filters added to it.
Egami.Video's filters derive from `AudioFilter`, which takes the rate and channels from the first frame and refuses frames
that differ; to put a filter of your own in a chain, implement `IAudioFilter`.

[!code-csharp[](../../../tests/Egami.DocSamples/AudioFilters.cs#audio-filter-chain)]

## Gain

`Gain` changes the level by a fixed number of `Decibels` or a linear `Factor`. Changing either between frames moves to the new
gain over 5 ms, so it does not click; unity gain passes samples through unchanged. Its `Envelope`, a `GainEnvelope`, changes
the gain over time: a list of `Keyframe`s on the frames' time line, values in decibels, each with an `Easing` (linear, ease
in, ease out, ease in and out, or hold) to the next. The same `Keyframe` type animates layers in video editing.

[!code-csharp[](../../../tests/Egami.DocSamples/AudioFilters.cs#audio-gain-envelope)]

## Fades and crossfades

`AudioFade` fades in (`FadeDirection.In`, silent before its start) or out (`FadeDirection.Out`, silent after its end) over a
stretch of the time line, and reaches full level or silence on the exact sample. `FadeCurve` has five shapes with the same
gains as FFmpeg's `afade` curves: `Linear` (`tri`, the default), `EqualPower` (`qsin`), `Logarithmic` (`log`),
`Exponential` (`exp`) and `SCurve` (`hsin`).

[!code-csharp[](../../../tests/Egami.DocSamples/AudioFilters.cs#audio-fades)]

`Crossfade` joins two streams with an overlap, like FFmpeg's `acrossfade`: the first stream's end fades out as the second's
start fades in, `EqualPower` by default so unrelated material keeps its loudness. It has two inputs, so it takes
`Send(input, frame)` and `Complete(input)` instead of the single-input calls.

[!code-csharp[](../../../tests/Egami.DocSamples/AudioFilters.cs#audio-crossfade)]

## Mixing and panning

`AudioMixer` adds several inputs, each times its weight (`SetWeight`), and places each by its timestamps, so a clip that starts
later comes in at its time. Inputs at another rate or with other channels are resampled and remixed on the way in. It does not
divide by the number of inputs unless `Normalize` is set, so a voice over music keeps its level. `MixDuration` says when the
mix ends: with the longest input (the default), the shortest, or input 0.

`Pan` places a mono input in stereo with the constant-power law (-3 dB in each channel at the centre), turns one side of a
stereo input down with `Position` or `Balance`, swaps the channels and flips their polarity. For any other channel matrix use
`ChannelMixer`.

[!code-csharp[](../../../tests/Egami.DocSamples/AudioFilters.cs#audio-mixer)]

## Timing

`Delay` puts silence in front of each channel, by one delay or one per channel, and hands out the delayed tail on `Flush`.
`Pad` adds silence at the end, a fixed amount or up to a length (`Pad.ToLength`). `Trim` keeps the part between two times, cut
on the exact sample, and keeps its timestamps; `Retime` gives the stream new timestamps that start at a time and run on without
gaps.

[!code-csharp[](../../../tests/Egami.DocSamples/AudioFilters.cs#audio-timing)]

## Tempo and speed

`Tempo` plays faster or slower and keeps the pitch, from a quarter of the speed to four times, with WSOLA (waveform similarity
overlap-add). `TempoPreset.Music` uses longer windows for steady rhythm and low notes, `TempoPreset.Speech` shorter ones for
speech. The output lasts the input's length divided by the factor, to the sample. `Speed` changes speed and pitch together by
resampling, like a tape played faster, and costs less.

[!code-csharp[](../../../tests/Egami.DocSamples/AudioFilters.cs#audio-tempo)]
