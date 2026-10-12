/* Prints what libvpx's VP8 decoder read from each frame of an IVF file: the frame header fields (RFC 6386 section 19 names), the
 * entropy state the next frame starts from, and every macroblock's modes, reference, segment and motion vectors. Egami's tests
 * compare their own decode with this, line for line. Built in tools/vp8ref/Dockerfile against libvpx's decoder internals, with
 * one accessor (vp8dump_pbi) appended to vp8_dx_iface.c there. Also writes the shown frames as packed I420 when given a second
 * path. Usage: vp8dump in.ivf [frames.yuv] > dump.txt */
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "vpx/vp8dx.h"
#include "vpx/vpx_decoder.h"
#include "vp8/decoder/onyxd_int.h"

void *vp8dump_pbi(vpx_codec_ctx_t *ctx);

static unsigned read_le(const unsigned char *p, int n) {
  unsigned v = 0;
  for (int i = n - 1; i >= 0; i--) v = (v << 8) | p[i];
  return v;
}

static void print_list(const char *name, const int *v, int n) {
  printf("%s =", name);
  for (int i = 0; i < n; i++) printf(" %d", v[i]);
  printf("\n");
}

static void print_bytes(const char *name, const unsigned char *v, int n) {
  printf("%s =", name);
  for (int i = 0; i < n; i++) printf(" %d", v[i]);
  printf("\n");
}

static void dump_frame(VP8D_COMP *pbi, int index, unsigned size) {
  VP8_COMMON *pc = &pbi->common;
  MACROBLOCKD *xd = &pbi->mb;
  int v[8];
  printf("frame %d size %u\n", index, size);
  printf("key_frame = %d\n", pc->frame_type == KEY_FRAME);
  printf("version = %d\n", pc->version);
  printf("show_frame = %d\n", pc->show_frame);
  printf("width = %d\nheight = %d\nhorizontal_scale = %d\nvertical_scale = %d\n", pc->Width, pc->Height, pc->horiz_scale,
         pc->vert_scale);
  printf("clamping_type = %d\n", pc->clamp_type);
  printf("segmentation_enabled = %d\n", xd->segmentation_enabled);
  printf("update_mb_segmentation_map = %d\n", xd->update_mb_segmentation_map);
  printf("update_segment_feature_data = %d\n", xd->update_mb_segmentation_data);
  printf("segment_feature_mode = %d\n", xd->mb_segment_abs_delta);
  for (int i = 0; i < 4; i++) v[i] = xd->segment_feature_data[MB_LVL_ALT_Q][i];
  print_list("quantizer_update_value", v, 4);
  for (int i = 0; i < 4; i++) v[i] = xd->segment_feature_data[MB_LVL_ALT_LF][i];
  print_list("loop_filter_update_value", v, 4);
  print_bytes("segment_prob", xd->mb_segment_tree_probs, 3);
  printf("filter_type = %d\n", pc->filter_type);
  printf("loop_filter_level = %d\n", pc->filter_level);
  printf("sharpness_level = %d\n", pc->sharpness_level);
  printf("loop_filter_adj_enable = %d\n", xd->mode_ref_lf_delta_enabled);
  printf("mode_ref_lf_delta_update = %d\n", xd->mode_ref_lf_delta_update);
  for (int i = 0; i < 4; i++) v[i] = xd->ref_lf_deltas[i];
  print_list("ref_frame_delta_magnitude", v, 4);
  for (int i = 0; i < 4; i++) v[i] = xd->mode_lf_deltas[i];
  print_list("mb_mode_delta_magnitude", v, 4);
  printf("log2_nbr_of_dct_partitions = %d\n", pc->multi_token_partition);
  printf("y_ac_qi = %d\ny_dc_delta = %d\ny2_dc_delta = %d\ny2_ac_delta = %d\nuv_dc_delta = %d\nuv_ac_delta = %d\n",
         pc->base_qindex, pc->y1dc_delta_q, pc->y2dc_delta_q, pc->y2ac_delta_q, pc->uvdc_delta_q, pc->uvac_delta_q);
  printf("refresh_golden_frame = %d\n", pc->refresh_golden_frame);
  printf("refresh_alternate_frame = %d\n", pc->refresh_alt_ref_frame);
  printf("copy_buffer_to_golden = %d\n", pc->copy_buffer_to_gf);
  printf("copy_buffer_to_alternate = %d\n", pc->copy_buffer_to_arf);
  printf("sign_bias_golden = %d\n", pc->ref_frame_sign_bias[GOLDEN_FRAME]);
  printf("sign_bias_alternate = %d\n", pc->ref_frame_sign_bias[ALTREF_FRAME]);
  printf("refresh_entropy_probs = %d\n", pc->refresh_entropy_probs);
  printf("refresh_last = %d\n", pc->refresh_last_frame);
  printf("mb_no_coeff_skip = %d\n", pc->mb_no_coeff_skip);
  printf("prob_skip_false = %d\n", pbi->prob_skip_false);
  if (pc->frame_type != KEY_FRAME) {
    printf("prob_intra = %d\nprob_last = %d\nprob_gf = %d\n", pbi->prob_intra, pbi->prob_last, pbi->prob_gf);
  }

  /* The entropy state the next frame starts from (after the restore when refresh_entropy_probs is 0). */
  print_bytes("next_intra_16x16_prob", pc->fc.ymode_prob, 4);
  print_bytes("next_intra_chroma_prob", pc->fc.uv_mode_prob, 3);
  print_bytes("next_mv_prob_row", pc->fc.mvc[0].prob, MVPcount);
  print_bytes("next_mv_prob_column", pc->fc.mvc[1].prob, MVPcount);
  {
    /* FNV-1a over the coefficient probabilities in bitstream order. */
    const unsigned char *p = &pc->fc.coef_probs[0][0][0][0];
    uint32_t h = 2166136261u;
    for (size_t i = 0; i < sizeof(pc->fc.coef_probs); i++) h = (h ^ p[i]) * 16777619u;
    printf("next_coeff_prob_hash = %u\n", h);
  }

  /* Macroblocks: row column ref mode uv segment skip need_to_clamp mv_row mv_col, then for B_PRED the 16 sub-block modes and for
   * SPLITMV the partitioning and the 16 sub-block vectors (row,col). Vectors are in libvpx's units, an eighth of a luma pixel; an
   * intra macroblock's is printed as 0 (a key frame leaves the previous frame's there, unused). */
  for (int r = 0; r < pc->mb_rows; r++) {
    for (int c = 0; c < pc->mb_cols; c++) {
      const MODE_INFO *mi = pc->mi + r * pc->mode_info_stride + c;
      const MB_MODE_INFO *m = &mi->mbmi;
      printf("mb %d %d %d %d %d %d %d %d %d %d", r, c, m->ref_frame, m->mode, m->ref_frame == INTRA_FRAME ? m->uv_mode : 0,
             m->segment_id, m->mb_skip_coeff, m->ref_frame == INTRA_FRAME ? 0 : m->need_to_clamp_mvs,
             m->ref_frame == INTRA_FRAME ? 0 : m->mv.as_mv.row, m->ref_frame == INTRA_FRAME ? 0 : m->mv.as_mv.col);
      if (m->mode == B_PRED) {
        printf(" b");
        for (int i = 0; i < 16; i++) printf(" %d", mi->bmi[i].as_mode);
      } else if (m->mode == SPLITMV) {
        printf(" s %d", m->partitioning);
        for (int i = 0; i < 16; i++) printf(" %d,%d", mi->bmi[i].mv.as_mv.row, mi->bmi[i].mv.as_mv.col);
      }
      printf("\n");
    }
  }
}

