"""Makes the FFV1 test corpus for V6: small clips encoded by FFmpeg's FFV1 encoder from synthetic lavfi sources, in Matroska
and AVI, and the frames FFmpeg's own FFV1 decoder gives for them. FFmpeg runs only as a black-box tool; its source is never read.

Inputs and outputs:
  tests/video/corpus/ffv1/<clip>.mkv, .avi     clips, committed, covering versions 0, 1 and 3; Golomb-Rice and the range coder
                                               with the default and a custom state table; both context models; 4:2:0, 4:2:2,
                                               4:4:4, 4:1:1, 4:1:0 and gray, each with and without alpha where FFmpeg writes it;
                                               RGB and RGBA through the reversible color transform; 8 to 16 bits; slice grids;
                                               slice CRCs on and off; non-keyframes; coded initial states and quantization
                                               tables (two passes); interlace and aspect ratio flags; odd sizes.
  tests/video/corpus/ffv1/<clip>.frames.json   the source bit depth, FFmpeg's pixel format, and per frame in output order the MD5
                                               of FFmpeg's decode: the planes one after the other in the order Y, U, V, alpha
                                               (G, B, R, alpha for RGB), at the source bit depth, one byte per sample at 8 bits
                                               and two (little-endian) above. Packed formats (bgr0, bgra, ya8, rgb48, rgba64) are
                                               split into planes first.

Usage, from the repo root:
  docker build -t egami-video-tools tools/video/docker
  python tools/video/make-ffv1-references.py            every clip and its frames
  python tools/video/make-ffv1-references.py name ...   only those clips
  python tools/video/make-ffv1-references.py --local    run the ffmpeg on the PATH instead of Docker
"""
import hashlib
import json
import os
import subprocess
import sys

IMAGE = "egami-video-tools"
OUT = "tests/video/corpus/ffv1"
TESTSRC = "testsrc2=size={size}:rate=25"
MANDEL = "mandelbrot=size={size}:rate=25"
CRC = "-slicecrc 1"

