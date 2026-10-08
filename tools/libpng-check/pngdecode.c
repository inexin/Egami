/*
 * pngdecode: decodes a PNG with libpng into raw 16-bit RGBA, little-endian, row by row, with no gamma or color
 * correction. Used only by CI to check Egami against the reference PNG library; it is not part of Egami.
 *
 * Usage: pngdecode input.png output.rgba16
 * Exit code 0 on success, 1 if libpng rejects the file, 2 on a usage or I/O error.
 */
#include <png.h>
#include <setjmp.h>
#include <stdio.h>
#include <stdlib.h>

int main(int argc, char **argv)
{
    if (argc != 3) {
        fprintf(stderr, "usage: pngdecode input.png output.rgba16\n");
        return 2;
    }

    FILE *in = fopen(argv[1], "rb");
    if (!in) {
        perror(argv[1]);
        return 2;
    }

    png_structp png = png_create_read_struct(PNG_LIBPNG_VER_STRING, NULL, NULL, NULL);
    png_infop info = png_create_info_struct(png);
    png_bytep pixels = NULL;
    png_bytepp rows = NULL;
    if (setjmp(png_jmpbuf(png))) {
        png_destroy_read_struct(&png, &info, NULL);
        free(rows);
        free(pixels);
        fclose(in);
        return 1;
    }

    png_init_io(png, in);
    png_read_info(png, info);

    /* Palette to RGB, low gray depths to 8 bits, tRNS to alpha; then gray to RGB, 8 to 16 bits (v * 257),
       opaque alpha for images without it, and little-endian samples. No gamma is applied. */
    png_set_expand(png);
    png_set_gray_to_rgb(png);
    png_set_expand_16(png);
    png_set_add_alpha(png, 0xFFFF, PNG_FILLER_AFTER);
    png_set_swap(png);
    png_set_interlace_handling(png);
    png_read_update_info(png, info);

    png_uint_32 width = png_get_image_width(png, info);
    png_uint_32 height = png_get_image_height(png, info);
    size_t row_bytes = png_get_rowbytes(png, info);
    if (row_bytes != (size_t)width * 8) {
        fprintf(stderr, "unexpected row size %zu for width %u\n", row_bytes, (unsigned)width);
        png_destroy_read_struct(&png, &info, NULL);
        fclose(in);
        return 2;
    }

    pixels = malloc(row_bytes * height);
    rows = malloc(sizeof(png_bytep) * height);
    for (png_uint_32 y = 0; y < height; y++) {
        rows[y] = pixels + (size_t)y * row_bytes;
    }

    png_read_image(png, rows);
    png_read_end(png, NULL);
    png_destroy_read_struct(&png, &info, NULL);
    fclose(in);

    FILE *out = fopen(argv[2], "wb");
    if (!out || fwrite(pixels, row_bytes, height, out) != height) {
        perror(argv[2]);
        return 2;
    }

    fclose(out);
    free(rows);
    free(pixels);
    return 0;
}
