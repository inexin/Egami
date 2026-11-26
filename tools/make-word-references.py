"""Builds the word boundary references of 2.8.5 (task 7): Chromium 129's Intl.Segmenter (word granularity) over the Thai,
Lao, Khmer and Myanmar UDHR texts in tests/reference/shaping/texts, written to tests/reference/layout/words.json.

Builds the egami-layoutref image (tools/layoutref/Dockerfile, Chromium 129 through Playwright 1.47.0, pinned by digest) as
tools/make-layout-references.py does and runs tools/layoutref/words.py in it.

Usage, from the repo root with Docker running:
  python tools/make-word-references.py
"""
import os
import subprocess

IMAGE = "egami-layoutref:2.8.2"
ENV = {**os.environ, "MSYS_NO_PATHCONV": "1"}


def main():
    subprocess.run(["docker", "build", "-q", "-t", IMAGE, os.path.join("tools", "layoutref")], check=True)
    subprocess.run(["docker", "run", "--rm", "-v", f"{os.getcwd()}:/w", IMAGE, "python", "/w/tools/layoutref/words.py", "/w"],
                   check=True, env=ENV)


main()
