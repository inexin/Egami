"""Builds the Phase 2.9.5 (AV1 inter frames) references, in Docker.

Uses the image of tools/make-avif-references.py (egami-avifref:2.9.2: libaom 3.15.2 and dav1d 1.5.4 built from source, run as
oracles only). Local inputs, never committed, fetched here when missing:
  .cache/avif/aom-test-data   libaom's AV1 test vectors (av1-1-b8-*, av1-1-b10-*) and their MD5 files from
                              https://storage.googleapis.com/aom-test-data, checked against libaom's test/test-data.sha1

tools/avifref/av1corpus.py then writes .cache/avif/reference/av1/<stream>.json: each output frame's size, bit depth and MD5
(with and without film grain) as libaom decodes it, for those vectors and for tests/video/corpus/av1.

Usage, from the repo root with Docker running:
  python tools/make-av1-references.py            fetch, then every reference not made yet
  python tools/make-av1-references.py NAME ...   the references of the streams whose names contain NAME, made again
"""
import hashlib
import os
import subprocess
import sys
import time
import urllib.request

IMAGE = "egami-avifref:2.9.2"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CACHE = os.path.join(ROOT, ".cache", "avif")
AOM = ("https://aomedia.googlesource.com/aom", "v3.15.2")
AOM_DATA = "https://storage.googleapis.com/aom-test-data/"


def download(url):
    for attempt in range(5):
        try:
            with urllib.request.urlopen(url, timeout=120) as r:
                return r.read()
        except Exception:
            time.sleep(2 << attempt)
    raise RuntimeError("download failed: " + url)


def fetch():
    sums = os.path.join(CACHE, "aom-3.15.2", "test", "test-data.sha1")
    if not os.path.isfile(sums):
        subprocess.run(["git", "clone", "-q", "--depth", "1", "--branch", AOM[1], AOM[0], os.path.join(CACHE, "aom-3.15.2")], check=True)
    vectors = os.path.join(CACHE, "aom-test-data")
    os.makedirs(vectors, exist_ok=True)
    for line in open(sums):
        if not line.strip():
            continue
        sha, name = line.split()
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


def run(*args):
    if subprocess.run(["docker", "image", "inspect", IMAGE], capture_output=True).returncode != 0:
        subprocess.run(["docker", "build", "-t", IMAGE, os.path.join(ROOT, "tools", "avifref")], check=True)
    env = dict(os.environ, MSYS_NO_PATHCONV="1")
    mounts = ["-v", f"{ROOT}:/w", "-v", f"{os.path.realpath(CACHE)}:/w/.cache/avif", "-v", f"{os.path.join(ROOT, 'tools', 'avifref')}:/t:ro"]
    subprocess.run(["docker", "run", "--rm", *mounts, IMAGE, "python3", "/t/av1corpus.py", *args], check=True, env=env)


fetch()
run(*sys.argv[1:])
