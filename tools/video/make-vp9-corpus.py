"""Makes the VP9 test corpus for V3: clips encoded by libvpx and FFmpeg, WebM, Matroska and IVF files holding them, WebM with
alpha, an HDR10 clip, and the expected frames, all from tools run in Docker as black boxes (tools/video/vp9ref: libvpx 1.14.1 with high
bit depth, FFmpeg and mkvmerge).

Inputs and outputs:
  tests/video/corpus/vp9/<clip>.ivf, .webm, .mkv   small clips, committed: vpxenc output in IVF, and the same packets remuxed by FFmpeg
                                            (-c copy) into WebM, and for some into Matroska. They cover profiles 0 to 3 at 8, 10
                                            and 12 bits, 4:2:0, 4:2:2, 4:4:0 (to check it is refused), 4:4:4 and RGB, hidden
                                            alternate reference frames in superframes, tiles, lossless coding, error resilient
                                            and frame parallel coding, segmentation, frequent keyframes and odd sizes.
  tests/video/corpus/vp9/alpha-*.webm              WebM with alpha, written by FFmpeg's libvpx-vp9 encoder (AlphaMode 1, the alpha frames in
                                            BlockAdditional).
  tests/video/corpus/vp9/hdr10.mkv                 a 10-bit BT.2020 PQ clip whose Colour element mkvmerge filled with mastering display and
                                            light level metadata.
  tests/video/corpus/vp9/hdr10plus.webm            the same frames in a WebM this script writes itself, each block carrying an HDR10+
                                            (SMPTE ST 2094-40) ITU-T T.35 payload in BlockAdditional with BlockAddID 4, as
                                            YouTube's HDR10+ files store it; hdr10plus.side_data.json holds ffprobe's reading of it.
  .cache/video/vp9/vectors/vp9?-2-*                the libvpx VP9 test vectors and their .md5 files, fetched from the WebM project's test
                                            data bucket and checked against libvpx's test/test-data.sha1. Their license is
                                            unconfirmed, so they and their references stay in .cache, never committed.
  .cache/video/vp9/large/*.ivf, *.webm             720p, 1080p and 2160p 8-bit clips and a 1080p 10-bit clip for the speed gate, with
                                            libvpx's and FFmpeg's decode times (timing.json).

References, next to each file (in .cache/video/vp9/reference for the vectors):
  <file>.json         ffprobe's streams and packets (as tools/video/make-corpus.py writes them), for the container gates
  <clip>.frames.json  per packet whether it shows a frame, and per shown frame the MD5 of vpxdec's output (packed planes at the
                      coded size, 16-bit little-endian samples above 8 bits) with its size; "ffmpeg" says whether FFmpeg's own VP9
                      decoder gave the same hashes. For a vector the hashes are also checked against the vector's .md5 file.
                      Alpha clips instead give the MD5 of each plane (y, u, v, a) as FFmpeg's libvpx decoder writes yuva420p.
  <clip>.dump.gz      what libvpx read from every frame it decoded, hidden ones included (vp9dump): the header fields, the
                      probabilities after adaptation, every block's modes and motion vectors (left out for vectors over 2 MB),
                      and the frame's MD5

Usage, from the repo root with Docker running:
  docker build -t egami-vp9ref:v3 tools/video/vp9ref
  python tools/video/make-vp9-corpus.py              vectors (fetched once), clips and references
  python tools/video/make-vp9-corpus.py --references references of the committed clips again, without encoding them anew
  python tools/video/make-vp9-corpus.py --vectors    vectors and their references only
  python tools/video/make-vp9-corpus.py --large      the speed gate's clips and timings
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

IMAGE = "egami-vp9ref:v3"
BUCKET = "https://storage.googleapis.com/downloads.webmproject.org/test_data/libvpx/"
CLIPS = "tests/video/corpus/vp9"
VECTORS = ".cache/video/vp9/vectors"
VECTOR_REFS = ".cache/video/vp9/reference"
LARGE = ".cache/video/vp9/large"
WORK = "/tmp/vp9work"

# The VP9 test vector names libvpx's own tests decode (test/test_vectors.cc, high bit depth build), plus the SVC vector.
SIZES = (8, 10, 16, 18, 32, 34, 64, 66)
VECTOR_NAMES = (
    [f"vp90-2-00-quantizer-{i:02}.webm" for i in range(64)]
    + [f"vp90-2-01-sharpness-{i}.webm" for i in range(1, 8)]
    + [f"vp90-2-02-size-{w:02}x{h:02}.webm" for w in SIZES for h in SIZES]
    + [f"vp90-2-02-size-{s}.webm" for s in ("130x132", "132x130", "132x132", "178x180", "180x178", "180x180")]
    + [f"vp90-2-03-size-{w}x{h}.webm" for w in (196, 198, 200, 202, 208, 210, 224, 226) for h in (196, 198, 200, 202, 208, 210, 224, 226)]
    + ["vp90-2-03-size-352x288.webm", "vp90-2-03-deltaq.webm", "vp90-2-05-resize.ivf", "vp90-2-06-bilinear.webm",
       "vp90-2-07-frame_parallel.webm", "vp90-2-08-tile_1x2_frame_parallel.webm", "vp90-2-08-tile_1x2.webm",
       "vp90-2-08-tile_1x4_frame_parallel.webm", "vp90-2-08-tile_1x4.webm", "vp90-2-08-tile_1x8_frame_parallel.webm",
       "vp90-2-08-tile_1x8.webm", "vp90-2-08-tile-4x4.webm", "vp90-2-08-tile-4x1.webm", "vp90-2-09-subpixel-00.ivf",
       "vp90-2-02-size-lf-1920x1080.webm", "vp90-2-09-aq2.webm", "vp90-2-09-lf_deltas.webm", "vp90-2-10-show-existing-frame.webm",
       "vp90-2-10-show-existing-frame2.webm", "vp90-2-11-size-351x287.webm", "vp90-2-11-size-351x288.webm",
       "vp90-2-11-size-352x287.webm", "vp90-2-12-droppable_1.ivf", "vp90-2-12-droppable_2.ivf", "vp90-2-12-droppable_3.ivf",
       "vp90-2-13-largescaling.webm"]
    + [f"vp90-2-14-resize-fp-tiles-{t}.webm" for t in ("1-16", "1-2-4-8-16", "1-2", "1-4", "16-1", "16-2", "16-4", "16-8-4-2-1",
                                                         "16-8", "1-8", "2-16", "2-1", "2-4", "2-8", "4-16", "4-1", "4-2", "4-8",
                                                         "8-16", "8-1", "8-2", "8-4")]
    + [f"vp90-2-14-resize-10frames-fp-tiles-{t}.webm" for t in ("1-2-4-8", "1-2", "1-4", "1-8", "2-1", "2-4", "2-8", "4-1", "4-2",
                                                                  "4-8", "8-1", "8-2", "8-4-2-1", "8-4")]
    + ["vp90-2-15-segkey.webm", "vp90-2-15-segkey_adpq.webm", "vp90-2-16-intra-only.webm", "vp90-2-17-show-existing-frame.webm",
       "vp90-2-18-resize.ivf", "vp90-2-19-skip.webm", "vp90-2-19-skip-01.webm", "vp90-2-19-skip-02.webm",
       "vp91-2-04-yuv444.webm", "vp91-2-04-yuv422.webm", "vp91-2-04-yuv440.webm",
       "vp92-2-20-10bit-yuv420.webm", "vp92-2-20-12bit-yuv420.webm", "vp93-2-20-10bit-yuv422.webm", "vp93-2-20-12bit-yuv422.webm",
       "vp93-2-20-10bit-yuv440.webm", "vp93-2-20-12bit-yuv440.webm", "vp93-2-20-10bit-yuv444.webm", "vp93-2-20-12bit-yuv444.webm",
       "vp90-2-20-big_superframe-01.webm", "vp90-2-20-big_superframe-02.webm", "vp90-2-22-svc_1280x720_1.webm",
       "vp90-2-22-svc_1280x720_3.ivf"]
    + [f"vp90-2-21-resize_inter_{s}_{n}.webm" for s in ("320x180", "320x240", "640x360", "640x480", "1280x720", "1920x1080")
       for n in ("5_1-2", "5_3-4", "7_1-2", "7_3-4")]
)

PATTERN = "testsrc2=size={w}x{h}:rate=30"
FRACTAL = "mandelbrot=size={w}x{h}:rate=30"

# name: (lavfi source, filter, input pixel format, frames, vpxenc options). Every clip is also remuxed to WebM; those in MKV to
# Matroska too. High bit depth clips give vpxenc the input bit depth; profile 1 and 3 clips their subsampling through the y4m.
CLIP_SET = {
    "p0-altref": (PATTERN.format(w=176, h=144), "null", "yuv420p", 40, "--good --cpu-used=2 --passes=2 --auto-alt-ref=1 --lag-in-frames=16 --target-bitrate=300"),
    "p0-tiles": (FRACTAL.format(w=1024, h=256), "null", "yuv420p", 6, "--good --cpu-used=4 --tile-columns=2 --tile-rows=2 --target-bitrate=600"),
    "p0-lossless": (PATTERN.format(w=128, h=96), "null", "yuv420p", 10, "--good --cpu-used=4 --lossless=1"),
    "p0-resilient": (FRACTAL.format(w=160, h=96), "null", "yuv420p", 30, "--rt --cpu-used=7 --error-resilient=1 --frame-parallel=1 --aq-mode=3 --target-bitrate=150"),
    "p0-keyframes": (PATTERN.format(w=128, h=96), "null", "yuv420p", 24, "--good --cpu-used=4 --kf-max-dist=5 --kf-min-dist=5 --sharpness=4 --target-bitrate=200"),
    "p0-odd-97x61": (PATTERN.format(w=98, h=62), "scale=97:61", "yuv420p", 20, "--good --cpu-used=2 --passes=2 --auto-alt-ref=1 --lag-in-frames=8 --target-bitrate=120"),
    "p0-aq": (FRACTAL.format(w=192, h=128), "null", "yuv420p", 16, "--good --cpu-used=3 --aq-mode=1 --target-bitrate=250"),
    "p1-444": (FRACTAL.format(w=160, h=96), "null", "yuv444p", 12, "--good --cpu-used=4 --profile=1 --target-bitrate=300"),
    "p1-422": (PATTERN.format(w=160, h=96), "null", "yuv422p", 12, "--good --cpu-used=4 --profile=1 --target-bitrate=300"),
    "p1-440": (PATTERN.format(w=96, h=64), "null", "yuv440p", 4, "--good --cpu-used=4 --profile=1 --target-bitrate=200"),
    "p2-10bit": (FRACTAL.format(w=160, h=96), "null", "yuv420p10le", 16, "--good --cpu-used=4 --profile=2 --bit-depth=10 --input-bit-depth=10 --auto-alt-ref=1 --lag-in-frames=8 --target-bitrate=300"),
    "p2-12bit": (PATTERN.format(w=128, h=96), "null", "yuv420p12le", 10, "--good --cpu-used=4 --profile=2 --bit-depth=12 --input-bit-depth=12 --target-bitrate=300"),
    "p3-10bit-422": (PATTERN.format(w=128, h=96), "null", "yuv422p10le", 10, "--good --cpu-used=4 --profile=3 --bit-depth=10 --input-bit-depth=10 --target-bitrate=300"),
    "p3-12bit-444": (FRACTAL.format(w=128, h=96), "null", "yuv444p12le", 10, "--good --cpu-used=4 --profile=3 --bit-depth=12 --input-bit-depth=12 --target-bitrate=300"),
}
MKV = {"p0-altref", "p2-10bit"}

# Clips FFmpeg's libvpx-vp9 wrapper writes directly as WebM: RGB (profile 1 with color_space 7) and alpha (AlphaMode 1, the alpha
# frames in BlockAdditional).
FFMPEG_SET = {
    "p1-rgb": ("testsrc2=size=128x96:rate=25", "format=gbrp", 10, "-pix_fmt gbrp -b:v 300k"),
}
ALPHA_SET = {
    "alpha-ramp": ("testsrc2=size=160x96:rate=25", "format=rgba,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='mod(X+Y+N*6,256)'", 20, "-b:v 300k"),
    "alpha-odd-75x45": ("testsrc2=size=76x46:rate=25", "scale=75:45,format=rgba,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='if(lt(hypot(X-37,Y-22),10+N),255,40)'", 16, "-b:v 150k -g 8"),
}

PACKET_FIELDS = ["stream_index", "pts", "dts", "duration", "size", "pos", "flags"]
STREAM_FIELDS = ["index", "codec_type", "codec_name", "codec_tag_string", "profile", "width", "height", "pix_fmt", "color_range",
                 "color_space", "color_transfer", "color_primaries", "chroma_location", "r_frame_rate", "avg_frame_rate",
                 "time_base", "nb_frames", "duration_ts", "side_data_list"]


def run(cmd):
    return subprocess.run(cmd, shell=True, check=True, capture_output=True)


def probe(path):
    data = json.loads(run(f"ffprobe -v error -show_streams -show_format -show_packets -of json {path}").stdout)
    trimmed = {
        "format": {k: data["format"].get(k) for k in ["format_name", "duration", "nb_streams"]},
        "streams": [{k: s[k] for k in STREAM_FIELDS if k in s} for s in data["streams"]],
        "packets": [{k: p[k] for k in PACKET_FIELDS if k in p} for p in data.get("packets", [])],
    }
    with open(path + ".json", "w", encoding="utf-8", newline="\n") as f:
        f.write('{"format":' + json.dumps(trimmed["format"]) + ',\n"streams":[\n')
        f.write(",\n".join(json.dumps(s) for s in trimmed["streams"]) + '],\n"packets":[\n')
        f.write(",\n".join(json.dumps(p) for p in trimmed["packets"]) + "]}\n")


def write_dump(path, dump):
    # No name and a zero time in the gzip header, so regenerating gives identical files.
    with open(path, "wb") as f:
        with gzip.GzipFile(fileobj=f, mode="wb", filename="", mtime=0) as gz:
            gz.write(dump.encode())


def frame_bytes(w, h, ssx, ssy, depth):
    cw, ch = (w + ssx) >> ssx, (h + ssy) >> ssy
    return (w * h + 2 * cw * ch) * (2 if depth > 8 else 1)


def parse_dump(dump):
    """Per packet: the frames libvpx decoded (header fields and MD5) and whether one was shown, with its size."""
    packets = []
    frame = None
    for line in dump.splitlines():
        parts = line.split(" ", 1)
        key = parts[0]
        if key == "packet":
            packets.append({"frames": [], "shown": None, "error": False})
        elif key == "frame":
            frame = {}
            packets[-1]["frames"].append(frame)
        elif key == "shown":
            packets[-1]["shown"] = tuple(map(int, parts[1].split("x")))
        elif key == "error":
            packets[-1]["error"] = True
        elif key in ("b", "mi", "probs"):
            continue
        elif frame is not None and len(parts) == 2:
            frame[key] = parts[1]
    return packets


def reference(ivf, target, name, blocks=True):
    """vp9dump and vpxdec on one IVF file: vpxdec's shown frames must equal the dump's; FFmpeg's native decoder is checked against them."""
    os.makedirs(WORK, exist_ok=True)
    dump = run(f"vp9dump {ivf}{'' if blocks else ' --no-blocks'}").stdout.decode()
    packets = parse_dump(dump)
    # --keep-going: streams that start with frames libvpx cannot decode yet (an intra-only frame before the first keyframe
    # refers to slots nothing filled) report the error and go on, as the dump does.
    subprocess.run(f"vpxdec --keep-going --rawvideo -o {WORK}/vpxdec.yuv {ivf}", shell=True, capture_output=True)
    with open(f"{WORK}/vpxdec.yuv", "rb") as f:
        raw = f.read()

    md5, sizes, depths, at = [], [], [], 0
    last = {}
    for p in packets:
        for fr in p["frames"]:
            if fr.get("show_existing_frame") == "0":
                last = fr
        if p["shown"] is None:
            continue
        w, h = p["shown"]
        depth = int(last.get("bit_depth", "8"))
        size = frame_bytes(w, h, int(last.get("subsampling_x", "1")), int(last.get("subsampling_y", "1")), depth)
        digest = hashlib.md5(raw[at:at + size]).hexdigest()
        at += size
        # The shown frame is the last one the packet decoded.
        if p["frames"] and p["frames"][-1].get("md5") != digest:
            raise ValueError(f"{name}: vpxdec and vp9dump disagree on a shown frame")
        md5.append(digest)
        sizes.append((w, h))
        depths.append(depth)
    if at != len(raw):
        raise ValueError(f"{name}: {len(raw)} bytes of frames, {at} expected")

    # FFmpeg's native VP9 decoder, as the design's cross-check: every frame passed through as decoded; only clean files of one
    # size and format are compared.
    ffmpeg = None
    if not any(p["error"] for p in packets) and len(set(sizes)) <= 1 and len(set(depths)) <= 1 and md5:
        out = run(f"ffmpeg -v error -c:v vp9 -i {ivf} -fps_mode passthrough -f framemd5 -").stdout.decode()
        theirs = [line.split(",")[-1].strip() for line in out.splitlines() if line and not line.startswith("#")]
        ffmpeg = theirs == md5

    entry = {
        "width": sizes[0][0] if sizes else 0,
        "height": sizes[0][1] if sizes else 0,
        "bit_depth": depths[0] if depths else 8,
        "packets": len(packets),
        "shown": [i for i, p in enumerate(packets) if p["shown"] is not None],
        "errors": [i for i, p in enumerate(packets) if p["error"]],
        "ffmpeg": ffmpeg,
        "md5": md5,
    }
    if len(set(sizes)) > 1:
        entry["sizes"] = [f"{w}x{h}" for w, h in sizes]
    with open(os.path.join(target, name + ".frames.json"), "w", newline="\n") as f:
        json.dump(entry, f, indent=1)
        f.write("\n")
    write_dump(os.path.join(target, name + ".dump.gz"), dump)
    return entry, dump


