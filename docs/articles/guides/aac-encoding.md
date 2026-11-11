# Encoding AAC

AAC LC (ISO/IEC 14496-3) is the audio codec of MP4, M4A and most streaming, and the one every phone, browser and player
decodes. Egami.Video encodes it fully managed with its own psychoacoustic model; parts of the bitstream side are translated
from oxideav-aac (MIT).

- 8 to 96 kHz (other rates are resampled to the next AAC rate up), mono to 5.1 and 7.1 in the layouts of channel
  configurations 1 to 7.
- An average bit rate with a bit reservoir, or a variable-bit-rate quality from 1 to 5.
- Block switching for attacks, TNS, mid/side and intensity stereo and perceptual noise substitution, chosen per block.
- AAC LC only: no HE-AAC (SBR) or HE-AACv2 (parametric stereo) yet.

## Writing .m4a and .aac files

`AudioWriter` writes AAC by default for .m4a, .mp4 and .aac (ADTS) files; set `AudioWriterOptions.Codec` to
`AudioCodecs.Aac` for Matroska (.mka, .mkv) and CAF. `AudioWriterOptions.Aac` takes an `AacEncoderOptions`; leave it out for
64 kbit/s per channel.

[!code-csharp[](../../../tests/Egami.DocSamples/Audio.cs#audio-aac-encode)]

Every stream starts with 1024 samples of priming, the encoder's delay. MP4 records it in the edit list and an iTunSMPB tag,
and Matroska as CodecDelay; the last packet's duration cuts the padding after the input, so `AudioReader` and FFmpeg give
back exactly the samples written. An .aac file has no place for either, so it keeps the priming and padding, as
every ADTS encoder's output does.

## Settings

| `AacEncoderOptions` | Default | What it does |
| --- | --- | --- |
| `Bitrate` | 64 kbit/s per channel | Bits per second for all channels together, 8 to 1536 kbit/s. Hard blocks borrow from a reservoir that easy ones fill, so the rate holds over a few seconds; easy audio may come out under it. |
| `Quality` | not set | 1 (smallest) to 5 (best) instead of a bitrate: each level holds the noise at one distance from the masking threshold, so the rate follows the audio. Stereo music comes out at roughly 50, 80, 112, 170 and 235 kbit/s. |
| `Bandwidth` | from the rate | The highest frequency coded, 1 to 24 kHz: about 11 kHz at 16 kbit/s per channel, 15 kHz at 24 and 20 kHz from 32, never above half the sample rate. |

Intensity stereo is used only below 40 kbit/s per channel, and noise substitution below 48 kbit/s per channel or at quality 1
and 2.

[!code-csharp[](../../../tests/Egami.DocSamples/Audio.cs#audio-aac-quality)]

## Transcoding to M4A

`MediaFile.Transcode` with `AudioCodecs.Aac` encodes every audio track in AAC and copies the video unchanged.

[!code-csharp[](../../../tests/Egami.DocSamples/Audio.cs#audio-aac-transcode)]

From the command line, `egami-video audio in.flac -o out.m4a` writes AAC; add `--bitrate 128k` or `--quality 4`. ALAC stays
available with `--codec alac`.

## Compared with FFmpeg and Fraunhofer FDK AAC

Egami.Video keeps a wider band than the other encoders: about 20 kHz from 32 kbit/s per channel, where Fraunhofer FDK AAC
keeps 13 to 17 kHz and FFmpeg's own encoder 12 to 20 kHz between 64 and 192 kbit/s in stereo. The rest of the band costs
bits that a narrower encoder spends below it, so at low rates the coded band is cleaner in theirs; set `Bandwidth` to match
them. In ViSQOL on the EBU SQAM recordings, the default scores above both at 64 to 192 kbit/s, and about level with Fraunhofer
FDK AAC when held to its bandwidth.
