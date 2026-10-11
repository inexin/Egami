/*
 * WebP reference helper for tools/make-webp-references.py, built inside its alpine container against libwebp (decode, encode,
 * demux). Prints one JSON object on stdout per call.
 *
 *   webpref version
 *   webpref info <in.webp>
 *       WebPGetFeatures and WebPDemux: width, height, alpha, animation, format, canvas, frame count, loop count, background.
 *   webpref decode <in.webp> <out.rgba> [nofancy]
 *       WebPDecode to RGBA, 8 bits a channel, rows packed (fancy upsampling unless nofancy, no dithering).
 *   webpref yuv <in.webp> <out.yuv>
 *       WebPDecodeYUV of a lossy still: the Y plane (width × height), then U and V ((width + 1) / 2 × (height + 1) / 2), packed.
 *   webpref anim <in.webp> <out.rgba>
 *       WebPAnimDecoder (RGBA, no threads): every composed canvas, one after the other; prints each frame's timestamp.
 *   webpref encode <in.rgba> <width> <height> <out.webp> [key=value ...]
 *       WebPEncode of raw RGBA with WebPConfigInit defaults changed by: lossless, q, m, z (WebPConfigLosslessPreset),
 *       partitions, segments, sns, f, sharpness, strong, autofilter, alpha_method, alpha_filter, alpha_q, exact, near, sharp_yuv,
 *       pass. Prints the size and the config used.
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <webp/decode.h>
#include <webp/demux.h>
#include <webp/encode.h>

static unsigned char *read_file(const char *path, size_t *size)
{
    FILE *f = fopen(path, "rb");
    if (!f) { perror(path); exit(1); }
    fseek(f, 0, SEEK_END);
    long n = ftell(f);
    fseek(f, 0, SEEK_SET);
    unsigned char *data = malloc(n > 0 ? n : 1);
    if (fread(data, 1, n, f) != (size_t)n) { perror(path); exit(1); }
    fclose(f);
    *size = n;
    return data;
}

static void write_file(const char *path, const void *data, size_t size)
{
    FILE *f = fopen(path, "wb");
    if (!f || fwrite(data, 1, size, f) != size) { perror(path); exit(1); }
    fclose(f);
}

static void fail(const char *what)
{
    fprintf(stderr, "%s\n", what);
    exit(1);
}

static int info(const char *path)
{
    size_t size;
    unsigned char *data = read_file(path, &size);
    WebPBitstreamFeatures features;
    if (WebPGetFeatures(data, size, &features) != VP8_STATUS_OK) fail("WebPGetFeatures failed");
    WebPData whole = { data, size };
    WebPDemuxer *demux = WebPDemux(&whole);
    if (!demux) fail("WebPDemux failed");
    printf("{\"width\": %d, \"height\": %d, \"alpha\": %s, \"animation\": %s, \"format\": %d, \"canvasWidth\": %u, "
           "\"canvasHeight\": %u, \"frames\": %u, \"loop\": %u, \"background\": %u, \"flags\": %u}\n",
           features.width, features.height, features.has_alpha ? "true" : "false", features.has_animation ? "true" : "false",
           features.format, WebPDemuxGetI(demux, WEBP_FF_CANVAS_WIDTH), WebPDemuxGetI(demux, WEBP_FF_CANVAS_HEIGHT),
           WebPDemuxGetI(demux, WEBP_FF_FRAME_COUNT), WebPDemuxGetI(demux, WEBP_FF_LOOP_COUNT),
           WebPDemuxGetI(demux, WEBP_FF_BACKGROUND_COLOR), WebPDemuxGetI(demux, WEBP_FF_FORMAT_FLAGS));
    WebPDemuxDelete(demux);
    free(data);
    return 0;
}

static int decode(const char *path, const char *out, int nofancy)
{
    size_t size;
    unsigned char *data = read_file(path, &size);
    WebPDecoderConfig config;
    if (!WebPInitDecoderConfig(&config)) fail("WebPInitDecoderConfig failed");
    config.options.no_fancy_upsampling = nofancy;
    config.output.colorspace = MODE_RGBA;
    if (WebPDecode(data, size, &config) != VP8_STATUS_OK) fail("WebPDecode failed");
    WebPRGBABuffer *rgba = &config.output.u.RGBA;
    int w = config.output.width, h = config.output.height;
    unsigned char *packed = malloc((size_t)w * h * 4);
    for (int y = 0; y < h; y++) memcpy(packed + (size_t)y * w * 4, rgba->rgba + (size_t)y * rgba->stride, (size_t)w * 4);
    write_file(out, packed, (size_t)w * h * 4);
    printf("{\"width\": %d, \"height\": %d}\n", w, h);
    free(packed);
    WebPFreeDecBuffer(&config.output);
    free(data);
    return 0;
}

static int yuv(const char *path, const char *out)
{
    size_t size;
    unsigned char *data = read_file(path, &size);
    int w, h, uvw, uvh, stride, uvstride;
    unsigned char *u, *v;
    unsigned char *y = WebPDecodeYUV(data, size, &w, &h, &u, &v, &stride, &uvstride);
    if (!y) fail("WebPDecodeYUV failed");
    uvw = (w + 1) / 2;
    uvh = (h + 1) / 2;
    size_t total = (size_t)w * h + 2 * (size_t)uvw * uvh;
    unsigned char *packed = malloc(total), *p = packed;
    for (int r = 0; r < h; r++, p += w) memcpy(p, y + (size_t)r * stride, w);
    for (int r = 0; r < uvh; r++, p += uvw) memcpy(p, u + (size_t)r * uvstride, uvw);
    for (int r = 0; r < uvh; r++, p += uvw) memcpy(p, v + (size_t)r * uvstride, uvw);
    write_file(out, packed, total);
    printf("{\"width\": %d, \"height\": %d, \"chromaWidth\": %d, \"chromaHeight\": %d}\n", w, h, uvw, uvh);
    free(packed);
    WebPFree(y);
    free(data);
    return 0;
}

static int anim(const char *path, const char *out)
{
    size_t size;
    unsigned char *data = read_file(path, &size);
    WebPData whole = { data, size };
    WebPAnimDecoderOptions options;
    if (!WebPAnimDecoderOptionsInit(&options)) fail("WebPAnimDecoderOptionsInit failed");
    options.color_mode = MODE_RGBA;
    options.use_threads = 0;
    WebPAnimDecoder *dec = WebPAnimDecoderNew(&whole, &options);
    if (!dec) fail("WebPAnimDecoderNew failed");
    WebPAnimInfo ai;
    WebPAnimDecoderGetInfo(dec, &ai);
    FILE *f = fopen(out, "wb");
    if (!f) { perror(out); exit(1); }
    printf("{\"width\": %u, \"height\": %u, \"loop\": %u, \"background\": %u, \"frames\": %u, \"timestamps\": [",
           ai.canvas_width, ai.canvas_height, ai.loop_count, ai.bgcolor, ai.frame_count);
    int first = 1;
    while (WebPAnimDecoderHasMoreFrames(dec))
    {
        uint8_t *buf;
        int timestamp;
        if (!WebPAnimDecoderGetNext(dec, &buf, &timestamp)) fail("WebPAnimDecoderGetNext failed");
        fwrite(buf, 1, (size_t)ai.canvas_width * ai.canvas_height * 4, f);
        printf("%s%d", first ? "" : ", ", timestamp);
        first = 0;
    }
    printf("]}\n");
    fclose(f);
    WebPAnimDecoderDelete(dec);
    free(data);
    return 0;
}

static int encode(int argc, char **argv)
{
    size_t size;
    unsigned char *rgba = read_file(argv[2], &size);
    int w = atoi(argv[3]), h = atoi(argv[4]);
    WebPConfig config;
    if (!WebPConfigInit(&config)) fail("WebPConfigInit failed");
    for (int i = 6; i < argc; i++)
    {
        char key[64];
        float value;
        if (sscanf(argv[i], "%63[^=]=%f", key, &value) != 2) fail("Options are key=value");
        int n = (int)value;
        if (!strcmp(key, "lossless")) config.lossless = n;
        else if (!strcmp(key, "q")) config.quality = value;
        else if (!strcmp(key, "m")) config.method = n;
        else if (!strcmp(key, "z")) { if (!WebPConfigLosslessPreset(&config, n)) fail("Bad z"); }
        else if (!strcmp(key, "partitions")) config.partitions = n;
        else if (!strcmp(key, "segments")) config.segments = n;
        else if (!strcmp(key, "sns")) config.sns_strength = n;
        else if (!strcmp(key, "f")) config.filter_strength = n;
        else if (!strcmp(key, "sharpness")) config.filter_sharpness = n;
        else if (!strcmp(key, "strong")) config.filter_type = n;
        else if (!strcmp(key, "autofilter")) config.autofilter = n;
        else if (!strcmp(key, "alpha_method")) config.alpha_compression = n;
        else if (!strcmp(key, "alpha_filter")) config.alpha_filtering = n;
        else if (!strcmp(key, "alpha_q")) config.alpha_quality = n;
        else if (!strcmp(key, "exact")) config.exact = n;
        else if (!strcmp(key, "near")) config.near_lossless = n;
        else if (!strcmp(key, "sharp_yuv")) config.use_sharp_yuv = n;
        else if (!strcmp(key, "pass")) config.pass = n;
        else fail("Unknown option");
    }
    if (!WebPValidateConfig(&config)) fail("WebPValidateConfig failed");
    WebPPicture picture;
    if (!WebPPictureInit(&picture)) fail("WebPPictureInit failed");
    picture.width = w;
    picture.height = h;
    picture.use_argb = 1;
    if (!WebPPictureImportRGBA(&picture, rgba, w * 4)) fail("WebPPictureImportRGBA failed");
    WebPMemoryWriter writer;
    WebPMemoryWriterInit(&writer);
    picture.writer = WebPMemoryWrite;
    picture.custom_ptr = &writer;
    if (!WebPEncode(&config, &picture)) fail("WebPEncode failed");
    write_file(argv[5], writer.mem, writer.size);
    printf("{\"size\": %zu, \"config\": {\"lossless\": %d, \"quality\": %g, \"method\": %d, \"partitions\": %d, \"segments\": %d, "
           "\"sns\": %d, \"filterStrength\": %d, \"sharpness\": %d, \"filterType\": %d, \"autofilter\": %d, \"alphaMethod\": %d, "
           "\"alphaFilter\": %d, \"alphaQuality\": %d, \"exact\": %d, \"nearLossless\": %d, \"sharpYuv\": %d}}\n",
           writer.size, config.lossless, config.quality, config.method, config.partitions, config.segments, config.sns_strength,
           config.filter_strength, config.filter_sharpness, config.filter_type, config.autofilter, config.alpha_compression,
           config.alpha_filtering, config.alpha_quality, config.exact, config.near_lossless, config.use_sharp_yuv);
    WebPMemoryWriterClear(&writer);
    WebPPictureFree(&picture);
    free(rgba);
    return 0;
}

int main(int argc, char **argv)
{
    if (argc >= 2 && !strcmp(argv[1], "version"))
    {
        int d = WebPGetDecoderVersion();
        printf("{\"version\": \"%d.%d.%d\"}\n", d >> 16, (d >> 8) & 255, d & 255);
        return 0;
    }
    if (argc == 3 && !strcmp(argv[1], "info")) return info(argv[2]);
    if (argc >= 4 && !strcmp(argv[1], "decode")) return decode(argv[2], argv[3], argc > 4 && !strcmp(argv[4], "nofancy"));
    if (argc == 4 && !strcmp(argv[1], "yuv")) return yuv(argv[2], argv[3]);
    if (argc == 4 && !strcmp(argv[1], "anim")) return anim(argv[2], argv[3]);
    if (argc >= 6 && !strcmp(argv[1], "encode")) return encode(argc, argv);
    fprintf(stderr, "usage: webpref version | info | decode | yuv | anim | encode (see the comment at the top)\n");
    return 2;
}
