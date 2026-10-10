"""Checks the ICC profiles Egami writes (ColorProfile.ToIcc) with Little CMS, Pillow and ImageMagick, in Docker.

Input: a directory with <name>.icc for each built-in encoding, grid.f32 (a 9-step sRGB grid as float32 RGB) and <name>.egami.f32
(what Egami converts the grid to in that encoding, relative colorimetric, float32 RGB or gray). Write them with a small program
that calls ToIcc and ColorTransform (see the 2.1.2 task 8 commit).

For each profile: Little CMS opens it and converts the grid from sRGB (the CC0 compact sRGB-v4 profile) to it, relative
colorimetric, unoptimized, in double precision; Pillow opens it with ImageCms; ImageMagick converts an image to it. The gate is
that all three accept every profile and Little CMS's conversion is within one 8-bit level of Egami's. Two explained exceptions:
Adobe RGB's pure 2.2 power curve turns the colorants' 16-bit rounding into about 2 levels next to 0 (so it is held to one level
above 2%), and PQ and HLG are not compared, since Little CMS ignores their cicp tag.

Usage, from the repo root with Docker running:
  python tools/check-written-icc.py .cache/written-icc
"""
import os
import struct
import subprocess
import sys

IMAGE = "alpine:3.20"
PACKAGES = "lcms2-dev gcc musl-dev imagemagick python3 py3-pillow"

HARNESS = r'''
#include <stdio.h>
#include <stdlib.h>
#include <lcms2.h>
int main(int argc, char **argv) {
  cmsHPROFILE s = cmsOpenProfileFromFile(argv[1], "r"), d = cmsOpenProfileFromFile(argv[2], "r");
  if (!s || !d) { fprintf(stderr, "cannot open %s\n", argv[2]); return 2; }
  int gray = cmsGetColorSpace(d) == cmsSigGrayData;
  cmsHTRANSFORM x = cmsCreateTransform(s, TYPE_RGB_DBL, d, gray ? TYPE_GRAY_DBL : TYPE_RGB_DBL, INTENT_RELATIVE_COLORIMETRIC, cmsFLAGS_NOOPTIMIZE | cmsFLAGS_NOCACHE);
  if (!x) { fprintf(stderr, "no transform to %s\n", argv[2]); return 3; }
  int n = atoi(argv[3]), co = gray ? 1 : 3;
  float *f = malloc(sizeof(float) * n * 3); double *a = malloc(sizeof(double) * n * 3), *b = malloc(sizeof(double) * n * 3);
  FILE *in = fopen(argv[4], "rb"); fread(f, sizeof(float), n * 3, in); fclose(in);
  for (int i = 0; i < n * 3; i++) a[i] = f[i];
  cmsDoTransform(x, a, b, n);
  for (int i = 0; i < n * co; i++) f[i] = (float)b[i];
  FILE *out = fopen(argv[5], "wb"); fwrite(f, sizeof(float), n * co, out); fclose(out);
  char desc[256]; cmsGetProfileInfoASCII(d, cmsInfoDescription, "en", "US", desc, sizeof desc);
  printf("%s: version %.1f, %s\n", argv[2], cmsGetProfileVersion(d), desc);
  return 0;
}
'''

PILLOW = r'''
import sys
from PIL import ImageCms
for path in sys.argv[1:]:
    p = ImageCms.getOpenProfile(path)
    print(path + ": Pillow reads", repr(ImageCms.getProfileDescription(p).strip()))
'''


def floats(path):
    data = open(path, "rb").read()
    return struct.unpack(f"<{len(data) // 4}f", data)


def main():
    folder = sys.argv[1].replace("\\", "/")
    names = sorted(n[:-4] for n in os.listdir(folder) if n.endswith(".icc"))
    with open(os.path.join(folder, "harness.c"), "w", newline="\n") as f:
        f.write(HARNESS)
    with open(os.path.join(folder, "pillow.py"), "w", newline="\n") as f:
        f.write(PILLOW)
    lines = [f"cd /repo && apk add -q {PACKAGES} >/dev/null && gcc -O2 -o /tmp/h {folder}/harness.c -llcms2 || exit 1", "status=0"]
    for name in names:
        icc = f"{folder}/{name}.icc"
        lines.append(f"/tmp/h tests/images/icc/compact/sRGB-v4.icc {icc} 729 {folder}/grid.f32 {folder}/{name}.lcms.f32 || status=1")
        gray = "-colorspace gray " if name.startswith("Gray") else ""
        lines.append(f"magick -size 4x4 xc:'rgb(200,100,50)' -profile tests/images/icc/compact/sRGB-v4.icc {gray}-profile {icc} /tmp/{name}.tif"
                     f" && echo '{icc}: ImageMagick converts to it' || status=1")
    lines.append(f"python3 {folder}/pillow.py " + " ".join(f"{folder}/{n}.icc" for n in names) + " || status=1")
    lines.append("magick -version | head -1; apk list -I lcms2 py3-pillow 2>/dev/null | cut -d' ' -f1; exit $status")
    result = subprocess.run(["docker", "run", "--rm", "-v", f"{os.getcwd()}:/repo", IMAGE, "sh", "-c", "\n".join(lines)])
    failed = result.returncode != 0

    for name in names:
        ours, theirs = floats(f"{folder}/{name}.egami.f32"), floats(f"{folder}/{name}.lcms.f32")
        pairs = [(min(max(a, 0), 1), min(max(b, 0), 1)) for a, b in zip(ours, theirs)]
        worst = max(abs(a - b) * 255 for a, b in pairs)
        # A pure power curve's slope is infinite at 0, so the 16-bit rounding of a profile's colorants shows as levels there.
        lit = max([abs(a - b) * 255 for a, b in pairs if max(a, b) >= 0.02] or [0])
        hdr = name.startswith("Rec2100")
        ok = hdr or worst <= 1 or (lit <= 1 and name.startswith("Adobe"))
        failed |= not ok
        note = " (HDR: Little CMS 2.19 does not apply cicp, so it uses the rolled-off fallback curve; accepted, not compared)" if hdr else ""
        print(f"{name}: Little CMS through the written profile against Egami's built-in, worst {worst:.3f} levels, {lit:.3f} above 2% {'ok' if ok else 'FAIL'}{note}")
    print("FAILED" if failed else "All written profiles accepted and within 1 level.")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
