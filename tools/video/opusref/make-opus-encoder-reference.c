/* Makes tests/video/corpus/opus-encode/encoder-ref.bin for Egami.Video's OpusEncoderTests: a synthetic signal encoded by
   libopus 1.5.2 in many settings (every mode, application, rate, frame duration, rate mode, FEC, DTX, forced mode and
   bandwidth, and multistream surround up to 7.1), recording each packet's length, FNV-1a hash and final range. The test
   encodes the same signal with Egami.Video's translation and expects the same packets.

   Build and run in the reference image (tools/video/opusref/Dockerfile), from the repo root:
     docker run --rm -v "$PWD:/w" egami-opusref:v5.1 sh -c "gcc -O2 -I/opus/include \
       /w/tools/video/opusref/make-opus-encoder-reference.c /build/libopus.a -lm -o /tmp/ref \
       && /tmp/ref /w/tests/video/corpus/opus-encode/encoder-ref.bin"

   The signal uses only additions, multiplications and divisions in double, so C and C# make the same samples bit for bit.

   Format, little-endian: "OPEN", u32 config count; per config the 16 i32 fields of struct config, then per packet u16
   length, u32 FNV-1a hash of the packet, u32 final range. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "opus.h"
#include "opus_multistream.h"

/* OPUS_SET_FORCE_MODE is in opus_private.h, which the installed headers leave out. */
#define SET_FORCE_MODE(x) 11002, (opus_int32)(x)

static void w32(FILE *f, unsigned v) { fwrite(&v, 4, 1, f); }
static void w16(FILE *f, unsigned v) { unsigned short s = (unsigned short)v; fwrite(&s, 2, 1, f); }

/* The test signal: a voiced buzz, noise, near-silence, a two-tone chord and true silence in turns of 250 ms. */
static void make_signal(float *pcm, int n, int ch, int fs)
{
    unsigned s = 12345;
    int seg_len = fs / 4;
    for (int i = 0; i < n; i++) {
        int seg = (i / seg_len) % 6;
        int p1 = fs / (100 + 20 * ((i / (fs / 8)) % 5));
        int p2 = fs / 330;
        for (int c = 0; c < ch; c++) {
            s = s * 1664525u + 1013904223u;
            double noise = (double)(s >> 8) / 16777216.0 * 2.0 - 1.0;
            int k = i + 7 * c;
            double saw1 = (double)(2 * (k % p1) - p1) / (double)p1;
            double saw2 = (double)(2 * (k % p2) - p2) / (double)p2;
            double gain = 1.0 - 0.1 * c;
            double v;
            switch (seg) {
            case 0: case 1: v = 0.3 * saw1 + 0.02 * noise; break;
            case 2: v = 0.2 * noise; break;
            case 3: v = 0.0005 * noise; break;
            case 4: v = 0.15 * saw1 + 0.12 * saw2 + 0.01 * noise; break;
            default: v = 0.0; break;
            }
            pcm[i * ch + c] = (float)(gain * v);
        }
    }
}

/* kind 0 opus_encoder, 1 multistream surround (family 0 or 1). Settings of -1000 are left at libopus's defaults. */
struct config { int kind, fs, ch, family, app, frame, bitrate, vbr, cvbr, complexity, fec, loss, dtx, bandwidth, signal, force_mode; };

#define A OPUS_APPLICATION_AUDIO
#define V OPUS_APPLICATION_VOIP
#define L OPUS_APPLICATION_RESTRICTED_LOWDELAY
#define D -1000

