"""Builds the Phase 2.9.3 JPEG XL corpus and references, in Docker.

The image tools/jxlref/Dockerfile (egami-jxlref:2.9.3) pins libjxl 0.12.0 (djxl, cjxl, jxlinfo), jxl-oxide-cli 0.12.6, ExifTool
and Squoosh's CLI 0.7.3; tools/jxlref/jxlcorpus.py writes the files (see its docstring). The tools run as oracles only.

Local inputs (never committed, fetched here when missing):
  .cache/jxl/conformance  https://github.com/libjxl/conformance at b1d0f990 (BSD-3), the ISO/IEC 18181-3 bitstreams, with
                          each test case's reference_image.npy and ICC profiles downloaded from the jxl-conformance bucket and
                          checked against the SHA-256 sums in its test.json (about 300 MB)
  .cache/jxl/testdata     https://github.com/libjxl/testdata at 73695d30 (CC BY 4.0; external/raw.pixls is CC0)
  .cache/jxl/chromium     Chromium's web test JPEG XL files at f7c81f6e (third_party/blink/web_tests/images/resources). The
                          Chromium tree is BSD-3 but the pictures' origins are not stated, so they stay in .cache

Usage, from the repo root with Docker running:
  python tools/make-jxl-references.py              fetch, then the corpus and every reference
  python tools/make-jxl-references.py fetch        the local inputs only
  python tools/make-jxl-references.py corpus       tests/images/jxl and tests/reference/jxl again
  python tools/make-jxl-references.py conformance  .cache/jxl/reference (djxl, jxl-oxide, jxlinfo and ExifTool per test case)
"""
import hashlib
import json
import os
import subprocess
import sys
import time
import urllib.request

IMAGE = "egami-jxlref:2.9.3"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CACHE = os.path.join(ROOT, ".cache", "jxl")
CONFORMANCE = ("https://github.com/libjxl/conformance", "b1d0f990b03e57bf6d137c365cd5dc8b470b9191")
TESTDATA = ("https://github.com/libjxl/testdata", "73695d303670c90e4d506ea89d9901b081385089")
BUCKET = "https://storage.googleapis.com/jxl-conformance/objects/"
CHROMIUM = ("https://raw.githubusercontent.com/chromium/chromium/f7c81f6ea0eae5589e742c3eb8c9dcb8961cf936/"
            "third_party/blink/web_tests/images/resources/")
CHROMIUM_FILES = ["3x3_gray_lossless.jxl", "5_frames_numbered.jxl", "fox-progressive.jxl", "high_bit_depth_1x1.jxl",
                  "image_decoder_frame_one_crash.jxl", "newtons_cradle.jxl", "zoltan.jxl"]


def download(url):
    for attempt in range(5):
        try:
            with urllib.request.urlopen(url, timeout=120) as r:
                return r.read()
        except Exception:
            time.sleep(2 << attempt)
    raise RuntimeError("download failed: " + url)


def clone(url, commit, folder):
    if not os.path.isdir(folder):
        subprocess.run(["git", "clone", "-q", url, folder], check=True)
    subprocess.run(["git", "-C", folder, "checkout", "-q", commit], check=True)


def fetch():
    folder = os.path.join(CACHE, "conformance")
    clone(*CONFORMANCE, folder)
    # The _5 cases link to their full cases' files; a Windows checkout writes those links as text, so copy the targets in.
    listing = subprocess.run(["git", "-C", folder, "ls-files", "-s"], capture_output=True, text=True, check=True).stdout
    for line in listing.splitlines():
        mode, _, _, path = line.split(None, 3)
        full = os.path.join(folder, path)
        if mode == "120000" and os.path.getsize(full) < 256:
            target = os.path.normpath(os.path.join(os.path.dirname(full), open(full).read().strip()))
            with open(target, "rb") as f:
                data = f.read()
            with open(full, "wb") as f:
                f.write(data)
    cases = os.path.join(folder, "testcases")
    for name in sorted(os.listdir(cases)):
        spec = os.path.join(cases, name, "test.json")
        if not os.path.isfile(spec):
            continue
        with open(spec) as f:
            sums = json.load(f).get("sha256sums", {})
        for blob, sha in sums.items():
            path = os.path.join(cases, name, blob)
            if os.path.isfile(path) and hashlib.sha256(open(path, "rb").read()).hexdigest() == sha:
                continue
            data = download(BUCKET + sha)
            if hashlib.sha256(data).hexdigest() != sha:
                raise RuntimeError(f"{name}/{blob}: SHA-256 mismatch")
            with open(path, "wb") as f:
                f.write(data)
            print(f"{name}/{blob}: {len(data)} bytes")

    testdata = os.path.join(CACHE, "testdata")
    if not os.path.isdir(testdata):
        subprocess.run(["git", "clone", "-q", "--filter=blob:none", TESTDATA[0], testdata], check=True)
    subprocess.run(["git", "-C", testdata, "checkout", "-q", TESTDATA[1]], check=True)

    chromium = os.path.join(CACHE, "chromium")
    os.makedirs(chromium, exist_ok=True)
    for name in CHROMIUM_FILES:
        path = os.path.join(chromium, name)
        if not os.path.isfile(path):
            with open(path, "wb") as f:
                f.write(download(CHROMIUM + name))


def run(*args):
    if subprocess.run(["docker", "image", "inspect", IMAGE], capture_output=True).returncode != 0:
        subprocess.run(["docker", "build", "-t", IMAGE, os.path.join(ROOT, "tools", "jxlref")], check=True)
    # .cache may be a junction to the main checkout's; Docker mounts its real folder.
    env = dict(os.environ, MSYS_NO_PATHCONV="1")
    subprocess.run(["docker", "run", "--rm", "-v", f"{ROOT}:/w", "-v", f"{os.path.realpath(CACHE)}:/w/.cache/jxl",
                    "-v", f"{os.path.join(ROOT, 'tools', 'jxlref')}:/t:ro", IMAGE, "python3", "/t/jxlcorpus.py", *args],
                   check=True, env=env)


steps = sys.argv[1:] or ["fetch", "corpus", "conformance"]
if "fetch" in steps:
    fetch()
if "corpus" in steps:
    run("corpus")
if "conformance" in steps:
    run("conformance")
