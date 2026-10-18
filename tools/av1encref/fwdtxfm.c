/* Prints libaom's forward transform output for deterministic inputs, as one JSON line per (size, type, bit depth, vector): the
   FNV-1a hash of the coefficients at the coded size, in libaom's order. Built against libaom 3.15.2 by
   tools/make-av1-encoder-references.py; tests/Egami.Tests/Formats/Av1/Av1ForwardTransformTests.cs makes the same inputs. */
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "av1/common/enums.h"

typedef void (*fwd)(const int16_t *input, int32_t *output, int stride, TX_TYPE tx_type, int bd);
void av1_fwd_txfm2d_4x4_c(const int16_t *, int32_t *, int, TX_TYPE, int);
void av1_fwd_txfm2d_8x8_c(const int16_t *, int32_t *, int, TX_TYPE, int);
void av1_fwd_txfm2d_16x16_c(const int16_t *, int32_t *, int, TX_TYPE, int);
void av1_fwd_txfm2d_32x32_c(const int16_t *, int32_t *, int, TX_TYPE, int);
void av1_fwd_txfm2d_64x64_c(const int16_t *, int32_t *, int, TX_TYPE, int);
void av1_fwd_txfm2d_4x8_c(const int16_t *, int32_t *, int, TX_TYPE, int);
void av1_fwd_txfm2d_8x4_c(const int16_t *, int32_t *, int, TX_TYPE, int);
void av1_fwd_txfm2d_8x16_c(const int16_t *, int32_t *, int, TX_TYPE, int);
void av1_fwd_txfm2d_16x8_c(const int16_t *, int32_t *, int, TX_TYPE, int);
void av1_fwd_txfm2d_16x32_c(const int16_t *, int32_t *, int, TX_TYPE, int);
void av1_fwd_txfm2d_32x16_c(const int16_t *, int32_t *, int, TX_TYPE, int);
void av1_fwd_txfm2d_32x64_c(const int16_t *, int32_t *, int, TX_TYPE, int);
void av1_fwd_txfm2d_64x32_c(const int16_t *, int32_t *, int, TX_TYPE, int);
void av1_fwd_txfm2d_4x16_c(const int16_t *, int32_t *, int, TX_TYPE, int);
void av1_fwd_txfm2d_16x4_c(const int16_t *, int32_t *, int, TX_TYPE, int);
void av1_fwd_txfm2d_8x32_c(const int16_t *, int32_t *, int, TX_TYPE, int);
void av1_fwd_txfm2d_32x8_c(const int16_t *, int32_t *, int, TX_TYPE, int);
void av1_fwd_txfm2d_16x64_c(const int16_t *, int32_t *, int, TX_TYPE, int);
void av1_fwd_txfm2d_64x16_c(const int16_t *, int32_t *, int, TX_TYPE, int);
void av1_fwht4x4_c(const int16_t *input, int32_t *output, int stride);

/* libaom reaches av1_round_shift_array through its run-time dispatch, which this harness leaves out. */
void av1_round_shift_array_c(int32_t *arr, int size, int bit);
void (*av1_round_shift_array)(int32_t *arr, int size, int bit) = av1_round_shift_array_c;

