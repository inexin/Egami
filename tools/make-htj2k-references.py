"""Builds the HTJ2K corpus in tests/images/htj2k and its references in tests/reference/htj2k, in Docker.

The image tools/htj2kref/Dockerfile (egami-htj2kref:1) pins OpenJPH 0.33.0 and OpenJPEG 2.5.4's tools, both BSD-2 and both used
only as command-line tools; tools/htj2kref/htcorpus.py encodes a crop of tests/images/ops/duck.png with ojph_compress and
decodes each file with opj_decompress (see that script for the list).

Local references (never committed: the conformance set's licence allows use for JPEG 2000 conformance testing only), which
HtJ2kConformanceTests reads when present:
  .cache/htj2k-codestreams     https://gitlab.com/wg1/htj2k-codestreams (the ISO/IEC 15444-4 HT codestreams and references)
  .cache/htj2k-opj             opj_decompress of each HT codestream there, and of openjpeg-data's nonregression/htj2k files,
                               to PGX
  .cache/htj2k-speed           the speed set egami-compare's HTJ2K benchmarks time, from the 4000 x 3000 benchmark photo in
                               .cache/bench-jpeg/photo.png (tools/make-jpeg-bench-files.py): HT lossless and lossy as JPH, HT
                               tiled, and its gray version HT lossless and lossy; and the same photo as T.800 codestreams
                               (legacy-*), to check the T.800 decode does not get slower

Usage, from the repo root with Docker running:
  python tools/make-htj2k-references.py [--local]
"""
import os
import subprocess
import sys

IMAGE = "egami-htj2kref:1"

LOCAL = r"""
set -e
mkdir -p /w/.cache/htj2k-opj
cd /w/.cache/htj2k-codestreams
for f in htj2k_bsets_profile*/*/*.j2k; do
  b=${f##*/}; opj_decompress -quiet -i $f -o /w/.cache/htj2k-opj/${b%.*}.pgx || true
done
# The JPH files' codestreams alone: opj_decompress applies palettes and color conversions to a JPH file, Egami's tests compare
# the components.
for f in testfiles_jph/*.jph; do
  b=${f##*/}
  python3 /w/tools/htj2kref/jp2c.py $f /tmp/$b.j2c
  opj_decompress -quiet -i /tmp/$b.j2c -o /w/.cache/htj2k-opj/${b%.*}.pgx || true
done
if [ -d /w/.cache/openjpeg-data/input/nonregression/htj2k ]; then
  cd /w/.cache/openjpeg-data/input/nonregression/htj2k
  for f in *.j2k *.jph *.jhc; do
    [ -f "$f" ] || continue
    opj_decompress -quiet -i $f -o /w/.cache/htj2k-opj/nr_${f%.*}.pgx || true
  done
fi
"""

SPEED = r"""
set -e
mkdir -p /w/.cache/htj2k-speed /tmp/s
cd /tmp/s
magick /w/.cache/bench-jpeg/photo.png -alpha off -depth 8 photo.ppm
magick photo.ppm -colorspace Gray -depth 8 scan.pgm
ojph_compress -i photo.ppm -o photo-ht-lossless.j2c -reversible true
ojph_compress -i photo.ppm -o photo-ht-lossy.j2c -qstep 0.01
ojph_compress -i photo.ppm -o /w/.cache/htj2k-speed/photo-ht-tiled.j2c -qstep 0.01 -tile_size {512,512}
ojph_compress -i scan.pgm -o /w/.cache/htj2k-speed/scan-ht-lossless.j2c -reversible true
ojph_compress -i scan.pgm -o /w/.cache/htj2k-speed/scan-ht-lossy.j2c -qstep 0.004
python3 /w/tools/htj2kref/htcorpus.py wrap photo-ht-lossless.j2c /w/.cache/htj2k-speed/photo-ht-lossless.jph
python3 /w/tools/htj2kref/htcorpus.py wrap photo-ht-lossy.j2c /w/.cache/htj2k-speed/photo-ht-lossy.jph
opj_compress -i photo.ppm -o /w/.cache/htj2k-speed/legacy-photo-lossless.j2k
opj_compress -i photo.ppm -o /w/.cache/htj2k-speed/legacy-photo-lossy.j2k -I -r 20
opj_compress -i scan.pgm -o /w/.cache/htj2k-speed/legacy-scan-lossless.j2k
"""


def docker(*args):
    subprocess.run(["docker", "run", "--rm", "-v", f"{os.getcwd()}:/w", IMAGE, *args], check=True, env={**os.environ, "MSYS_NO_PATHCONV": "1"})


def main():
    subprocess.run(["docker", "build", "-t", IMAGE, "tools/htj2kref"], check=True)
    docker("python3", "/w/tools/htj2kref/htcorpus.py")
    if "--local" in sys.argv:
        if not os.path.isdir(".cache/htj2k-codestreams"):
            subprocess.run(["git", "clone", "--depth", "1", "https://gitlab.com/wg1/htj2k-codestreams", ".cache/htj2k-codestreams"], check=True)
        docker("sh", "-c", LOCAL)
        if os.path.isfile(".cache/bench-jpeg/photo.png"):
            docker("sh", "-c", SPEED)


if __name__ == "__main__":
    main()