static const struct config configs[] = {
    /* kind fs     ch fam app frame bitrate vbr cvbr cx fec loss dtx bandwidth signal force */
    {0, 48000, 1, 0, A, 960, D, 1, 1, 10, 0, 0, 0, D, D, D},
    {0, 48000, 1, 0, V, 960, 12000, 1, 1, 10, 0, 0, 0, D, D, D},
    {0, 48000, 1, 0, V, 960, 24000, 1, 1, 10, 0, 0, 0, D, D, D},
    {0, 48000, 1, 0, A, 960, 20000, 1, 1, 8, 0, 0, 0, D, D, D},
    {0, 48000, 2, 0, A, 960, D, 1, 1, 10, 0, 0, 0, D, D, D},
    {0, 48000, 2, 0, A, 960, 24000, 1, 1, 10, 0, 0, 0, D, D, D},
    {0, 48000, 2, 0, V, 960, 40000, 1, 1, 10, 0, 0, 0, D, D, D},
    {0, 48000, 2, 0, A, 960, 64000, 0, 0, 10, 0, 0, 0, D, D, D},
    {0, 48000, 2, 0, A, 480, 48000, 1, 0, 10, 0, 0, 0, D, D, D},
    {0, 48000, 2, 0, A, 120, 64000, 1, 1, 10, 0, 0, 0, D, D, D},
    {0, 48000, 1, 0, A, 240, 32000, 1, 0, 10, 0, 0, 0, D, D, D},
    {0, 48000, 1, 0, A, 2880, 20000, 1, 1, 10, 0, 0, 0, D, D, D},
    {0, 48000, 1, 0, V, 5760, 16000, 1, 1, 10, 0, 0, 0, D, D, D},
    {0, 48000, 2, 0, A, 4800, 32000, 1, 1, 10, 0, 0, 0, D, D, D},
    {0, 48000, 2, 0, A, 1920, 96000, 1, 0, 10, 0, 0, 0, D, D, D},
    {0, 48000, 1, 0, V, 960, 16000, 1, 1, 10, 1, 20, 0, D, D, D},
    {0, 48000, 1, 0, V, 960, 32000, 1, 1, 10, 2, 10, 0, D, D, D},
    {0, 48000, 1, 0, V, 960, 16000, 1, 1, 10, 0, 0, 1, D, D, D},
    {0, 48000, 1, 0, V, 960, 16000, 1, 1, 5, 0, 0, 1, D, D, D},
    {0, 48000, 1, 0, A, 960, 24000, 1, 1, 10, 0, 0, 0, D, OPUS_SIGNAL_VOICE, D},
    {0, 48000, 1, 0, A, 960, 24000, 1, 1, 10, 0, 0, 0, D, OPUS_SIGNAL_MUSIC, D},
    {0, 48000, 1, 0, A, 960, 32000, 1, 1, 10, 0, 0, 0, OPUS_BANDWIDTH_SUPERWIDEBAND, D, 1001},
    {0, 48000, 1, 0, A, 960, 12000, 1, 1, 10, 0, 0, 0, D, D, 1000},
    {0, 48000, 2, 0, V, 960, 48000, 1, 1, 10, 0, 0, 0, D, D, 1002},
    {0, 48000, 1, 0, L, 480, 32000, 1, 1, 10, 0, 0, 0, D, D, D},
    {0, 48000, 2, 0, L, 240, 96000, 0, 0, 10, 0, 0, 0, D, D, D},
    {0, 48000, 1, 0, A, 960, 8000, 0, 0, 3, 0, 0, 0, D, D, D},
    {0, 48000, 1, 0, A, 960, -1, 1, 1, 10, 0, 0, 0, D, D, D},
    {0, 48000, 2, 0, A, 960, 6000, 1, 1, 10, 0, 0, 0, D, D, D},
    {0, 48000, 2, 0, A, 960, 256000, 1, 1, 10, 0, 0, 0, D, D, D},
    {0, 48000, 1, 0, A, 960, 32000, 1, 1, 0, 0, 0, 0, D, D, D},
    {0, 8000, 1, 0, V, 160, 12000, 1, 1, 10, 0, 0, 0, D, D, D},
    {0, 12000, 1, 0, V, 240, 16000, 1, 1, 10, 0, 0, 0, D, D, D},
    {0, 16000, 2, 0, V, 320, 24000, 1, 1, 10, 0, 0, 0, D, D, D},
    {0, 24000, 2, 0, A, 480, 48000, 1, 1, 10, 0, 0, 0, D, D, D},
    {0, 16000, 1, 0, A, 640, 20000, 0, 0, 7, 0, 0, 0, D, D, D},
    {1, 48000, 1, 0, A, 960, 64000, 1, 0, 10, 0, 0, 0, D, D, D},
    {1, 48000, 2, 0, A, 960, 128000, 1, 0, 10, 0, 0, 0, D, D, D},
    {1, 48000, 3, 1, A, 960, 192000, 1, 0, 10, 0, 0, 0, D, D, D},
    {1, 48000, 4, 1, A, 960, 256000, 1, 0, 10, 0, 0, 0, D, D, D},
    {1, 48000, 5, 1, A, 480, 320000, 1, 0, 10, 0, 0, 0, D, D, D},
    {1, 48000, 6, 1, A, 960, D, 1, 0, 10, 0, 0, 0, D, D, D},
    {1, 48000, 6, 1, A, 960, 384000, 0, 0, 10, 0, 0, 0, D, D, D},
    {1, 48000, 7, 1, A, 2880, 448000, 1, 0, 10, 0, 0, 0, D, D, D},
    {1, 48000, 8, 1, A, 960, 512000, 1, 0, 10, 0, 0, 0, D, D, D},
    {1, 24000, 6, 1, V, 480, 160000, 1, 0, 6, 0, 0, 0, D, D, D},
};

