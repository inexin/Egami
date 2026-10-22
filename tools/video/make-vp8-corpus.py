"""Makes the VP8 test corpus for V2: clips encoded by libvpx, WebM, Matroska and IVF files holding them, WebM with alpha, and the
expected frames, all from tools run in Docker as black boxes. libvpx 1.14.1 comes from Egami's reference image (the sibling Egami
checkout's tools/vp8ref, also used by Egami 2.3.5), whose FFmpeg makes the sources, the WebM remuxes and the alpha clips.

Inputs and outputs:
  tests/video/corpus/vp8/<clip>.ivf, .webm, .mkv   small clips, committed: vpxenc output in IVF, and the same packets remuxed by FFmpeg
                                            (-c copy) into WebM and Matroska. They cover VP8 versions 0 to 3, one to eight token
                                            partitions, golden and hidden alternate reference frames, segmentation, error resilient
                                            coding, frequent key frames and odd sizes.
  tests/video/corpus/vp8/alpha-*.webm              WebM with alpha, written by FFmpeg's libvpx encoder (AlphaMode 1, the alpha frames in
                                            BlockAdditional).
  .cache/video/vp8/vectors/vp80-*.ivf              the libvpx VP8 test vectors, fetched from the WebM project's test data bucket. Their
                                            license is unconfirmed, so they and their references stay in .cache, never committed.
  .cache/video/vp8/large/*.ivf, *.webm             720p and 1080p clips for the speed gate, with libvpx's and FFmpeg's decode times.

References, next to each file:
  <file>.json         ffprobe's streams and packets (as tools/video/make-corpus.py writes them), for the container gates
  <clip>.frames.json  per packet whether it is shown, and per shown frame the MD5 of vpxdec's output (packed I420 at the display size);
                      "ffmpeg" says whether FFmpeg's own VP8 decoder gave the same hashes. Alpha clips instead give the MD5 of each
                      plane (y, u, v, a) as FFmpeg's libvpx decoder writes yuva420p.
  <clip>.dump.gz      what libvpx read from every frame (vp8dump): header fields under RFC 6386 names, then each macroblock

Usage, from the repo root with Docker running and the Egami checkout beside this one:
  docker build -t egami-vp8ref:2.3.5 tools/vp8ref
  python tools/video/make-vp8-corpus.py              vectors (fetched once), clips and references
  python tools/video/make-vp8-corpus.py --references references of the committed clips again, without encoding them anew
  python tools/video/make-vp8-corpus.py --vectors    vectors only
  python tools/video/make-vp8-corpus.py --large      the speed gate's clips and timings
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
CLIPS = "tests/video/corpus/vp8"
VECTORS = ".cache/video/vp8/vectors"
VECTOR_REFS = ".cache/video/vp8/reference"
LARGE = ".cache/video/vp8/large"
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

PATTERN = "-f lavfi -i testsrc2=size={w}x{h}:rate=30"
FRACTAL = "-f lavfi -i mandelbrot=size={w}x{h}:rate=30"

# name: (source, filter, frames, vpxenc options). Every clip is also remuxed to WebM; those marked "mkv" to Matroska too.
CLIP_SET = {
    "v0-altref": (PATTERN.format(w=176, h=144), "null", 40, "--good --passes=2 --auto-alt-ref=1 --lag-in-frames=16 --arnr-maxframes=5 --target-bitrate=250"),
    "v0-partitions-8": (FRACTAL.format(w=176, h=144), "null", 15, "--good --token-parts=3 --target-bitrate=300"),
    "v0-keyframes": (PATTERN.format(w=128, h=96), "null", 30, "--good --kf-max-dist=7 --kf-min-dist=7 --target-bitrate=200"),
    "v0-resilient": (FRACTAL.format(w=144, h=96), "null", 30, "--rt --cpu-used=-6 --error-resilient=1 --target-bitrate=150"),
    "v1-pan": (FRACTAL.format(w=160, h=96), "null", 20, "--good --profile=1 --target-bitrate=200"),
    "v2-pan": (PATTERN.format(w=160, h=96), "null", 20, "--good --profile=2 --target-bitrate=200"),
    "v3-pan": (FRACTAL.format(w=160, h=96), "null", 20, "--good --profile=3 --target-bitrate=200"),
    "v0-odd-97x61": (PATTERN.format(w=98, h=62), "scale=97:61", 20, "--good --passes=2 --auto-alt-ref=1 --lag-in-frames=8 --target-bitrate=120"),
}
MKV = {"v0-altref", "v0-keyframes"}

# Alpha clips: an RGBA pattern whose alpha moves, encoded by FFmpeg's libvpx wrapper (which writes the alpha as a second VP8
# stream in BlockAdditional).
ALPHA_SET = {
    "alpha-ramp": ("testsrc2=size=160x96:rate=25", "format=rgba,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='mod(X+Y+N*6,256)'", 25, "-b:v 300k"),
    "alpha-odd-75x45": ("testsrc2=size=76x46:rate=25", "scale=75:45,format=rgba,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='if(lt(hypot(X-37,Y-22),10+N),255,40)'", 20, "-b:v 150k -g 8"),
}

PACKET_FIELDS = ["stream_index", "pts", "dts", "duration", "size", "pos", "flags"]
STREAM_FIELDS = ["index", "codec_type", "codec_name", "codec_tag_string", "width", "height", "pix_fmt", "r_frame_rate",
                 "avg_frame_rate", "time_base", "nb_frames", "duration_ts"]


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


def split_md5s(raw, sizes, planes=3):
    """Hashes packed frames at their display sizes: whole frames, or each plane when planes is 4 (yuva420p)."""
    out = []
    at = 0
    for w, h in sizes:
        cw, ch = (w + 1) // 2, (h + 1) // 2
        lengths = [w * h, cw * ch, cw * ch] + ([w * h] if planes == 4 else [])
        if planes == 3:
            size = sum(lengths)
            out.append(hashlib.md5(raw[at:at + size]).hexdigest())
            at += size
        else:
            frame = {}
            for name, length in zip("yuva", lengths):
                frame[name] = hashlib.md5(raw[at:at + length]).hexdigest()
                at += length
            out.append(frame)
    if at != len(raw):
        raise ValueError(f"{len(raw)} bytes of frames, {at} expected")
    return out


def write_dump(path, dump):
    # No name and a zero time in the gzip header, so regenerating gives identical files.
    with open(path, "wb") as f:
        with gzip.GzipFile(fileobj=f, mode="wb", filename="", mtime=0) as gz:
            gz.write(dump.encode())


def reference(ivf, target, name):
    """vp8dump and vpxdec on one IVF file: the frames must agree; FFmpeg's native decoder is checked against them."""
    os.makedirs(WORK, exist_ok=True)
    dump = run(f"vp8dump {ivf} {WORK}/dump.yuv").stdout.decode()
    run(f"vpxdec --i420 --rawvideo -o {WORK}/vpxdec.yuv {ivf}")
    with open(f"{WORK}/dump.yuv", "rb") as f:
        frames = f.read()
    with open(f"{WORK}/vpxdec.yuv", "rb") as f:
        if f.read() != frames:
            raise ValueError(f"{name}: vpxdec and vp8dump disagree")

    packets, sizes = [], []
    for line in dump.splitlines():
        parts = line.split()
        if parts[0] == "frame":
            packets.append({"shown": False, "error": "error" in parts})
        elif parts[0] == "shown":
            packets[-1]["shown"] = True
            sizes.append(tuple(map(int, parts[2].split("x"))))
    md5 = split_md5s(frames, sizes)

    # FFmpeg's native VP8 decoder, as the design's cross-check: every frame passed through as decoded (none dropped or repeated for a
    # constant rate); only clean files of one size are compared.
    ffmpeg = None
    if not any(p["error"] for p in packets) and len(set(sizes)) <= 1:
        run(f"ffmpeg -v error -y -c:v vp8 -i {ivf} -fps_mode passthrough -f rawvideo -pix_fmt yuv420p {WORK}/ffmpeg.yuv")
        with open(f"{WORK}/ffmpeg.yuv", "rb") as f:
            ffmpeg = f.read() == frames

    entry = {
        "width": sizes[0][0] if sizes else 0,
        "height": sizes[0][1] if sizes else 0,
        "packets": len(packets),
        "shown": [i for i, p in enumerate(packets) if p["shown"]],
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
    return entry


def alpha_reference(webm, name):
    run(f"ffmpeg -v error -y -c:v libvpx -i {webm} -fps_mode passthrough -f rawvideo -pix_fmt yuva420p {WORK}/alpha.yuv")
    info = json.loads(run(f"ffprobe -v error -show_streams -of json {webm}").stdout)["streams"][0]
    with open(f"{WORK}/alpha.yuv", "rb") as f:
        raw = f.read()
    w, h = info["width"], info["height"]
    count = len(raw) // (2 * w * h + 2 * ((w + 1) // 2) * ((h + 1) // 2))
    entry = {"width": w, "height": h, "md5": split_md5s(raw, [(w, h)] * count, planes=4)}
    with open(os.path.join(CLIPS, name + ".frames.json"), "w", newline="\n") as f:
        json.dump(entry, f, indent=1)
        f.write("\n")
    return entry


def make_clip(name, source, vf, frames, options):
    os.makedirs(WORK, exist_ok=True)
    y4m = f"{WORK}/{name}.y4m"
    run(f"ffmpeg -v error -y {source} -vf \"{vf}\" -frames:v {frames} -pix_fmt yuv420p -f yuv4mpegpipe {y4m}")
    run(f"vpxenc -q --codec=vp8 --ivf {options} --threads=1 -o {CLIPS}/{name}.ivf {y4m}")


def remux(name):
    run(f"ffmpeg -v error -y -i {CLIPS}/{name}.ivf -c copy {CLIPS}/{name}.webm")
    probe(f"{CLIPS}/{name}.webm")
    if name in MKV:
        run(f"ffmpeg -v error -y -i {CLIPS}/{name}.ivf -c copy {CLIPS}/{name}.mkv")
        probe(f"{CLIPS}/{name}.mkv")


def inside(vectors_only, keep_clips):
    os.makedirs(VECTOR_REFS, exist_ok=True)
    count = 0
    for file in sorted(os.listdir(VECTORS)):
        if file.endswith(".ivf"):
            reference(os.path.join(VECTORS, file), VECTOR_REFS, file[:-4])
            probe(os.path.join(VECTORS, file))
            count += 1
    print(f"vectors: {count} files")
    if vectors_only:
        return

    os.makedirs(CLIPS, exist_ok=True)
    manifest = {"tools": "libvpx 1.14.1 vpxenc and vpxdec (generic C build), FFmpeg " + run("ffmpeg -version").stdout.decode().split()[2],
                "clips": {}}
    for name, (source, vf, frames, options) in CLIP_SET.items():
        if not keep_clips:
            make_clip(name, source, vf, frames, options)
            remux(name)
        probe(f"{CLIPS}/{name}.ivf")
        entry = reference(f"{CLIPS}/{name}.ivf", CLIPS, name)
        with gzip.open(os.path.join(CLIPS, name + ".dump.gz"), "rt") as f:
            text = f.read()
        features = {
            "segmentation": "segmentation_enabled = 1" in text,
            "golden": "refresh_golden_frame = 1" in text or "copy_buffer_to_golden = 1" in text,
            "hidden": len(entry["shown"]) < entry["packets"],
            "no_entropy_carry": "refresh_entropy_probs = 0" in text,
        }
        manifest["clips"][name] = {"source": source.split("=")[0].split()[-1], "filter": vf, "frames": frames, "vpxenc": options,
                                   "size": f"{entry['width']}x{entry['height']}", "packets": entry["packets"],
                                   "shown": len(entry["shown"]), "ffmpeg_agrees": entry["ffmpeg"], **features}
        print(f"{name}: {entry['width']}x{entry['height']}, {entry['packets']} packets, {len(entry['shown'])} shown, {features}")
    for name, (source, vf, frames, options) in ALPHA_SET.items():
        out = f"{CLIPS}/{name}.webm"
        if not keep_clips:
            run(f"ffmpeg -v error -y -f lavfi -i {source} -vf \"{vf}\" -frames:v {frames} -c:v libvpx -pix_fmt yuva420p -auto-alt-ref 0 {options} {out}")
        probe(out)
        entry = alpha_reference(out, name)
        manifest["clips"][name] = {"source": source, "filter": vf, "frames": frames, "ffmpeg": options, "size": f"{entry['width']}x{entry['height']}"}
        print(f"{name}: {entry['width']}x{entry['height']}, {len(entry['md5'])} frames with alpha")
    with open(os.path.join(CLIPS, "manifest.json"), "w", newline="\n") as f:
        json.dump(manifest, f, indent=1)
        f.write("\n")


LARGE_SET = {
    # 10 seconds at 30 frames a second of a moving fractal with film grain, so the encoder spends its whole bit budget as on camera
    # footage; 8 token partitions so libvpx's threads have rows to share.
    "720p": ("mandelbrot=size=1280x720:rate=30,noise=alls=10:allf=t", 300, "--good --cpu-used=4 --target-bitrate=2500 --token-parts=3 --auto-alt-ref=1 --lag-in-frames=16 --passes=2"),
    "1080p": ("mandelbrot=size=1920x1080:rate=30,noise=alls=10:allf=t", 300, "--good --cpu-used=4 --target-bitrate=5000 --token-parts=3 --auto-alt-ref=1 --lag-in-frames=16 --passes=2"),
}


def best_of(command, pattern, count=5):
    best = None
    for _ in range(count):
        out = run(command + " 2>&1").stdout.decode()
        value = pattern(out)
        best = value if best is None else min(best, value)
    return best


def vpxdec_ms(ivf, threads):
    # "303 decoded frames/300 showed frames in 980413 us (305.99 fps)": the time per packet decoded, SIMD build, frames not written.
    def parse(out):
        match = re.search(r"(\d+) decoded frames/\d+ showed frames in (\d+) us", out)
        return int(match.group(2)) / 1000 / int(match.group(1))
    return best_of(f"vpxdec-fast --noblit --summary -t {threads} {ivf}", parse)


def ffmpeg_ms(ivf, threads, frames):
    # -benchmark prints "bench: utime=...s stime=...s rtime=...s"; rtime is the wall time of the whole decode.
    def parse(out):
        return float(re.search(r"rtime=([\d.]+)s", out).group(1)) * 1000 / frames
    return best_of(f"ffmpeg -hide_banner -nostats -benchmark -threads {threads} -c:v vp8 -i {ivf} -f null -", parse)


def large():
    os.makedirs(LARGE, exist_ok=True)
    timing = {}
    cores = os.cpu_count()
    for name, (source, frames, options) in LARGE_SET.items():
        ivf = os.path.join(LARGE, name + ".ivf")
        y4m = f"{WORK}/{name}.y4m"
        os.makedirs(WORK, exist_ok=True)
        run(f"ffmpeg -v error -y -f lavfi -i \"{source}\" -frames:v {frames} -pix_fmt yuv420p -f yuv4mpegpipe {y4m}")
        run(f"vpxenc -q --codec=vp8 --ivf {options} --threads=8 -o {ivf} {y4m}")
        os.remove(y4m)
        run(f"ffmpeg -v error -y -i {ivf} -c copy {LARGE}/{name}.webm")
        entry = reference(ivf, LARGE, name)
        timing[name] = {"packets": entry["packets"], "cores": cores,
                        "vpxdec_one_thread_ms": vpxdec_ms(ivf, 1), "vpxdec_all_cores_ms": vpxdec_ms(ivf, cores),
                        "ffmpeg_one_thread_ms": ffmpeg_ms(ivf, 1, entry["packets"]), "ffmpeg_all_cores_ms": ffmpeg_ms(ivf, cores, entry["packets"])}
        print(f"{name}: {timing[name]}")
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

    if "--large" not in sys.argv:
        fetch_vectors()
    repo = os.getcwd()
    args = [a for a in sys.argv[1:] if a in ("--vectors", "--references", "--large")]
    env = dict(os.environ, MSYS_NO_PATHCONV="1")
    subprocess.run(["docker", "run", "--rm", "-v", f"{repo}:/work", "-w", "/work", IMAGE, "python3", "tools/video/make-vp8-corpus.py",
                    "--inside", *args], check=True, env=env)


if __name__ == "__main__":
    main()
