"""Builds the Phase 2.1.1 references: PFM float maps from ImageMagick and Netpbm, and raw CMYK decodes from libjpeg-turbo, in Docker.

PFM (tests/images/pfm, tests/reference/pfm):
  magick-rgb.pfm, magick-gray.pfm   written by ImageMagick from known floats (pfm-source.json gives them)
  netpbm-rgb.pfm, netpbm-gray.pfm   written by Netpbm's pamtopfm from 16-bit PAM and PGM files made by hand here
  <file>.magick.f32.gz              every PFM in tests/images/pfm read by ImageMagick: 32-bit little-endian floats, top row
                                    first, three channels for PF and one for Pf
  pfm-source.json                   the floats given to ImageMagick, and the PAM samples and maxval given to pamtopfm, so a test
                                    can check what each tool wrote and that Egami writes the same bytes

CMYK (tests/images/jpeg, tests/reference/jpeg):
  duck-ycck.jpg                     a YCCK JPEG, written by TurboJPEG from duck-cmyk.jpg's samples (TurboJPEG writes CMYK as YCCK)
  <file>.turbo-cmyk.gz              duck-cmyk.jpg and duck-ycck.jpg decoded by TurboJPEG to TJPF_CMYK: the samples as stored, four
                                    bytes a pixel, gzipped

Usage, from the repo root with Docker running:
  python tools/make-float-cmyk-references.py
The script runs alpine:3.20 and installs ImageMagick, Netpbm, libjpeg-turbo and gcc inside it with apk.
"""
import gzip
import json
import os
import struct
import subprocess
import sys

IMAGE = "alpine:3.20"
PACKAGES = "imagemagick netpbm libjpeg-turbo-dev gcc musl-dev"
PFM = "tests/images/pfm"
PFM_REF = "tests/reference/pfm"
JPEG = "tests/images/jpeg"
JPEG_REF = "tests/reference/jpeg"

# A small C program: decode a JPEG to TJPF_CMYK and write the raw samples; or re-encode those samples as a CMYK (YCCK) JPEG.
TURBO = r'''
#include <stdio.h>
#include <stdlib.h>
#include <turbojpeg.h>
static unsigned char *slurp(const char *path, unsigned long *size) {
  FILE *f = fopen(path, "rb"); fseek(f, 0, SEEK_END); *size = ftell(f); fseek(f, 0, SEEK_SET);
  unsigned char *data = malloc(*size); fread(data, 1, *size, f); fclose(f); return data;
}
int main(int argc, char **argv) {
  unsigned long size; unsigned char *jpeg = slurp(argv[2], &size);
  tjhandle d = tjInitDecompress(); int w, h, sub, cs;
  if (tjDecompressHeader3(d, jpeg, size, &w, &h, &sub, &cs)) { fprintf(stderr, "%s\n", tjGetErrorStr2(d)); return 1; }
  unsigned char *cmyk = malloc((size_t)w * h * 4);
  if (tjDecompress2(d, jpeg, size, cmyk, w, 0, h, TJPF_CMYK, 0)) { fprintf(stderr, "%s\n", tjGetErrorStr2(d)); return 1; }
  if (argv[1][0] == 'd') { FILE *o = fopen(argv[3], "wb"); fwrite(cmyk, 1, (size_t)w * h * 4, o); fclose(o); printf("%d %d\n", w, h); return 0; }
  tjhandle c = tjInitCompress(); unsigned char *out = NULL; unsigned long outSize = 0;
  if (tjCompress2(c, cmyk, w, 0, h, TJPF_CMYK, &out, &outSize, TJSAMP_420, 90, 0)) { fprintf(stderr, "%s\n", tjGetErrorStr2(c)); return 1; }
  FILE *o = fopen(argv[3], "wb"); fwrite(out, 1, outSize, o); fclose(o); return 0;
}
'''


def floats(width, height, channels, seed):
    """Deterministic floats with values below 0, inside 0..1, above 1, and a few awkward ones (tiny, large, negative zero)."""
    values = []
    state = seed
    for i in range(width * height * channels):
        state = (state * 1103515245 + 12345) & 0x7FFFFFFF
        values.append(((state % 20001) - 2000) / 10000.0)
    values[0:4] = [0.0, -0.0, 1e-30, 65504.0][: min(4, len(values))]
    return [struct.unpack("<f", struct.pack("<f", v))[0] for v in values]


