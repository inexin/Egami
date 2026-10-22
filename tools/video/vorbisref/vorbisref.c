/* Writes the samples libvorbisfile decodes from an Ogg Vorbis file as raw little-endian 32-bit floats, channels interleaved in
   the Vorbis order, and prints "channels rate samples" to stderr. Egami.Video's own test tool, built against libvorbis (BSD-3)
   in tools/video/vorbisref. Usage: vorbisref in.ogg out.f32 */
#include <stdio.h>
#include <vorbis/vorbisfile.h>

int main(int argc, char **argv)
{
    OggVorbis_File vf;
    if (argc != 3 || ov_fopen(argv[1], &vf) != 0) {
        fprintf(stderr, "usage: vorbisref in.ogg out.f32\n");
        return 1;
    }

    vorbis_info *vi = ov_info(&vf, -1);
    FILE *out = fopen(argv[2], "wb");
    long total = 0;
    int section;
    float **pcm;
    for (;;) {
        long n = ov_read_float(&vf, &pcm, 4096, &section);
        if (n == 0) break;
        if (n < 0) continue;
        for (long i = 0; i < n; i++)
            for (int c = 0; c < vi->channels; c++)
                fwrite(&pcm[c][i], sizeof(float), 1, out);
        total += n;
    }

    fprintf(stderr, "%d %ld %ld\n", vi->channels, vi->rate, total);
    fclose(out);
    ov_clear(&vf);
    return 0;
}
