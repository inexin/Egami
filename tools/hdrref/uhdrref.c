// libultrahdr references for Egami's gain map tests.
//
//   uhdrref meta in.jpg                  prints the gain map metadata and sizes as JSON
//   uhdrref decode in.jpg boost out.raw  decodes to linear half-float RGBA at a display boost (headroom) and writes int32
//                                        width and height, then the pixels (8 bytes each)
//   uhdrref time in.jpg boost runs       decodes runs times, prints the best time in milliseconds
//   uhdrref encode in.rgbahalf w h scale runs
//                                        encodes linear Rec. 709 half-float RGBA as Ultra HDR with the defaults and a gain map
//                                        scale, runs times; prints the best time in milliseconds and the file size
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#include "ultrahdr_api.h"

static unsigned char* readFile(const char* path, size_t* size)
{
    FILE* f = fopen(path, "rb");
    if (!f) return NULL;
    fseek(f, 0, SEEK_END);
    *size = (size_t)ftell(f);
    fseek(f, 0, SEEK_SET);
    unsigned char* data = malloc(*size);
    if (fread(data, 1, *size, f) != *size) { fclose(f); free(data); return NULL; }
    fclose(f);
    return data;
}

static uhdr_codec_private_t* open(unsigned char* data, size_t size, float boost)
{
    uhdr_codec_private_t* dec = uhdr_create_decoder();
    uhdr_compressed_image_t image = { data, size, size, UHDR_CG_UNSPECIFIED, UHDR_CT_UNSPECIFIED, UHDR_CR_UNSPECIFIED };
    uhdr_error_info_t e = uhdr_dec_set_image(dec, &image);
    if (e.error_code == UHDR_CODEC_OK) e = uhdr_dec_set_out_img_format(dec, UHDR_IMG_FMT_64bppRGBAHalfFloat);
    if (e.error_code == UHDR_CODEC_OK) e = uhdr_dec_set_out_color_transfer(dec, UHDR_CT_LINEAR);
    if (e.error_code == UHDR_CODEC_OK && boost > 0) e = uhdr_dec_set_out_max_display_boost(dec, boost);
    if (e.error_code != UHDR_CODEC_OK) {
        fprintf(stderr, "%s\n", e.has_detail ? e.detail : "setup failed");
        exit(1);
    }
    return dec;
}

static double now(void)
{
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return t.tv_sec * 1e3 + t.tv_nsec / 1e6;
}

