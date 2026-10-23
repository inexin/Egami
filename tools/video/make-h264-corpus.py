"""Makes the H.264 test corpus for V7: small clips encoded by FFmpeg with x264 from synthetic lavfi sources, and the frames
FFmpeg's own H.264 decoder gives for them. FFmpeg and x264 run only as black-box tools; their source is never read.

Inputs and outputs:
  tests/video/corpus/h264/<clip>.h264        raw Annex B streams, committed, covering CAVLC and CABAC, I, P and B slices
                                             (spatial and temporal direct), several slices, several reference frames, explicit and
                                             implicit weighted prediction, the 8x8 transform with and without scaling matrices,
                                             High 10, High 4:2:2 at 8 and 10 bits, monochrome, the Intra profiles and a cropped
                                             size.
  tests/video/corpus/h264/<clip>.mp4         some of the same streams in MP4 (avcC, length-prefixed samples).
  tests/video/corpus/h264/<clip>.frames.json the stream's size and bit depth, and per frame in output order the MD5 of FFmpeg's
                                             decode: the planes at the cropped size one after the other, one byte per sample at 8
                                             bits and two (little-endian) above.

Usage, from the repo root:
  docker build -t egami-video-tools tools/video/docker
  python tools/video/make-h264-corpus.py            every clip and its frames
  python tools/video/make-h264-corpus.py name ...   only those clips
  python tools/video/make-h264-corpus.py --local    run the ffmpeg on the PATH instead of Docker
"""
import json
import os
import subprocess
import sys

IMAGE = "egami-video-tools"
OUT = "tests/video/corpus/h264"
TESTSRC = "-f lavfi -i testsrc2=size={size}:rate=25,format={fmt} -frames:v {frames}"
MANDEL = "-f lavfi -i mandelbrot=size={size}:rate=25,format={fmt} -frames:v {frames}"

# name: (source, pixel format, x264 settings, size, frames). Every clip is coded by libx264 into a raw Annex B stream.
CLIPS = {
    "cavlc-intra": (TESTSRC, "yuv420p", "-profile:v baseline -x264-params keyint=1", "176x144", 3),
    "cavlc-p": (TESTSRC, "yuv420p", "-profile:v baseline -x264-params keyint=30:ref=3", "176x144", 12),
    "cavlc-slices": (TESTSRC, "yuv420p", "-profile:v baseline -x264-params keyint=8:slices=4:ref=2", "176x144", 10),
    "cavlc-b": (MANDEL, "yuv420p", "-profile:v main -x264-params cabac=0:bframes=3:b-pyramid=normal:ref=3:weightp=0", "176x144", 14),
    "cabac-p": (TESTSRC, "yuv420p", "-profile:v main -x264-params bframes=0:ref=4:weightp=0", "176x144", 12),
    "cabac-b-spatial": (MANDEL, "yuv420p", "-profile:v main -x264-params bframes=3:b-pyramid=normal:direct=spatial:ref=3:weightp=0:weightb=0", "176x144", 16),
    "cabac-b-temporal": (MANDEL, "yuv420p", "-profile:v main -x264-params bframes=3:b-pyramid=none:direct=temporal:ref=3:weightp=0:weightb=0", "176x144", 16),
    "cabac-weighted": (MANDEL, "yuv420p", "-profile:v main -x264-params bframes=2:weightp=2:weightb=1:ref=3", "176x144", 14),
    "cabac-slices": (TESTSRC, "yuv420p", "-profile:v main -x264-params slices=3:bframes=2:ref=2", "176x144", 10),
    "high-8x8": (MANDEL, "yuv420p", "-profile:v high -x264-params bframes=2:8x8dct=1:ref=2", "176x144", 12),
    "high-8x8-cavlc": (MANDEL, "yuv420p", "-profile:v high -x264-params cabac=0:bframes=2:8x8dct=1:ref=2", "176x144", 12),
    "high-cqm": (MANDEL, "yuv420p", "-profile:v high -x264-params bframes=2:8x8dct=1:cqm=jvt", "176x144", 10),
    "high-crop": (TESTSRC, "yuv420p", "-profile:v high -x264-params bframes=2", "170x100", 10),
    "high10": (MANDEL, "yuv420p10le", "-profile:v high10 -x264-params bframes=2:ref=2", "176x144", 10),
    "high10-intra": (MANDEL, "yuv420p10le", "-profile:v high10 -x264-params keyint=1", "176x144", 3),
    "high422": (MANDEL, "yuv422p", "-profile:v high422 -x264-params bframes=2", "176x144", 10),
    "high422-cavlc": (MANDEL, "yuv422p", "-profile:v high422 -x264-params cabac=0:bframes=2", "176x144", 10),
    "high422-10": (MANDEL, "yuv422p10le", "-profile:v high422 -x264-params bframes=2", "176x144", 10),
    "gray": (MANDEL, "gray", "-profile:v high -x264-params bframes=2", "176x144", 8),
    "open-gop": (MANDEL, "yuv420p", "-profile:v high -x264-params keyint=8:min-keyint=8:open-gop=1:bframes=3:ref=3", "176x144", 20),
    "intra-refresh": (TESTSRC, "yuv420p", "-profile:v main -x264-params keyint=10:intra-refresh=1:bframes=0:ref=1", "176x144", 16),
    "constrained-intra": (TESTSRC, "yuv420p", "-profile:v main -x264-params constrained-intra=1:bframes=2", "176x144", 10),
    "many-refs": (MANDEL, "yuv420p", "-profile:v high -x264-params ref=16:bframes=3:b-pyramid=normal:partitions=all:me=umh", "176x144", 30),
    "slice-max": (TESTSRC, "yuv420p", "-profile:v high -x264-params slice-max-size=300:bframes=2", "176x144", 8),
    "weighted-cavlc": (MANDEL, "yuv420p", "-profile:v high -x264-params cabac=0:weightp=2:bframes=2:weightb=1:ref=3", "176x144", 12),
    "deblock-offsets": (TESTSRC, "yuv420p", "-profile:v high -x264-params deblock=-3,3:chroma-qp-offset=-5:bframes=2", "176x144", 8),
    "qp-low": (MANDEL, "yuv420p", "-profile:v high -x264-params qp=1:bframes=2", "176x144", 4),
    "qp-high": (TESTSRC, "yuv420p", "-profile:v high -x264-params qp=51:bframes=2", "176x144", 8),
    "tiny-motion": (TESTSRC, "yuv420p", "-profile:v high -x264-params bframes=3:ref=4:me=esa:merange=32:partitions=all", "48x32", 16),
    "high10-qp-low": (MANDEL, "yuv420p10le", "-profile:v high10 -x264-params qp=1:bframes=2", "176x144", 4),
    "high422-10-intra": (MANDEL, "yuv422p10le", "-profile:v high422 -x264-params keyint=1", "176x144", 3),
}

