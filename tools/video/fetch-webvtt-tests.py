"""Fetches the web-platform-tests WebVTT parsing suite (BSD-3-Clause) into .cache/video/wpt, for WebVttConformanceTests.

Only webvtt/parsing is checked out (a sparse, shallow clone), at the commit below so results do not move under us. The tests
are read in place and never committed. The file parsing tests need Node.js on the PATH to run their assertions.

Usage: python tools/video/fetch-webvtt-tests.py [--commit <sha>]   (from the repo root)
"""
import os
import subprocess
import sys

COMMIT = "fc50deede0c32c6ffaabc3686eb3c3c699e5f1f5"
REPO = "https://github.com/web-platform-tests/wpt"
TARGET = os.path.join(".cache", "video", "wpt")


def git(*args, cwd=TARGET):
    subprocess.run(["git", *args], cwd=cwd, check=True)


def main():
    commit = sys.argv[sys.argv.index("--commit") + 1] if "--commit" in sys.argv else COMMIT
    if not os.path.isdir(os.path.join(TARGET, ".git")):
        os.makedirs(os.path.dirname(TARGET), exist_ok=True)
        git("clone", "--filter=blob:none", "--no-checkout", "--depth", "1", REPO, TARGET, cwd=".")
        git("sparse-checkout", "set", "webvtt/parsing")
    git("fetch", "--depth", "1", "origin", commit)
    git("checkout", "--quiet", commit)
    count = sum(len(files) for _, _, files in os.walk(os.path.join(TARGET, "webvtt", "parsing")))
    print(f"web-platform-tests {commit[:12]}: {count} files in {TARGET}/webvtt/parsing")


if __name__ == "__main__":
    main()