def check_md5_file(md5_path, entry, name):
    """A vector's .md5 file holds libvpx's own MD5 of every shown frame; it must equal what vpxdec gave here."""
    with open(md5_path) as f:
        theirs = [line.split()[0] for line in f if line.strip()]
    if theirs != entry["md5"]:
        raise ValueError(f"{name}: the .md5 file disagrees with vpxdec ({len(theirs)} against {len(entry['md5'])} frames)")


def alpha_reference(webm, name):
    run(f"ffmpeg -v error -y -c:v libvpx-vp9 -i {webm} -fps_mode passthrough -f rawvideo -pix_fmt yuva420p {WORK}/alpha.yuv")
    info = json.loads(run(f"ffprobe -v error -show_streams -of json {webm}").stdout)["streams"][0]
    with open(f"{WORK}/alpha.yuv", "rb") as f:
        raw = f.read()
    w, h = info["width"], info["height"]
    cw, ch = (w + 1) // 2, (h + 1) // 2
    lengths = [w * h, cw * ch, cw * ch, w * h]
    frames, at = [], 0
    while at < len(raw):
        frame = {}
        for plane, length in zip("yuva", lengths):
            frame[plane] = hashlib.md5(raw[at:at + length]).hexdigest()
            at += length
        frames.append(frame)
    entry = {"width": w, "height": h, "md5": frames}
    with open(os.path.join(CLIPS, name + ".frames.json"), "w", newline="\n") as f:
        json.dump(entry, f, indent=1)
        f.write("\n")
    return entry


