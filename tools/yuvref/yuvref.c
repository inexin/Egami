/*
 * Raw-plane reference helper for tools/make-yuv-references.py, built inside its alpine container against libjpeg-turbo's
 * TurboJPEG 3 API. Prints one JSON object on stdout per call.
 *
 *   yuvref version
 *   yuvref info <in.jpg>
 *       width, height, subsampling, colorspace, precision, arithmetic, progressive from the header.
 *   yuvref decode <in.jpg> <denominator> <out.yuv>
 *       tj3DecompressToYUVPlanes8 at scale 1/denominator, no padding (strides = plane widths); writes Y, U, V (gray: Y)
 *       packed, row by row, and prints the output size, subsampling and each plane's size (tj3YUVPlaneWidth/Height).
 *   yuvref encode <in.yuv> <width> <height> <subsampling> <quality> <out.jpg>
 *       tj3CompressFromYUVPlanes8 from packed planes as decode writes them: baseline, Huffman tables from the spec (no
 *       optimization), no restart markers, accurate integer DCT.
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <jconfig.h>
#include <turbojpeg.h>

/* jconfig.h defines the version as bare tokens (3.0.3). */
#define STRING(x) STRING2(x)
#define STRING2(x) #x

static const char *SUBSAMP[] = { "444", "422", "420", "gray", "440", "411", "441" };
static const char *COLORSPACE[] = { "rgb", "ycbcr", "gray", "cmyk", "ycck" };

static void fail(tjhandle handle, const char *what)
{
    fprintf(stderr, "%s: %s\n", what, handle ? tj3GetErrorStr(handle) : "error");
    exit(1);
}

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

static void write_file(const char *path, const unsigned char *data, size_t size)
{
    FILE *f = fopen(path, "wb");
    if (!f || fwrite(data, 1, size, f) != size) { perror(path); exit(1); }
    fclose(f);
}

static int subsamp_of(const char *name)
{
    for (int i = 0; i < (int)(sizeof SUBSAMP / sizeof *SUBSAMP); i++)
        if (!strcmp(name, SUBSAMP[i])) return i;
    fprintf(stderr, "unknown subsampling %s\n", name);
    exit(1);
}

static const char *name_of(const char **names, int count, int value)
{
    return value >= 0 && value < count ? names[value] : "unknown";
}

static int info(const char *path)
{
    size_t size;
    unsigned char *jpeg = read_file(path, &size);
    tjhandle h = tj3Init(TJINIT_DECOMPRESS);
    if (!h) fail(NULL, "tj3Init");
    if (tj3DecompressHeader(h, jpeg, size) < 0) fail(h, "tj3DecompressHeader");
    printf("{\"width\": %d, \"height\": %d, \"subsampling\": \"%s\", \"colorspace\": \"%s\", \"precision\": %d, "
           "\"arithmetic\": %s, \"progressive\": %s}\n",
           tj3Get(h, TJPARAM_JPEGWIDTH), tj3Get(h, TJPARAM_JPEGHEIGHT),
           name_of(SUBSAMP, 7, tj3Get(h, TJPARAM_SUBSAMP)), name_of(COLORSPACE, 5, tj3Get(h, TJPARAM_COLORSPACE)),
           tj3Get(h, TJPARAM_PRECISION), tj3Get(h, TJPARAM_ARITHMETIC) ? "true" : "false",
           tj3Get(h, TJPARAM_PROGRESSIVE) ? "true" : "false");
    tj3Destroy(h);
    free(jpeg);
    return 0;
}

static int decode(const char *path, int denominator, const char *out)
{
    size_t size;
    unsigned char *jpeg = read_file(path, &size);
    tjhandle h = tj3Init(TJINIT_DECOMPRESS);
    if (!h) fail(NULL, "tj3Init");
    if (tj3DecompressHeader(h, jpeg, size) < 0) fail(h, "tj3DecompressHeader");
    tjscalingfactor scale = { 1, denominator };
    if (tj3SetScalingFactor(h, scale) < 0) fail(h, "tj3SetScalingFactor");
    int subsamp = tj3Get(h, TJPARAM_SUBSAMP);
    if (subsamp < 0) { fprintf(stderr, "unknown subsampling\n"); return 1; }
    int width = TJSCALED(tj3Get(h, TJPARAM_JPEGWIDTH), scale);
    int height = TJSCALED(tj3Get(h, TJPARAM_JPEGHEIGHT), scale);
    int planes = subsamp == TJSAMP_GRAY ? 1 : 3;
    int pw[3], ph[3], strides[3];
    size_t offset[3], total = 0;
    for (int i = 0; i < planes; i++) {
        pw[i] = tj3YUVPlaneWidth(i, width, subsamp);
        ph[i] = tj3YUVPlaneHeight(i, height, subsamp);
        strides[i] = pw[i];
        offset[i] = total;
        total += (size_t)pw[i] * ph[i];
    }
    unsigned char *buffer = malloc(total);
    unsigned char *dst[3];
    for (int i = 0; i < planes; i++) dst[i] = buffer + offset[i];
    if (tj3DecompressToYUVPlanes8(h, jpeg, size, dst, strides) < 0) fail(h, "tj3DecompressToYUVPlanes8");
    write_file(out, buffer, total);
    printf("{\"width\": %d, \"height\": %d, \"subsampling\": \"%s\", \"planes\": [", width, height, SUBSAMP[subsamp]);
    for (int i = 0; i < planes; i++)
        printf("%s{\"width\": %d, \"height\": %d}", i ? ", " : "", pw[i], ph[i]);
    printf("]}\n");
    tj3Destroy(h);
    free(buffer);
    free(jpeg);
    return 0;
}

