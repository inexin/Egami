"""Builds the VP8 video references for Phase 2.3.5 with libvpx 1.14.1 in Docker (tools/vp8ref/Dockerfile).

Inputs:
  .cache/vp8/vectors/vp80-*.ivf   the libvpx VP8 test vectors (vp80-00 to vp80-06), fetched here from the WebM project's test data
                                  bucket. Their license is unconfirmed, so they and everything made from them stay in .cache.
  tests/images/vp8/*.ivf          clips made here by vpxenc (and FFmpeg for the raw input, run only as a tool) from the public
                                  domain duck and egret photos and FFmpeg's testsrc2 pattern; small, committed. They cover the four
                                  versions, one to eight token partitions, golden and hidden alternate frames, error resilient
                                  coding (no probability carry-over), segmentation, sharpness, many key frames and odd sizes.

References, per file (tests/reference/vp8 for the clips, .cache/vp8/reference for the vectors):
  <name>.json      the frame size, which packets are shown, and the MD5 of each shown frame as vpxdec writes it (packed I420 at
                   the display size, chroma (w + 1) / 2 by (h + 1) / 2)
  <name>.dump.gz   vp8dump's reading of every packet: header fields under RFC 6386 names, the entropy state the next frame starts
                   from, and each macroblock's modes, reference, segment, skip flag and motion vectors

Usage, from the repo root with Docker running:
  docker build -t egami-vp8ref:2.3.5 tools/vp8ref
  python tools/make-vp8-references.py              vectors (fetched once) and clips
  python tools/make-vp8-references.py --vectors    vectors only, leaving the committed clips alone
  python tools/make-vp8-references.py --references the references of the committed clips again, without encoding them anew
  python tools/make-vp8-references.py --large      720p and 1080p clips for the speed gate in .cache/vp8/large, with their
                                                   references and vpxdec's decode times (timing.json)
"""
import gzip
import hashlib
import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.request

IMAGE = "egami-vp8ref:2.3.5"
BUCKET = "https://storage.googleapis.com/downloads.webmproject.org/test_data/libvpx/"
VECTORS = ".cache/vp8/vectors"
VECTOR_REFS = ".cache/vp8/reference"
CLIPS = "tests/images/vp8"
CLIP_REFS = "tests/reference/vp8"
WORK = "/tmp/vp8work"

# The test vector names libvpx's own tests decode (test/test_vectors.cc).
VECTOR_NAMES = (
    [f"vp80-00-comprehensive-{i:03}.ivf" for i in range(1, 19)]
    + [f"vp80-01-intra-{n}.ivf" for n in (1400, 1411, 1416, 1417)]
    + [f"vp80-02-inter-{n}.ivf" for n in (1402, 1412, 1418, 1424)]
    + [f"vp80-03-segmentation-{n}.ivf" for n in ("01", "02", "03", "04")]
    + [f"vp80-03-segmentation-{n}.ivf" for n in (1401, 1403) + tuple(range(1407, 1411)) + (1413, 1414, 1415)
       + tuple(range(1425, 1430)) + tuple(range(1432, 1444))]
    + [f"vp80-04-partitions-{n}.ivf" for n in (1404, 1405, 1406)]
    + [f"vp80-05-sharpness-{n}.ivf" for n in range(1428, 1439)]
    + ["vp80-06-smallsize.ivf"]
)

DUCK = "-loop 1 -i tests/images/ops/duck.png"
EGRET = "-loop 1 -i tests/images/ops/egret.png"
PATTERN = "-f lavfi -i testsrc2=size={size}:rate=30"

# name: (source, ffmpeg filter, frames, vpxenc options). Sources are panned and zoomed so the encoder finds real motion.
PAN = "scale=480:-2,crop={w}:{h}:x='min(iw-{w},n*3)':y='min(ih-{h},n*2)'"
ZOOM = "scale=600:-2,zoompan=z='1+0.01*on':x='iw/2-iw/zoom/2':y='ih/2-ih/zoom/2':d=1:s={w}x{h}"
CLIP_SET = {
    "v0-pan": (DUCK, PAN.format(w=176, h=144), 30, "--good --cpu-used=1 --target-bitrate=300"),
    "v0-zoom-lowrate": (EGRET, ZOOM.format(w=176, h=144), 30, "--good --cpu-used=0 --end-usage=cbr --target-bitrate=60"),
    "v0-altref": (DUCK, PAN.format(w=160, h=96), 40, "--good --passes=2 --auto-alt-ref=1 --lag-in-frames=16 --arnr-maxframes=5 --target-bitrate=200"),
    "v0-keyframes": (PATTERN.format(size="128x96"), "null", 30, "--good --kf-max-dist=7 --kf-min-dist=7 --target-bitrate=250"),
    "v0-resilient": (EGRET, PAN.format(w=144, h=96), 30, "--rt --cpu-used=-6 --error-resilient=1 --target-bitrate=150"),
    "v0-rt-static": (PATTERN.format(size="160x96"), "null", 30, "--rt --cpu-used=-4 --static-thresh=500 --noise-sensitivity=0 --target-bitrate=120"),
    "v0-partitions-2": (DUCK, PAN.format(w=176, h=144), 15, "--good --token-parts=1 --target-bitrate=300"),
    "v0-partitions-4": (DUCK, PAN.format(w=176, h=144), 15, "--good --token-parts=2 --target-bitrate=300"),
    "v0-partitions-8": (DUCK, PAN.format(w=176, h=144), 15, "--good --token-parts=3 --target-bitrate=300"),
    "v0-sharpness-7": (EGRET, PAN.format(w=128, h=96), 20, "--good --sharpness=7 --target-bitrate=150"),
    "v1-pan": (DUCK, PAN.format(w=160, h=96), 25, "--good --profile=1 --target-bitrate=200"),
    "v2-pan": (EGRET, PAN.format(w=160, h=96), 25, "--good --profile=2 --target-bitrate=200"),
    "v3-pan": (DUCK, PAN.format(w=160, h=96), 25, "--good --profile=3 --target-bitrate=200"),
    "v0-odd-97x61": (EGRET, PAN.format(w=98, h=62) + ",scale=97:61", 20, "--good --passes=2 --auto-alt-ref=1 --lag-in-frames=8 --target-bitrate=120"),
    "v0-odd-17x9": (PATTERN.format(size="18x10"), "scale=17:9", 20, "--good --target-bitrate=30"),
    "v1-odd-33x47": (DUCK, PAN.format(w=34, h=48) + ",scale=33:47", 20, "--good --profile=1 --target-bitrate=60"),
}


