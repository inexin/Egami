/* Makes tests/video/corpus/audio/silk-vectors.bin for SilkTests: build and run it in egami-opusref (see tools/video/opusref):
   gcc -O2 -o silk-vectors silk-vectors.c -I/opus/include -I/opus/src -I/opus/silk -I/opus/celt -I/build /build/libopus.a -lm
   ./silk-vectors silk-vectors.bin 60
   It encodes a synthetic signal SILK-only with libopus 1.5.2 and decodes it with silk_Decode, recording every packet. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include "opus.h"
#include "opus_private.h"
#include "API.h"
#include "entdec.h"

typedef struct { int api_rate, api_ch, enc_ch, bandwidth, frame_ms, bitrate, fec, loss_mod, switch_bw, toggle_ch; } cfg;

static unsigned fnv(const short *x, int n, unsigned h)
{
    for (int i = 0; i < n; i++) {
        unsigned short v = (unsigned short)x[i];
        h = (h ^ (v & 0xFF)) * 16777619u;
        h = (h ^ (v >> 8)) * 16777619u;
    }
    return h;
}

static void w32(FILE *f, unsigned v) { fwrite(&v, 4, 1, f); }
static void w16(FILE *f, unsigned v) { unsigned short s = (unsigned short)v; fwrite(&s, 2, 1, f); }

static unsigned lcg = 12345;
static double rnd(void) { lcg = lcg * 1103515245u + 12345u; return ((lcg >> 8) & 0xFFFF) / 32768.0 - 1.0; }

/* A voiced/unvoiced/silent test signal at 48 kHz. */
static void signal48(short *pcm, int n, int ch, int start)
{
    for (int i = 0; i < n; i++) {
        double t = (start + i) / 48000.0;
        int seg = (int)(t * 4) % 8;
        double f0 = 110 + 60 * sin(2 * M_PI * 0.7 * t);
        double ph = 2 * M_PI * (110 * t - 60 / (2 * M_PI * 0.7) * cos(2 * M_PI * 0.7 * t));
        double v = 0;
        for (int h = 1; h * f0 < 3800; h++) v += sin(h * ph) / (h * 0.8 + 0.2 * h * h * f0 / 1000.0);
        double s;
        if (seg < 4) s = 0.25 * v;
        else if (seg == 4) s = 0.15 * rnd();
        else if (seg == 5) s = 0.0005 * rnd();
        else s = 0.2 * v + 0.05 * rnd();
        for (int c = 0; c < ch; c++) {
            double y = c == 0 ? s : 0.6 * s + 0.1 * sin(2 * M_PI * 330 * t);
            int q = (int)lrint(y * 32767);
            pcm[i * ch + c] = (short)(q > 32767 ? 32767 : q < -32768 ? -32768 : q);
        }
    }
}

