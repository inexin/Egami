# Encoding Opus

Opus (RFC 6716) is the audio codec of WebM and of .opus files, and the best general-purpose lossy codec for both speech and
music. Egami.Video encodes it fully managed: the encoder is translated from libopus 1.5.2 and gives the same packets, byte for
byte, in every mode, so the quality and the bitrate are libopus's.

- SILK (speech), CELT (music) and hybrid frames, chosen per frame from the content and the bitrate, as libopus chooses.
- 1 to 8 channels: mono and stereo in one stream (mapping family 0), and 3 to 8 channels as surround streams in Vorbis channel
  order (mapping family 1, up to 7.1), with libopus's surround masking and its bitrate split between streams.
- 8, 12, 16, 24 and 48 kHz coded directly; other rates are resampled to 48 kHz first.
- Variable, constrained variable and constant bitrate, frames of 2.5 to 120 ms, in-band FEC and DTX.

## Writing .opus files

`AudioWriter` writes Opus by default for Ogg (.opus, .ogg, .oga) and WebM files; set `AudioWriterOptions.Codec` to
`AudioCodecs.Opus` for Matroska (.mka, .mkv) and MP4. `AudioWriterOptions.Opus` takes an `OpusEncoderOptions`; leave it
out for the defaults, which suit music and most speech.

[!code-csharp[](../../../tests/Egami.DocSamples/Audio.cs#audio-opus-encode)]

The packets are timed at 48 kHz, as Opus always decodes. The encoder looks 6.5 ms ahead (2.5 ms with
`OpusApplication.LowDelay`), so the stream starts with that much extra audio: the pre-skip, which the header (OpusHead) and
the track's `CodecDelay` record and every decoder drops. At the end the encoder codes silence until the look-ahead is out, and
the last packet's duration says where the input ended: Ogg writes it as the last granule position, Matroska and WebM as
DiscardPadding and MP4 as the edit list. `AudioReader`, opusdec and FFmpeg give back exactly the samples written (FFmpeg
ignores the end of an MP4 edit list, so it keeps the padding of an MP4 file).

## Settings

| `OpusEncoderOptions` | Default | What it does |
| --- | --- | --- |
| `Bitrate` | 64 kbit/s per channel | Bits per second for all channels together. Opus codes 6 to 510 kbit/s for stereo; 24 kbit/s is good speech, 64 to 96 kbit/s stereo is transparent for most music. |
| `RateMode` | `Vbr` | `Vbr` spends bits where the audio needs them, so the average follows the content; `ConstrainedVbr` keeps a short window near the bitrate, for streaming; `Cbr` gives every packet the same size. |
| `Application` | `Audio` | `Audio` codes for fidelity; `Voice` favours SILK and intelligibility; `LowDelay` turns SILK off and cuts the look-ahead to 2.5 ms. |
| `FrameDuration` | 20 ms | 2.5, 5, 10, 20, 40, 60, 80, 100 or 120 ms. Short frames lower the delay and cost bits; long ones save a little on speech. |
| `Effort` | 10 | 0 to 10, libopus's complexity. Below 10 the tonality analysis is off and the searches are simpler. |
| `Bandwidth` | automatic | The widest band coded, from `OpusBandwidth.Narrowband` (4 kHz) to `Fullband` (20 kHz). |
| `InbandFec` | off | Speech packets carry a coarse copy of the packet before, which a decoder uses when that one is lost. |
| `ExpectedLoss` | 0 % | The share of packets expected to be lost, which the encoder protects against. |
| `Dtx` | off | Speech silence is sent as one tiny packet every 400 ms. |

With `Vbr` the bitrate is an average over typical content, not a cap: a clip of pure tones may come out 20 % over it and
noise 15 % under, exactly as with libopus. Use `ConstrainedVbr` when the rate must hold over each second.

## The encoder by hand

`AudioCodecs.Opus.CreateEncoder` gives the `IAudioEncoder` the writer uses. `AudioEncoderOptions.Opus` carries the settings;
`FrameSize` and `Level` are not used by Opus.

[!code-csharp[](../../../tests/Egami.DocSamples/Audio.cs#audio-opus-cbr)]

## Transcoding to WebM

WebM holds only Opus and Vorbis audio. `MediaFile.Transcode` with no codec and a WebM output copies Opus and Vorbis tracks and
encodes the others in Opus, so a Matroska or MP4 file with FLAC or PCM audio becomes a valid WebM. Audio that no registered
codec decodes is copied, and the WebM writer refuses it.

[!code-csharp[](../../../tests/Egami.DocSamples/Audio.cs#audio-opus-webm)]

## Compared with libopus and FFmpeg

The packets are libopus 1.5.2's, so files match what `opusenc` or `ffmpeg -c:a libopus` make with the same settings, apart from
the container. FFmpeg's own `opus` encoder is a different, CELT-only encoder.