# name: (source, extra filter, pixel format, size, frames, FFV1 settings, container). FFmpeg's -coder: rice is Golomb-Rice,
# range_def the range coder with the default state table, range_tab with a custom one.
CLIPS = {
    # Versions 0 and 1: parameters in each keyframe, one slice.
    "v0-420-rice": (TESTSRC, None, "yuv420p", "96x64", 3, "-level 0 -coder rice -g 1", "mkv"),
    "v0-420-range": (MANDEL, None, "yuv420p", "96x64", 3, "-level 0 -coder range_def -g 1", "mkv"),
    "v0-bgr0-rice": (TESTSRC, None, "bgr0", "64x48", 2, "-level 0 -coder rice -g 1", "avi"),
    "v0-bgr0-range": (TESTSRC, None, "bgr0", "64x48", 2, "-level 0 -coder range_def -g 1", "mkv"),
    "v1-bgr0-rice": (TESTSRC, None, "bgr0", "64x48", 2, "-level 1 -coder rice -g 1", "mkv"),
    "v1-422-tab-large": (MANDEL, None, "yuv422p", "96x64", 3, "-level 1 -coder range_tab -context 1 -g 1", "mkv"),
    "v1-bgra-range": (TESTSRC, "format=rgba,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='mod(X*3+Y,256)'", "bgra", "64x48", 2, "-level 1 -coder range_tab -g 1", "avi"),
    "v1-gray10": (MANDEL, None, "gray10le", "80x48", 2, "-level 1 -coder range_tab -g 1", "mkv"),
    "v1-gop": (TESTSRC, None, "yuv420p", "64x48", 6, "-level 1 -coder range_tab -g 3", "mkv"),
    "v1-gop-rice": (TESTSRC, None, "yuv420p", "64x48", 6, "-level 1 -coder rice -g 3", "avi"),
    # Version 3: configuration record, slices with headers and footers.
    "v3-420": (TESTSRC, None, "yuv420p", "128x96", 3, f"-level 3 -coder range_tab -slices 4 {CRC} -g 1", "mkv"),
    "v3-420-def": (MANDEL, None, "yuv420p", "128x96", 2, f"-level 3 -coder range_def -slices 4 {CRC} -g 1", "mkv"),
    "v3-420-rice": (TESTSRC, None, "yuv420p", "128x96", 3, f"-level 3 -coder rice -slices 4 {CRC} -g 1", "mkv"),
    "v3-420-avi": (MANDEL, None, "yuv420p", "128x96", 2, f"-level 3 -coder range_tab -slices 6 {CRC} -g 1", "avi"),
    "v3-422p10": (MANDEL, None, "yuv422p10le", "128x96", 2, f"-level 3 -coder range_tab -slices 6 {CRC} -g 1", "mkv"),
    "v3-444p12-large": (MANDEL, None, "yuv444p12le", "96x64", 2, f"-level 3 -coder range_tab -context 1 -slices 9 {CRC} -g 1", "mkv"),
    "v3-444p16": (TESTSRC, None, "yuv444p16le", "96x64", 2, f"-level 3 -coder range_tab -slices 4 {CRC} -g 1", "mkv"),
    "v3-420p16": (MANDEL, None, "yuv420p16le", "96x64", 2, f"-level 3 -coder range_def -slices 4 {CRC} -g 1", "mkv"),
    "v3-420p9": (MANDEL, None, "yuv420p9le", "96x64", 2, f"-level 3 -coder range_tab -slices 4 {CRC} -g 1", "mkv"),
    "v3-422p14": (TESTSRC, None, "yuv422p14le", "96x64", 2, f"-level 3 -coder range_tab -slices 4 {CRC} -g 1", "mkv"),
    "v3-411": (MANDEL, None, "yuv411p", "100x60", 2, f"-level 3 -coder range_tab -slices 4 {CRC} -g 1", "mkv"),
    "v3-410": (TESTSRC, None, "yuv410p", "100x62", 2, f"-level 3 -coder range_tab -slices 4 {CRC} -g 1", "mkv"),
    "v3-gray": (MANDEL, None, "gray", "96x64", 2, f"-level 3 -coder range_tab -slices 4 {CRC} -g 1", "mkv"),
    "v3-gray-rice": (MANDEL, None, "gray", "96x64", 2, f"-level 3 -coder rice -slices 4 {CRC} -g 1", "mkv"),
    "v3-gray16": (MANDEL, None, "gray16le", "96x64", 2, f"-level 3 -coder range_tab -slices 4 {CRC} -g 1", "mkv"),
    "v3-ya8": (TESTSRC, "format=yuva444p,geq=lum='lum(X,Y)':a='mod(X+Y*2,256)'", "ya8", "64x48", 2, f"-level 3 -coder range_tab -slices 4 {CRC} -g 1", "mkv"),
    "v3-yuva420p": (TESTSRC, "format=yuva444p,geq=lum='lum(X,Y)':cb='cb(X,Y)':cr='cr(X,Y)':a='mod(X*2+Y,256)'", "yuva420p", "96x64", 2, f"-level 3 -coder range_tab -slices 4 {CRC} -g 1", "mkv"),
    "v3-yuva444p10": (MANDEL, None, "yuva444p10le", "64x48", 2, f"-level 3 -coder range_tab -slices 4 {CRC} -g 1", "mkv"),
    "v3-yuva422p-rice": (TESTSRC, "format=yuva444p,geq=lum='lum(X,Y)':cb='cb(X,Y)':cr='cr(X,Y)':a='mod(X+Y,256)'", "yuva422p", "64x48", 2, f"-level 3 -coder rice -slices 4 {CRC} -g 1", "mkv"),
    "v3-bgr0": (TESTSRC, None, "bgr0", "96x64", 2, f"-level 3 -coder range_tab -slices 4 {CRC} -g 1", "mkv"),
    "v3-bgr0-rice": (MANDEL, None, "bgr0", "96x64", 2, f"-level 3 -coder rice -slices 4 {CRC} -g 1", "mkv"),
    "v3-bgra": (TESTSRC, "format=rgba,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='mod(X+Y*3,256)'", "bgra", "64x48", 2, f"-level 3 -coder range_tab -slices 4 {CRC} -g 1", "mkv"),
    "v3-gbrp9": (MANDEL, None, "gbrp9le", "64x48", 2, f"-level 3 -coder range_tab -slices 4 {CRC} -g 1", "mkv"),
    "v3-gbrp10": (MANDEL, None, "gbrp10le", "96x64", 2, f"-level 3 -coder range_tab -slices 4 {CRC} -g 1", "mkv"),
    "v3-gbrp12": (TESTSRC, None, "gbrp12le", "64x48", 2, f"-level 3 -coder range_tab -slices 4 {CRC} -g 1", "mkv"),
    "v3-gbrp14": (MANDEL, None, "gbrp14le", "64x48", 2, f"-level 3 -coder range_tab -slices 4 {CRC} -g 1", "mkv"),
    "v3-gbrp16": (TESTSRC, None, "gbrp16le", "64x48", 2, f"-level 3 -coder range_tab -slices 4 {CRC} -g 1", "mkv"),
    "v3-gbrap10": (MANDEL, None, "gbrap10le", "64x48", 2, f"-level 3 -coder range_tab -slices 4 {CRC} -g 1", "mkv"),
    "v3-gbrap16": (TESTSRC, None, "gbrap16le", "64x48", 2, f"-level 3 -coder range_tab -slices 4 {CRC} -g 1", "mkv"),
    "v3-nocrc": (TESTSRC, None, "yuv420p", "96x64", 2, "-level 3 -coder range_tab -slices 4 -slicecrc 0 -g 1", "mkv"),
    "v3-gop": (MANDEL, None, "yuv420p", "96x64", 7, f"-level 3 -coder range_tab -slices 4 {CRC} -g 4", "mkv"),
    "v3-gop-rice": (MANDEL, None, "yuv422p", "96x64", 7, f"-level 3 -coder rice -slices 4 {CRC} -g 4", "mkv"),
    "v3-qtable": (MANDEL, None, "yuv420p", "96x64", 2, f"-level 3 -coder range_tab -qtable greater8bit -slices 4 {CRC} -g 1", "mkv"),
    "v3-interlaced": (TESTSRC, "setfield=tff,setsar=16/15", "yuv422p", "96x64", 2, f"-level 3 -coder range_tab -slices 4 {CRC} -g 1 -flags +ildct", "mkv"),
    "v3-odd-97x61": (MANDEL, "scale=97:61", "yuv420p", "98x62", 3, f"-level 3 -coder range_tab -slices 12 {CRC} -g 1", "mkv"),
    "v3-odd-rgb-33x17": (TESTSRC, "scale=33:17", "bgr0", "34x18", 2, f"-level 3 -coder rice -slices 4 {CRC} -g 1", "mkv"),
    "v3-slices-24": (MANDEL, None, "yuv420p10le", "384x288", 1, f"-level 3 -coder range_tab -slices 24 {CRC} -g 1", "mkv"),
    "v3-slices-1": (TESTSRC, None, "yuv444p", "64x48", 2, f"-level 3 -coder range_tab -slices 1 {CRC} -g 1", "mkv"),
    "v3-bgr0-rice-s1": (TESTSRC, None, "bgr0", "64x48", 2, "-level 3 -coder rice -slices 1 -g 1", "mkv"),
}