static const fwd funcs[19] = {
  av1_fwd_txfm2d_4x4_c,   av1_fwd_txfm2d_8x8_c,   av1_fwd_txfm2d_16x16_c, av1_fwd_txfm2d_32x32_c, av1_fwd_txfm2d_64x64_c,
  av1_fwd_txfm2d_4x8_c,   av1_fwd_txfm2d_8x4_c,   av1_fwd_txfm2d_8x16_c,  av1_fwd_txfm2d_16x8_c,  av1_fwd_txfm2d_16x32_c,
  av1_fwd_txfm2d_32x16_c, av1_fwd_txfm2d_32x64_c, av1_fwd_txfm2d_64x32_c, av1_fwd_txfm2d_4x16_c,  av1_fwd_txfm2d_16x4_c,
  av1_fwd_txfm2d_8x32_c,  av1_fwd_txfm2d_32x8_c,  av1_fwd_txfm2d_16x64_c, av1_fwd_txfm2d_64x16_c,
};
static const int wide[19] = { 4, 8, 16, 32, 64, 4, 8, 8, 16, 16, 32, 32, 64, 4, 16, 8, 32, 16, 64 };
static const int high[19] = { 4, 8, 16, 32, 64, 8, 4, 16, 8, 32, 16, 64, 32, 16, 4, 32, 8, 64, 16 };
/* The 1D types (0 DCT, 1 ADST, 2 flipped ADST, 3 identity) of each TX_TYPE: vertical, then horizontal. */
static const int vtx[16] = { 0, 1, 0, 1, 2, 0, 2, 1, 2, 3, 0, 3, 1, 3, 2, 3 };
static const int htx[16] = { 0, 0, 1, 1, 0, 2, 2, 2, 1, 3, 3, 0, 3, 1, 3, 2 };

static int allowed(int points, int type) { return points <= 16 || (points == 32 && (type == 0 || type == 3)) || (points == 64 && type == 0); }

/* The input every test makes: an LCG, kind 0 random over the whole range, 1 random small, 2 the extremes, 3 a ramp. */
static uint32_t state;
static uint32_t next(void) { state = state * 1103515245u + 12345u; return state >> 8; }

static void make(int16_t *input, int w, int h, int bd, int kind, uint32_t seed) {
  state = seed;
  int max = (1 << bd) - 1;
  for (int i = 0; i < w * h; i++) {
    int v;
    if (kind == 0) v = (int)(next() % (2 * max + 1)) - max;
    else if (kind == 1) v = (int)(next() % 33) - 16;
    else if (kind == 2) v = (next() & 1) ? max : -max;
    else v = ((i % w) * 37 + (i / w) * 11) % (2 * max + 1) - max;
    input[i] = (int16_t)v;
  }
}

static uint64_t hash(const int32_t *values, int n) {
  uint64_t h = 1469598103934665603ull;
  for (int i = 0; i < n; i++) {
    uint32_t v = (uint32_t)values[i];
    for (int b = 0; b < 4; b++) { h ^= (v >> (8 * b)) & 0xFF; h *= 1099511628211ull; }
  }
  return h;
}

int main(void) {
  static int16_t input[64 * 64];
  static int32_t output[64 * 64];
  for (int size = 0; size < 19; size++) {
    int w = wide[size], h = high[size];
    int cw = w < 32 ? w : 32, ch = h < 32 ? h : 32;
    for (int type = 0; type < 16; type++) {
      if (!allowed(h, vtx[type]) || !allowed(w, htx[type])) continue;
      for (int bd = 8; bd <= 12; bd += 2) {
        for (int kind = 0; kind < 4; kind++) {
          uint32_t seed = (uint32_t)(size * 1000 + type * 50 + bd * 4 + kind);
          make(input, w, h, bd, kind, seed);
          memset(output, 0, sizeof(output));
          funcs[size](input, output, w, (TX_TYPE)type, bd);
          printf("{\"size\": %d, \"type\": %d, \"bd\": %d, \"kind\": %d, \"hash\": \"%016llx\"}\n", size, type, bd, kind,
                 (unsigned long long)hash(output, cw * ch));
        }
      }
    }
  }
  for (int bd = 8; bd <= 12; bd += 2) {
    for (int kind = 0; kind < 4; kind++) {
      uint32_t seed = (uint32_t)(99000 + bd * 4 + kind);
      make(input, 4, 4, bd, kind, seed);
      memset(output, 0, sizeof(output));
      av1_fwht4x4_c(input, output, 4);
      printf("{\"size\": -1, \"type\": 0, \"bd\": %d, \"kind\": %d, \"hash\": \"%016llx\"}\n", bd, kind, (unsigned long long)hash(output, 16));
    }
  }
  return 0;
}
