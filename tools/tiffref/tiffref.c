/*
 * TIFF reference helper for tools/make-tiff-references.py, built inside its alpine container against libtiff. Prints one JSON
 * object on stdout per call.
 *
 *   tiffref version
 *   tiffref info <in.tif>
 *       Every directory of the main chain and every SubIFD: the tags a reader needs (size, samples, photometric, compression,
 *       predictor, planar, strips or tiles, sample format, extra samples, fill order, orientation, subfile type), plus the
 *       file's byte order and whether it is BigTIFF.
 *   tiffref raw <in.tif> <directory> <out.raw>
 *       The decoded samples of one directory as libtiff hands them out: decompressed, predictor undone, fill order applied,
 *       16- to 64-bit samples in the host's (little-endian) order. Planes one after the other (one plane when chunky); each
 *       row is ceil(width × samples in the plane × bits / 8) bytes, packed most significant bit first below 8 bits. JPEG
 *       pages are read with JPEGCOLORMODE RGB (so YCbCr comes out as RGB) and SGILog pages as 32-bit float XYZ (or Y).
 *   tiffref rgba <in.tif> <directory> <out.rgba>
 *       TIFFReadRGBAImageOriented, top-left: 8-bit RGBA, rows top-down. libtiff premultiplies unassociated alpha here.
 *   tiffref logluv <in.f32> <width> <height> <out.tif> <logl|logluv|logluv24>
 *       Writes SGILog from float samples (Y, or XYZ for LogLuv), one strip of 8 rows at a time.
 *   tiffref webp <in.raw> <width> <height> <samples> <out.tif> <quality>
 *       Writes 8-bit RGB or RGBA (unassociated) with WebP compression in strips of 16 rows: lossy at quality 0 to 100, lossless
 *       above 100 (tiffcp cannot ask for lossless).
 */
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <tiffio.h>

static void fail(const char *what)
{
    fprintf(stderr, "%s\n", what);
    exit(1);
}

static void quiet(const char *module, const char *fmt, va_list ap)
{
    (void)module;
    (void)fmt;
    (void)ap;
}

static uint32_t tag32(TIFF *tif, ttag_t tag, uint32_t fallback)
{
    uint32_t v;
    return TIFFGetField(tif, tag, &v) ? v : fallback;
}

static uint16_t tag16(TIFF *tif, ttag_t tag, uint16_t fallback)
{
    uint16_t v;
    return TIFFGetField(tif, tag, &v) ? v : fallback;
}

static void print_directory(TIFF *tif, int index, int sub)
{
    uint16_t extra_count = 0, *extra = NULL;
    uint16_t sub_count = 0;
    uint64_t *sub_offsets = NULL;
    TIFFGetField(tif, TIFFTAG_EXTRASAMPLES, &extra_count, &extra);
    TIFFGetField(tif, TIFFTAG_SUBIFD, &sub_count, &sub_offsets);
    printf("{\"index\":%d,\"sub\":%d,\"width\":%u,\"height\":%u,\"bits\":%u,\"samples\":%u,\"photometric\":%u,"
           "\"compression\":%u,\"predictor\":%u,\"planar\":%u,\"tiled\":%s,\"tileWidth\":%u,\"tileHeight\":%u,"
           "\"rowsPerStrip\":%u,\"sampleFormat\":%u,\"fillOrder\":%u,\"orientation\":%u,\"subfileType\":%u,\"subIfds\":%u,"
           "\"extraSamples\":[",
           index, sub, tag32(tif, TIFFTAG_IMAGEWIDTH, 0), tag32(tif, TIFFTAG_IMAGELENGTH, 0),
           tag16(tif, TIFFTAG_BITSPERSAMPLE, 1), tag16(tif, TIFFTAG_SAMPLESPERPIXEL, 1),
           tag16(tif, TIFFTAG_PHOTOMETRIC, 0xFFFF), tag16(tif, TIFFTAG_COMPRESSION, 1), tag16(tif, TIFFTAG_PREDICTOR, 1),
           tag16(tif, TIFFTAG_PLANARCONFIG, 1), TIFFIsTiled(tif) ? "true" : "false", tag32(tif, TIFFTAG_TILEWIDTH, 0),
           tag32(tif, TIFFTAG_TILELENGTH, 0), tag32(tif, TIFFTAG_ROWSPERSTRIP, 0), tag16(tif, TIFFTAG_SAMPLEFORMAT, 1),
           tag16(tif, TIFFTAG_FILLORDER, 1), tag16(tif, TIFFTAG_ORIENTATION, 1), tag32(tif, TIFFTAG_SUBFILETYPE, 0),
           sub_count);
    for (int i = 0; i < extra_count; i++)
        printf("%s%u", i ? "," : "", extra[i]);
    printf("]");
    uint16_t *red, *green, *blue;
    uint16_t bits = tag16(tif, TIFFTAG_BITSPERSAMPLE, 1);
    if (tag16(tif, TIFFTAG_PHOTOMETRIC, 0) == PHOTOMETRIC_PALETTE && bits <= 16 && TIFFGetField(tif, TIFFTAG_COLORMAP, &red, &green, &blue))
    {
        int n = 1 << bits;
        printf(",\"map\":[");
        for (int i = 0; i < 3 * n; i++)
            printf("%s%u", i ? "," : "", (i < n ? red : i < 2 * n ? green : blue)[i % n]);
        printf("]");
    }
    printf("}");
}