def make_clip(name, source, vf, pix_fmt, frames, options):
    os.makedirs(WORK, exist_ok=True)
    y4m = f"{WORK}/{name}.y4m"
    if pix_fmt == "yuv440p":
        # FFmpeg's YUV4MPEG2 writer has no 4:4:0, so vpxenc reads raw I440.
        w, h = re.search(r"size=(\d+)x(\d+)", source).groups()
        run(f"ffmpeg -v error -y -f lavfi -i {source} -vf \"{vf}\" -frames:v {frames} -pix_fmt yuv440p -f rawvideo {y4m}")
        run(f"vpxenc -q --codec=vp9 --ivf {options} --threads=1 --i440 -w {w} -h {h} --fps=30/1 -o {CLIPS}/{name}.ivf {y4m}")
        return
    run(f"ffmpeg -v error -y -f lavfi -i {source} -vf \"{vf}\" -frames:v {frames} -pix_fmt {pix_fmt} -strict -1 -f yuv4mpegpipe {y4m}")
    run(f"vpxenc -q --codec=vp9 --ivf {options} --threads=1 -o {CLIPS}/{name}.ivf {y4m}")


def remux(name):
    run(f"ffmpeg -v error -y -i {CLIPS}/{name}.ivf -c copy {CLIPS}/{name}.webm")
    probe(f"{CLIPS}/{name}.webm")
    if name in MKV:
        run(f"ffmpeg -v error -y -i {CLIPS}/{name}.ivf -c copy {CLIPS}/{name}.mkv")
        probe(f"{CLIPS}/{name}.mkv")