int main(int argc, char** argv)
{
    if (argc < 3) {
        fprintf(stderr, "usage: uhdrref meta in.jpg | decode in.jpg boost out.raw | time in.jpg boost runs\n");
        return 2;
    }
    size_t size;
    unsigned char* data = readFile(argv[2], &size);
    if (!data) return 1;
    if (strcmp(argv[1], "meta") == 0) {
        uhdr_codec_private_t* dec = open(data, size, 0);
        uhdr_error_info_t e = uhdr_dec_probe(dec);
        if (e.error_code != UHDR_CODEC_OK) { fprintf(stderr, "%s\n", e.has_detail ? e.detail : "probe failed"); return 1; }
        uhdr_gainmap_metadata_t* m = uhdr_dec_get_gainmap_metadata(dec);
        printf("{\"width\":%d,\"height\":%d,\"gainmapWidth\":%d,\"gainmapHeight\":%d,", uhdr_dec_get_image_width(dec),
               uhdr_dec_get_image_height(dec), uhdr_dec_get_gainmap_width(dec), uhdr_dec_get_gainmap_height(dec));
        printf("\"maxContentBoost\":[%.9g,%.9g,%.9g],\"minContentBoost\":[%.9g,%.9g,%.9g],\"gamma\":[%.9g,%.9g,%.9g],",
               m->max_content_boost[0], m->max_content_boost[1], m->max_content_boost[2], m->min_content_boost[0],
               m->min_content_boost[1], m->min_content_boost[2], m->gamma[0], m->gamma[1], m->gamma[2]);
        printf("\"offsetSdr\":[%.9g,%.9g,%.9g],\"offsetHdr\":[%.9g,%.9g,%.9g],", m->offset_sdr[0], m->offset_sdr[1], m->offset_sdr[2],
               m->offset_hdr[0], m->offset_hdr[1], m->offset_hdr[2]);
        printf("\"hdrCapacityMin\":%.9g,\"hdrCapacityMax\":%.9g,\"useBaseColorSpace\":%d,", m->hdr_capacity_min, m->hdr_capacity_max,
               m->use_base_cg);
        // The gamut of the linear decode (0 BT.709, 1 Display P3, 2 BT.2100): the HDR intent's, not always the base image's.
        e = uhdr_decode(dec);
        printf("\"outputGamut\":%d}\n", e.error_code == UHDR_CODEC_OK ? (int)uhdr_get_decoded_image(dec)->cg : -1);
        uhdr_release_decoder(dec);
        return 0;
    }
    float boost = argc > 3 ? (float)atof(argv[3]) : 0;
    if (strcmp(argv[1], "decode") == 0 && argc == 5) {
        uhdr_codec_private_t* dec = open(data, size, boost);
        uhdr_error_info_t e = uhdr_decode(dec);
        if (e.error_code != UHDR_CODEC_OK) { fprintf(stderr, "%s\n", e.has_detail ? e.detail : "decode failed"); return 1; }
        uhdr_raw_image_t* img = uhdr_get_decoded_image(dec);
        FILE* f = fopen(argv[4], "wb");
        int header[2] = { (int)img->w, (int)img->h };
        fwrite(header, sizeof header, 1, f);
        for (unsigned int y = 0; y < img->h; y++) {
            fwrite((unsigned char*)img->planes[UHDR_PLANE_PACKED] + (size_t)y * img->stride[UHDR_PLANE_PACKED] * 8, 8, img->w, f);
        }
        fclose(f);
        uhdr_release_decoder(dec);
        return 0;
    }
    if (strcmp(argv[1], "time") == 0 && argc == 5) {
        double best = 1e30;
        for (int r = 0; r < atoi(argv[4]); r++) {
            double t0 = now();
            uhdr_codec_private_t* dec = open(data, size, boost);
            uhdr_decode(dec);
            uhdr_release_decoder(dec);
            double t = now() - t0;
            if (t < best) best = t;
        }
        printf("%s %.3f\n", argv[2], best);
        return 0;
    }
    if (strcmp(argv[1], "encode") == 0 && argc == 7) {
        int w = atoi(argv[3]), h = atoi(argv[4]), scale = atoi(argv[5]);
        double best = 1e30;
        size_t bytes = 0;
        for (int r = 0; r < atoi(argv[6]); r++) {
            double t0 = now();
            uhdr_codec_private_t* enc = uhdr_create_encoder();
            uhdr_raw_image_t raw = { UHDR_IMG_FMT_64bppRGBAHalfFloat, UHDR_CG_BT_709, UHDR_CT_LINEAR, UHDR_CR_FULL_RANGE, (unsigned)w, (unsigned)h,
                                     { data, NULL, NULL }, { (unsigned)w, 0, 0 } };
            uhdr_error_info_t e = uhdr_enc_set_raw_image(enc, &raw, UHDR_HDR_IMG);
            if (e.error_code == UHDR_CODEC_OK) e = uhdr_enc_set_gainmap_scale_factor(enc, scale);
            if (e.error_code == UHDR_CODEC_OK) e = uhdr_encode(enc);
            if (e.error_code != UHDR_CODEC_OK) { fprintf(stderr, "%s\n", e.has_detail ? e.detail : "encode failed"); return 1; }
            bytes = uhdr_get_encoded_stream(enc)->data_sz;
            uhdr_release_encoder(enc);
            double t = now() - t0;
            if (t < best) best = t;
        }
        printf("%s %.3f %zu\n", argv[2], best, bytes);
        return 0;
    }
    return 2;
}
