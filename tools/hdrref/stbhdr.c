// Radiance references from stb_image: decode a .hdr file to raw floats, and time decode and encode.
//   stbhdr decode in.hdr out.raw     writes int32 width, height, channels, then width * height * channels float32 (little-endian)
//   stbhdr time in.hdr runs          decodes and re-encodes runs times, prints the best decode and encode in milliseconds
#define STB_IMAGE_IMPLEMENTATION
#define STBI_ONLY_HDR
#include "stb_image.h"
#define STB_IMAGE_WRITE_IMPLEMENTATION
#include "stb_image_write.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

static double now(void)
{
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return t.tv_sec * 1e3 + t.tv_nsec / 1e6;
}

static void sink(void *context, void *data, int size)
{
    (void)data;
    *(size_t *)context += (size_t)size;
}

int main(int argc, char **argv)
{
    if (argc == 4 && strcmp(argv[1], "decode") == 0) {
        int w, h, c;
        float *pixels = stbi_loadf(argv[2], &w, &h, &c, 0);
        if (!pixels) {
            fprintf(stderr, "%s: %s\n", argv[2], stbi_failure_reason());
            return 1;
        }
        FILE *f = fopen(argv[3], "wb");
        int header[3] = { w, h, c };
        fwrite(header, sizeof header, 1, f);
        fwrite(pixels, sizeof(float), (size_t)w * h * c, f);
        fclose(f);
        stbi_image_free(pixels);
        return 0;
    }
    if (argc == 4 && strcmp(argv[1], "time") == 0) {
        FILE *f = fopen(argv[2], "rb");
        fseek(f, 0, SEEK_END);
        long size = ftell(f);
        fseek(f, 0, SEEK_SET);
        unsigned char *data = malloc(size);
        if (fread(data, 1, size, f) != (size_t)size) return 1;
        fclose(f);
        int runs = atoi(argv[3]);
        double decode = 1e30, encode = 1e30;
        for (int i = 0; i < runs; i++) {
            int w, h, c;
            double t0 = now();
            float *pixels = stbi_loadf_from_memory(data, (int)size, &w, &h, &c, 0);
            double t1 = now();
            size_t written = 0;
            stbi_write_hdr_to_func(sink, &written, w, h, c, pixels);
            double t2 = now();
            stbi_image_free(pixels);
            if (t1 - t0 < decode) decode = t1 - t0;
            if (t2 - t1 < encode) encode = t2 - t1;
        }
        printf("%s decode %.3f encode %.3f\n", argv[2], decode, encode);
        free(data);
        return 0;
    }
    fprintf(stderr, "usage: stbhdr decode in.hdr out.raw | stbhdr time in.hdr runs\n");
    return 2;
}
