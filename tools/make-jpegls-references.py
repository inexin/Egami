"""Fetches the JPEG-LS conformance set into .cache/jpegls (never committed).

The ITU-T T.87 conformance images (annex E: test8.ppm, test16.pgm and their components, and the t8*.jls and t16*.jls streams
coded from them) as the CharLS project keeps them in test/conformance (team-charls/charls, BSD-3), at a pinned tag. The
JPEG-LS corpus tests (Egami.Formats.Extended.Tests, JpegLsConformanceTests) decode each stream and compare it with its source
image when EGAMI_CORPUS names jpegls. Only the images are fetched; no CharLS code.

Usage, from the repo root:
  python tools/make-jpegls-references.py
"""
import os
import subprocess
import tempfile
import shutil

TAG = "2.4.5"
FILES = [
    "t16e0.jls", "t16e3.jls", "t8c0e0.jls", "t8c0e3.jls", "t8c1e0.jls", "t8c1e3.jls", "t8c2e0.jls", "t8c2e3.jls",
    "t8nde0.jls", "t8nde3.jls", "t8sse0.jls", "t8sse3.jls", "test16.pgm", "test8.ppm", "test8b.pgm", "test8bs2.pgm",
    "test8g.pgm", "test8gr4.pgm", "test8r.pgm", "README.md",
]


def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    target = os.path.join(root, ".cache", "jpegls", "conformance")
    os.makedirs(target, exist_ok=True)
    with tempfile.TemporaryDirectory() as work:
        clone = os.path.join(work, "charls")
        subprocess.check_call(["git", "clone", "-q", "--depth", "1", "--branch", TAG, "--filter=blob:none", "--sparse",
                               "https://github.com/team-charls/charls.git", clone])
        subprocess.check_call(["git", "-C", clone, "sparse-checkout", "set", "test/conformance"])
        for name in FILES:
            shutil.copyfile(os.path.join(clone, "test", "conformance", name), os.path.join(target, name))
    print(f"{len(FILES)} files in {target}")


if __name__ == "__main__":
    main()