# Clips coded in two passes, so the configuration record carries initial states.
TWO_PASS = {
    "v3-2pass": (MANDEL, None, "yuv420p", "96x64", 3, f"-level 3 -coder range_tab -context 1 -slices 4 {CRC} -g 1", "mkv"),
    "v3-2pass-rgb": (TESTSRC, None, "gbrp10le", "64x48", 3, f"-level 3 -coder range_tab -slices 4 {CRC} -g 1", "mkv"),
}

# Bytes per sample and how a packed format splits into planes (channel indexes per output plane, in Y U V A or G B R A order).
PACKED = {
    "bgr0": (1, 4, [1, 0, 2]),
    "bgra": (1, 4, [1, 0, 2, 3]),
    "ya8": (1, 2, [0, 1]),
    "rgb48le": (2, 3, [1, 2, 0]),
    "rgba64le": (2, 4, [1, 2, 0, 3]),
}


def run(command, local):
    if local:
        subprocess.run(["sh", "-c", f"cd {OUT} && {command}"], check=True)
    else:
        subprocess.run(["docker", "run", "--rm", "-v", f"{os.path.abspath(OUT)}:/work", IMAGE, "sh", "-c", command], check=True)


def output(command, local, binary=False):
    if local:
        args = ["sh", "-c", f"cd {OUT} && {command}"]
    else:
        args = ["docker", "run", "--rm", "-v", f"{os.path.abspath(OUT)}:/work", IMAGE, "sh", "-c", command]
    return subprocess.run(args, check=True, capture_output=True, text=not binary).stdout


def depth_of(fmt):
    for d in (9, 10, 12, 14, 16):
        if f"p{d}" in fmt or f"gray{d}" in fmt or f"ap{d}" in fmt:
            return d
    return 16 if fmt in ("rgb48le", "rgba64le") else 8