static int info(const char *path)
{
    TIFF *tif = TIFFOpen(path, "r");
    if (!tif) fail("TIFFOpen failed");
    printf("{\"bigEndian\":%s,\"bigTiff\":%s,\"pages\":[", TIFFIsBigEndian(tif) ? "true" : "false",
           TIFFIsBigTIFF(tif) ? "true" : "false");
    int index = 0;
    int first = 1;
    do
    {
        uint16_t sub_count = 0;
        uint64_t *sub_offsets = NULL;
        printf("%s", first ? "" : ",");
        first = 0;
        print_directory(tif, index, -1);
        if (TIFFGetField(tif, TIFFTAG_SUBIFD, &sub_count, &sub_offsets) && sub_count > 0)
        {
            uint64_t offsets[64];
            int n = sub_count < 64 ? sub_count : 64;
            memcpy(offsets, sub_offsets, n * sizeof(uint64_t));
            for (int i = 0; i < n; i++)
            {
                if (!TIFFSetSubDirectory(tif, offsets[i])) fail("TIFFSetSubDirectory failed");
                printf(",");
                print_directory(tif, index, i);
            }

            if (!TIFFSetDirectory(tif, (tdir_t)index)) fail("TIFFSetDirectory failed");
        }

        index++;
    } while (TIFFReadDirectory(tif));
    printf("],\"count\":%d}\n", index);
    TIFFClose(tif);
    return 0;
}

static TIFF *open_directory(const char *path, const char *which)
{
    TIFF *tif = TIFFOpen(path, "r");
    if (!tif) fail("TIFFOpen failed");
    /* "3" is directory 3; "3.1" is SubIFD 1 of directory 3. */
    int index = atoi(which);
    if (!TIFFSetDirectory(tif, (tdir_t)index)) fail("TIFFSetDirectory failed");
    const char *dot = strchr(which, '.');
    if (dot)
    {
        uint16_t sub_count = 0;
        uint64_t *sub_offsets = NULL;
        int sub = atoi(dot + 1);
        if (!TIFFGetField(tif, TIFFTAG_SUBIFD, &sub_count, &sub_offsets) || sub >= sub_count) fail("no such SubIFD");
        if (!TIFFSetSubDirectory(tif, sub_offsets[sub])) fail("TIFFSetSubDirectory failed");
    }

    return tif;
}

