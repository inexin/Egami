# Audio

Egami.Video decodes and encodes audio with the same send and receive loop as video. It is fully managed and ships:

- Codecs: FLAC (RFC 9639) decode and encode, integer and float PCM, and Opus (RFC 6716) decode.
- Containers: WAV with RF64 and BW64 for files over 4 GB, Sony Wave64, native FLAC and Ogg, read and written; FLAC, PCM
  and Opus in Matroska, WebM and MP4 too.
- A resampler between any two rates, sample format conversion with dither, and channel layouts with downmix and upmix.

Vorbis and other audio tracks are copied through untouched.

## Frames

An `AudioFrame` holds `SampleCount` samples for every channel of its `Layout`, its `SampleRate`, its `Format` (a
`SampleFormat`) and the `Timestamp` of its first sample. Frame memory comes from the context's memory source; dispose each
frame. `AsSpan<T>(channel)` reads a channel of a planar frame, or every channel of an interleaved one; `GetSpan<T>` writes.

[!code-csharp[](../../../tests/Egami.DocSamples/Audio.cs#audio-frames)]

`SampleFormat` has unsigned 8-bit, signed 16, 32 and 64-bit, and 32 and 64-bit float samples, each interleaved or planar.
Integer frames say how many low bits they use in `ValidBits`: 24-bit audio is `S32` with 24 valid bits, so WAV and FLAC
round trip exactly. `Convert` and `CopyTo` change the format, with triangular dither when `AudioConvertOptions.Dither` is
set; `SampleFormatExtensions` gives the size and kind of each format.

[!code-csharp[](../../../tests/Egami.DocSamples/Audio.cs#audio-convert)]

## Reading audio

`AudioReader` opens a file and decodes its first audio track (or the one `AudioReaderOptions.TrackNumber` names). Frames come
as planar float by default; `AudioReaderOptions` asks for another `SampleFormat` (or `null` for the codec's own), another
`SampleRate` (resampled, with a `ResamplerQuality`) and another `Layout` (mixed). Codec delay and the packets an MP4 edit list
leaves out are trimmed, so the audio has exactly the samples the encoder was given; `KeepAllSamples` keeps them. `Seek` is
sample exact.

[!code-csharp[](../../../tests/Egami.DocSamples/Audio.cs#audio-read)]

## Writing audio

`AudioWriter` encodes frames into a file or stream; the container comes from the extension. `AudioWriterOptions` sets the
`Codec` (FLAC for .flac, PCM elsewhere by default), the `SampleRate` and `Layout` to write (converted when the frames differ),
the `BitsPerSample` and the compression `Level`. Frames may have any length and format.

[!code-csharp[](../../../tests/Egami.DocSamples/Audio.cs#audio-write)]

`VideoWriterOptions.AudioCodec`, with `AudioSampleRate` and `AudioLayout`, adds an audio track to a `VideoWriter`, which
`WriteAudio` fills.

[!code-csharp[](../../../tests/Egami.DocSamples/Audio.cs#audio-video)]

`MediaFile.Transcode` copies a file like `MediaFile.Remux` but decodes its audio and encodes it again with another codec,
keeping video and the other tracks as they are.

[!code-csharp[](../../../tests/Egami.DocSamples/Audio.cs#audio-transcode)]

## Channel layouts and mixing

A `ChannelLayout` lists the `Speakers` its channels feed, in WAVE_FORMAT_EXTENSIBLE order, which every decoder hands out
whatever order its codec stores. Presets run from `Mono` to `Surround714`; `Unpositioned` is for ambisonic or unknown channels,
and `ForChannelCount` gives the layout WAV and FLAC assume when a file names none.

[!code-csharp[](../../../tests/Egami.DocSamples/Audio.cs#audio-layouts)]

`ChannelMixer` mixes one layout into another with a matrix. The default one follows ITU-R BS.775: center and surrounds at
-3 dB, LFE dropped unless given a level, and upmixing only routes the speakers both layouts share. A matrix of your own works
too.

[!code-csharp[](../../../tests/Egami.DocSamples/Audio.cs#audio-mix)]

## Resampling

`Resampler` converts between any two rates with a polyphase windowed-sinc filter. `ResamplerQuality.Good`, the default, is
flat within 0.1 dB to 0.9 of the lower Nyquist frequency and 100 dB down above it; `Fast` and `Best` (140 dB) trade speed
for steepness. Time stays exact: the first output sample has the first input sample's time, and the output has the input's
length at the new rate.

[!code-csharp[](../../../tests/Egami.DocSamples/Audio.cs#audio-resample)]

## Codecs by hand

An `AudioCodec` creates an `IAudioDecoder` for a track and an `IAudioEncoder` from `AudioEncoderOptions`; `AudioCodecs`
finds them in a `MediaContext`. `PcmCodec` has little-endian, big-endian and float instances; `FlacCodec` encodes at levels 0
to 8 with the reference encoder's meaning, default 5, and sizes within 1 % of libFLAC's. Decoders take
`AudioDecoderOptions`: `Errors` as a `DecodeErrorMode`, and `VerifyChecksums` for FLAC's frame CRCs and STREAMINFO MD5
(reported as `Flac.BadCrc` and `Flac.Md5Mismatch`). `IAudioDecoder.SendLoss` reports a lost packet.

[!code-csharp[](../../../tests/Egami.DocSamples/Audio.cs#audio-encode)]

[!code-csharp[](../../../tests/Egami.DocSamples/Audio.cs#audio-codecs)]

## Opus

`OpusCodec` decodes every Opus stream: SILK (speech), CELT (music) and hybrid frames at every bandwidth, mono, stereo and the
multichannel mappings of RFC 7845 (families 0, 1 and 255). Frames come out as planar float at 48 kHz, Opus's own rate, with the
channels in the canonical order; ask `AudioReaderOptions.SampleRate` for another rate. The pre-skip (the track's
`CodecDelay`) and the end padding are trimmed. A lost packet, reported with `IAudioDecoder.SendLoss` or seen as a gap in the
timestamps, is concealed, or recovered from the next packet's in-band FEC when it carries some. The decoder passes the RFC
8251 test vectors.

[!code-csharp[](../../../tests/Egami.DocSamples/Audio.cs#audio-opus)]

## Containers

`WavFormat.Instance` reads WAV, RF64 and BW64 (PCM, float, WAVE_FORMAT_EXTENSIBLE with its channel mask, and streamed files
whose sizes were never filled in), and writes WAV that turns into RF64 by itself past 4 GB. `WavFormat.Wave64` is Sony
Wave64. LIST/INFO and id3 tags become `MediaInfo.Tags`, and cue points become chapters. `FlacFormat` reads and writes native
FLAC: Vorbis comments as tags, pictures as attachments (cover.* for the front cover), and a seek table every 10 seconds.

[!code-csharp[](../../../tests/Egami.DocSamples/Audio.cs#audio-containers)]

`OggFormat` reads and writes Ogg (.ogg, .opus, .oga) with Opus, Vorbis and FLAC. Timestamps come from the granule positions,
seeking from an index built in one pass over the page headers, and pages with a bad CRC are skipped with an `ogg-crc`
warning. A chained file (one stream after another) reads as one track when the settings stay the same, each link's TITLE a
chapter; a link that changes them ends the track with an `ogg-chained-stream` warning.

[!code-csharp[](../../../tests/Egami.DocSamples/Audio.cs#audio-ogg)]
