# Audio

Egami.Video decodes and encodes audio with the same send and receive loop as video. It is fully managed and ships:

- Codecs: FLAC (RFC 9639) decode and encode, integer and float PCM, Opus (RFC 6716) decode and encode, Apple Lossless
  (ALAC) decode and encode, Vorbis I decode, MPEG audio (MP3, MP2 and MP1) decode, Dolby AC-3 (ATSC A/52) decode and G.711
  μ-law and A-law decode.
- Containers: WAV with RF64 and BW64 for files over 4 GB, Sony Wave64, native FLAC, Ogg, MP3, AIFF and AIFF-C, Apple CAF
  and raw AC-3 and E-AC-3, read and written; these codecs in Matroska, WebM and MP4 too.
- A resampler between any two rates, sample format conversion with dither, and channel layouts with downmix and upmix.
- Streaming filters for gain, fades and crossfades, mixing and panning, delay and trimming, and tempo and speed: see
  [Audio filters](audio-filters.md).

Other audio tracks, such as AAC and E-AC-3, are copied through untouched.

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
`Codec` (FLAC for .flac, Opus for Ogg and WebM, PCM elsewhere by default), the `SampleRate` and `Layout` to write (converted when the frames differ),
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

`OpusCodec` encodes too, with the packets libopus 1.5.2 makes: see [Encoding Opus](opus-encoding.md).

## Vorbis

`VorbisCodec` decodes Vorbis I streams from Ogg, WebM, Matroska and MP4: mono to 255 channels, every rate, both floor types
and all three residue types (floor 0, which no current encoder writes, is untested). Frames come out as planar float at the stream's rate, with 3 to 8 channels remapped from Vorbis
order to the canonical order. The end is trimmed to Ogg's last granule position, or to Matroska's DiscardPadding, and output
matches libvorbis to within 2^-16 of full scale. A packet that cannot be decoded is reported as `Vorbis.CorruptPacket` and,
with `DecodeErrorMode.Conceal`, left as silence; the decoder starts its overlap again at the next packet.

