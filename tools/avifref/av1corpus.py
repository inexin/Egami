"""Writes AV1 clips and per-frame references for Phase 2.9.5, inside the egami-avifref image (tools/make-av1-references.py).

"sequences" writes avifdec's frames of the corpus's AVIF sequences (tests/reference/avif/sequences.json). "clips" encodes our own short clips with aomenc 3.15.2 into /w/tests/video/corpus/av1, one per inter tool or format: a
synthetic texture under a slow zoom and rotation with a second texture sliding across it, so global motion, warps, OBMC and
compound prediction all have something to do. Every other argument names streams whose references are made again.

For each stream under /w/.cache/avif/aom-test-data (libaom's av1-1-b8-* and av1-1-b10-* test vectors) and /w/tests/video/corpus/av1
(our own clips), libaom 3.15.2 decodes it through av1frames.c, once as is and once with film grain off, and the frames' MD5s go to
/w/.cache/avif/reference/av1/<stream>.json: {"frames": [[width, height, bit depth, monochrome, md5, md5 without grain], ...]}. The
MD5 is over the planes Y, U and V cropped to the frame, as little-endian 16-bit samples. libaom's own .md5 file (one MD5 per
frame) is checked against the frames, so the tool reads the stream as aomdec does.
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
    # libaom's .md5 files hash 8-bit frames as bytes and deeper ones as 16-bit little-endian samples, a monochrome frame with
    # its neutral grey 4:2:0 chroma planes.
    w, h, bd, mono, _, raw = frame
    if mono:
        grey = (1 << (bd - 1)).to_bytes(2, "little") * (2 * ((w + 1) >> 1) * ((h + 1) >> 1))
        raw = raw + grey
    return hashlib.md5(raw[0::2] if bd == 8 else raw).hexdigest()


# name: (width, height, frames, bit depth, chroma, extra aomenc arguments)
CLIPS = {
    "tools-8bit": (96, 64, 12, 8, "420", []),
    "odd-size": (99, 67, 10, 8, "420", []),
    "tools-10bit": (96, 64, 10, 10, "420", []),
    "tools-12bit": (96, 64, 8, 12, "420", ["--profile=2"]),
    "yuv444": (80, 48, 8, 8, "444", ["--profile=1"]),
    "yuv422": (80, 48, 8, 10, "422", ["--profile=2"]),
    "monochrome": (80, 48, 8, 8, "400", ["--monochrome"]),
    "compound-masks": (96, 64, 12, 8, "420", ["--enable-masked-comp=1", "--enable-interinter-wedge=1", "--enable-diff-wtd-comp=1",
                                               "--enable-dist-wtd-comp=1", "--enable-onesided-comp=1"]),
    "interintra": (96, 64, 12, 8, "420", ["--enable-interintra-comp=1", "--enable-smooth-interintra=1", "--enable-interintra-wedge=1"]),
    "warp-global": (128, 96, 10, 8, "420", ["--enable-warped-motion=1", "--enable-global-motion=1", "--enable-obmc=1"]),
    "no-dual-filter": (96, 64, 8, 8, "420", ["--enable-dual-filter=0"]),
    "resize": (128, 96, 16, 8, "420", ["--resize-mode=2"]),
    "superres": (128, 96, 12, 8, "420", ["--superres-mode=2"]),
    "switch-frames": (96, 64, 12, 8, "420", ["--sframe-dist=4", "--sframe-mode=1"]),
    "error-resilient": (96, 64, 10, 8, "420", ["--error-resilient=1"]),
    "tiles": (256, 128, 6, 8, "420", ["--tile-columns=2", "--tile-rows=1"]),
    "screen": (96, 64, 8, 8, "420", ["--tune-content=screen"]),
    "lossless": (64, 48, 6, 8, "420", ["--lossless=1"]),
    "no-cdf-update": (96, 64, 8, 8, "420", ["--cdf-update-mode=0"]),
    "forward-keyframes": (96, 64, 24, 8, "420", ["--enable-fwd-kf=1", "--kf-max-dist=12", "--lag-in-frames=16"]),
    "segmentation": (128, 96, 12, 8, "420", ["--aq-mode=1"]),
    "segmentation-cyclic": (128, 96, 16, 8, "420", ["--aq-mode=3", "--rt", "--lag-in-frames=0"]),
}


def texture(w, h, seed):
    import numpy as np
    rng = np.random.default_rng(seed)
    y, x = np.mgrid[0:h, 0:w].astype(np.float64)
    t = np.zeros((h, w))
    for _ in range(6):
        fx, fy, ph = rng.uniform(0.02, 0.25), rng.uniform(0.02, 0.25), rng.uniform(0, 6.3)
        t += np.sin(x * fx + y * fy + ph)
    return (t - t.min()) / (t.max() - t.min() + 1e-9)


def source(name, w, h, frames, depth, chroma):
    import numpy as np
    big = 512
    base = [texture(big, big, s) for s in (1, 2, 3)]
    obj = [texture(64, 64, s) for s in (4, 5, 6)]
    maxv = (1 << depth) - 1
    tag = {"420": "420", "422": "422", "444": "444", "400": "mono"}[chroma]
    if depth > 8:
        tag += f"p{depth}"
    path = f"/tmp/{name}.y4m"
    with open(path, "wb") as f:
        f.write(f"YUV4MPEG2 W{w} H{h} F30:1 Ip A1:1 C{tag}\n".encode())
        for n in range(frames):
            angle, zoom = 0.01 * n, 1 + 0.01 * n
            yy, xx = np.mgrid[0:h, 0:w].astype(np.float64)
            cx, cy = xx - w / 2, yy - h / 2
            sx = (np.cos(angle) * cx - np.sin(angle) * cy) / zoom + big / 2 + 2 * n
            sy = (np.sin(angle) * cx + np.cos(angle) * cy) / zoom + big / 2 + n
            planes = []
            for p in range(3):
                img = base[p][np.clip(sy.astype(int), 0, big - 1), np.clip(sx.astype(int), 0, big - 1)]
                ox, oy = (5 * n) % max(1, w - 16), (3 * n) % max(1, h - 16)
                ow, oh = min(32, w - ox), min(32, h - oy)
                img[oy:oy + oh, ox:ox + ow] = obj[p][:oh, :ow]
                planes.append(np.round(img * maxv).astype(np.uint16))
            sub = {"420": (2, 2), "422": (2, 1), "444": (1, 1), "400": None}[chroma]
            f.write(b"FRAME\n")
            out = [planes[0]]
            if sub:
                out += [pl[::sub[1], ::sub[0]] for pl in planes[1:]]
            for pl in out:
                f.write(pl.astype("<u2" if depth > 8 else np.uint8).tobytes())
    return path


def clips():
    out = "/w/tests/video/corpus/av1"
    os.makedirs(out, exist_ok=True)
    for name, (w, h, frames, depth, chroma, extra) in CLIPS.items():
        src = source(name, w, h, frames, depth, chroma)
        args = ["aomenc", "--quiet", "--ivf", f"--limit={frames}", "--cpu-used=4", "--end-usage=q", "--cq-level=40",
                f"--bit-depth={depth}", f"--input-bit-depth={depth}", "-o", f"{out}/{name}.ivf", *extra]
        if chroma == "444":
            args.append("--i444")
        elif chroma == "422":
            args.append("--i422")
        subprocess.run([*args, src], check=True)
        print(f"{name}.ivf: {os.path.getsize(f'{out}/{name}.ivf')} bytes")


def sequences():
    """avifdec 1.4.2's every frame (--index all, as y4m) of the AVIF sequences in the corpus: /w/tests/reference/avif/sequences.json
    gives each file's y4m colorspace and each frame's MD5 over its y4m payload (the planes, 16-bit little-endian above 8 bits)."""
    import glob
    out = {}
    for path in sorted(glob.glob("/w/tests/images/avif/*sequence*.avif") + glob.glob("/w/tests/images/avif/libavif/colors-animated-*.avif")):
        name = os.path.relpath(path, "/w/tests/images/avif").replace(os.sep, "/")
        work = "/tmp/seq"
        subprocess.run(["rm", "-rf", work], check=True)
        os.makedirs(work)
        subprocess.run(["avifdec", "--index", "all", path, f"{work}/f.y4m"], check=True, capture_output=True)
        frames, colorspace = [], None
        for frame in sorted(glob.glob(f"{work}/f-*.y4m")):
            data = open(frame, "rb").read()
            head, _, rest = data.partition(b"\n")
            colorspace = next((t[1:].decode() for t in head.split() if t.startswith(b"C")), "420jpeg")
            payload = rest.partition(b"\n")[2]
            frames.append(hashlib.md5(payload).hexdigest())
        out[name] = {"colorspace": colorspace, "frames": frames}
        print(f"{name}: {len(frames)} frames, {colorspace}")
    with open("/w/tests/reference/avif/sequences.json", "w") as f:
        json.dump(out, f, indent=1)


def main():
    subprocess.run(["gcc", "-O2", "-o", "/tmp/av1frames", "/t/av1frames.c", "-laom"], check=True)
    if sys.argv[1:] == ["clips"]:
        clips()
        return

    if sys.argv[1:] == ["sequences"]:
        sequences()
        return
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
