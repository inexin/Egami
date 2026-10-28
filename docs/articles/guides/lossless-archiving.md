# Lossless archiving with FFV1

FFV1 (RFC 9043) is the lossless video codec memory institutions archive with: a decode gives back every sample, each slice of
every frame carries a CRC, and every frame can stand on its own. Egami.Video reads and writes it in Matroska and AVI with no
native code, frame for frame equal to FFmpeg's decoder, and FFmpeg decodes Egami.Video's files to the same samples.

## Writing a master

Pick `VideoCodecs.Ffv1` and write frames as you would with any codec. Planes keep their layout and depth (4:2:0, 4:2:2, 4:4:4,
4:1:1, 4:1:0 or gray, with or without alpha, 8 to 16 bits), so a 10-bit 4:2:2 capture stays 10-bit 4:2:2. Write Matroska (`.mkv`),
the usual archival pairing; AVI works too.

[!code-csharp[](../../../tests/Egami.DocSamples/Archiving.cs#archive-write)]

The defaults are the profile archives use, which FFmpeg users get by adding `-level 3 -g 1 -slicecrc 1 -slices 16`:

| Setting | Default | FFmpeg flag |
| --- | --- | --- |
| Version | 3, with a configuration record in the container | `-level 3` |
| Keyframes | every frame (`KeyframeInterval` 1) | `-g 1` |
| Slice CRCs | on (`Ffv1EncoderOptions.SliceCrc`) | `-slicecrc 1` |
| Slices | 1 up to 352×288, 4 up to 720p, 16 up to 1440p, 24 above | `-slices` |
| Coder | range coder with a custom state table | `-coder range_tab` |
| Context model | small (`Effort` up to 6) | `-context 0` |

Files come within 1 % of FFmpeg's size at the same settings, and slices are coded in parallel on `EgamiContext.MaxParallelism`
threads with the same file at any thread count.

## Checking fixity

`Ffv1Codec.Verify` checks the configuration record's CRC and every slice's without decoding a picture, about 50 times faster than a
decode, so it suits routine checks of a whole collection. `Ffv1VerifyResult.Errors` names each damaged frame and slice. A decode
with `Errors = DecodeErrorMode.Conceal` reports the same damage as `Ffv1.SliceCrc` and shows the last good frame in the damaged
slice's place. After a migration, a full check decodes the master and compares it with the source:

[!code-csharp[](../../../tests/Egami.DocSamples/Archiving.cs#archive-verify)]

Version 0 and 1 files, and version 3 files written without slice CRCs, have nothing to check per slice: `HasSliceCrcs` is false.

## RGB sources

Scanned film, graphics and screen captures in RGB stay RGB: frames given as RGB rasters (8 or 16 bits a channel) are coded through
FFV1's reversible color transform, and decode as G, B and R planes tagged `YuvMatrix.Identity`, which convert back to RGB exactly.

[!code-csharp[](../../../tests/Egami.DocSamples/Archiving.cs#archive-rgb)]

Planes decoded from a 9, 11, 13, 14 or 15-bit source sit in 10, 12 or 16-bit planes with their samples shifted up; set
`Ffv1EncoderOptions.BitsPerSample` to write them back at the source depth. The track's `BitDepth` gives the depth a file holds.

## Working copies

For copies made to work on rather than to keep, Golomb-Rice coding is faster than the range coder, spacing keyframes makes files
smaller, and more slices use more cores. `Quality` does not apply to FFV1, and setting it reports `Ffv1.QualityIgnored`.

[!code-csharp[](../../../tests/Egami.DocSamples/Archiving.cs#archive-options)]