[!code-csharp[](../../../tests/Egami.DocSamples/Audio.cs#audio-vorbis)]

## MP3

`Mp3Codec` decodes MPEG audio (ISO/IEC 11172-3 and 13818-3): Layers I, II and III of MPEG-1, MPEG-2 and MPEG-2.5, every rate
from 8 to 48 kHz, mono, stereo, joint and dual channel, and free-format bit rates. Frames come out as planar float, one per MPEG
frame, and pass the ISO/IEC 11172-4 compliance streams at full accuracy. The decoder is translated from minimp3 (CC0). Damaged
frames are found by resynchronizing on three matching headers, and a frame whose CRC-16 does not match is reported as
`Mp3.BadCrc`; with `DecodeErrorMode.Conceal` a bad frame becomes silence of its own length, so the timing stays right.

`Mp3Format` reads .mp3, .mp2, .mp1 and .mpa files: ID3v2.2 to 2.4 tags (text frames as tags, pictures as attachments with the
front cover named cover.*, CHAP frames as chapters), ID3v1 and APEv2 tags, and the Xing, Info or VBRI frame. The LAME tag's
encoder delay and padding become the track's `CodecDelay` and the last packet's duration, so the audio is gapless: a LAME
file decodes to exactly the samples the encoder was given. Seeks are exact: the first seek reads every frame header into an
index, and the reader starts early enough to fill the bit reservoir. Set `Mp3Format.FastSeek` (registered with
`MediaContext.WithContainer`) to seek by the Xing table instead, which reads nothing first but lands within about a percent.
The writer stores an ID3v2.4 tag and the frames, with a Xing header and LAME tag on a seekable stream, so MP3 leaves Matroska
or MP4 without a re-encode.

[!code-csharp[](../../../tests/Egami.DocSamples/Audio.cs#audio-mp3)]

## Containers

`WavFormat.Instance` reads WAV, RF64 and BW64 (PCM, float, WAVE_FORMAT_EXTENSIBLE with its channel mask, and streamed files
whose sizes were never filled in), and writes WAV that turns into RF64 by itself past 4 GB. `WavFormat.Wave64` is Sony
Wave64. LIST/INFO and id3 tags become `MediaInfo.Tags`, and cue points become chapters. `FlacFormat` reads and writes native
FLAC: Vorbis comments as tags, pictures as attachments (cover.* for the front cover), and a seek table every 10 seconds.

[!code-csharp[](../../../tests/Egami.DocSamples/Audio.cs#audio-containers)]

`AiffFormat` reads AIFF and AIFF-C (.aiff, .aif, .aifc): big-endian PCM of 8 to 32 bits, AIFF-C's little-endian `sowt`,
`in24`, `in32`, `fl32` and `fl64` samples, and G.711 `ulaw` and `alaw`, which `G711Codec` decodes to 16-bit samples (it
decodes G.711 in WAV, AVI and MOV too). Other AIFF-C compressions, such as ima4, MACE and QDesign, pass through undecoded
with an `X_AIFC/` codec id. NAME, AUTH, `(c) ` and ANNO chunks and an ID3 chunk become tags, MARK markers become chapters,
and a CHAN chunk gives the channel layout. The writer stores integer PCM as AIFF and float as AIFF-C, the title, artist,
copyright and comment in text chunks, other tags and cover art in an ID3 chunk, and chapters as markers. AIFF's sizes come
before the samples, so a non-seekable output is written through a temporary file; AIFF holds at most 4 GB.

[!code-csharp[](../../../tests/Egami.DocSamples/Audio.cs#audio-aiff)]

`CafFormat` reads and writes Apple's Core Audio Format (.caf): PCM of 8 to 32 bits in either byte order, 32 and 64-bit float,
G.711, ALAC, Opus, AAC, MP3, AC-3 and FLAC, with 64-bit chunk sizes and so no 4 GB limit. The packet table (pakt) cuts
variable-size packets and gives the priming frames, which become the track's `CodecDelay`, and the padding at the end; the
magic cookie (kuki) becomes the track's `CodecPrivate`. The info chunk becomes tags, markers and regions named by the strg
chunk become chapters, and a chan chunk gives the layout. Other formats pass through with an `X_CAF/` codec id. The writer
streams PCM and G.711: on a non-seekable output the data chunk's size is -1, meaning "to the end of the file". For other
codecs it puts the packet table after the samples, or before them through a temporary file when the output cannot seek, so
the file can be read as a stream. Reading a file whose packet table follows the samples needs a seekable stream.

[!code-csharp[](../../../tests/Egami.DocSamples/Audio.cs#audio-caf)]

`Ac3Format` reads and writes raw Dolby AC-3 and E-AC-3 streams (.ac3, .eac3, .ec3). Each packet is one AC-3 sync frame, or
one E-AC-3 independent frame with the dependent substreams that follow it, as MP4 and Matroska store them; the track's rate,
channels and layout come from the first frame's header (`CodecIds.Ac3`, `CodecIds.EAc3`). Bytes between frames are skipped,
and a seek builds a frame index in one pass. Both pass through MP4 (ac-3 and ec-3 sample entries; the writer builds the dac3
or dec3 box from the first frame), Matroska and CAF unchanged.

[!code-csharp[](../../../tests/Egami.DocSamples/Audio.cs#audio-ac3)]

`Ac3Codec` decodes AC-3 (ATSC A/52) in every channel mode, 1/0 to 3/2 and dual mono, with or without the LFE, at 32, 44.1 and
48 kHz and 32 to 640 kbit/s, from raw .ac3 files and from AC-3 tracks in MP4, Matroska, AVI and CAF. Frames are 1536 samples of
planar float in Egami.Video's channel order. Both frame CRCs are checked unless `VerifyChecksums` is off; a damaged frame is
reported as `Ac3.BadCrc` or `Ac3.CorruptFrame` and handled as `Errors` says, and Conceal puts silence of its length in its
place. The stream's dynamic range control is applied in full, as the standard asks; `Ac3DecoderOptions.DynamicRange` scales it
(0 turns it off) and `Ac3DecoderOptions.Downmix` mixes to a smaller layout with the stream's own center and surround mix
levels, scaled so the mix cannot clip. An `AudioReader` whose `Layout` is mono or stereo uses that downmix on wider AC-3
tracks too. Dialogue normalization is left to the player. E-AC-3 passes through undecoded.

[!code-csharp[](../../../tests/Egami.DocSamples/Audio.cs#audio-ac3-decode)]

`AlacCodec` decodes and encodes Apple Lossless (ALAC) at 16, 20, 24 and 32 bits with 1 to 8 channels. It is translated from
Apple's reference codec, so its packets are the bytes Apple's encoder makes: level 1, the default, searches the stereo matrix
and predictor order, and level 0 is Apple's fast mode. Each packet holds 4096 samples unless `FrameSize` says otherwise, and
the track's `CodecPrivate` is the 24-byte ALACSpecificConfig. ALAC goes in MP4 and M4A (an `alac` box), QuickTime MOV (read
from its `wave` atom), Matroska (`A_ALAC`) and CAF. ALAC fixes the speakers by the channel count (for example center, left,
right and back center for four channels), so a frame with the same number of channels is stored as it is and read back with
ALAC's layout. Extracting an ALAC track with the CLI makes an .m4a file.

[!code-csharp[](../../../tests/Egami.DocSamples/Audio.cs#audio-alac)]

`OggFormat` reads and writes Ogg (.ogg, .opus, .oga) with Opus, Vorbis and FLAC. Timestamps come from the granule positions,
seeking from an index built in one pass over the page headers, and pages with a bad CRC are skipped with an `ogg-crc`
warning. A chained file (one stream after another) reads as one track when the settings stay the same, each link's TITLE a
chapter; a link that changes them ends the track with an `ogg-chained-stream` warning.

[!code-csharp[](../../../tests/Egami.DocSamples/Audio.cs#audio-ogg)]