# Clips also written into MP4, so the avcC path is checked.
MP4 = ["cabac-b-spatial", "high10"]


def run(command, local):
    if local:
        subprocess.run(["sh", "-c", f"cd {OUT} && {command}"], check=True)
    else:
        subprocess.run(["docker", "run", "--rm", "-v", f"{os.path.abspath(OUT)}:/work", IMAGE, "sh", "-c", command], check=True)


def output(command, local):
    if local:
        return subprocess.run(["sh", "-c", f"cd {OUT} && {command}"], check=True, capture_output=True, text=True).stdout
    return subprocess.run(["docker", "run", "--rm", "-v", f"{os.path.abspath(OUT)}:/work", IMAGE, "sh", "-c", command],
                          check=True, capture_output=True, text=True).stdout


def make(name, local):
    source, fmt, settings, size, frames = CLIPS[name]
    src = source.format(size=size, fmt=fmt, frames=frames)
    run(f"ffmpeg -y -v error -nostdin {src} -c:v libx264 -pix_fmt {fmt} {settings} -bsf:v filter_units=remove_types=6 {name}.h264", local)
    if name in MP4:
        run(f"ffmpeg -y -v error -nostdin -r 25 -i {name}.h264 -c copy {name}.mp4", local)
    probe = json.loads(output(f"ffprobe -v error -select_streams v:0 -show_entries stream=width,height,pix_fmt -of json {name}.h264", local))
    stream = probe["streams"][0]
    md5 = output(f"ffmpeg -v error -nostdin -i {name}.h264 -f framemd5 -", local)
    hashes = [line.split(",")[-1].strip() for line in md5.splitlines() if line and not line.startswith("#")]
    depth = 10 if "10" in stream["pix_fmt"] else 8
    with open(os.path.join(OUT, name + ".frames.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump({"width": stream["width"], "height": stream["height"], "pixelFormat": stream["pix_fmt"], "bitDepth": depth, "md5": hashes}, f, indent=1)
        f.write("\n")
    print(f"{name}: {os.path.getsize(os.path.join(OUT, name + '.h264'))} bytes, {len(hashes)} frames")


def main():
    os.makedirs(OUT, exist_ok=True)
    local = "--local" in sys.argv
    only = [a for a in sys.argv[1:] if not a.startswith("--")]
    for name in CLIPS:
        if not only or name in only:
            make(name, local)


if __name__ == "__main__":
    main()
