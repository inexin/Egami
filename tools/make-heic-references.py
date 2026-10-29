"""Builds the Phase 2.9.4 HEIC corpus and references, in Docker.

The image tools/heicref/Dockerfile (egami-heicref:2.9.4) builds HM 18.0, the HEVC reference software (BSD 3-clause), from source
and adds libheif 1.23.4 (heif-dec, heif-info, heif-enc with x265 and kvazaar), libde265 1.0.15's dec265, x265 4.1, kvazaar 2.3.1
and ExifTool from Debian. libheif, libde265 and x265 run as black-box tools only; their sources are never read.
tools/heicref/heiccorpus.py writes the files (see its docstring); tools/heicref/heif.py is the small HEIF writer it uses.

Local inputs (never committed, fetched here when missing):
  .cache/heic/phones/<item>/        phone photos from the Internet Archive (https://archive.org/details/<item>), checked against
                                    the SHA-1 in each item's metadata; the committed ones are cut down from these
  .cache/heic/ffc/                  MPEG's File Format conformance HEIF files (https://github.com/MPEGGroup/FileFormatConformance
                                    at 767a5bc, Git LFS objects), most of them Nokia's conformance files with no licence of their own
  .cache/heic/nokia/                Nokia's HEIF conformance files and bitstreams (https://github.com/nokiatech/heif_conformance at
                                    6ff1e33), no licence file
  .cache/heic/hevc/{HEVC_v1,RExt}/  the ITU-T H.265.1 conformance streams (https://www.itu.int/wftp3/av-arch/jctvc-site/
                                    bitstream_exchange/draft_conformance/), about 200 MB of zips, unpacked; redistribution terms
                                    are not confirmed, so they never leave this machine

Usage, from the repo root with Docker running:
  python tools/make-heic-references.py              fetch, then the corpus and every reference
  python tools/make-heic-references.py fetch        the local inputs only
  python tools/make-heic-references.py corpus       tests/images/heic and tests/reference/heic again
  python tools/make-heic-references.py conformance  .cache/heic/reference/{HEVC_v1,RExt,nokia} (HM's decode of every stream)
  python tools/make-heic-references.py extra        .cache/heic/reference/extra (the HEIF files that stay in .cache)
  python tools/make-heic-references.py suite        the conformance summaries' suite MD5s again, without decoding
  python tools/make-heic-references.py traces       HM's syntax traces: tests/reference/heic/hm-headers.json.gz and .cache
  python tools/make-heic-references.py frames       .cache/heic/reference/{HEVC_v1,RExt}/frames.json: the MD5 of every frame HM
                                                    outputs, in output order (2.9.10's inter decoding)
"""
import hashlib
import json
import os
import re
import subprocess
import sys
import time
import urllib.parse
import urllib.request
import zipfile

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "heicref"))
from phones import PHONES  # noqa: E402

IMAGE = "egami-heicref:2.9.4"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CACHE = os.path.join(ROOT, ".cache", "heic")
FFC = ("MPEGGroup/FileFormatConformance", "767a5bcb9fa4841d1b3bb0ca112a6e7d4c6bf067")
NOKIA = ("https://github.com/nokiatech/heif_conformance", "6ff1e33481d325296d8a787432453ef3fa91f591")
ITU = "https://www.itu.int/wftp3/av-arch/jctvc-site/bitstream_exchange/draft_conformance/"


def download(url, timeout=300):
    for attempt in range(5):
        try:
            with urllib.request.urlopen(url, timeout=timeout) as r:
                return r.read()
        except Exception:
            time.sleep(2 << attempt)
    raise RuntimeError("download failed: " + url)


def fetch():
    os.makedirs(CACHE, exist_ok=True)
    for entry in PHONES:
        item, file, sha1 = entry[0], entry[1], entry[2]
        path = os.path.join(CACHE, "phones", item, os.path.basename(file))
        if os.path.isfile(path) and hashlib.sha1(open(path, "rb").read()).hexdigest() == sha1:
            continue
        data = download(f"https://archive.org/download/{item}/{urllib.parse.quote(file)}")
        if hashlib.sha1(data).hexdigest() != sha1:
            raise RuntimeError(f"{item}/{file}: SHA-1 mismatch")
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as f:
            f.write(data)
        print(f"{item}/{file}: {len(data)} bytes")

    folder = os.path.join(CACHE, "ffc")
    if not os.path.isfile(os.path.join(folder, "fetched")):
        tree = json.loads(download(f"https://api.github.com/repos/{FFC[0]}/git/trees/{FFC[1]}?recursive=1"))["tree"]
        for node in tree:
            path = node["path"]
            if node["type"] != "blob" or "/heif/" not in path or not path.endswith((".heic", ".heif", ".json")):
                continue
            url = f"https://media.githubusercontent.com/media/{FFC[0]}/{FFC[1]}/{path}" if not path.endswith(".json") \
                else f"https://raw.githubusercontent.com/{FFC[0]}/{FFC[1]}/{path}"
            data = download(url)
            out = os.path.join(folder, os.path.relpath(path, "data/file_features"))
            os.makedirs(os.path.dirname(out), exist_ok=True)
            with open(out, "wb") as f:
                f.write(data)
        open(os.path.join(folder, "fetched"), "w").close()

    folder = os.path.join(CACHE, "nokia")
    if not os.path.isdir(folder):
        subprocess.run(["git", "clone", "-q", NOKIA[0], folder], check=True)
    subprocess.run(["git", "-C", folder, "checkout", "-q", NOKIA[1]], check=True)

    for suite in ("HEVC_v1", "RExt"):
        folder = os.path.join(CACHE, "hevc", suite)
        os.makedirs(folder, exist_ok=True)
        listing = download(ITU + suite + "/").decode("latin-1")
        for name in sorted(set(re.findall(r'HREF="[^"]*/([^/"]+\.zip)"', listing))):
            unpacked = os.path.join(folder, name[:-4])
            if os.path.isdir(unpacked):
                continue
            data = download(ITU + suite + "/" + name, timeout=900)
            archive = os.path.join(folder, name)
            with open(archive, "wb") as f:
                f.write(data)
            with zipfile.ZipFile(archive) as z:
                z.extractall(unpacked)
            os.remove(archive)
            print(f"{suite}/{name}: {len(data)} bytes")


def run(*args):
    if subprocess.run(["docker", "image", "inspect", IMAGE], capture_output=True).returncode != 0:
        subprocess.run(["docker", "build", "-t", IMAGE, os.path.join(ROOT, "tools", "heicref")], check=True)
    # .cache may be a junction to the main checkout's; Docker mounts real folders.
    os.makedirs(CACHE, exist_ok=True)
    env = dict(os.environ, MSYS_NO_PATHCONV="1")
    mounts = ["-v", f"{ROOT}:/w", "-v", f"{os.path.realpath(CACHE)}:/w/.cache/heic",
              "-v", f"{os.path.join(ROOT, 'tools', 'heicref')}:/t:ro", "-v", f"{os.path.join(ROOT, 'tools', 'avifref')}:/a:ro"]
    subprocess.run(["docker", "run", "--rm", *mounts, IMAGE, "python3", "/t/heiccorpus.py", *args], check=True, env=env)


if __name__ == "__main__":
    steps = sys.argv[1:] or ["fetch", "corpus", "conformance", "extra", "traces"]
    if "fetch" in steps:
        fetch()
    for step in ("corpus", "conformance", "extra", "suite", "traces", "frames"):
        if step in steps:
            run(step)
