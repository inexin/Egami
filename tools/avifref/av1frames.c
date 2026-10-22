// Decodes an AV1 stream with libaom 3.15.2 (its public decoder API only) and writes each output frame to stdout for
// tools/avifref/av1corpus.py to hash: a 24-byte header (width, height, bit depth, monochrome flag, chroma shift x and y; little-endian uint32 each),
// then the planes Y, U and V (Y only when monochrome), cropped to the frame, as little-endian 16-bit samples whatever the depth.
// Run as an oracle for Phase 2.9.5.
//
// Usage: av1frames [--no-grain] [--annexb] [--oppoint N] [--all-layers] stream
//   The stream is IVF when it starts with DKIF, else a low-overhead (Section 5) OBU stream, or Annex B with --annexb.
#include <aom/aom_decoder.h>
#include <aom/aomdx.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static unsigned char * read_all(const char * path, size_t * size)
{
    FILE * f = fopen(path, "rb");
    if (!f)
    {
        perror(path);
        exit(2);
    }

    fseek(f, 0, SEEK_END);
    *size = (size_t)ftell(f);
    fseek(f, 0, SEEK_SET);
    unsigned char * data = malloc(*size + 1);
    if (fread(data, 1, *size, f) != *size)
    {
        exit(2);
    }

    fclose(f);
    return data;
}

static void put32(uint32_t v)
{
    unsigned char b[4] = { v & 255, (v >> 8) & 255, (v >> 16) & 255, v >> 24 };
    fwrite(b, 1, 4, stdout);
}

static void write_frame(const aom_image_t * img)
{
    int mono = img->monochrome;
    int high = (img->fmt & AOM_IMG_FMT_HIGHBITDEPTH) != 0;
    put32(img->d_w);
    put32(img->d_h);
    put32(img->bit_depth);
    put32(mono);
    put32(img->x_chroma_shift);
    put32(img->y_chroma_shift);
    unsigned char * row = malloc(img->d_w * 2);
    for (int plane = 0; plane < (mono ? 1 : 3); plane++)
    {
        int w = plane ? (img->d_w + img->x_chroma_shift) >> img->x_chroma_shift : img->d_w;
        int h = plane ? (img->d_h + img->y_chroma_shift) >> img->y_chroma_shift : img->d_h;
        for (int y = 0; y < h; y++)
        {
            const unsigned char * src = img->planes[plane] + (size_t)y * img->stride[plane];
            for (int x = 0; x < w; x++)
            {
                unsigned v = high ? ((const uint16_t *)src)[x] : src[x];
                row[2 * x] = v & 255;
                row[2 * x + 1] = v >> 8;
            }

            fwrite(row, 1, (size_t)w * 2, stdout);
        }
    }

    free(row);
}

static void decode(aom_codec_ctx_t * codec, const unsigned char * data, size_t size)
{
    if (aom_codec_decode(codec, data, size, NULL) != AOM_CODEC_OK)
    {
        fprintf(stderr, "decode failed: %s\n", aom_codec_error_detail(codec) ? aom_codec_error_detail(codec) : aom_codec_error(codec));
    }

    aom_codec_iter_t iter = NULL;
    aom_image_t * img;
    while ((img = aom_codec_get_frame(codec, &iter)) != NULL)
    {
        write_frame(img);
    }
}

static size_t leb128(const unsigned char * p, size_t n, size_t * len)
{
    size_t value = 0;
    for (size_t i = 0; i < 8 && i < n; i++)
    {
        value |= (size_t)(p[i] & 127) << (7 * i);
        if (!(p[i] & 128))
        {
            *len = i + 1;
            return value;
        }
    }

    *len = n;
    return value;
}

int main(int argc, char ** argv)
{
    int no_grain = 0, annexb = 0, oppoint = 0, all_layers = 0;
    const char * path = NULL;
    for (int i = 1; i < argc; i++)
    {
        if (!strcmp(argv[i], "--no-grain")) no_grain = 1;
        else if (!strcmp(argv[i], "--annexb")) annexb = 1;
        else if (!strcmp(argv[i], "--all-layers")) all_layers = 1;
        else if (!strcmp(argv[i], "--oppoint") && i + 1 < argc) oppoint = atoi(argv[++i]);
        else path = argv[i];
    }

    size_t size;
    unsigned char * data = read_all(path, &size);
    aom_codec_ctx_t codec;
    aom_codec_dec_cfg_t cfg = { 0 };
    cfg.threads = 1;
    cfg.allow_lowbitdepth = 1;
    if (aom_codec_dec_init(&codec, aom_codec_av1_dx(), &cfg, 0))
    {
        return 2;
    }

    aom_codec_control(&codec, AV1D_SET_SKIP_FILM_GRAIN, no_grain);
    aom_codec_control(&codec, AV1D_SET_IS_ANNEXB, annexb);
    aom_codec_control(&codec, AV1D_SET_OPERATING_POINT, oppoint);
    aom_codec_control(&codec, AV1D_SET_OUTPUT_ALL_LAYERS, all_layers);
    if (size >= 32 && !memcmp(data, "DKIF", 4))
    {
        size_t at = data[6] | (data[7] << 8);
        while (at + 12 <= size)
        {
            size_t n = data[at] | (data[at + 1] << 8) | (data[at + 2] << 16) | ((size_t)data[at + 3] << 24);
            at += 12;
            if (n > size - at)
            {
                break;
            }

            decode(&codec, data + at, n);
            at += n;
        }
    }
    else if (annexb)
    {
        // Annex B: one temporal_unit(size) at a time.
        size_t at = 0;
        while (at < size)
        {
            size_t len;
            size_t n = leb128(data + at, size - at, &len);
            if (n > size - at - len)
            {
                break;
            }

            decode(&codec, data + at, n + len);
            at += len + n;
        }
    }
    else
    {
        // Section 5: split at temporal delimiters.
        size_t start = 0, at = 0;
        while (at < size)
        {
            int type = (data[at] >> 3) & 15, ext = (data[at] >> 2) & 1;
            size_t len;
            size_t n = leb128(data + at + 1 + ext, size - at - 1 - ext, &len);
            if (type == 2 && at > start)
            {
                decode(&codec, data + start, at - start);
                start = at;
            }

            at += 1 + ext + len + n;
        }

        if (at > start)
        {
            decode(&codec, data + start, size - start);
        }
    }

    // Flush.
    decode(&codec, NULL, 0);
    aom_codec_destroy(&codec);
    return 0;
}
