"""Makes the HEVC test corpus for V8: small clips encoded by FFmpeg with x265 from synthetic lavfi sources, and the frames
FFmpeg's own HEVC decoder gives for them. FFmpeg and x265 run only as black-box tools; their source is never read.

Inputs and outputs:
  tests/video/corpus/hevc/<clip>.h265        raw Annex B streams, committed, covering intra, P and B pictures with pyramids,
                                             Main, Main 10, Main 12, 4:2:2, 4:4:4 and monochrome, tiles, wavefronts, several
                                             slices, open GOPs (CRA with leading pictures), weighted prediction, AMP, lossless
                                             coding, a cropped size, and HDR10 mastering display and content light level SEI.
                                             Every picture carries x265's MD5 decoded picture hash SEI.
  tests/video/corpus/hevc/<clip>.mp4         some of the same streams in MP4 (hvcC, length-prefixed samples) or Matroska (.mkv).
  tests/video/corpus/hevc/<clip>.frames.json the stream's size and bit depth, and per frame in output order the MD5 of FFmpeg's
                                             decode: the planes at the cropped size one after the other, one byte per sample at 8
                                             bits and two (little-endian) above. For the clips in HM, whose chroma FFmpeg (5.1 and
                                             8.1) decodes against the stream's own picture hashes, the frames are the reference
                                             decoder HM's instead (the egami-heicref image of tools/heicref).

Usage, from the repo root:
  docker build -t egami-video-tools tools/video/docker
  docker build -t egami-heicref:2.9.4 tools/heicref     (for the clips in HM)
  python tools/video/make-hevc-corpus.py            every clip and its frames
  python tools/video/make-hevc-corpus.py name ...   only those clips
  python tools/video/make-hevc-corpus.py --local    run the ffmpeg on the PATH instead of Docker
"""
import hashlib
import json
import os
import subprocess
import sys

IMAGE = "egami-video-tools"
OUT = "tests/video/corpus/hevc"
TESTSRC = "-f lavfi -i testsrc2=size={size}:rate=25,format={fmt} -frames:v {frames}"
MANDEL = "-f lavfi -i mandelbrot=size={size}:rate=25,format={fmt} -frames:v {frames}"
FADE = "-f lavfi -i testsrc2=size={size}:rate=25,fade=in:0:8,format={fmt} -frames:v {frames}"

# x265's own logging and the info SEI with its version string are left out, so a new x265 build gives the same streams.
COMMON = "log-level=error:info=0:hash=1"

# name: (source, pixel format, x265 profile and settings, size, frames). Every clip is coded by libx265 into a raw Annex B stream.
CLIPS = {
    "intra": (TESTSRC, "yuv420p", f"-profile:v main -x265-params {COMMON}:keyint=1", "176x144", 3),
    "p": (TESTSRC, "yuv420p", f"-profile:v main -x265-params {COMMON}:bframes=0:ref=3", "176x144", 12),
    "b": (MANDEL, "yuv420p", f"-profile:v main -x265-params {COMMON}:bframes=4:b-pyramid=1:ref=4", "176x144", 17),
    "b-adapt": (MANDEL, "yuv420p", f"-profile:v main -x265-params {COMMON}:bframes=8:b-adapt=2:rc-lookahead=12:ref=5", "176x144", 20),
    "open-gop": (MANDEL, "yuv420p", f"-profile:v main -x265-params {COMMON}:keyint=8:min-keyint=8:open-gop=1:bframes=3:ref=3", "176x144", 20),
    "weighted": (FADE, "yuv420p", f"-profile:v main -x265-params {COMMON}:weightp=1:weightb=1:bframes=3", "176x144", 14),
    "amp": (MANDEL, "yuv420p", f"-profile:v main -x265-params {COMMON}:amp=1:rect=1:bframes=2:me=star:subme=7", "176x144", 10),
    "ctu16": (TESTSRC, "yuv420p", f"-profile:v main -x265-params {COMMON}:ctu=16:bframes=2:wpp=0:pmode=0", "352x288", 6),
    "wpp": (MANDEL, "yuv420p", f"-profile:v main -x265-params {COMMON}:ctu=16:wpp=1:bframes=2", "352x288", 6),
    "slices": (TESTSRC, "yuv420p", f"-profile:v main -x265-params {COMMON}:slices=4:bframes=2", "352x288", 6),
    "no-sao": (MANDEL, "yuv420p", f"-profile:v main -x265-params {COMMON}:sao=0:deblock=-2,2:bframes=2", "176x144", 8),
    "tmvp-off": (MANDEL, "yuv420p", f"-profile:v main -x265-params {COMMON}:temporal-mvp=0:bframes=3", "176x144", 10),
    "lossless": (TESTSRC, "yuv420p", f"-profile:v main -x265-params {COMMON}:lossless=1:bframes=2", "96x64", 4),
    "crop": (TESTSRC, "yuv420p", f"-profile:v main -x265-params {COMMON}:bframes=2", "170x100", 8),
    "qp-high": (TESTSRC, "yuv420p", f"-profile:v main -x265-params {COMMON}:qp=51:bframes=2", "176x144", 6),
    "qp-low": (MANDEL, "yuv420p", f"-profile:v main -x265-params {COMMON}:qp=2:bframes=2", "176x144", 4),
    "main10": (MANDEL, "yuv420p10le", f"-profile:v main10 -x265-params {COMMON}:bframes=3:ref=3", "176x144", 10),
    "main12": (MANDEL, "yuv420p12le", f"-profile:v main12 -x265-params {COMMON}:bframes=2", "176x144", 8),
    "422-10": (MANDEL, "yuv422p10le", f"-profile:v main422-10 -x265-params {COMMON}:bframes=2", "176x144", 8),
    "444": (MANDEL, "yuv444p", f"-profile:v main444-8 -x265-params {COMMON}:bframes=2", "176x144", 8),
    "444-12": (MANDEL, "yuv444p12le", f"-profile:v main444-12 -x265-params {COMMON}:bframes=2", "176x144", 6),
    "gray": (MANDEL, "gray", f"-x265-params {COMMON}:bframes=2", "176x144", 6),
    "hdr10": (MANDEL, "yuv420p10le", f"-profile:v main10 -x265-params '{COMMON}:bframes=2:hdr10=1:repeat-headers=1:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1):max-cll=1000,400'", "176x144", 6),
}