def features_of(packets):
    frames = [f for p in packets for f in p["frames"]]
    def any_of(key, value):
        return any(f.get(key) == value for f in frames)
    return {
        "superframes": any(len(p["frames"]) > 1 for p in packets),
        "hidden": any_of("show_frame", "0"),
        "show_existing": any_of("show_existing_frame", "1"),
        "intra_only": any_of("intra_only", "1"),
        "segmentation": any_of("segmentation_enabled", "1"),
        "tiles": any(f.get("tile_cols_log2", "0") != "0" or f.get("tile_rows_log2", "0") != "0" for f in frames),
        "lossless": any_of("base_q_idx", "0"),
        "compound": any_of("reference_select", "1") or any_of("reference_select", "2"),
        "error_resilient": any_of("error_resilient_mode", "1"),
    }


def inside_vectors():
    os.makedirs(VECTOR_REFS, exist_ok=True)
    count = 0
    for file in sorted(os.listdir(VECTORS)):
        if not (file.endswith(".webm") or file.endswith(".ivf")):
            continue
        path = os.path.join(VECTORS, file)
        name = file.rsplit(".", 1)[0]
        ivf = path
        if file.endswith(".webm"):
            ivf = f"{WORK}/{name}.ivf"
            os.makedirs(WORK, exist_ok=True)
            run(f"ffmpeg -v error -y -i {path} -c copy -copyinkf -fflags -autobsf {ivf}")
        entry, _ = reference(ivf, VECTOR_REFS, name, blocks=os.path.getsize(path) < 2_000_000)
        if os.path.exists(path + ".md5"):
            check_md5_file(path + ".md5", entry, name)
        probe(path)
        os.replace(path + ".json", os.path.join(VECTOR_REFS, file + ".json"))
        count += 1
    print(f"vectors: {count} files")


