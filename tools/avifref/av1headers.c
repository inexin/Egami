// Prints the sequence header and the first output frame's header of an AV1 stream as dav1d 1.5.4 reads them, as JSON named
// with the AV1 specification's syntax element and variable names, so the Phase 2.9.2 header reader can be compared with dav1d's.
// Uses dav1d's public API (Dav1dPicture's seq_hdr and frame_hdr) only. Run as an oracle.
//
// Usage: av1headers stream.obu     (a low-overhead Section 5 stream: OBUs with obu_size)
#include <dav1d/dav1d.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int first = 1;

static void field(const char * name, long long value)
{
    printf("%s\"%s\": %lld", first ? "" : ", ", name, value);
    first = 0;
}

static void indexed(const char * name, int i, long long value)
{
    char buffer[96];
    snprintf(buffer, sizeof buffer, "%s[%d]", name, i);
    field(buffer, value);
}

static void indexed2(const char * name, int i, int j, long long value)
{
    char buffer[96];
    snprintf(buffer, sizeof buffer, "%s[%d][%d]", name, i, j);
    field(buffer, value);
}

static long long clip(long long value, long long limit)
{
    return value < -limit ? -limit : value > limit ? limit : value;
}

static void sequence(const Dav1dSequenceHeader * s)
{
    field("seq_profile", s->profile);
    field("still_picture", s->still_picture);
    field("reduced_still_picture_header", s->reduced_still_picture_header);
    field("timing_info_present_flag", s->timing_info_present);
    field("decoder_model_info_present_flag", s->decoder_model_info_present);
    field("operating_points_cnt_minus_1", s->num_operating_points - 1);
    for (int i = 0; i < s->num_operating_points; i++) {
        indexed("operating_point_idc", i, s->operating_points[i].idc);
        // dav1d adds 2 to the major level of a full sequence header but not of a reduced still picture one.
        int major = s->operating_points[i].major_level - (s->reduced_still_picture_header ? 0 : 2);
        indexed("seq_level_idx", i, (major << 2) | s->operating_points[i].minor_level);
        indexed("seq_tier", i, s->operating_points[i].tier);
    }
    field("frame_width_bits_minus_1", s->width_n_bits - 1);
    field("frame_height_bits_minus_1", s->height_n_bits - 1);
    field("max_frame_width_minus_1", s->max_width - 1);
    field("max_frame_height_minus_1", s->max_height - 1);
    field("frame_id_numbers_present_flag", s->frame_id_numbers_present);
    field("use_128x128_superblock", s->sb128);
    field("enable_filter_intra", s->filter_intra);
    field("enable_intra_edge_filter", s->intra_edge_filter);
    field("enable_interintra_compound", s->inter_intra);
    field("enable_masked_compound", s->masked_compound);
    field("enable_warped_motion", s->warped_motion);
    field("enable_dual_filter", s->dual_filter);
    field("enable_order_hint", s->order_hint);
    field("enable_jnt_comp", s->jnt_comp);
    field("enable_ref_frame_mvs", s->ref_frame_mvs);
    field("seq_force_screen_content_tools", s->screen_content_tools);
    field("seq_force_integer_mv", s->force_integer_mv);
    field("OrderHintBits", s->order_hint_n_bits);
    field("enable_superres", s->super_res);
    field("enable_cdef", s->cdef);
    field("enable_restoration", s->restoration);
    field("BitDepth", s->hbd == 2 ? 12 : s->hbd == 1 ? 10 : 8);
    field("mono_chrome", s->monochrome);
    field("color_description_present_flag", s->color_description_present);
    field("color_primaries", s->pri);
    field("transfer_characteristics", s->trc);
    field("matrix_coefficients", s->mtrx);
    field("color_range", s->color_range);
    field("subsampling_x", s->ss_hor);
    field("subsampling_y", s->ss_ver);
    field("chroma_sample_position", s->chr);
    field("separate_uv_delta_q", s->separate_uv_delta_q);
    field("film_grain_params_present", s->film_grain_present);
}

