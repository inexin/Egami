"""Builds the JBIG2 corpus in tests/images/jbig2 and its references in tests/reference/jbig2, in Docker.

The image tools/codecref/Dockerfile (egami-codecref:2.3.4) pins jbig2enc 0.30, jbig2dec 0.20 and PDFium through pypdfium2;
tools/codecref/jbig2corpus.py draws the sources, encodes them with jbig2enc in every mode it has and decodes each page with
PDFium and jbig2dec (see that script for the list). jbig2dec is AGPL-3.0 and only runs as a tool in the container.

Local corpus (never committed): `--local` also fetches third-party streams into .cache/jbig2-local and writes their
references to .cache/jbig2-local/ref: PDFBox's JBIG2 test streams (https://github.com/apache/pdfbox-jbig2, under
src/test/resources) and the T.88 Annex H datastream, taken from the Recommendation's text when .cache/standards/t88.txt
exists (the T.88 attachment, with the ITU conformance streams Jbig2ConformanceTests reads from .cache/standards/t88conf).

Usage, from the repo root with Docker running:
  python tools/make-jbig2-references.py [--local]
"""
import os
import re
import subprocess
import sys
import urllib.request

IMAGE = "egami-codecref:2.3.4"
PDFBOX = "https://raw.githubusercontent.com/apache/pdfbox-jbig2/master/src/test/resources/"
PDFBOX_FILES = [f"images/{n}.jb2" for n in ("002", "003", "005", "006")] + \
    [f"images/2012311000{n}.jb2" for n in range(1, 10)] + ["images/20123110010.jb2", "org/apache/pdfbox/jbig2/github/21.jb2"]


def docker(*args):
    root = os.getcwd()
    subprocess.run(["docker", "run", "--rm", "-v", f"{root}:/w", IMAGE, *args], check=True, env={**os.environ, "MSYS_NO_PATHCONV": "1"})


def annex_h(text_path, target):
    """Writes the T.88 H.1 datastream, printed as offset: bytes lines in the Recommendation, to <target>."""
    lines = open(text_path, encoding="utf-8").read().split("\n")
    start = next(i for i, line in enumerate(lines) if "The datastream is:" in line)
    out = bytearray()
    for line in lines[start + 1:]:
        if "The datastream is decoded as follows" in line:
            break
        m = re.match(r"\s*([0-9A-F]{4}): ((?:[0-9A-F]{2} ?)+)\s*$", line)
        if m:
            if int(m.group(1), 16) != len(out):
                raise SystemExit("The Annex H listing is out of order.")
            out += bytes.fromhex(m.group(2))
    with open(target, "wb") as f:
        f.write(out)


def main():
    subprocess.run(["docker", "build", "-t", IMAGE, "tools/codecref"], check=True)
    docker("python3", "/w/tools/codecref/jbig2corpus.py")
    if "--local" in sys.argv:
        local = ".cache/jbig2-local"
        os.makedirs(local, exist_ok=True)
        for path in PDFBOX_FILES:
            name = "pdfbox-" + path.split("/")[-1].replace(".jb2", "") + ".jb2"
            if path.endswith("21.jb2"):
                name = "pdfbox-github-21.jb2"
            if not os.path.exists(f"{local}/{name}"):
                urllib.request.urlretrieve(PDFBOX + path, f"{local}/{name}")
        if os.path.exists(".cache/standards/t88.txt"):
            annex_h(".cache/standards/t88.txt", f"{local}/annex-h.jb2")
        docker("python3", "/w/tools/codecref/jbig2corpus.py", "--local", "/w/" + local)


main()
