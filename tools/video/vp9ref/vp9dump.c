/* vp9dump: decodes a VP9 IVF file with libvpx and prints, for every frame libvpx decodes (each frame of a superframe on its own),
 * what it read: the uncompressed and compressed header fields as VP9_COMMON holds them, the probabilities after backward adaptation
 * (the FRAME_CONTEXT bytes in libvpx's layout, as hex), every block's modes and motion vectors, and the MD5 of the decoded frame
 * (packed planes at the coded size, 16-bit little-endian samples above 8 bits). Egami.Video's tests compare their own decoder's
 * state with these lines. Built in tools/video/vp9ref against libvpx 1.14.1, whose vp9_receive_compressed_data calls vp9dump_hook after
 * each frame.
 *
 * Usage: vp9dump file.ivf [--no-blocks] > dump.txt
 */
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "md5_utils.h"
#include "vpx/vp8dx.h"
#include "vpx/vpx_decoder.h"
#include "vpx_ports/mem.h"
#include "vp9/common/vp9_onyxc_int.h"
#include "vp9/decoder/vp9_decoder.h"

static int g_blocks = 1;
static int g_frame = 0;

static void hash_plane(MD5Context *md5, const uint8_t *buf, int stride, int w, int h, int high) {
  for (int y = 0; y < h; y++) {
    if (high) {
      const uint16_t *row = CONVERT_TO_SHORTPTR(buf) + (size_t)y * stride;
      for (int x = 0; x < w; x++) {
        uint8_t le[2] = { (uint8_t)row[x], (uint8_t)(row[x] >> 8) };
        MD5Update(md5, le, 2);
      }
    } else {
      MD5Update(md5, buf + (size_t)y * stride, w);
    }
  }
}

static void print_md5(const char *name, const YV12_BUFFER_CONFIG *b) {
  MD5Context md5;
  unsigned char digest[16];
  int high = (b->flags & YV12_FLAG_HIGHBITDEPTH) != 0;
  MD5Init(&md5);
  hash_plane(&md5, b->y_buffer, b->y_stride, b->y_crop_width, b->y_crop_height, high);
  hash_plane(&md5, b->u_buffer, b->uv_stride, b->uv_crop_width, b->uv_crop_height, high);
  hash_plane(&md5, b->v_buffer, b->uv_stride, b->uv_crop_width, b->uv_crop_height, high);
  MD5Final(digest, &md5);
  printf("%s ", name);
  for (int i = 0; i < 16; i++) printf("%02x", digest[i]);
  printf("\n");
}

