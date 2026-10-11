"""Builds the JPEG 2000 corpus in tests/images/jpeg2000 and its references in tests/reference/jpeg2000, in Docker.

The image tools/codecref/Dockerfile (egami-codecref:2.3.4) pins OpenJPEG 2.5.4's tools; tools/codecref/jpeg2000corpus.py
encodes a crop of tests/images/ops/duck.png with opj_compress in every coding option it has and decodes each file with
opj_decompress (see that script for the list).

Local references (never committed), which Jpeg2000ConformanceTests and Jpeg2000StreamingTests read when present:
  .cache/openjpeg-data         https://github.com/uclouvain/openjpeg-data (input/conformance and baseline/conformance)
  .cache/j2k-opj               opj_decompress of each conformance codestream to PGX
  .cache/j2k-opj-r             the same with -r 1 and -r 2
  .cache/j2k-pillow            Pillow's decode of each conformance JP2 file to PNG
  .cache/j2k-speed             the speed set egami-compare's Jpeg2000 benchmarks time: zoo1.jp2 (3906 x 2602) re-encoded
                               lossless, lossy, layered and tiled, and its gray scan lossless and lossy

Usage, from the repo root with Docker running:
  python tools/make-jpeg2000-references.py [--local]
"""
import os
import subprocess
import sys

IMAGE = "egami-codecref:2.3.4"

LOCAL = r"""
set -e
cd /w/.cache/openjpeg-data/input/conformance
mkdir -p /w/.cache/j2k-opj /w/.cache/j2k-opj-r /w/.cache/j2k-pillow
for f in p*.j2k; do
  opj_decompress -quiet -i $f -o /w/.cache/j2k-opj/${f%.j2k}.pgx || true
  for r in 1 2; do opj_decompress -quiet -r $r -i $f -o /w/.cache/j2k-opj-r/${f%.j2k}_r$r.pgx || true; done
done
mkdir -p /w/.cache/j2k-speed
cd /w/.cache/j2k-speed
opj_decompress -quiet -i /w/.cache/openjpeg-data/input/conformance/zoo1.jp2 -o photo.ppm
magick photo.ppm -colorspace Gray -depth 8 scan.pgm
opj_compress -i photo.ppm -o photo-lossless.j2k
opj_compress -i photo.ppm -o photo-lossy.j2k -I -r 20
opj_compress -i photo.ppm -o photo-layers.jp2 -I -r 80,40,20
opj_compress -i photo.ppm -o photo-tiled.j2k -I -r 20 -t 512,512
opj_compress -i scan.pgm -o scan-lossless.j2k
opj_compress -i scan.pgm -o scan-lossy.jp2 -I -r 10
rm photo.ppm scan.pgm
cd /w/.cache/openjpeg-data/input/conformance
python3 - <<'PY'
import glob, os
from PIL import Image
for path in glob.glob('*.jp2'):
    image = Image.open(path)
    image.load()
    image.convert('RGBA').save('/w/.cache/j2k-pillow/' + os.path.basename(path) + '.png')
PY
"""


def docker(*args):
    subprocess.run(["docker", "run", "--rm", "-v", f"{os.getcwd()}:/w", IMAGE, *args], check=True, env={**os.environ, "MSYS_NO_PATHCONV": "1"})


def main():
    subprocess.run(["docker", "build", "-t", IMAGE, "tools/codecref"], check=True)
    docker("python3", "/w/tools/codecref/jpeg2000corpus.py")
    if "--local" in sys.argv:
        if not os.path.isdir(".cache/openjpeg-data/input/conformance"):
            subprocess.run(["git", "clone", "--depth", "1", "https://github.com/uclouvain/openjpeg-data", ".cache/openjpeg-data"], check=True)
        docker("sh", "-c", LOCAL)


if __name__ == "__main__":
    main()