static void frame(const Dav1dFrameHeader * f, const Dav1dSequenceHeader * s)
{
    int planes = s->monochrome ? 1 : 3;
    field("show_existing_frame", f->show_existing_frame);
    field("frame_type", f->frame_type);
    field("show_frame", f->show_frame);
    field("showable_frame", f->showable_frame);
    field("error_resilient_mode", f->error_resilient_mode);
    field("disable_cdf_update", f->disable_cdf_update);
    field("allow_screen_content_tools", f->allow_screen_content_tools);
    field("force_integer_mv", f->force_integer_mv);
    field("frame_size_override_flag", f->frame_size_override);
    field("order_hint", f->frame_offset);
    field("primary_ref_frame", f->primary_ref_frame);
    field("refresh_frame_flags", f->refresh_frame_flags);
    field("FrameWidth", f->width[0]);
    field("UpscaledWidth", f->width[1]);
    field("FrameHeight", f->height);
    field("RenderWidth", f->render_width);
    field("RenderHeight", f->render_height);
    field("use_superres", f->super_res.enabled);
    field("SuperresDenom", f->super_res.width_scale_denominator);
    field("allow_intrabc", f->allow_intrabc);
    field("disable_frame_end_update_cdf", !f->refresh_context);
    field("uniform_tile_spacing_flag", f->tiling.uniform);
    field("TileColsLog2", f->tiling.log2_cols);
    field("TileRowsLog2", f->tiling.log2_rows);
    field("TileCols", f->tiling.cols);
    field("TileRows", f->tiling.rows);
    for (int i = 0; i <= f->tiling.cols; i++)
        indexed("MiColStartSb", i, f->tiling.col_start_sb[i]);
    for (int i = 0; i <= f->tiling.rows; i++)
        indexed("MiRowStartSb", i, f->tiling.row_start_sb[i]);
    field("context_update_tile_id", f->tiling.update);
    if (f->tiling.log2_cols || f->tiling.log2_rows)
        field("TileSizeBytes", f->tiling.n_bytes);
    field("base_q_idx", f->quant.yac);
    field("DeltaQYDc", f->quant.ydc_delta);
    if (planes > 1) {
        field("DeltaQUDc", f->quant.udc_delta);
        field("DeltaQUAc", f->quant.uac_delta);
        field("DeltaQVDc", f->quant.vdc_delta);
        field("DeltaQVAc", f->quant.vac_delta);
    }
    field("using_qmatrix", f->quant.qm);
    if (f->quant.qm) {
        field("qm_y", f->quant.qm_y);
        field("qm_u", f->quant.qm_u);
        field("qm_v", f->quant.qm_v);
    }
    field("segmentation_enabled", f->segmentation.enabled);
    if (f->segmentation.enabled) {
        field("segmentation_update_map", f->segmentation.update_map);
        field("segmentation_temporal_update", f->segmentation.temporal);
        field("segmentation_update_data", f->segmentation.update_data);
        for (int i = 0; i < 8; i++) {
            // dav1d keeps the coded values; the specification's FeatureData is clipped to each feature's limit.
            const Dav1dSegmentationData * d = &f->segmentation.seg_data.d[i];
            indexed2("FeatureData", i, 0, clip(d->delta_q, 255));
            indexed2("FeatureData", i, 1, clip(d->delta_lf_y_v, 63));
            indexed2("FeatureData", i, 2, clip(d->delta_lf_y_h, 63));
            indexed2("FeatureData", i, 3, clip(d->delta_lf_u, 63));
            indexed2("FeatureData", i, 4, clip(d->delta_lf_v, 63));
            indexed("SegRefFrame", i, d->ref);
            indexed("SegSkip", i, d->skip);
            indexed("SegGlobalMv", i, d->globalmv);
        }
        field("SegIdPreSkip", f->segmentation.seg_data.preskip);
        // dav1d gives -1 when no feature is enabled; the specification's LastActiveSegId stays 0.
        field("LastActiveSegId", f->segmentation.seg_data.last_active_segid < 0 ? 0 : f->segmentation.seg_data.last_active_segid);
    }
    for (int i = 0; i < 8; i++) {
        indexed("LosslessArray", i, f->segmentation.lossless[i]);
        indexed("SegQIdx", i, f->segmentation.qidx[i]);
    }
    field("delta_q_present", f->delta.q.present);
    field("delta_q_res", f->delta.q.res_log2);
    field("delta_lf_present", f->delta.lf.present);
    field("delta_lf_res", f->delta.lf.res_log2);
    field("delta_lf_multi", f->delta.lf.multi);
    // dav1d's all_lossless is the specification's CodedLossless (every segment lossless, superres or not).
    field("CodedLossless", f->all_lossless);

    // The loop filter and CDEF fields are printed only when the frame codes them; dav1d leaves its own values otherwise.
    int coded_lossless = 1;
    for (int i = 0; i < 8; i++)
        coded_lossless &= f->segmentation.lossless[i];
    if (!coded_lossless && !f->allow_intrabc) {
        field("loop_filter_level[0]", f->loopfilter.level_y[0]);
        field("loop_filter_level[1]", f->loopfilter.level_y[1]);
        if (planes > 1) {
            field("loop_filter_level[2]", f->loopfilter.level_u);
            field("loop_filter_level[3]", f->loopfilter.level_v);
        }
        field("loop_filter_sharpness", f->loopfilter.sharpness);
        field("loop_filter_delta_enabled", f->loopfilter.mode_ref_delta_enabled);
        field("loop_filter_delta_update", f->loopfilter.mode_ref_delta_update);
        for (int i = 0; i < 8; i++)
            indexed("loop_filter_ref_deltas", i, f->loopfilter.mode_ref_deltas.ref_delta[i]);
        for (int i = 0; i < 2; i++)
            indexed("loop_filter_mode_deltas", i, f->loopfilter.mode_ref_deltas.mode_delta[i]);
    }
    if (!coded_lossless && !f->allow_intrabc && s->cdef) {
        field("CdefDamping", f->cdef.damping);
        field("cdef_bits", f->cdef.n_bits);
        for (int i = 0; i < (1 << f->cdef.n_bits); i++) {
            int sec = f->cdef.y_strength[i] & 3;
            indexed("cdef_y_pri_strength", i, f->cdef.y_strength[i] >> 2);
            indexed("cdef_y_sec_strength", i, sec == 3 ? 4 : sec);
            if (planes > 1) {
                sec = f->cdef.uv_strength[i] & 3;
                indexed("cdef_uv_pri_strength", i, f->cdef.uv_strength[i] >> 2);
                indexed("cdef_uv_sec_strength", i, sec == 3 ? 4 : sec);
            }
        }
    }
    for (int i = 0; i < planes; i++)
        indexed("lr_type", i, f->restoration.type[i]);
    if (f->restoration.type[0] || f->restoration.type[1] || f->restoration.type[2]) {
        field("LoopRestorationSizeLog2[0]", f->restoration.unit_size[0]);
        if (planes > 1 && (f->restoration.type[1] || f->restoration.type[2]))
            field("LoopRestorationSizeLog2[1]", f->restoration.unit_size[1]);
    }
    field("TxMode", f->txfm_mode);
    field("reference_select", f->switchable_comp_refs);
    field("skip_mode_present", f->skip_mode_enabled);
    field("allow_warped_motion", f->warp_motion);
    field("reduced_tx_set", f->reduced_txtp_set);
    field("apply_grain", f->film_grain.present);
    if (f->film_grain.present) {
        const Dav1dFilmGrainData * g = &f->film_grain.data;
        field("update_grain", f->film_grain.update);
        field("grain_seed", g->seed);
        field("num_y_points", g->num_y_points);
        for (int i = 0; i < g->num_y_points; i++) {
            indexed("point_y_value", i, g->y_points[i][0]);
            indexed("point_y_scaling", i, g->y_points[i][1]);
        }
        field("chroma_scaling_from_luma", g->chroma_scaling_from_luma);
        field("num_cb_points", g->num_uv_points[0]);
        for (int i = 0; i < g->num_uv_points[0]; i++) {
            indexed("point_cb_value", i, g->uv_points[0][i][0]);
            indexed("point_cb_scaling", i, g->uv_points[0][i][1]);
        }
        field("num_cr_points", g->num_uv_points[1]);
        for (int i = 0; i < g->num_uv_points[1]; i++) {
            indexed("point_cr_value", i, g->uv_points[1][i][0]);
            indexed("point_cr_scaling", i, g->uv_points[1][i][1]);
        }
        field("grain_scaling_minus_8", g->scaling_shift - 8);
        field("ar_coeff_lag", g->ar_coeff_lag);
        int luma = 2 * g->ar_coeff_lag * (g->ar_coeff_lag + 1);
        if (g->num_y_points)
            for (int i = 0; i < luma; i++)
                indexed("ar_coeffs_y_plus_128", i, g->ar_coeffs_y[i] + 128);
        int chroma = luma + (g->num_y_points ? 1 : 0);
        if (g->chroma_scaling_from_luma || g->num_uv_points[0])
            for (int i = 0; i < chroma; i++)
                indexed("ar_coeffs_cb_plus_128", i, g->ar_coeffs_uv[0][i] + 128);
        if (g->chroma_scaling_from_luma || g->num_uv_points[1])
            for (int i = 0; i < chroma; i++)
                indexed("ar_coeffs_cr_plus_128", i, g->ar_coeffs_uv[1][i] + 128);
        field("ar_coeff_shift_minus_6", (long long)g->ar_coeff_shift - 6);
        field("grain_scale_shift", g->grain_scale_shift);
        if (g->num_uv_points[0]) {
            // dav1d keeps the multipliers less 128 and the offset less 256.
            field("cb_mult", g->uv_mult[0] + 128);
            field("cb_luma_mult", g->uv_luma_mult[0] + 128);
            field("cb_offset", g->uv_offset[0] + 256);
        }
        if (g->num_uv_points[1]) {
            field("cr_mult", g->uv_mult[1] + 128);
            field("cr_luma_mult", g->uv_luma_mult[1] + 128);
            field("cr_offset", g->uv_offset[1] + 256);
        }
        field("overlap_flag", g->overlap_flag);
        field("clip_to_restricted_range", g->clip_to_restricted_range);
    }
}