# Clips also written into MP4 or Matroska, so the hvcC path is checked.
MP4 = ["b", "main10", "hdr10"]
MKV = ["p"]

# Clips whose frames come from HM: FFmpeg's chroma at some CTB corners does not match the stream's picture hashes, HM's does.
HM = ["ctu16"]
HM_IMAGE = "egami-heicref:2.9.4"


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


def hm_frames(name, stream, depth):
    """The MD5 of each frame HM decodes, in output order: its YUV file cut into frames at the cropped size."""
    subprocess.run(["docker", "run", "--rm", "-v", f"{os.path.abspath(OUT)}:/work", HM_IMAGE, "sh", "-c",
                    f"TAppDecoder -b /work/{name}.h265 -o /work/{name}.hm.yuv -d 0 > /dev/null"], check=True)
    path = os.path.join(OUT, name + ".hm.yuv")
    with open(path, "rb") as f:
        data = f.read()
    os.remove(path)
    width, height, fmt = stream["width"], stream["height"], stream["pix_fmt"]
    chroma = {"420": 2 * ((width + 1) // 2) * ((height + 1) // 2), "422": 2 * ((width + 1) // 2) * height, "444": 2 * width * height}
    size = (width * height + (0 if fmt.startswith("gray") else chroma[fmt[3:6]])) * (1 if depth == 8 else 2)
    return [hashlib.md5(data[i:i + size]).hexdigest() for i in range(0, len(data), size)]


def make(name, local):
    source, fmt, settings, size, frames = CLIPS[name]
    src = source.format(size=size, fmt=fmt, frames=frames)
    run(f"ffmpeg -y -v error -nostdin {src} -c:v libx265 -pix_fmt {fmt} {settings} {name}.h265", local)
    if name in MP4:
        run(f"ffmpeg -y -v error -nostdin -fflags +genpts -r 25 -i {name}.h265 -c copy -tag:v hvc1 {name}.mp4", local)
    if name in MKV:
        run(f"ffmpeg -y -v error -nostdin -fflags +genpts -r 25 -i {name}.h265 -c copy {name}.mkv", local)
    probe = json.loads(output(f"ffprobe -v error -select_streams v:0 -show_entries stream=width,height,pix_fmt -of json {name}.h265", local))
    stream = probe["streams"][0]
    depth = 12 if "12" in stream["pix_fmt"] else 10 if "10" in stream["pix_fmt"] else 8
    check = output(f"ffmpeg -v error -nostdin -err_detect crccheck -i {name}.h265 -f null - 2>&1", local)
    if "mismatching checksum" in check and name not in HM:
        print(f"{name}: FFmpeg's decode does not match the picture hashes; add it to HM")
    if name in HM:
        hashes = hm_frames(name, stream, depth)
    else:
        md5 = output(f"ffmpeg -v error -nostdin -i {name}.h265 -f framemd5 -", local)
        hashes = [line.split(",")[-1].strip() for line in md5.splitlines() if line and not line.startswith("#")]
    result = {"width": stream["width"], "height": stream["height"], "pixelFormat": stream["pix_fmt"], "bitDepth": depth, "md5": hashes}
    with open(os.path.join(OUT, name + ".frames.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(result, f, indent=1)
        f.write("\n")
    print(f"{name}: {os.path.getsize(os.path.join(OUT, name + '.h265'))} bytes, {len(hashes)} frames")


def main():
    os.makedirs(OUT, exist_ok=True)
    local = "--local" in sys.argv
    only = [a for a in sys.argv[1:] if not a.startswith("--")]
    for name in CLIPS:
        if not only or name in only:
            make(name, local)


if __name__ == "__main__":
    main()