void vp9dump_hook(VP9Decoder *pbi) {
  VP9_COMMON *cm = &pbi->common;
  printf("frame %d\n", g_frame++);
  printf("profile %d\n", cm->profile);
  printf("show_existing_frame %d\n", cm->show_existing_frame);
  if (cm->show_existing_frame) {
    print_md5("md5", &cm->buffer_pool->frame_bufs[cm->new_fb_idx].buf);
    return;
  }

  printf("frame_type %d\n", cm->frame_type);
  printf("show_frame %d\n", cm->show_frame);
  printf("error_resilient_mode %d\n", cm->error_resilient_mode);
  printf("intra_only %d\n", cm->intra_only);
  printf("reset_frame_context %d\n", cm->reset_frame_context);
  printf("bit_depth %d\n", (int)cm->bit_depth);
  printf("color_space %d\n", (int)cm->color_space);
  printf("color_range %d\n", (int)cm->color_range);
  printf("subsampling_x %d\n", cm->subsampling_x);
  printf("subsampling_y %d\n", cm->subsampling_y);
  printf("refresh_frame_flags %d\n", pbi->refresh_frame_flags);
  printf("frame_width %d\n", cm->width);
  printf("frame_height %d\n", cm->height);
  printf("render_width %d\n", cm->render_width);
  printf("render_height %d\n", cm->render_height);
  for (int i = 1; i <= 3; i++) printf("ref_frame_sign_bias[%d] %d\n", i, cm->ref_frame_sign_bias[i]);
  printf("allow_high_precision_mv %d\n", cm->allow_high_precision_mv);
  printf("interp_filter %d\n", (int)cm->interp_filter);
  printf("refresh_frame_context %d\n", cm->refresh_frame_context);
  printf("frame_parallel_decoding_mode %d\n", cm->frame_parallel_decoding_mode);
  printf("frame_context_idx %u\n", cm->frame_context_idx);
  printf("loop_filter_level %d\n", cm->lf.filter_level);
  printf("loop_filter_sharpness %d\n", cm->lf.sharpness_level);
  printf("loop_filter_delta_enabled %d\n", cm->lf.mode_ref_delta_enabled);
  for (int i = 0; i < 4; i++) printf("loop_filter_ref_deltas[%d] %d\n", i, cm->lf.ref_deltas[i]);
  for (int i = 0; i < 2; i++) printf("loop_filter_mode_deltas[%d] %d\n", i, cm->lf.mode_deltas[i]);
  printf("base_q_idx %d\n", cm->base_qindex);
  printf("delta_q_y_dc %d\n", cm->y_dc_delta_q);
  printf("delta_q_uv_dc %d\n", cm->uv_dc_delta_q);
  printf("delta_q_uv_ac %d\n", cm->uv_ac_delta_q);
  printf("segmentation_enabled %d\n", cm->seg.enabled);
  if (cm->seg.enabled) {
    printf("segmentation_update_map %d\n", cm->seg.update_map);
    printf("segmentation_temporal_update %d\n", cm->seg.temporal_update);
    printf("segmentation_abs_or_delta_update %d\n", cm->seg.abs_delta);
    for (int i = 0; i < 7; i++) printf("segmentation_tree_probs[%d] %d\n", i, cm->seg.tree_probs[i]);
    for (int i = 0; i < 3; i++) printf("segmentation_pred_prob[%d] %d\n", i, cm->seg.pred_probs[i]);
    for (int s = 0; s < 8; s++)
      for (int f = 0; f < 4; f++)
        if (cm->seg.feature_mask[s] & (1u << f)) printf("feature_value[%d][%d] %d\n", s, f, cm->seg.feature_data[s][f]);
  }
  printf("tile_cols_log2 %d\n", cm->log2_tile_cols);
  printf("tile_rows_log2 %d\n", cm->log2_tile_rows);
  printf("tx_mode %d\n", (int)cm->tx_mode);
  printf("reference_select %d\n", (int)cm->reference_mode);
  printf("comp_fixed_ref %d\n", (int)cm->comp_fixed_ref);
  printf("comp_var_ref %d %d\n", (int)cm->comp_var_ref[0], (int)cm->comp_var_ref[1]);

  /* The adapted probabilities: every byte of FRAME_CONTEXT before 'initialized', in libvpx's layout. */
  printf("probs ");
  const uint8_t *fc = (const uint8_t *)cm->fc;
  for (size_t i = 0; i < offsetof(FRAME_CONTEXT, initialized); i++) printf("%02x", fc[i]);
  printf("\n");

  if (g_blocks) {
    printf("mi %d %d\n", cm->mi_rows, cm->mi_cols);
    for (int r = 0; r < cm->mi_rows; r++) {
      for (int c = 0; c < cm->mi_cols; c++) {
        MODE_INFO *mi = cm->mi_grid_visible[r * cm->mi_stride + c];
        if (mi == NULL) continue;
        if (c > 0 && cm->mi_grid_visible[r * cm->mi_stride + c - 1] == mi) continue;
        if (r > 0 && cm->mi_grid_visible[(r - 1) * cm->mi_stride + c] == mi) continue;
        printf("b %d %d %d %d %d %d %d %d %d %d %d %d %d %d %d %d", r, c, mi->sb_type, mi->mode, mi->uv_mode, mi->ref_frame[0],
               mi->ref_frame[1], mi->mv[0].as_mv.row, mi->mv[0].as_mv.col, mi->mv[1].as_mv.row, mi->mv[1].as_mv.col, mi->tx_size,
               mi->skip, mi->segment_id, mi->interp_filter, mi->seg_id_predicted);
        if (mi->sb_type < BLOCK_8X8) {
          for (int i = 0; i < 4; i++)
            printf(" %d:%d,%d:%d,%d", mi->bmi[i].as_mode, mi->bmi[i].as_mv[0].as_mv.row, mi->bmi[i].as_mv[0].as_mv.col,
                   mi->bmi[i].as_mv[1].as_mv.row, mi->bmi[i].as_mv[1].as_mv.col);
        }
        printf("\n");
      }
    }
  }

  print_md5("md5", &cm->buffer_pool->frame_bufs[cm->new_fb_idx].buf);
}

static unsigned int le32(const unsigned char *p) { return p[0] | (p[1] << 8) | (p[2] << 16) | ((unsigned int)p[3] << 24); }

int main(int argc, char **argv) {
  if (argc < 2) {
    fprintf(stderr, "usage: vp9dump file.ivf [--no-blocks]\n");
    return 2;
  }
  if (argc > 2 && strcmp(argv[2], "--no-blocks") == 0) g_blocks = 0;
  FILE *f = fopen(argv[1], "rb");
  if (!f) return 1;
  unsigned char header[32];
  if (fread(header, 1, 32, f) != 32 || memcmp(header, "DKIF", 4) != 0) return 1;
  vpx_codec_ctx_t codec;
  vpx_codec_dec_cfg_t cfg = { 1, 0, 0 };
  if (vpx_codec_dec_init(&codec, vpx_codec_vp9_dx(), &cfg, 0)) return 1;
  unsigned char fh[12];
  int packet = 0;
  unsigned char *buf = NULL;
  while (fread(fh, 1, 12, f) == 12) {
    unsigned int size = le32(fh);
    buf = realloc(buf, size ? size : 1);
    if (fread(buf, 1, size, f) != size) break;
    printf("packet %d %u\n", packet, size);
    if (vpx_codec_decode(&codec, buf, size, NULL, 0)) {
      const char *detail = vpx_codec_error_detail(&codec);
      printf("error %s %s\n", vpx_codec_error(&codec), detail ? detail : "");
    }
    vpx_codec_iter_t iter = NULL;
    vpx_image_t *img;
    while ((img = vpx_codec_get_frame(&codec, &iter)) != NULL) printf("shown %ux%u\n", img->d_w, img->d_h);
    packet++;
  }
  free(buf);
  vpx_codec_destroy(&codec);
  return 0;
}