static void free_data(const uint8_t * data, void * cookie)
{
    (void)cookie;
    free((void *)data);
}

int main(int argc, char * argv[])
{
    if (argc != 2) {
        fprintf(stderr, "Usage: av1headers stream.obu\n");
        return 2;
    }
    FILE * file = fopen(argv[1], "rb");
    if (!file) {
        fprintf(stderr, "Cannot open %s\n", argv[1]);
        return 2;
    }
    fseek(file, 0, SEEK_END);
    long size = ftell(file);
    fseek(file, 0, SEEK_SET);
    uint8_t * bytes = malloc(size > 0 ? size : 1);
    if (fread(bytes, 1, size, file) != (size_t)size) {
        fprintf(stderr, "Cannot read %s\n", argv[1]);
        return 2;
    }
    fclose(file);

    Dav1dSettings settings;
    dav1d_default_settings(&settings);
    settings.n_threads = 1;
    settings.max_frame_delay = 1;
    Dav1dContext * context;
    if (dav1d_open(&context, &settings) < 0) {
        return 2;
    }
    Dav1dData data;
    dav1d_data_wrap(&data, bytes, size, free_data, NULL);
    Dav1dPicture picture = { 0 };
    int result = 0, got = 0;
    while (!got) {
        if (data.sz > 0) {
            result = dav1d_send_data(context, &data);
            if (result < 0 && result != DAV1D_ERR(EAGAIN))
                break;
        }
        result = dav1d_get_picture(context, &picture);
        if (result == 0)
            got = 1;
        else if (result != DAV1D_ERR(EAGAIN) || data.sz == 0) {
            if (data.sz == 0 && result == DAV1D_ERR(EAGAIN)) {
                result = dav1d_get_picture(context, &picture);
                got = result == 0;
            }
            break;
        }
    }
    if (!got) {
        printf("{\"error\": %d}\n", result);
        return 1;
    }
    printf("{\"sequence\": {");
    first = 1;
    sequence(picture.seq_hdr);
    printf("}, \"frame\": {");
    first = 1;
    frame(picture.frame_hdr, picture.seq_hdr);
    printf("}}\n");
    dav1d_picture_unref(&picture);
    dav1d_data_unref(&data);
    dav1d_close(&context);
    return 0;
}