def inside(keep_clips):
    os.makedirs(CLIPS, exist_ok=True)
    manifest = {"tools": "libvpx 1.14.1 vpxenc and vpxdec (generic C build, high bit depth), FFmpeg "
                + run("ffmpeg -version").stdout.decode().split()[2] + ", mkvmerge", "clips": {}}
    for name, (source, vf, pix_fmt, frames, options) in CLIP_SET.items():
        if not keep_clips:
            make_clip(name, source, vf, pix_fmt, frames, options)
            remux(name)
        probe(f"{CLIPS}/{name}.ivf")
        entry, dump = reference(f"{CLIPS}/{name}.ivf", CLIPS, name)
        features = features_of(parse_dump(dump))
        manifest["clips"][name] = {"source": source.split("=")[0], "filter": vf, "pix_fmt": pix_fmt, "frames": frames,
                                   "vpxenc": options, "size": f"{entry['width']}x{entry['height']}", "bit_depth": entry["bit_depth"],
                                   "packets": entry["packets"], "shown": len(entry["shown"]), "errors": len(entry["errors"]),
                                   "ffmpeg_agrees": entry["ffmpeg"], **features}
        print(f"{name}: {entry['width']}x{entry['height']}, {entry['packets']} packets, {len(entry['shown'])} shown, {features}")

    for name, (source, vf, frames, options) in FFMPEG_SET.items():
        out = f"{CLIPS}/{name}.webm"
        if not keep_clips:
            run(f"ffmpeg -v error -y -f lavfi -i {source} -vf \"{vf}\" -frames:v {frames} -c:v libvpx-vp9 {options} {out}")
            run(f"ffmpeg -v error -y -i {out} -c copy -copyinkf -fflags -autobsf {CLIPS}/{name}.ivf")
        probe(out)
        entry, dump = reference(f"{CLIPS}/{name}.ivf", CLIPS, name)
        manifest["clips"][name] = {"source": source, "filter": vf, "frames": frames, "ffmpeg": options,
                                   "size": f"{entry['width']}x{entry['height']}", "packets": entry["packets"],
                                   "shown": len(entry["shown"]), "ffmpeg_agrees": entry["ffmpeg"], **features_of(parse_dump(dump))}
        print(f"{name}: {entry['width']}x{entry['height']}, {entry['packets']} packets")

    for name, (source, vf, frames, options) in ALPHA_SET.items():
        out = f"{CLIPS}/{name}.webm"
        if not keep_clips:
            run(f"ffmpeg -v error -y -f lavfi -i {source} -vf \"{vf}\" -frames:v {frames} -c:v libvpx-vp9 -pix_fmt yuva420p {options} {out}")
        probe(out)
        entry = alpha_reference(out, name)
        manifest["clips"][name] = {"source": source, "filter": vf, "frames": frames, "ffmpeg": options, "size": f"{entry['width']}x{entry['height']}"}
        print(f"{name}: {entry['width']}x{entry['height']}, {len(entry['md5'])} frames with alpha")

    # HDR10: 10-bit BT.2020 PQ from FFmpeg, its Colour element completed by mkvmerge with mastering display and light levels.
    name = "hdr10"
    if not keep_clips:
        run(f"ffmpeg -v error -y -f lavfi -i {FRACTAL.format(w=160, h=96)} -frames:v 8 -pix_fmt yuv420p10le -c:v libvpx-vp9 "
            f"-b:v 300k -color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc -color_range tv {WORK}/hdr10.webm")
        run(f"mkvmerge -q -o {CLIPS}/{name}.mkv --colour-matrix-coefficients 0:9 --colour-transfer-characteristics 0:16 "
            f"--colour-primaries 0:9 --colour-range 0:1 --max-content-light 0:1000 --max-frame-light 0:400 "
            f"--chromaticity-coordinates 0:0.708,0.292,0.170,0.797,0.131,0.046 --white-colour-coordinates 0:0.3127,0.3290 "
            f"--max-luminance 0:1000 --min-luminance 0:0.005 {WORK}/hdr10.webm")
        run(f"ffmpeg -v error -y -i {CLIPS}/{name}.mkv -c copy -copyinkf -fflags -autobsf {CLIPS}/{name}.ivf")
    probe(f"{CLIPS}/{name}.mkv")
    frames = json.loads(run(f"ffprobe -v error -select_streams v -show_frames -read_intervals %+#1 -of json {CLIPS}/{name}.mkv").stdout)
    with open(f"{CLIPS}/{name}.side_data.json", "w", newline="\n") as f:
        json.dump(frames["frames"][0].get("side_data_list", []), f, indent=1)
        f.write("\n")
    entry, _ = reference(f"{CLIPS}/{name}.ivf", CLIPS, name)
    manifest["clips"][name] = {"source": "mandelbrot", "frames": 8, "size": f"{entry['width']}x{entry['height']}", "bit_depth": 10,
                               "ffmpeg_agrees": entry["ffmpeg"]}

    # HDR10+: the same frames with ST 2094-40 payloads in BlockAdditional, read back by ffprobe.
    make_hdr10plus(f"{CLIPS}/hdr10.ivf", f"{CLIPS}/hdr10plus.webm")
    probe(f"{CLIPS}/hdr10plus.webm")
    # ffprobe repeats a key for each value of a list (maxscl, the percentiles, the anchors); keep them all, as lists.
    frames = json.loads(run(f"ffprobe -v error -select_streams v -show_frames -of json {CLIPS}/hdr10plus.webm").stdout,
                        object_pairs_hook=repeated_keys_as_lists)["frames"]
    with open(f"{CLIPS}/hdr10plus.side_data.json", "w", newline="\n") as f:
        json.dump([fr.get("side_data_list", []) for fr in frames], f, indent=1)
        f.write("\n")

    with open(os.path.join(CLIPS, "manifest.json"), "w", newline="\n") as f:
        json.dump(manifest, f, indent=1)
        f.write("\n")