def main():
    os.makedirs(PFM_REF, exist_ok=True)
    os.makedirs(JPEG_REF, exist_ok=True)
    work = ".cache/refs-2.1.1"
    os.makedirs(work, exist_ok=True)

    # Inputs for ImageMagick (raw floats) and Netpbm (16-bit PAM and PGM).
    source = {"magick-rgb": {"width": 5, "height": 3, "channels": 3, "floats": floats(5, 3, 3, 7)},
              "magick-gray": {"width": 4, "height": 4, "channels": 1, "floats": floats(4, 4, 1, 11)},
              "netpbm-rgb": {"width": 6, "height": 2, "channels": 3, "maxval": 1000, "samples": [(i * 37) % 1001 for i in range(36)]},
              "netpbm-gray": {"width": 3, "height": 5, "channels": 1, "maxval": 65535, "samples": [(i * 4099) % 65536 for i in range(15)]}}
    for name in ("magick-rgb", "magick-gray"):
        s = source[name]
        with open(os.path.join(work, name + ".raw"), "wb") as f:
            f.write(struct.pack("<%df" % len(s["floats"]), *s["floats"]))
    for name in ("netpbm-rgb", "netpbm-gray"):
        s = source[name]
        magic = "P6" if s["channels"] == 3 else "P5"
        with open(os.path.join(work, name + ".pnm"), "wb") as f:
            f.write(f"{magic}\n{s['width']} {s['height']}\n{s['maxval']}\n".encode())
            f.write(struct.pack(">%dH" % len(s["samples"]), *s["samples"]))
    with open(os.path.join(work, "turbo.c"), "w", newline="\n") as f:
        f.write(TURBO)

    script = f"""
set -e
apk add --no-cache {PACKAGES} > /dev/null
cd /repo
W={work}
gcc -O2 -o /tmp/turbo $W/turbo.c -lturbojpeg
magick -size 5x3 -depth 32 -define quantum:format=floating-point -endian LSB rgb:$W/magick-rgb.raw {PFM}/magick-rgb.pfm
magick -size 4x4 -depth 32 -define quantum:format=floating-point -endian LSB gray:$W/magick-gray.raw {PFM}/magick-gray.pfm
pamtopfm < $W/netpbm-rgb.pnm > {PFM}/netpbm-rgb.pfm
pamtopfm < $W/netpbm-gray.pnm > {PFM}/netpbm-gray.pfm
for f in {PFM}/*.pfm; do
  n=$(basename $f .pfm)
  case $(head -c 2 $f) in PF) map=rgb;; *) map=gray;; esac
  magick $f -depth 32 -define quantum:format=floating-point -endian LSB $map:- | gzip -9n > {PFM_REF}/$n.magick.f32.gz
done
/tmp/turbo d {JPEG}/duck-cmyk.jpg $W/duck-cmyk.cmyk > $W/duck-cmyk.size
gzip -9nc $W/duck-cmyk.cmyk > {JPEG_REF}/duck-cmyk.turbo-cmyk.gz
/tmp/turbo c {JPEG}/duck-cmyk.jpg {JPEG}/duck-ycck.jpg
/tmp/turbo d {JPEG}/duck-ycck.jpg $W/duck-ycck.cmyk > $W/duck-ycck.size
gzip -9nc $W/duck-ycck.cmyk > {JPEG_REF}/duck-ycck.turbo-cmyk.gz
magick -version | head -1 > $W/versions.txt
apk info -v netpbm libjpeg-turbo 2>/dev/null >> $W/versions.txt
"""
    repo = os.getcwd()
    subprocess.run(["docker", "run", "--rm", "-v", f"{repo}:/repo", IMAGE, "sh", "-c", script], check=True)

    versions = open(os.path.join(work, "versions.txt")).read().strip().splitlines()
    source["tools"] = versions
    with open(os.path.join(PFM_REF, "pfm-source.json"), "w", newline="\n") as f:
        json.dump(source, f, indent=1)
    print("Tools:", "; ".join(versions))
    print("Wrote", PFM_REF, "and", JPEG_REF)


if __name__ == "__main__":
    sys.exit(main())
