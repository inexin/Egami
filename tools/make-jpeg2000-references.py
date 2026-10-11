"""Builds the JPEG 2000 corpus in tests/images/jpeg2000 and its references in tests/reference/jpeg2000, in Docker.

The image tools/codecref/Dockerfile (egami-codecref:2.3.4) pins OpenJPEG 2.5.4's tools; tools/codecref/jpeg2000corpus.py
encodes a crop of tests/images/ops/duck.png with opj_compress in every coding option it has and decodes each file with
opj_decompress (see that script for the list).

Local references (never committed), which Jpeg2000ConformanceTests and Jpeg2000StreamingTests read when present:
  .cache/openjpeg-data         https://github.com/uclouvain/openjpeg-data (input/conformance and baseline/conformance)
  .cache/j2k-opj               opj_decompress of each conformance codestream to PGX
  .cache/j2k-opj-r             the same with -r 1 and -r 2
  .cache/j2k-pillow            Pillow's decode of each conformance JP2 file to PNG

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