def plane_sizes(fmt, w, h):
    """The (width, height) of each plane of a planar FFmpeg pixel format, in its memory order."""
    if fmt.startswith("gray"):
        return [(w, h)]
    if fmt.startswith("gbr"):
        return [(w, h)] * (4 if fmt.startswith("gbrap") else 3)
    sub = {"420": (1, 1), "422": (1, 0), "444": (0, 0), "411": (2, 0), "410": (2, 2), "440": (0, 1)}
    key = fmt[4:7] if fmt.startswith("yuva") else fmt[3:6]
    sx, sy = sub[key]
    cw, ch = -(-w >> sx), -(-h >> sy)
    return [(w, h), (cw, ch), (cw, ch)] + ([(w, h)] if fmt.startswith("yuva") else [])


def planes_of(raw, fmt, w, h):
    """Splits one decoded frame into planes in Y, U, V, A (G, B, R, A) order."""
    if fmt in PACKED:
        size, channels, order = PACKED[fmt]
        planes = []
        for c in order:
            plane = bytearray()
            for i in range(w * h):
                at = (i * channels + c) * size
                plane += raw[at:at + size]
            planes.append(bytes(plane))
        return planes
    size = 1 if depth_of(fmt) == 8 else 2
    planes, at = [], 0
    for pw, ph in plane_sizes(fmt, w, h):
        planes.append(raw[at:at + pw * ph * size])
        at += pw * ph * size
    if at != len(raw):
        raise ValueError(f"{fmt} {w}x{h}: frame has {len(raw)} bytes, planes {at}")
    return planes


def references(name, file, local):
    probe = json.loads(output(f"ffprobe -v error -select_streams v:0 -show_entries stream=width,height,pix_fmt,field_order,sample_aspect_ratio -of json {file}", local))
    stream = probe["streams"][0]
    w, h, fmt = stream["width"], stream["height"], stream["pix_fmt"]
    raw = output(f"ffmpeg -v error -nostdin -i {file} -f rawvideo -", local, binary=True)
    frame_bytes = sum(pw * ph for pw, ph in plane_sizes(fmt, w, h)) * (1 if depth_of(fmt) == 8 else 2) if fmt not in PACKED else w * h * PACKED[fmt][0] * PACKED[fmt][1]
    if len(raw) % frame_bytes:
        raise ValueError(f"{name}: {len(raw)} bytes is not a whole number of {frame_bytes}-byte frames")
    md5 = []
    for at in range(0, len(raw), frame_bytes):
        md5.append(hashlib.md5(b"".join(planes_of(raw[at:at + frame_bytes], fmt, w, h))).hexdigest())
    with open(os.path.join(OUT, name + ".frames.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump({"file": file, "width": w, "height": h, "pixelFormat": fmt, "bitDepth": depth_of(fmt),
                   "fieldOrder": stream.get("field_order"), "sampleAspectRatio": stream.get("sample_aspect_ratio"), "md5": md5}, f, indent=1)
        f.write("\n")
    print(f"{name}: {os.path.getsize(os.path.join(OUT, file))} bytes, {len(md5)} frames, {fmt}")


def make(name, spec, local, two_pass=False):
    source, extra, fmt, size, frames, settings, container = spec
    vf = f'-vf "{extra},format={fmt}"' if extra else f"-vf format={fmt}"
    src = f"-f lavfi -i {source.format(size=size)} -frames:v {frames} {vf}"
    file = f"{name}.{container}"
    common = f"ffmpeg -y -v error -nostdin {src} -an -c:v ffv1 -pix_fmt {fmt} {settings} -fflags +bitexact -map_metadata -1"
    if two_pass:
        run(f"{common} -pass 1 -passlogfile {name} -f null - && {common} -pass 2 -passlogfile {name} {file} && rm -f {name}-0.log", local)
    else:
        run(f"{common} {file}", local)
    references(name, file, local)


def main():
    os.makedirs(OUT, exist_ok=True)
    local = "--local" in sys.argv
    only = [a for a in sys.argv[1:] if not a.startswith("--")]
    for name, spec in CLIPS.items():
        if not only or name in only:
            make(name, spec, local)
    for name, spec in TWO_PASS.items():
        if not only or name in only:
            make(name, spec, local, two_pass=True)


if __name__ == "__main__":
    main()
