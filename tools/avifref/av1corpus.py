"""Writes per-frame references of AV1 streams for Phase 2.9.5, inside the egami-avifref image (tools/make-av1-references.py).

For each stream under /w/.cache/avif/aom-test-data (libaom's av1-1-b8-* and av1-1-b10-* test vectors) and /w/tests/video/corpus/av1
(our own clips), libaom 3.15.2 decodes it through av1frames.c, once as is and once with film grain off, and the frames' MD5s go to
/w/.cache/avif/reference/av1/<stream>.json: {"frames": [[width, height, bit depth, monochrome, md5, md5 without grain], ...]}. The
MD5 is over the planes Y, U and V cropped to the frame, as little-endian 16-bit samples. libaom's own whole-stream .md5 file is
(one MD5 per frame) is checked against the frames, so the tool reads the stream as aomdec does.
"""
import glob
import hashlib
import json
import os
import struct
import subprocess
import sys

OUT = "/w/.cache/avif/reference/av1"


def hashes(path, *flags):
    data = subprocess.run(["/tmp/av1frames", *flags, path], capture_output=True, check=True).stdout
    at, result = 0, []
    while at < len(data):
        w, h, bd, mono, sx, sy = struct.unpack_from("<6I", data, at)
        at += 24
        size = w * h * 2
        if not mono:
            size += 2 * 2 * ((w + sx) >> sx) * ((h + sy) >> sy)
        result.append([w, h, bd, mono, hashlib.md5(data[at:at + size]).hexdigest(), data[at:at + size]])
        at += size
    return result


def aomdec_md5(frame):
    # libaom's .md5 files hash 8-bit frames as bytes and deeper ones as 16-bit little-endian samples.
    w, h, bd, mono, _, raw = frame
    return hashlib.md5(raw[0::2] if bd == 8 else raw).hexdigest()


def main():
    subprocess.run(["gcc", "-O2", "-o", "/tmp/av1frames", "/t/av1frames.c", "-laom"], check=True)
    os.makedirs(OUT, exist_ok=True)
    streams = sorted(glob.glob("/w/.cache/avif/aom-test-data/av1-1-b*.ivf")) + sorted(glob.glob("/w/tests/video/corpus/av1/*.ivf"))
    only = sys.argv[1:]
    for path in streams:
        name = os.path.basename(path)
        if only and not any(o in name for o in only):
            continue
        out = os.path.join(OUT, name + ".json")
        if os.path.isfile(out) and not only:
            continue
        grain = hashes(path)
        plain = hashes(path, "--no-grain")
        expected = path + ".md5"
        if os.path.isfile(expected):
            want = [line.split()[0] for line in open(expected) if line.strip()]
            got = [aomdec_md5(frame) for frame in grain]
            if want != got:
                print(f"{name}: frame MD5s differ from libaom's .md5 file", file=sys.stderr)
        rows = [[g[0], g[1], g[2], g[3], g[4], p[4]] for g, p in zip(grain, plain)]
        with open(out, "w") as f:
            json.dump({"frames": rows}, f)
        print(f"{name}: {len(rows)} frames")


main()
