/* Makes tests/video/corpus/opus-celt/celt-ref.bin: CELT streams encoded from a synthetic signal with libopus 1.5.2 and
   decoded by libopus itself, for Egami.Video's CeltTests. Opus streams go through opus_decode_float (CELT-only packets with a
   TOC byte); raw streams through libopus's internal CELT encoder and decoder, which lets them use the hybrid start band 17.

   Build and run in the reference image (tools/video/opusref/Dockerfile), from the repo root:
     docker run --rm -v "$PWD:/w" egami-opusref:v5.1 sh -c "gcc -O2 -I/opus/include -I/opus/src \
       /w/tools/video/opusref/make-celt-reference.c /build/libopus.a -lm -o /tmp/ref && /tmp/ref /w/tests/video/corpus/opus-celt/celt-ref.bin small"
   Without "small" it writes every stream at full length with float samples (EGAMI_CELT_REF reads such a file).

   Format, little-endian: "CELT" (float samples) or "CEL6" (16-bit samples, round(32768 x) clamped), u32 stream count; per
   stream u16 encoded channels, decoder channels, frame size, frame count, raw (0 Opus, 1 raw CELT), start band, end band; per
   frame u16 length, u16 lost, the packet, u32 final range of the decoder, the decoded interleaved samples. */
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "opus.h"
#include "opus_private.h"

/* opus_private.h brings in celt/celt.h: the internal CELT encoder and decoder and their requests. */

static unsigned rnd = 12345;
static float frand(void) { rnd = rnd * 1664525u + 1013904223u; return ((rnd >> 8) / 16777216.0f) * 2 - 1; }
static void w32(FILE *f, unsigned v) { fwrite(&v, 4, 1, f); }
static void w16(FILE *f, unsigned v) { unsigned short s = (unsigned short)v; fwrite(&s, 2, 1, f); }

struct config { int ch, dch, fs, br, vbr, bw, fmono, loss, frames, cplx, raw, start, end, bytes; };

static const struct config configs[] = {
    /* ch dch  fs     br vbr bandwidth                 mono loss frames cplx raw start end bytes */
    {1, 1, 960, 32000, 1, OPUS_BANDWIDTH_FULLBAND, 0, 0, 50, 10, 0, 0, 21, 0},
    {2, 2, 960, 64000, 1, OPUS_BANDWIDTH_FULLBAND, 0, 0, 50, 10, 0, 0, 21, 0},
    {2, 2, 480, 128000, 0, OPUS_BANDWIDTH_FULLBAND, 0, 0, 60, 10, 0, 0, 21, 0},
    {2, 2, 240, 96000, 1, OPUS_BANDWIDTH_SUPERWIDEBAND, 0, 0, 80, 10, 0, 0, 19, 0},
    {1, 1, 120, 48000, 1, OPUS_BANDWIDTH_FULLBAND, 0, 0, 120, 10, 0, 0, 21, 0},
    {2, 2, 120, 24000, 0, OPUS_BANDWIDTH_WIDEBAND, 0, 0, 120, 5, 0, 0, 17, 0},
    {1, 1, 960, 8000, 0, OPUS_BANDWIDTH_NARROWBAND, 0, 0, 40, 10, 0, 0, 13, 0},
    {2, 2, 960, 16000, 1, OPUS_BANDWIDTH_FULLBAND, 0, 0, 40, 10, 0, 0, 21, 0},
    {2, 2, 960, 320000, 1, OPUS_BANDWIDTH_FULLBAND, 0, 0, 30, 10, 0, 0, 21, 0},
    {2, 2, 960, 40000, 1, OPUS_BANDWIDTH_FULLBAND, 1, 0, 40, 10, 0, 0, 21, 0},
    {2, 1, 480, 64000, 1, OPUS_BANDWIDTH_FULLBAND, 0, 0, 40, 10, 0, 0, 21, 0},
    {1, 2, 480, 48000, 1, OPUS_BANDWIDTH_FULLBAND, 0, 0, 40, 10, 0, 0, 21, 0},
    {1, 1, 960, 32000, 1, OPUS_BANDWIDTH_FULLBAND, 0, 5, 60, 10, 0, 0, 21, 0},
    {2, 2, 480, 64000, 1, OPUS_BANDWIDTH_FULLBAND, 0, 4, 80, 10, 0, 0, 21, 0},
    {2, 2, 240, 12000, 1, OPUS_BANDWIDTH_FULLBAND, 0, 0, 60, 0, 0, 0, 21, 0},
    {1, 1, 480, 510000, 0, OPUS_BANDWIDTH_FULLBAND, 0, 0, 30, 10, 0, 0, 21, 0},
    {1, 1, 960, 0, 0, 0, 0, 0, 40, 10, 1, 17, 21, 50},
    {2, 2, 480, 0, 0, 0, 0, 0, 60, 10, 1, 17, 19, 40},
    {2, 2, 960, 0, 0, 0, 0, 5, 50, 10, 1, 17, 21, 120},
};