static int raw(const char *path, const char *which, const char *out)
{
    TIFF *tif = open_directory(path, which);
    uint16_t compression = tag16(tif, TIFFTAG_COMPRESSION, 1);
    uint16_t photometric = tag16(tif, TIFFTAG_PHOTOMETRIC, 0);
    if (compression == COMPRESSION_JPEG && photometric == PHOTOMETRIC_YCBCR)
        TIFFSetField(tif, TIFFTAG_JPEGCOLORMODE, JPEGCOLORMODE_RGB);
    if (compression == COMPRESSION_SGILOG || compression == COMPRESSION_SGILOG24)
        TIFFSetField(tif, TIFFTAG_SGILOGDATAFMT, SGILOGDATAFMT_FLOAT);

    uint32_t width = tag32(tif, TIFFTAG_IMAGEWIDTH, 0), height = tag32(tif, TIFFTAG_IMAGELENGTH, 0);
    uint16_t bits = tag16(tif, TIFFTAG_BITSPERSAMPLE, 1), samples = tag16(tif, TIFFTAG_SAMPLESPERPIXEL, 1);
    uint16_t planar = tag16(tif, TIFFTAG_PLANARCONFIG, 1);
    if (compression == COMPRESSION_SGILOG || compression == COMPRESSION_SGILOG24)
    {
        bits = 32;
        samples = photometric == PHOTOMETRIC_LOGL ? 1 : 3;
    }

    int planes = planar == PLANARCONFIG_SEPARATE ? samples : 1;
    int unit_samples = planar == PLANARCONFIG_SEPARATE ? 1 : samples;
    uint64_t row_bytes = ((uint64_t)width * unit_samples * bits + 7) / 8;
    uint64_t plane_bytes = row_bytes * height;
    int failed = 0;
    unsigned char *image = calloc(planes * plane_bytes, 1);
    if (!image) fail("out of memory");

    if (TIFFIsTiled(tif))
    {
        uint32_t tw = tag32(tif, TIFFTAG_TILEWIDTH, 0), th = tag32(tif, TIFFTAG_TILELENGTH, 0);
        tmsize_t tile_size = TIFFTileSize(tif);
        unsigned char *tile = malloc(tile_size);
        uint64_t tile_row = ((uint64_t)tw * unit_samples * bits + 7) / 8;
        for (int p = 0; p < planes; p++)
            for (uint32_t y = 0; y < height; y += th)
                for (uint32_t x = 0; x < width; x += tw)
                {
                    memset(tile, 0, tile_size);
                    if (TIFFReadTile(tif, tile, x, y, 0, (uint16_t)p) < 0) fail("TIFFReadTile failed");
                    uint64_t at = ((uint64_t)x * unit_samples * bits) / 8;
                    uint64_t copy = row_bytes - at < tile_row ? row_bytes - at : tile_row;
                    for (uint32_t r = 0; r < th && y + r < height; r++)
                        memcpy(image + p * plane_bytes + (y + r) * row_bytes + at, tile + r * tile_row, copy);
                }
        free(tile);
    }
    else
    {
        uint32_t rows_per_strip = tag32(tif, TIFFTAG_ROWSPERSTRIP, height);
        if (rows_per_strip > height) rows_per_strip = height;
        tmsize_t strip_size = TIFFStripSize(tif);
        unsigned char *strip = malloc(strip_size);
        uint32_t strips = (height + rows_per_strip - 1) / rows_per_strip;
        for (int p = 0; p < planes; p++)
            for (uint32_t s = 0; s < strips; s++)
            {
                memset(strip, 0, strip_size);
                if (TIFFReadEncodedStrip(tif, TIFFComputeStrip(tif, s * rows_per_strip, (uint16_t)p), strip, -1) < 0)
                {
                    /* A damaged strip: keep what libtiff decoded of it and go on, as readers do. */
                    failed++;
                }
                uint32_t rows = height - s * rows_per_strip < rows_per_strip ? height - s * rows_per_strip : rows_per_strip;
                memcpy(image + p * plane_bytes + (uint64_t)s * rows_per_strip * row_bytes, strip, rows * row_bytes);
            }
        free(strip);
    }

    FILE *f = fopen(out, "wb");
    if (!f || fwrite(image, 1, planes * plane_bytes, f) != planes * plane_bytes) fail("write failed");
    fclose(f);
    printf("{\"bytes\":%llu,\"planes\":%d,\"rowBytes\":%llu,\"bits\":%u}\n", (unsigned long long)(planes * plane_bytes), planes,
           (unsigned long long)row_bytes, bits);
    free(image);
    TIFFClose(tif);
    return 0;
}

static int rgba(const char *path, const char *which, const char *out)
{
    TIFF *tif = open_directory(path, which);
    uint32_t width = tag32(tif, TIFFTAG_IMAGEWIDTH, 0), height = tag32(tif, TIFFTAG_IMAGELENGTH, 0);
    uint32_t *raster = _TIFFmalloc((tmsize_t)width * height * 4);
    if (!raster) fail("out of memory");
    if (!TIFFReadRGBAImageOriented(tif, width, height, raster, ORIENTATION_TOPLEFT, 0)) fail("TIFFReadRGBAImage failed");
    unsigned char *bytes = malloc((size_t)width * height * 4);
    for (size_t i = 0; i < (size_t)width * height; i++)
    {
        bytes[i * 4] = TIFFGetR(raster[i]);
        bytes[i * 4 + 1] = TIFFGetG(raster[i]);
        bytes[i * 4 + 2] = TIFFGetB(raster[i]);
        bytes[i * 4 + 3] = TIFFGetA(raster[i]);
    }

    FILE *f = fopen(out, "wb");
    if (!f || fwrite(bytes, 1, (size_t)width * height * 4, f) != (size_t)width * height * 4) fail("write failed");
    fclose(f);
    printf("{\"bytes\":%zu}\n", (size_t)width * height * 4);
    free(bytes);
    _TIFFfree(raster);
    TIFFClose(tif);
    return 0;
}