class Bits:
    """An MSB-first bit writer for the HDR10+ payload."""

    def __init__(self):
        self.bits = []

    def put(self, value, count):
        for i in range(count - 1, -1, -1):
            self.bits.append((value >> i) & 1)

    def bytes(self):
        bits = self.bits + [0] * (-len(self.bits) % 8)
        return bytes(int("".join(map(str, bits[i:i + 8])), 2) for i in range(0, len(bits), 8))


PERCENTAGES = [1, 5, 10, 25, 50, 75, 90, 95, 99]


def hdr10plus_payload(frame):
    """An ITU-T T.35 HDR10+ payload (SMPTE ST 2094-40, application version 1): one window, a target display of 400 cd/m², the
    frame's MaxSCL, average and percentiles in 0.1 cd/m² steps, and a tone mapping curve with nine Bezier anchors."""
    b = Bits()
    b.put(0xB5, 8)       # itu_t_t35_country_code: United States
    b.put(0x003C, 16)    # itu_t_t35_terminal_provider_code
    b.put(0x0001, 16)    # itu_t_t35_terminal_provider_oriented_code
    b.put(4, 8)          # application_identifier
    b.put(1, 8)          # application_version
    b.put(1, 2)          # num_windows
    b.put(400, 27)       # targeted_system_display_maximum_luminance
    b.put(0, 1)          # targeted_system_display_actual_peak_luminance_flag
    for i in range(3):
        b.put(9000 + 500 * i + 37 * frame, 17)   # maxscl[i]
    b.put(1200 + 11 * frame, 17)                 # average_maxrgb
    b.put(len(PERCENTAGES), 4)                   # num_distribution_maxrgb_percentiles
    for i, percentage in enumerate(PERCENTAGES):
        b.put(percentage, 7)
        b.put(100 * (i + 1) + frame, 17)
    b.put(100 + frame, 10)  # fraction_bright_pixels
    b.put(0, 1)             # mastering_display_actual_peak_luminance_flag
    b.put(1, 1)             # tone_mapping_flag
    b.put(1000 + frame, 12)  # knee_point_x
    b.put(1500 + frame, 12)  # knee_point_y
    b.put(9, 4)             # num_bezier_curve_anchors
    for i in range(9):
        b.put(100 * (i + 1) + frame, 10)
    b.put(0, 1)             # color_saturation_mapping_flag
    return b.bytes()