def run(cmd, **kwargs):
    return subprocess.run(cmd, shell=True, check=True, capture_output=True, **kwargs)


def frame_md5s(raw, sizes):
    """Splits packed I420 frames at their display sizes and hashes each."""
    out = []
    at = 0
    for width, height in sizes:
        size = width * height + 2 * ((width + 1) // 2) * ((height + 1) // 2)
        out.append(hashlib.md5(raw[at:at + size]).hexdigest())
        at += size
    if at != len(raw):
        raise ValueError(f"{len(raw)} bytes of frames, {at} expected")
    return out


def reference(ivf, target, name):
    """Runs vp8dump and vpxdec on one file and writes its JSON and dump; vpxdec's frames must equal vp8dump's."""
    os.makedirs(WORK, exist_ok=True)
    dump = run(f"vp8dump {ivf} {WORK}/dump.yuv").stdout.decode()
    run(f"vpxdec --i420 --rawvideo -o {WORK}/vpxdec.yuv {ivf}")
    with open(f"{WORK}/dump.yuv", "rb") as f:
        frames = f.read()
    with open(f"{WORK}/vpxdec.yuv", "rb") as f:
        if f.read() != frames:
            raise ValueError(f"{name}: vpxdec and vp8dump disagree")

    packets = []
    sizes = []
    for line in dump.splitlines():
        parts = line.split()
        if parts[0] == "frame":
            packets.append({"shown": False, "error": "error" in parts})
        elif parts[0] == "shown":
            packets[-1]["shown"] = True
            sizes.append(tuple(map(int, parts[2].split("x"))))
    entry = {
        "width": sizes[0][0] if sizes else 0,
        "height": sizes[0][1] if sizes else 0,
        "packets": len(packets),
        "shown": [i for i, p in enumerate(packets) if p["shown"]],
        "errors": [i for i, p in enumerate(packets) if p["error"]],
        "md5": frame_md5s(frames, sizes),
    }
    if len(set(sizes)) > 1:
        entry["sizes"] = [f"{w}x{h}" for w, h in sizes]
    with open(os.path.join(target, name + ".json"), "w", newline="\n") as f:
        json.dump(entry, f, indent=1)
        f.write("\n")
    with open(os.path.join(target, name + ".dump.gz"), "wb") as f:
        # No name and a zero time in the header, so regenerating gives identical files.
        with gzip.GzipFile(fileobj=f, mode="wb", filename="", mtime=0) as gz:
            gz.write(dump.encode())
    return entry


def make_clip(name, source, vf, frames, options):
    os.makedirs(WORK, exist_ok=True)
    y4m = f"{WORK}/{name}.y4m"
    run(f"ffmpeg -v error -y {source} -vf \"{vf}\" -frames:v {frames} -pix_fmt yuv420p -f yuv4mpegpipe {y4m}")
    out = os.path.join(CLIPS, name + ".ivf")
    run(f"vpxenc -q --codec=vp8 --ivf {options} --threads=1 -o {out} {y4m}")


def inside(vectors_only, keep_clips):
    os.makedirs(VECTOR_REFS, exist_ok=True)
    versions = run("vpxdec --help 2>&1 | head -1 || true").stdout.decode().strip()
    summary = {}
    for file in sorted(os.listdir(VECTORS)):
        if file.endswith(".ivf"):
            summary[file] = reference(os.path.join(VECTORS, file), VECTOR_REFS, file[:-4])["packets"]
    print(f"vectors: {len(summary)} files, {sum(summary.values())} packets")
    if vectors_only:
        return

    os.makedirs(CLIPS, exist_ok=True)
    os.makedirs(CLIP_REFS, exist_ok=True)
    manifest = {"tools": "libvpx 1.14.1 vpxenc and vpxdec (generic C build), FFmpeg " + run("ffmpeg -version").stdout.decode().split()[2],
                "clips": {}}
    for name, (source, vf, frames, options) in CLIP_SET.items():
        if not keep_clips:
            make_clip(name, source, vf, frames, options)
        entry = reference(os.path.join(CLIPS, name + ".ivf"), CLIP_REFS, name)
        manifest["clips"][name] = {"source": source.split()[-1], "filter": vf, "frames": frames, "vpxenc": options,
                                   "size": f"{entry['width']}x{entry['height']}", "packets": entry["packets"],
                                   "shown": len(entry["shown"])}
        print(f"{name}: {entry['width']}x{entry['height']}, {entry['packets']} packets, {len(entry['shown'])} shown")
    with open(os.path.join(CLIP_REFS, "manifest.json"), "w", newline="\n") as f:
        json.dump(manifest, f, indent=1)
        f.write("\n")


LARGE = ".cache/vp8/large"
LARGE_SET = {
    # 10 seconds at 30 frames a second, panning and zooming over the photos, as a camera clip would; 8 token partitions so libvpx's
    # threads have rows to share.
    "720p": (DUCK, "scale=2400:-2," + ZOOM.format(w=1280, h=720).replace("scale=600:-2,", ""), 300, "--good --cpu-used=4 --target-bitrate=2500 --token-parts=3 --auto-alt-ref=1 --lag-in-frames=16 --passes=2"),
    "1080p": (EGRET, "scale=3200:-2," + ZOOM.format(w=1920, h=1080).replace("scale=600:-2,", ""), 300, "--good --cpu-used=4 --target-bitrate=5000 --token-parts=3 --auto-alt-ref=1 --lag-in-frames=16 --passes=2"),
    "1080p-1part": (EGRET, "scale=3200:-2," + ZOOM.format(w=1920, h=1080).replace("scale=600:-2,", ""), 300, "--good --cpu-used=4 --target-bitrate=5000 --token-parts=0 --passes=2"),
}


def time_vpxdec(ivf, threads):
    """Returns vpxdec's decode time per frame in milliseconds, the best of five runs (SIMD build, frames not written)."""
    best = None
    for _ in range(5):
        out = run(f"vpxdec-fast --noblit --summary -t {threads} {ivf} 2>&1").stdout.decode()
        # "303 decoded frames/300 showed frames in 980413 us (305.99 fps)": the time per packet decoded.
        match = re.search(r"(\d+) decoded frames/\d+ showed frames in (\d+) us", out)
        ms = int(match.group(2)) / 1000 / int(match.group(1))
        best = ms if best is None else min(best, ms)
    return best


def large():
    os.makedirs(LARGE, exist_ok=True)
    timing = {}
    for name, (source, vf, frames, options) in LARGE_SET.items():
        out = os.path.join(LARGE, name + ".ivf")
        if not os.path.exists(out):
            y4m = f"{WORK}/{name}.y4m"
            os.makedirs(WORK, exist_ok=True)
            run(f"ffmpeg -v error -y {source} -vf \"{vf}\" -frames:v {frames} -pix_fmt yuv420p -f yuv4mpegpipe {y4m}")
            run(f"vpxenc -q --codec=vp8 --ivf {options} --threads=8 -o {out} {y4m}")
            os.remove(y4m)
        reference(out, LARGE, name)
        cores = os.cpu_count()
        timing[name] = {"one_thread_ms": time_vpxdec(out, 1), "all_cores_ms": time_vpxdec(out, cores), "cores": cores}
        print(f"{name}: vpxdec {timing[name]['one_thread_ms']:.2f} ms a frame on 1 thread, {timing[name]['all_cores_ms']:.2f} on {cores}")
    with open(os.path.join(LARGE, "timing.json"), "w", newline="\n") as f:
        json.dump(timing, f, indent=1)
        f.write("\n")


def fetch_vectors():
    os.makedirs(VECTORS, exist_ok=True)
    for name in VECTOR_NAMES:
        path = os.path.join(VECTORS, name)
        if os.path.exists(path):
            continue
        try:
            with urllib.request.urlopen(BUCKET + name) as response:
                data = response.read()
        except urllib.error.HTTPError as e:
            print(f"{name}: {e.code}, skipped")
            continue
        with open(path, "wb") as f:
            f.write(data)
        print(f"{name}: {len(data)} bytes")


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "--inside":
        if "--large" in sys.argv:
            large()
        else:
            inside("--vectors" in sys.argv, "--references" in sys.argv)
        return

    fetch_vectors()
    repo = os.getcwd()
    args = [a for a in sys.argv[1:] if a in ("--vectors", "--references", "--large")]
    env = dict(os.environ, MSYS_NO_PATHCONV="1")
    subprocess.run(["docker", "run", "--rm", "-v", f"{repo}:/work", "-w", "/work", IMAGE, "python3", "tools/make-vp8-references.py",
                    "--inside", *args], check=True, env=env)


if __name__ == "__main__":
    main()