static void make_signal(float *in, int fs, int ch, long *t, double *ph, double *ph2)
{
    for (int i = 0; i < fs; i++, (*t)++)
    {
        double sec = *t / 48000.0;
        double fr = 200 + 3000 * fmod(sec * 4, 1.0);
        *ph += 2 * M_PI * fr / 48000;
        *ph2 += 2 * M_PI * 440 / 48000;
        float env = (*t % 4800) < 300 ? 1.0f : 0.3f;
        float silence = (sec > 0.08 && sec < 0.11) ? 0 : 1;
        float l = silence * (0.3f * (float)sin(*ph) + 0.05f * frand() * env + 0.2f * (float)sin(*ph2) * ((*t / 2400) & 1));
        float r = silence * (0.25f * (float)sin(*ph * 1.01 + 0.5) + 0.05f * frand() * env);
        in[i * ch] = l;
        if (ch == 2)
            in[i * ch + 1] = r;
    }
}

int main(int argc, char **argv)
{
    int small = argc > 2 && strcmp(argv[2], "small") == 0;
    int n = sizeof(configs) / sizeof(configs[0]);
    FILE *f = fopen(argv[1], "wb");
    fwrite(small ? "CEL6" : "CELT", 4, 1, f);
    w32(f, n);
    for (int s = 0; s < n; s++)
    {
        const struct config *c = &configs[s];
        int err, ch = c->ch, fs = c->fs;
        int frames = small ? (c->frames * 2 + 9) / 10 : c->frames;
        OpusEncoder *enc = NULL;
        OpusDecoder *dec = NULL;
        CELTEncoder *cenc = NULL;
        CELTDecoder *cdec = NULL;
        if (c->raw)
        {
            cenc = malloc(celt_encoder_get_size(ch));
            celt_encoder_init(cenc, 48000, ch, 0);
            opus_custom_encoder_ctl(cenc, CELT_SET_SIGNALLING(0));
            opus_custom_encoder_ctl(cenc, CELT_SET_START_BAND_REQUEST, c->start);
            opus_custom_encoder_ctl(cenc, CELT_SET_END_BAND_REQUEST, c->end);
            opus_custom_encoder_ctl(cenc, OPUS_SET_VBR(0));
            cdec = malloc(celt_decoder_get_size(c->dch));
            celt_decoder_init(cdec, 48000, c->dch);
            opus_custom_decoder_ctl(cdec, CELT_SET_START_BAND_REQUEST, c->start);
            opus_custom_decoder_ctl(cdec, CELT_SET_END_BAND_REQUEST, c->end);
            opus_custom_decoder_ctl(cdec, CELT_SET_CHANNELS_REQUEST, ch);
        }
        else
        {
            enc = opus_encoder_create(48000, ch, OPUS_APPLICATION_AUDIO, &err);
            dec = opus_decoder_create(48000, c->dch, &err);
            opus_encoder_ctl(enc, OPUS_SET_FORCE_MODE(MODE_CELT_ONLY));
            opus_encoder_ctl(enc, OPUS_SET_BITRATE(c->br));
            opus_encoder_ctl(enc, OPUS_SET_VBR(c->vbr));
            opus_encoder_ctl(enc, OPUS_SET_BANDWIDTH(c->bw));
            opus_encoder_ctl(enc, OPUS_SET_COMPLEXITY(c->cplx));
            if (c->fmono)
                opus_encoder_ctl(enc, OPUS_SET_FORCE_CHANNELS(1));
        }

        w16(f, ch); w16(f, c->dch); w16(f, fs); w16(f, frames); w16(f, c->raw); w16(f, c->start); w16(f, c->end);
        float *in = malloc(sizeof(float) * fs * ch);
        float *out = malloc(sizeof(float) * fs * c->dch);
        unsigned char pkt[1500];
        double ph = 0, ph2 = 0;
        long t = 0;
        for (int k = 0; k < frames; k++)
        {
            make_signal(in, fs, ch, &t, &ph, &ph2);
            int lost = c->loss && (k % c->loss) == c->loss - 1;
            int len, ret;
            opus_uint32 rng = 0;
            if (c->raw)
            {
                len = celt_encode_with_ec(cenc, in, fs, pkt, c->bytes, NULL);
                ret = celt_decode_with_ec(cdec, lost ? NULL : pkt, lost ? 0 : len, out, fs, NULL, 0);
                opus_custom_decoder_ctl(cdec, OPUS_GET_FINAL_RANGE(&rng));
            }
            else
            {
                len = opus_encode_float(enc, in, fs, pkt, 1275);
                ret = lost ? opus_decode_float(dec, NULL, 0, out, fs, 0) : opus_decode_float(dec, pkt, len, out, fs, 0);
                opus_decoder_ctl(dec, OPUS_GET_FINAL_RANGE(&rng));
            }

            if (ret != fs)
            {
                fprintf(stderr, "stream %d frame %d: %d\n", s, k, ret);
                return 1;
            }

            w16(f, len); w16(f, lost);
            fwrite(pkt, 1, len, f);
            w32(f, rng);
            if (small)
            {
                for (int i = 0; i < fs * c->dch; i++)
                {
                    long v = lrintf(out[i] * 32768.0f);
                    w16(f, (unsigned)(v > 32767 ? 32767 : v < -32768 ? -32768 : v));
                }
            }
            else
                fwrite(out, sizeof(float), fs * c->dch, f);
        }

        free(in);
        free(out);
        if (enc) opus_encoder_destroy(enc);
        if (dec) opus_decoder_destroy(dec);
        free(cenc);
        free(cdec);
    }

    fclose(f);
    return 0;
}