static int logluv(const char *in, int width, int height, const char *out, const char *mode)
{
    int gray = strcmp(mode, "logl") == 0;
    int samples = gray ? 1 : 3;
    size_t count = (size_t)width * height * samples;
    float *data = malloc(count * sizeof(float));
    FILE *f = fopen(in, "rb");
    if (!f || fread(data, sizeof(float), count, f) != count) fail("read failed");
    fclose(f);
    TIFF *tif = TIFFOpen(out, "w");
    if (!tif) fail("TIFFOpen failed");
    TIFFSetField(tif, TIFFTAG_IMAGEWIDTH, width);
    TIFFSetField(tif, TIFFTAG_IMAGELENGTH, height);
    TIFFSetField(tif, TIFFTAG_COMPRESSION, strcmp(mode, "logluv24") == 0 ? COMPRESSION_SGILOG24 : COMPRESSION_SGILOG);
    TIFFSetField(tif, TIFFTAG_PHOTOMETRIC, gray ? PHOTOMETRIC_LOGL : PHOTOMETRIC_LOGLUV);
    TIFFSetField(tif, TIFFTAG_SAMPLESPERPIXEL, samples);
    TIFFSetField(tif, TIFFTAG_PLANARCONFIG, PLANARCONFIG_CONTIG);
    TIFFSetField(tif, TIFFTAG_SGILOGDATAFMT, SGILOGDATAFMT_FLOAT);
    TIFFSetField(tif, TIFFTAG_ROWSPERSTRIP, 8);
    for (int y = 0; y < height; y++)
        if (TIFFWriteScanline(tif, data + (size_t)y * width * samples, y, 0) < 0) fail("TIFFWriteScanline failed");
    TIFFClose(tif);
    free(data);
    printf("{\"written\":true}\n");
    return 0;
}

static int webp(const char *in, int width, int height, int samples, const char *out, int quality)
{
    size_t count = (size_t)width * height * samples;
    unsigned char *data = malloc(count);
    FILE *f = fopen(in, "rb");
    if (!f || fread(data, 1, count, f) != count) fail("read failed");
    fclose(f);
    TIFF *tif = TIFFOpen(out, "w");
    if (!tif) fail("TIFFOpen failed");
    TIFFSetField(tif, TIFFTAG_IMAGEWIDTH, width);
    TIFFSetField(tif, TIFFTAG_IMAGELENGTH, height);
    TIFFSetField(tif, TIFFTAG_BITSPERSAMPLE, 8);
    TIFFSetField(tif, TIFFTAG_SAMPLESPERPIXEL, samples);
    TIFFSetField(tif, TIFFTAG_PHOTOMETRIC, PHOTOMETRIC_RGB);
    TIFFSetField(tif, TIFFTAG_PLANARCONFIG, PLANARCONFIG_CONTIG);
    if (samples == 4)
    {
        uint16_t extra = EXTRASAMPLE_UNASSALPHA;
        TIFFSetField(tif, TIFFTAG_EXTRASAMPLES, 1, &extra);
    }

    TIFFSetField(tif, TIFFTAG_COMPRESSION, COMPRESSION_WEBP);
    if (quality > 100)
        TIFFSetField(tif, TIFFTAG_WEBP_LOSSLESS, 1);
    else
        TIFFSetField(tif, TIFFTAG_WEBP_LEVEL, quality);
    TIFFSetField(tif, TIFFTAG_ROWSPERSTRIP, 16);
    for (int y = 0; y < height; y++)
        if (TIFFWriteScanline(tif, data + (size_t)y * width * samples, y, 0) < 0) fail("TIFFWriteScanline failed");
    TIFFClose(tif);
    free(data);
    printf("{\"written\":true}\n");
    return 0;
}

int main(int argc, char **argv)
{
    TIFFSetWarningHandler(quiet);
    if (argc >= 2 && strcmp(argv[1], "version") == 0)
    {
        printf("{\"version\":\"%s\"}\n", strtok(strdup(TIFFGetVersion()), "\n"));
        return 0;
    }

    if (argc == 3 && strcmp(argv[1], "info") == 0) return info(argv[2]);
    if (argc == 5 && strcmp(argv[1], "raw") == 0) return raw(argv[2], argv[3], argv[4]);
    if (argc == 5 && strcmp(argv[1], "rgba") == 0) return rgba(argv[2], argv[3], argv[4]);
    if (argc == 7 && strcmp(argv[1], "logluv") == 0) return logluv(argv[2], atoi(argv[3]), atoi(argv[4]), argv[5], argv[6]);
    if (argc == 8 && strcmp(argv[1], "webp") == 0)
        return webp(argv[2], atoi(argv[3]), atoi(argv[4]), atoi(argv[5]), argv[6], atoi(argv[7]));
    fail("usage: tiffref version | info <in> | raw <in> <dir> <out> | rgba <in> <dir> <out> | logluv <in> <w> <h> <out> <mode>");
    return 1;
}
