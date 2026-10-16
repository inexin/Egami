// avifbench times libavif decoding an AVIF file from memory through dav1d and converting it to RGB (as Raster.Load does): the
// best of N runs after warm-up runs, at the given thread count for both dav1d and the YUV to RGB conversion. The Phase 2.9.2
// speed gate (tools/time-avif-gate.py) compares Egami against it.
//   avifbench <file.avif> <threads> [runs]
#include <avif/avif.h>
#include <stdio.h>
#include <stdlib.h>
#include <time.h>

static double now(void)
{
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return t.tv_sec * 1000.0 + t.tv_nsec / 1e6;
}

static int decode(const uint8_t * data, size_t size, int threads, uint32_t * width, uint32_t * height)
{
    avifDecoder * decoder = avifDecoderCreate();
    decoder->maxThreads = threads;
    decoder->codecChoice = AVIF_CODEC_CHOICE_DAV1D;
    avifImage * image = avifImageCreateEmpty();
    int ok = avifDecoderReadMemory(decoder, image, data, size) == AVIF_RESULT_OK;
    if (ok) {
        avifRGBImage rgb;
        avifRGBImageSetDefaults(&rgb, image);
        rgb.depth = image->depth > 8 ? 16 : 8;
        rgb.maxThreads = threads;
        ok = avifRGBImageAllocatePixels(&rgb) == AVIF_RESULT_OK && avifImageYUVToRGB(image, &rgb) == AVIF_RESULT_OK;
        avifRGBImageFreePixels(&rgb);
        *width = image->width;
        *height = image->height;
    }
    avifImageDestroy(image);
    avifDecoderDestroy(decoder);
    return ok;
}

int main(int argc, char ** argv)
{
    if (argc < 3) {
        fprintf(stderr, "usage: avifbench <file.avif> <threads> [runs]\n");
        return 2;
    }
    FILE * f = fopen(argv[1], "rb");
    if (!f) {
        return 1;
    }
    fseek(f, 0, SEEK_END);
    size_t size = (size_t)ftell(f);
    fseek(f, 0, SEEK_SET);
    uint8_t * data = malloc(size);
    if (fread(data, 1, size, f) != size) {
        return 1;
    }
    fclose(f);
    int threads = atoi(argv[2]);
    int runs = argc > 3 ? atoi(argv[3]) : 10;
    uint32_t width = 0, height = 0;
    for (int i = 0; i < 3; ++i) {
        if (!decode(data, size, threads, &width, &height)) {
            fprintf(stderr, "decode failed\n");
            return 1;
        }
    }
    double best = 1e30;
    for (int i = 0; i < runs; ++i) {
        double start = now();
        decode(data, size, threads, &width, &height);
        double elapsed = now() - start;
        best = elapsed < best ? elapsed : best;
    }
    printf("%u x %u: %.2f ms on %d thread(s)\n", width, height, best, threads);
    free(data);
    return 0;
}