int main(int argc, char **argv) {
  if (argc < 2) {
    fprintf(stderr, "usage: vp8dump in.ivf [frames.yuv]\n");
    return 2;
  }

  FILE *in = fopen(argv[1], "rb");
  FILE *out = argc > 2 ? fopen(argv[2], "wb") : NULL;
  unsigned char header[32];
  if (!in || fread(header, 1, 32, in) != 32 || memcmp(header, "DKIF", 4) != 0) {
    fprintf(stderr, "not an IVF file\n");
    return 1;
  }

  fseek(in, read_le(header + 6, 2), SEEK_SET);
  vpx_codec_ctx_t codec;
  vpx_codec_dec_cfg_t cfg = { 1, 0, 0 };
  if (vpx_codec_dec_init(&codec, vpx_codec_vp8_dx(), &cfg, 0)) {
    fprintf(stderr, "decoder init failed\n");
    return 1;
  }

  unsigned char frame_header[12];
  unsigned char *data = NULL;
  int index = 0;
  while (fread(frame_header, 1, 12, in) == 12) {
    unsigned size = read_le(frame_header, 4);
    data = realloc(data, size ? size : 1);
    if (fread(data, 1, size, in) != size) break;
    vpx_codec_err_t err = vpx_codec_decode(&codec, data, size, NULL, 0);
    VP8D_COMP *pbi = vp8dump_pbi(&codec);
    if (err) {
      printf("frame %d size %u error %d %s\n", index, size, err, vpx_codec_error_detail(&codec) ? vpx_codec_error_detail(&codec) : "");
    } else if (pbi) {
      dump_frame(pbi, index, size);
    }

    vpx_codec_iter_t iter = NULL;
    vpx_image_t *img;
    while ((img = vpx_codec_get_frame(&codec, &iter)) != NULL) {
      printf("shown %d %ux%u\n", index, img->d_w, img->d_h);
      if (out) {
        for (int p = 0; p < 3; p++) {
          unsigned w = p ? (img->d_w + 1) / 2 : img->d_w;
          unsigned h = p ? (img->d_h + 1) / 2 : img->d_h;
          for (unsigned y = 0; y < h; y++) fwrite(img->planes[p] + y * img->stride[p], 1, w, out);
        }
      }
    }

    index++;
  }

  vpx_codec_destroy(&codec);
  free(data);
  if (out) fclose(out);
  fclose(in);
  return 0;
}