int main(int argc, char **argv)
{
    static const cfg cfgs[] = {
        { 48000, 1, 1, OPUS_BANDWIDTH_NARROWBAND, 20, 12000, 0, 9, 0, 0 },
        { 48000, 2, 1, OPUS_BANDWIDTH_MEDIUMBAND, 20, 16000, 0, 11, 0, 0 },
        { 16000, 1, 1, OPUS_BANDWIDTH_WIDEBAND, 10, 20000, 0, 13, 0, 0 },
        { 48000, 2, 2, OPUS_BANDWIDTH_WIDEBAND, 20, 24000, 0, 10, 0, 0 },
        { 24000, 1, 2, OPUS_BANDWIDTH_WIDEBAND, 40, 20000, 0, 7, 0, 0 },
        { 48000, 2, 2, OPUS_BANDWIDTH_MEDIUMBAND, 60, 18000, 1, 5, 0, 0 },
        { 8000, 1, 1, OPUS_BANDWIDTH_WIDEBAND, 20, 22000, 1, 4, 0, 0 },
        { 12000, 1, 1, OPUS_BANDWIDTH_NARROWBAND, 60, 10000, 0, 6, 0, 0 },
        { 12000, 2, 1, OPUS_BANDWIDTH_WIDEBAND, 20, 18000, 1, 6, 0, 0 },
        { 48000, 1, 1, OPUS_BANDWIDTH_WIDEBAND, 20, 16000, 0, 8, 1, 0 },
        { 48000, 2, 2, OPUS_BANDWIDTH_WIDEBAND, 10, 28000, 1, 5, 0, 0 },
        { 48000, 2, 2, OPUS_BANDWIDTH_WIDEBAND, 20, 32000, 0, 9, 0, 1 },
        { 16000, 1, 2, OPUS_BANDWIDTH_MEDIUMBAND, 40, 24000, 1, 4, 0, 1 },
        { 48000, 2, 2, OPUS_BANDWIDTH_WIDEBAND, 20, 30000, 0, 0, 1, 0 },
    };
    int ncfg = sizeof(cfgs) / sizeof(cfgs[0]);
    int packets = argc > 2 ? atoi(argv[2]) : 60;
    FILE *f = fopen(argv[1], "wb");
    fwrite("SLKV", 4, 1, f);
    w32(f, ncfg);
    for (int c = 0; c < ncfg; c++) {
        const cfg *k = &cfgs[c];
        int err, decsize;
        OpusEncoder *enc = opus_encoder_create(48000, k->enc_ch, OPUS_APPLICATION_VOIP, &err);
        opus_encoder_ctl(enc, OPUS_SET_FORCE_MODE(MODE_SILK_ONLY));
        opus_encoder_ctl(enc, OPUS_SET_BANDWIDTH(k->bandwidth));
        opus_encoder_ctl(enc, OPUS_SET_BITRATE(k->bitrate));
        opus_encoder_ctl(enc, OPUS_SET_COMPLEXITY(10));
        if (k->fec) {
            opus_encoder_ctl(enc, OPUS_SET_INBAND_FEC(1));
            opus_encoder_ctl(enc, OPUS_SET_PACKET_LOSS_PERC(20));
        }
        int fs = 48 * k->frame_ms;
        static unsigned char pkt[400][1500];
        static int len[400];
        short *pcm = malloc(sizeof(short) * fs * 2);
        lcg = 777 + c;
        for (int p = 0; p < packets; p++) {
            if (k->switch_bw) {
                static const int bws[3] = { OPUS_BANDWIDTH_WIDEBAND, OPUS_BANDWIDTH_NARROWBAND, OPUS_BANDWIDTH_MEDIUMBAND };
                if (p % 15 == 0) {
                    /* A reset makes the encoder switch its internal rate at once. */
                    opus_encoder_ctl(enc, OPUS_RESET_STATE);
                    opus_encoder_ctl(enc, OPUS_SET_BANDWIDTH(bws[(p / 15) % 3]));
                }
            }
            if (k->toggle_ch) {
                opus_encoder_ctl(enc, OPUS_SET_FORCE_CHANNELS((p / 10) % 2 ? 1 : 2));
            }
            signal48(pcm, fs, k->enc_ch, p * fs);
            len[p] = opus_encode(enc, pcm, fs, pkt[p], 1500);
            if (len[p] < 2 || (pkt[p][0] & 3) != 0 || (pkt[p][0] >> 3) > 11) { fprintf(stderr, "bad packet %d %d\n", c, p); return 1; }
        }
        silk_Get_Decoder_Size(&decsize);
        void *dec = malloc(decsize);
        silk_InitDecoder(dec);
        w32(f, k->api_rate);
        w32(f, k->api_ch);
        w32(f, packets);
        short out[2 * 48 * 60];
        for (int p = 0; p < packets; p++) {
            int toc = pkt[p][0];
            int config = toc >> 3;
            int stereo = (toc >> 2) & 1;
            static const int rates[3] = { 8000, 12000, 16000 };
            static const int durs[4] = { 10, 20, 40, 60 };
            int internal = rates[config / 4];
            int ms = durs[config % 4];
            int lost = k->loss_mod && p % k->loss_mod == k->loss_mod - 1 && p + 1 < packets;
            int action = 0;
            const unsigned char *data = pkt[p] + 1;
            int dlen = len[p] - 1;
            if (lost) {
                int ntoc = pkt[p + 1][0];
                /* FEC only from a next packet with the same configuration, as opus_decode_frame needs. */
                if (k->fec && (ntoc >> 3) == config && ((ntoc >> 2) & 1) == stereo) { action = 2; data = pkt[p + 1] + 1; dlen = len[p + 1] - 1; }
                else { action = 1; dlen = 0; }
            }
            ec_dec ec;
            ec_dec_init(&ec, (unsigned char *)data, dlen);
            silk_DecControlStruct ctl;
            memset(&ctl, 0, sizeof(ctl));
            ctl.nChannelsAPI = k->api_ch;
            ctl.nChannelsInternal = stereo + 1;
            ctl.API_sampleRate = k->api_rate;
            ctl.internalSampleRate = internal;
            ctl.payloadSize_ms = ms;
            int total = 0, ret = 0;
            int want = k->api_rate / 1000 * ms;
            int calls = 0;
            unsigned h = 2166136261u;
            while (total < want) {
                opus_int32 n = 0;
                int r = silk_Decode(dec, &ctl, action, total == 0, &ec, out + total * k->api_ch, &n, 0);
                ret |= r;
                h = fnv(out + total * k->api_ch, n * k->api_ch, h);
                total += n;
                calls++;
                if (n <= 0) break;
            }
            /* packet record */
            fputc(action, f);
            w16(f, len[p]);
            fwrite(pkt[p], 1, len[p], f);
            w32(f, h);
            w32(f, total);
            w32(f, action == 1 ? 0 : ec.rng);
            w32(f, (unsigned)ctl.prevPitchLag);
            w32(f, (unsigned)ret);
        }
        free(dec);
        free(pcm);
        opus_encoder_destroy(enc);
    }
    fclose(f);
    return 0;
}
