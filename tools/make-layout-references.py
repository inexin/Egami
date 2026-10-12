"""Builds the paragraph layout references of Phase 2.8.2 (question 23): line breaks, line boxes, glyph positions, carets, hit
tests and selections from a pinned headless Chromium.

1. Downloads the Chinese, Japanese and Korean corpus texts (the opening of the Universal Declaration of Human Rights, from the
   UDHR in XML project at the commit 2.8.1's shaping corpus pins) into tests/reference/layout/texts; the other scripts reuse
   tests/reference/shaping/texts.
2. Cuts Noto Sans CJK SC (fetched to .cache/fonts, SHA-256 checked) down to the corpus's characters in the egami-fontref image
   (tools/layoutref/derive.py) as tests/fonts/derived/NotoSansCJK-layout-subset.otf.
3. Builds the egami-layoutref image (tools/layoutref/Dockerfile: Chromium 129 through Playwright 1.47.0, pinned by digest) and
   runs tools/layoutref/layout.py, which writes tests/reference/layout/corpus.json.gz and features.json.gz.
4. With --check, runs the layout a second time into .cache/layoutref and fails unless both runs give the same bytes.

Usage, from the repo root with Docker running:
  python tools/make-layout-references.py [--check]
"""
import hashlib
import os
import shutil
import subprocess
import sys
import urllib.request
import xml.etree.ElementTree as ET

IMAGE = "egami-layoutref:2.8.2"
FONTREF_IMAGE = "egami-fontref:2.7.2"
CJK_URL = "https://github.com/notofonts/noto-cjk/raw/Sans2.004/Sans/OTF/SimplifiedChinese/NotoSansCJKsc-Regular.otf"
CJK_SHA256 = "2c76254f6fc379fddfce0a7e84fb5385bb135d3e399294f6eeb6680d0365b74b"
UDHR_COMMIT = "588b3f4b2d0467aff54842a4b926551b69d5a66a"
UDHR_URL = f"https://raw.githubusercontent.com/eric-muller/udhr/{UDHR_COMMIT}/data/udhr/udhr_{{0}}.xml"
TEXTS = {"chinese": "cmn_hans", "japanese": "jpn", "korean": "kor"}
LINES_PER_TEXT = 24
ENV = {**os.environ, "MSYS_NO_PATHCONV": "1"}


def download(url, path, sha256=None):
    if not os.path.exists(path):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        print(f"Downloading {url}")
        with urllib.request.urlopen(url) as response:
            data = response.read()
        with open(path, "wb") as f:
            f.write(data)
    with open(path, "rb") as f:
        data = f.read()
    digest = hashlib.sha256(data).hexdigest()
    if sha256 and digest != sha256:
        raise SystemExit(f"{path}: SHA-256 {digest} does not match the pinned {sha256}; delete it and run again.")
    return data


def texts():
    for text, code in TEXTS.items():
        data = download(UDHR_URL.format(code), os.path.join(".cache", "layoutref", f"udhr_{code}.xml"))
        root = ET.fromstring(data)
        lines = []
        for element in root.iter():
            if element.tag.split("}")[-1] in ("title", "para", "listitem") and element.text:
                line = " ".join("".join(element.itertext()).split())
                if line:
                    lines.append(line)
            if len(lines) == LINES_PER_TEXT:
                break
        path = os.path.join("tests", "reference", "layout", "texts", f"{text}.txt")
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write("\n".join(lines) + "\n")


def docker(image, *args):
    subprocess.run(["docker", "run", "--rm", "-v", f"{os.getcwd()}:/w", image, *args], check=True, env=ENV)


def main():
    texts()
    download(CJK_URL, os.path.join(".cache", "fonts", "NotoSansCJKsc-Regular.otf"), CJK_SHA256)
    docker(FONTREF_IMAGE, "python", "/w/tools/layoutref/derive.py", "/w")
    subprocess.run(["docker", "build", "-q", "-t", IMAGE, os.path.join("tools", "layoutref")], check=True)
    docker(IMAGE, "python", "/w/tools/layoutref/layout.py", "/w")
    if "--check" in sys.argv:
        again = os.path.join(".cache", "layoutref", "again")
        shutil.rmtree(again, ignore_errors=True)
        docker(IMAGE, "python", "/w/tools/layoutref/layout.py", "/w", "/w/.cache/layoutref/again")
        for name in sorted(os.listdir(again)):
            with open(os.path.join(again, name), "rb") as a, open(os.path.join("tests", "reference", "layout", name), "rb") as b:
                if a.read() != b.read():
                    raise SystemExit(f"{name}: a second Chromium run gave different output")
        print("A second run gave the same bytes.")


main()