#define SECONDS_TENTHS 15

static unsigned fnv(const unsigned char *p, int n)
{
    unsigned h = 2166136261u;
    for (int i = 0; i < n; i++) h = (h ^ p[i]) * 16777619u;
    return h;
}

int main(int argc, char **argv)
{
    if (argc < 2) { fprintf(stderr, "usage: make-opus-encoder-reference out.bin\n"); return 1; }
    FILE *out = fopen(argv[1], "wb");
    if (!out) { perror(argv[1]); return 1; }
    int count = (int)(sizeof(configs) / sizeof(configs[0]));
    fwrite("OPEN", 1, 4, out);
    w32(out, (unsigned)count);
    static unsigned char buf[8 * 7680];
    for (int k = 0; k < count; k++) {
        const struct config *c = &configs[k];
        const int *fields = &c->kind;
        for (int f = 0; f < 16; f++) w32(out, (unsigned)fields[f]);
        int n = c->fs * SECONDS_TENTHS / 10;
        int packets = n / c->frame;
        float *pcm = malloc(sizeof(float) * (size_t)n * c->ch);
        make_signal(pcm, n, c->ch, c->fs);
        int err = 0;
        OpusEncoder *e = NULL;
        OpusMSEncoder *m = NULL;
        if (c->kind == 0) {
            e = opus_encoder_create(c->fs, c->ch, c->app, &err);
        } else {
            int streams, coupled; unsigned char map[8];
            m = opus_multistream_surround_encoder_create(c->fs, c->ch, c->family, &streams, &coupled, map, c->app, &err);
        }
        if (err) { fprintf(stderr, "config %d: create %d\n", k, err); return 1; }
#define CTL(x) do { if (e) opus_encoder_ctl(e, x); else opus_multistream_encoder_ctl(m, x); } while (0)
        if (c->bitrate != D) CTL(OPUS_SET_BITRATE(c->bitrate));
        CTL(OPUS_SET_VBR(c->vbr));
        CTL(OPUS_SET_VBR_CONSTRAINT(c->cvbr));
        CTL(OPUS_SET_COMPLEXITY(c->complexity));
        CTL(OPUS_SET_INBAND_FEC(c->fec));
        CTL(OPUS_SET_PACKET_LOSS_PERC(c->loss));
        CTL(OPUS_SET_DTX(c->dtx));
        if (e) {
            opus_encoder_ctl(e, OPUS_SET_BANDWIDTH(c->bandwidth));
            opus_encoder_ctl(e, OPUS_SET_SIGNAL(c->signal));
            opus_encoder_ctl(e, SET_FORCE_MODE(c->force_mode));
        }
        for (int p = 0; p < packets; p++) {
            const float *in = pcm + (size_t)p * c->frame * c->ch;
            int len = e ? opus_encode_float(e, in, c->frame, buf, 1276)
                        : opus_multistream_encode_float(m, in, c->frame, buf, (int)sizeof(buf));
            if (len < 0) { fprintf(stderr, "config %d packet %d: encode %d\n", k, p, len); return 1; }
            opus_uint32 range;
            CTL(OPUS_GET_FINAL_RANGE(&range));
            w16(out, (unsigned)len);
            w32(out, fnv(buf, len));
            w32(out, range);
        }
        if (e) opus_encoder_destroy(e); else opus_multistream_encoder_destroy(m);
        free(pcm);
    }
    fclose(out);
    return 0;
}