static int encode(const char *path, int width, int height, int subsamp, int quality, const char *out)
{
    size_t size;
    unsigned char *yuv = read_file(path, &size);
    int planes = subsamp == TJSAMP_GRAY ? 1 : 3;
    const unsigned char *src[3];
    int strides[3];
    size_t total = 0;
    for (int i = 0; i < planes; i++) {
        strides[i] = tj3YUVPlaneWidth(i, width, subsamp);
        src[i] = yuv + total;
        total += (size_t)strides[i] * tj3YUVPlaneHeight(i, height, subsamp);
    }
    if (total != size) { fprintf(stderr, "%s: %zu bytes, planes need %zu\n", path, size, total); return 1; }
    tjhandle h = tj3Init(TJINIT_COMPRESS);
    if (!h) fail(NULL, "tj3Init");
    if (tj3Set(h, TJPARAM_SUBSAMP, subsamp) < 0 || tj3Set(h, TJPARAM_QUALITY, quality) < 0
        || tj3Set(h, TJPARAM_FASTDCT, 0) < 0 || tj3Set(h, TJPARAM_OPTIMIZE, 0) < 0
        || tj3Set(h, TJPARAM_PROGRESSIVE, 0) < 0 || tj3Set(h, TJPARAM_ARITHMETIC, 0) < 0
        || tj3Set(h, TJPARAM_RESTARTBLOCKS, 0) < 0 || tj3Set(h, TJPARAM_RESTARTROWS, 0) < 0)
        fail(h, "tj3Set");
    unsigned char *jpeg = NULL;
    size_t jpeg_size = 0;
    if (tj3CompressFromYUVPlanes8(h, src, width, strides, height, &jpeg, &jpeg_size) < 0)
        fail(h, "tj3CompressFromYUVPlanes8");
    write_file(out, jpeg, jpeg_size);
    printf("{\"size\": %zu, \"params\": {\"TJPARAM_SUBSAMP\": \"TJSAMP_%s\", \"TJPARAM_QUALITY\": %d, "
           "\"TJPARAM_FASTDCT\": %d, \"TJPARAM_OPTIMIZE\": %d, \"TJPARAM_PROGRESSIVE\": %d, \"TJPARAM_ARITHMETIC\": %d, "
           "\"TJPARAM_RESTARTBLOCKS\": %d, \"TJPARAM_RESTARTROWS\": %d, \"TJPARAM_LOSSLESS\": %d}}\n",
           jpeg_size, subsamp == TJSAMP_GRAY ? "GRAY" : SUBSAMP[subsamp], tj3Get(h, TJPARAM_QUALITY),
           tj3Get(h, TJPARAM_FASTDCT), tj3Get(h, TJPARAM_OPTIMIZE), tj3Get(h, TJPARAM_PROGRESSIVE),
           tj3Get(h, TJPARAM_ARITHMETIC), tj3Get(h, TJPARAM_RESTARTBLOCKS), tj3Get(h, TJPARAM_RESTARTROWS),
           tj3Get(h, TJPARAM_LOSSLESS));
    tj3Free(jpeg);
    tj3Destroy(h);
    free(yuv);
    return 0;
}

int main(int argc, char **argv)
{
    if (argc == 2 && !strcmp(argv[1], "version")) {
        printf("{\"version\": \"libjpeg-turbo %s, TurboJPEG 3 API\"}\n", STRING(LIBJPEG_TURBO_VERSION));
        return 0;
    }
    if (argc == 3 && !strcmp(argv[1], "info")) return info(argv[2]);
    if (argc == 5 && !strcmp(argv[1], "decode")) return decode(argv[2], atoi(argv[3]), argv[4]);
    if (argc == 8 && !strcmp(argv[1], "encode"))
        return encode(argv[2], atoi(argv[3]), atoi(argv[4]), subsamp_of(argv[5]), atoi(argv[6]), argv[7]);
    fprintf(stderr, "usage: yuvref version | info <jpg> | decode <jpg> <denominator> <out.yuv> | "
                    "encode <yuv> <width> <height> <subsampling> <quality> <out.jpg>\n");
    return 2;
}
