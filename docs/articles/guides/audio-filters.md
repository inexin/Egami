# Audio filters

Egami.Video's audio filters change level, time, tone and placement of decoded audio as it streams: gain and gain envelopes,
fades and crossfades, mixing and panning, delay, padding, trimming and new timestamps, tempo and speed changes, EQ, dynamics
(compressor, gate and limiter), loudness normalization and silence removal. They live in
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

## EQ

`Biquad` is a second-order filter from the Audio EQ Cookbook in one of eight `BiquadKind`s: low and high pass, band pass,
notch, all pass, peaking and low and high shelves. Its width is a Q (0.7071, `Biquad.ButterworthQ`, by default) or, with
`Biquad.FromBandwidth`, a bandwidth in octaves; peaking and shelves take a `Gain` in decibels. `Butterworth` is a steeper low
or high pass, order 2 to 8 (12 to 48 dB per octave), flat in the pass band and -3 dB at its corner. `ParametricEqualizer` runs
a list of `EqualizerBand`s as one filter. All of them run in double precision in place, and refuse a frequency at or above half
the sample rate.

[!code-csharp[](../../../tests/Egami.DocSamples/AudioFilters.cs#audio-equalizer)]

## Dynamics

`Compressor` turns the level down above a `Threshold` by a `Ratio`, with a soft `Knee`, `Attack` and `Release` times and
`Makeup` gain; `Gate` turns it down below a threshold, by up to its `Range`, to keep noise out of the pauses. Both measure the
loudest channel (`LevelDetection.Rms` by default, or `Peak`) and give every channel the same gain. `Limiter` looks 5 ms ahead,
so no sample goes over its `Ceiling`; with `TruePeak` it also catches the peaks between samples by oversampling, as ITU-R
BS.1770 measures them. Its output is held back by its `Latency` and keeps the input's timestamps.

[!code-csharp[](../../../tests/Egami.DocSamples/AudioFilters.cs#audio-dynamics)]

With `Sidechain` on, a compressor takes its level from another signal given to `SendSidechain`, lined up by timestamps, so
music ducks under a voice. Input waits until the sidechain covers it; `CompleteSidechain` says no more comes.

[!code-csharp[](../../../tests/Egami.DocSamples/AudioFilters.cs#audio-ducking)]

## Loudness

`LoudnessNormalizer` brings audio to a `LoudnessTarget`: an integrated loudness, a true-peak ceiling and, for the dynamic mode,
a loudness range. The presets are `EbuR128` (-23 LUFS, -1 dBTP, the default), `AtscA85` (-24, -2), `Streaming` (-14, -1) and
`Podcast` (-16, -1). Given a first pass's `LoudnessReport` from `LoudnessMeter`, it is linear: one gain, so the dynamics stay
as they are, and a true-peak `Limiter` only when that gain would take the peaks over the ceiling. `AudioFilters.NormalizeLoudnessAsync`
runs both passes over every audio track of a file, copying the video and other tracks. `LoudnessNormalizer.Dynamic` works in one
pass, for input that cannot be read twice: it follows the loudness with 1.5 s of lookahead and keeps it within the target's
range, so it changes the dynamics and lands less exactly on the target.

[!code-csharp[](../../../tests/Egami.DocSamples/AudioFilters.cs#audio-loudness)]

`AudioFilters.ApplyAsync` runs filters of your own on every audio track of a file the same way. Filtered tracks are encoded
with the codec you give, else their own when the container holds it, else the container's usual one.

## Silence

`SilenceRemover` cuts silence out: a sample is silent when the loudest channel's RMS over 20 ms is under its `Threshold`
(-50 dBFS by default). `SilenceRemoval.Trim` cuts the start and the end; `SilenceRemoval.Shorten` also cuts pauses longer than
`MaxGap` (1 s) to `KeepGap` (0.3 s). Every cut fades or crossfades over 10 ms. The result is shorter than the input and its
timestamps run on without gaps, so use it on audio alone, not on a track that has to stay in step with video.

[!code-csharp[](../../../tests/Egami.DocSamples/AudioFilters.cs#audio-silence)]
