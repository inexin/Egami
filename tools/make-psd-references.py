"""Builds the PSD and PSB corpus in tests/images/psd and its references in tests/reference/psd, in Docker.

The image egami-iconref:2.3.10 (tools/iconref/Dockerfile: ImageMagick 7.1.2, Pillow 13.0, exiftool 13.55, psd-tools 1.10.9) is
built if needed, then tools/iconref/psdcorpus.py writes the files; its docstring lists them. Licenses: made from ops/duck.png,
public domain as it is (tests/images/LICENSES).

Usage, from the repo root with Docker running:
  python tools/make-psd-references.py
"""
import os
import subprocess

IMAGE = "egami-iconref:2.3.10"


def main():
    root = os.getcwd()
    subprocess.run(["docker", "build", "-q", "-t", IMAGE, "tools/iconref"], check=True)
    subprocess.run(["docker", "run", "--rm", "-v", f"{root}:/w", IMAGE, "python3", "/w/tools/iconref/psdcorpus.py"],
                   check=True, env={**os.environ, "MSYS_NO_PATHCONV": "1"})


if __name__ == "__main__":
    main()
