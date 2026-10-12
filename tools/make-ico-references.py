"""Builds the ICO and CUR corpus in tests/images/ico and its references in tests/reference/ico, in Docker.

The image egami-iconref:2.3.3 (tools/iconref/Dockerfile: ImageMagick 7.1.2, Pillow 12.3) is built if needed, then
tools/iconref/icocorpus.py writes the files; its docstring lists them. Licenses: made from ops/duck.png and ops/egret.png, public
domain as they are (tests/images/LICENSES).

Usage, from the repo root with Docker running:
  python tools/make-ico-references.py
"""
import os
import subprocess

IMAGE = "egami-iconref:2.3.3"


def main():
    root = os.getcwd()
    subprocess.run(["docker", "build", "-q", "-t", IMAGE, "tools/iconref"], check=True)
    subprocess.run(["docker", "run", "--rm", "-v", f"{root}:/w", IMAGE, "python3", "/w/tools/iconref/icocorpus.py"],
                   check=True, env={**os.environ, "MSYS_NO_PATHCONV": "1"})


if __name__ == "__main__":
    main()
