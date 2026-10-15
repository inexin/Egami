"""Builds the Phase 2.9.2 AVIF corpus and references, in Docker.

The image tools/avifref/Dockerfile (egami-avifref:2.9.2) pins dav1d 1.5.4, libaom 3.15.2 and libavif 1.4.2 built from source,
SVT-AV1 2.3.0 and rav1e 0.7.1 from Debian, and ExifTool; tools/avifref/avifcorpus.py writes the files (see its docstring). The
tools run as oracles only.

Local inputs (never committed, fetched here when missing):
  .cache/avif/libavif-1.4.2         https://github.com/AOMediaCodec/libavif at v1.4.2 (BSD-2); its tests/data files whose
                                    pictures are libavif's are copied into the corpus, the others stay here
  .cache/avif/av1-avif-bf4c18d      https://github.com/AOMediaCodec/av1-avif at bf4c18d1, the AVIF test files from Apple
                                    (CC BY-SA 4.0), Link-U (CC BY-SA 4.0), Microsoft (copyright in each file's Exif), Netflix
                                    (CC BY-NC-ND 4.0) and Xiph (CC BY-SA 3.0 and CC BY 3.0)
  .cache/avif/aom-3.15.2            libaom at v3.15.2, for test/test-data.sha1
  .cache/avif/aom-test-data         libaom's AV1 test vectors (av1-1-b8-*, av1-1-b10-*) and their MD5 files from
                                    https://storage.googleapis.com/aom-test-data, checked against test-data.sha1
  .cache/avif/dav1d-test-data-1.5.4 https://code.videolan.org/videolan/dav1d-test-data at 1.5.4
  .cache/avif/argon                 the Argon Streams AV1 suite v2.1.1 (7 GB zip, checked against AOM's MD5), unpacked. Large,
                                    so .cache/avif/argon may be a junction to another drive. Never leaves this machine.

Usage, from the repo root with Docker running:
  python tools/make-avif-references.py              fetch, then the corpus and every reference
  python tools/make-avif-references.py fetch        the local inputs only (add "argon" to fetch the Argon streams too)
  python tools/make-avif-references.py corpus       tests/images/avif and tests/reference/avif again
  python tools/make-avif-references.py conformance  .cache/avif/reference/{aom,dav1d,argon} (first frames of every stream)
  python tools/make-avif-references.py extra        .cache/avif/reference/extra (the AVIF files that stay in .cache)
"""
import hashlib
import os
import subprocess
import sys
import time
import urllib.request
import zipfile

IMAGE = "egami-avifref:2.9.2"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CACHE = os.path.join(ROOT, ".cache", "avif")
LIBAVIF = ("https://github.com/AOMediaCodec/libavif", "v1.4.2")
AV1_AVIF = ("https://github.com/AOMediaCodec/av1-avif", "bf4c18d1f3971069b75e87d6ee469790589f4f09")
AOM = ("https://aomedia.googlesource.com/aom", "v3.15.2")
DAV1D_DATA = ("https://code.videolan.org/videolan/dav1d-test-data.git", "1.5.4")
AOM_DATA = "https://storage.googleapis.com/aom-test-data/"
ARGON = ("https://aom-cwg-av1-argon-streams-public.s3.us-east-1.amazonaws.com/"
         "argon_coveragetool_av1_base_and_extended_profiles_v2.1.1.zip", "5dd3554c3f73c80f06c7fceeaa1f5dd9")


def download(url):
    for attempt in range(5):
        try:
            with urllib.request.urlopen(url, timeout=120) as r:
                return r.read()
        except Exception:
            time.sleep(2 << attempt)
    raise RuntimeError("download failed: " + url)


def clone(url, ref, folder, shallow=True):
    if not os.path.isdir(folder):
        args = ["--depth", "1", "--branch", ref] if shallow else []
        subprocess.run(["git", "clone", "-q", *args, url, folder], check=True)
    if not shallow:
        subprocess.run(["git", "-C", folder, "checkout", "-q", ref], check=True)


def fetch(argon=False):
    os.makedirs(CACHE, exist_ok=True)
    clone(*LIBAVIF, os.path.join(CACHE, "libavif-1.4.2"))
    clone(*AV1_AVIF, os.path.join(CACHE, "av1-avif-bf4c18d"), shallow=False)
    clone(*AOM, os.path.join(CACHE, "aom-3.15.2"))
    clone(*DAV1D_DATA, os.path.join(CACHE, "dav1d-test-data-1.5.4"))

    vectors = os.path.join(CACHE, "aom-test-data")
    os.makedirs(vectors, exist_ok=True)
    with open(os.path.join(CACHE, "aom-3.15.2", "test", "test-data.sha1")) as f:
        sums = [line.split() for line in f if line.strip()]
    for sha, name in sums:
        name = name.lstrip("*")
        if not name.startswith("av1-1-b") or not name.endswith((".ivf", ".ivf.md5")):
            continue
        path = os.path.join(vectors, name)
        if os.path.isfile(path) and hashlib.sha1(open(path, "rb").read()).hexdigest() == sha:
            continue
        data = download(AOM_DATA + name)
        if hashlib.sha1(data).hexdigest() != sha:
            raise RuntimeError(f"{name}: SHA-1 mismatch")
        with open(path, "wb") as f:
            f.write(data)
        print(f"{name}: {len(data)} bytes")

    if argon:
        folder = os.path.join(CACHE, "argon")
        os.makedirs(folder, exist_ok=True)
        archive = os.path.join(folder, "argon.zip")
        if not os.path.isfile(os.path.join(folder, "unpacked")):
            if not os.path.isfile(archive):
                subprocess.run(["curl", "-sS", "-C", "-", "--retry", "5", "-o", archive, ARGON[0]], check=True)
            digest = hashlib.md5()
            with open(archive, "rb") as f:
                for block in iter(lambda: f.read(1 << 24), b""):
                    digest.update(block)
            if digest.hexdigest() != ARGON[1]:
                raise RuntimeError("Argon streams: MD5 mismatch")
            with zipfile.ZipFile(archive) as z:
                z.extractall(folder)
            open(os.path.join(folder, "unpacked"), "w").close()


def run(*args):
    if subprocess.run(["docker", "image", "inspect", IMAGE], capture_output=True).returncode != 0:
        subprocess.run(["docker", "build", "-t", IMAGE, os.path.join(ROOT, "tools", "avifref")], check=True)
    # .cache may be a junction to the main checkout's, and .cache/avif/argon one to another drive; Docker mounts real folders.
    env = dict(os.environ, MSYS_NO_PATHCONV="1")
    mounts = ["-v", f"{ROOT}:/w", "-v", f"{os.path.realpath(CACHE)}:/w/.cache/avif",
              "-v", f"{os.path.realpath(os.path.join(ROOT, '.cache', 'jxl'))}:/w/.cache/jxl:ro",
              "-v", f"{os.path.join(ROOT, 'tools', 'avifref')}:/t:ro"]
    argon = os.path.join(CACHE, "argon")
    if os.path.isdir(argon):
        mounts += ["-v", f"{os.path.realpath(argon)}:/w/.cache/avif/argon"]
    subprocess.run(["docker", "run", "--rm", *mounts, IMAGE, "python3", "/t/avifcorpus.py", *args], check=True, env=env)


steps = sys.argv[1:] or ["fetch", "corpus", "conformance", "extra"]
if "fetch" in steps:
    fetch("argon" in steps)
for step in ("corpus", "conformance", "extra"):
    if step in steps:
        run(step)