def ebml_id(value):
    return value.to_bytes((value.bit_length() + 7) // 8, "big")


def ebml_size(size):
    length = 1
    while size >= (1 << (7 * length)) - 1:
        length += 1
    return ((1 << (7 * length)) | size).to_bytes(length, "big")


def element(element_id, payload):
    return ebml_id(element_id) + ebml_size(len(payload)) + payload


def uint(element_id, value):
    return element(element_id, value.to_bytes(max(1, (value.bit_length() + 7) // 8), "big"))


def read_ivf(path):
    with open(path, "rb") as f:
        data = f.read()
    header_size = int.from_bytes(data[6:8], "little")
    width, height = int.from_bytes(data[12:14], "little"), int.from_bytes(data[14:16], "little")
    rate, scale = int.from_bytes(data[16:20], "little"), int.from_bytes(data[20:24], "little")
    frames, at = [], header_size
    while at + 12 <= len(data):
        size = int.from_bytes(data[at:at + 4], "little")
        pts = int.from_bytes(data[at + 4:at + 12], "little")
        frames.append((pts, data[at + 12:at + 12 + size]))
        at += 12 + size
    return width, height, rate, scale, frames


def repeated_keys_as_lists(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            result[key] = (result[key] if isinstance(result[key], list) else [result[key]]) + [value]
        else:
            result[key] = value
    return result


def make_hdr10plus(ivf, out):
    """Writes a WebM of an IVF clip's frames with an HDR10+ payload in every block (BlockAddID 4) and HDR10 Colour metadata."""
    width, height, rate, scale, frames = read_ivf(ivf)
    header = element(0x1A45DFA3, uint(0x4286, 1) + uint(0x42F7, 1) + uint(0x42F2, 4) + uint(0x42F3, 8) + element(0x4282, b"webm")
                     + uint(0x4287, 4) + uint(0x4285, 2))
    info = element(0x1549A966, uint(0x2AD7B1, 1000000) + element(0x4D80, b"make-vp9-corpus.py") + element(0x5741, b"make-vp9-corpus.py"))
    colour = element(0x55B0, uint(0x55B1, 9) + uint(0x55B9, 1) + uint(0x55BA, 16) + uint(0x55BB, 9) + uint(0x55BC, 1000) + uint(0x55BD, 400))
    video = element(0xE0, uint(0xB0, width) + uint(0xBA, height) + colour)
    mapping = element(0x41E4, uint(0x41F0, 4) + uint(0x41E7, 4))
    track = element(0xAE, uint(0xD7, 1) + uint(0x73C5, 1) + uint(0x83, 1) + element(0x86, b"V_VP9") + uint(0x55EE, 4) + mapping + video)
    tracks = element(0x1654AE6B, track)
    groups = b""
    previous = None
    for index, (pts, data) in enumerate(frames):
        ms = pts * scale * 1000 // rate
        key = (data[0] >> 6) == 2 and ((data[0] >> 2) & 1) == 0 and ((data[0] >> 3) & 1) == 0
        block = element(0xA1, b"\x81" + ms.to_bytes(2, "big", signed=True) + b"\x00" + data)
        more = element(0x75A1, element(0xA6, uint(0xEE, 4) + element(0xA5, hdr10plus_payload(index))))
        reference = b"" if key or previous is None else element(0xFB, (previous - ms).to_bytes(2, "big", signed=True))
        groups += element(0xA0, block + more + reference)
        previous = ms
    cluster = element(0x1F43B675, uint(0xE7, 0) + groups)
    segment = element(0x18538067, info + tracks + cluster)
    with open(out, "wb") as f:
        f.write(header + segment)


LARGE_SET = {
    # 10 seconds at 30 frames a second of a moving fractal with film grain, so the encoder spends its whole bit budget as on camera
    # footage; tile columns as YouTube's encodes use them, so libvpx's threads have work to share.
    "720p": ("mandelbrot=size=1280x720:rate=30,noise=alls=10:allf=t", "yuv420p", 300, "--good --cpu-used=4 --target-bitrate=1800 --tile-columns=2 --auto-alt-ref=1 --lag-in-frames=16 --passes=2"),
    "1080p": ("mandelbrot=size=1920x1080:rate=30,noise=alls=10:allf=t", "yuv420p", 300, "--good --cpu-used=4 --target-bitrate=3500 --tile-columns=2 --auto-alt-ref=1 --lag-in-frames=16 --passes=2"),
    "2160p": ("mandelbrot=size=3840x2160:rate=30,noise=alls=10:allf=t", "yuv420p", 120, "--good --cpu-used=5 --target-bitrate=12000 --tile-columns=3 --auto-alt-ref=1 --lag-in-frames=16 --passes=2"),
    "1080p-10bit": ("mandelbrot=size=1920x1080:rate=30,noise=alls=10:allf=t", "yuv420p10le", 150, "--good --cpu-used=4 --profile=2 --bit-depth=10 --input-bit-depth=10 --target-bitrate=4000 --tile-columns=2 --auto-alt-ref=1 --lag-in-frames=16 --passes=2"),
}


def best_of(command, pattern, count=5):
    best = None
    for _ in range(count):
        out = run(command + " 2>&1").stdout.decode()
        value = pattern(out)
        best = value if best is None else min(best, value)
    return best


def vpxdec_ms(ivf, threads, hbd):
    # "303 decoded frames/300 showed frames in 980413 us (305.99 fps)": the time per packet decoded, SIMD build, frames not written.
    def parse(out):
        match = re.search(r"(\d+) decoded frames/\d+ showed frames in (\d+) us", out)
        return int(match.group(2)) / 1000 / int(match.group(1))
    return best_of(f"vpxdec-fast{'-hbd' if hbd else ''} --noblit --summary -t {threads} {ivf}", parse)


def ffmpeg_ms(ivf, threads, frames):
    # -benchmark prints "bench: utime=...s stime=...s rtime=...s"; rtime is the wall time of the whole decode.
    def parse(out):
        return float(re.search(r"rtime=([\d.]+)s", out).group(1)) * 1000 / frames
    return best_of(f"ffmpeg -hide_banner -nostats -benchmark -threads {threads} -c:v vp9 -i {ivf} -f null -", parse)


def large():
    os.makedirs(LARGE, exist_ok=True)
    timing = {}
    cores = os.cpu_count()
    for name, (source, pix_fmt, frames, options) in LARGE_SET.items():
        ivf = os.path.join(LARGE, name + ".ivf")
        y4m = f"{WORK}/{name}.y4m"
        os.makedirs(WORK, exist_ok=True)
        if not os.path.exists(ivf):
            run(f"ffmpeg -v error -y -f lavfi -i \"{source}\" -frames:v {frames} -pix_fmt {pix_fmt} -strict -1 -f yuv4mpegpipe {y4m}")
            run(f"vpxenc -q --codec=vp9 --ivf {options} --threads=8 -o {ivf} {y4m}")
            os.remove(y4m)
            run(f"ffmpeg -v error -y -i {ivf} -c copy {LARGE}/{name}.webm")
        entry, _ = reference(ivf, LARGE, name, blocks=False)
        hbd = "10bit" in name
        timing[name] = {"packets": entry["packets"], "cores": cores,
                        "vpxdec_one_thread_ms": vpxdec_ms(ivf, 1, hbd), "vpxdec_all_cores_ms": vpxdec_ms(ivf, cores, hbd),
                        "ffmpeg_one_thread_ms": ffmpeg_ms(ivf, 1, entry["packets"]), "ffmpeg_all_cores_ms": ffmpeg_ms(ivf, cores, entry["packets"])}
        print(f"{name}: {timing[name]}")
    with open(os.path.join(LARGE, "timing.json"), "w", newline="\n") as f:
        json.dump(timing, f, indent=1)
        f.write("\n")


def fetch_vectors():
    os.makedirs(VECTORS, exist_ok=True)
    sums = {}
    env = dict(os.environ, MSYS_NO_PATHCONV="1")
    listing = subprocess.run(["docker", "run", "--rm", IMAGE, "cat", "/libvpx-src/test/test-data.sha1"], check=True,
                             capture_output=True, env=env).stdout.decode()
    for line in listing.splitlines():
        digest, _, file = line.partition(" *")
        sums[file.strip()] = digest
    for name in VECTOR_NAMES:
        for file in (name, name + ".md5"):
            path = os.path.join(VECTORS, file)
            if os.path.exists(path):
                continue
            try:
                with urllib.request.urlopen(BUCKET + file) as response:
                    data = response.read()
            except urllib.error.HTTPError as e:
                print(f"{file}: {e.code}, skipped")
                continue
            if file in sums and hashlib.sha1(data).hexdigest() != sums[file]:
                raise ValueError(f"{file}: SHA-1 does not match libvpx's test/test-data.sha1")
            with open(path, "wb") as f:
                f.write(data)
            print(f"{file}: {len(data)} bytes")


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "--inside":
        if "--large" in sys.argv:
            large()
        elif "--vectors" in sys.argv:
            inside_vectors()
        else:
            inside_vectors()
            inside("--references" in sys.argv)
        return

    if "--large" not in sys.argv:
        fetch_vectors()
    repo = os.getcwd()
    args = [a for a in sys.argv[1:] if a in ("--vectors", "--references", "--large")]
    env = dict(os.environ, MSYS_NO_PATHCONV="1")
    subprocess.run(["docker", "run", "--rm", "-v", f"{repo}:/work", "-w", "/work", IMAGE, "python3", "tools/video/make-vp9-corpus.py",
                    "--inside", *args], check=True, env=env)


if __name__ == "__main__":
    main()
