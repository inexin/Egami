"""Makes the AV1 container corpus for V4.2 from our own AV1 clips (tests/video/corpus/av1/*.ivf, encoded by libaom's aomenc in
tools/avifref/av1corpus.py), with tools run in Docker as black boxes: FFmpeg, ffprobe and mkvmerge (egami-video-tools, see
tools/video/docker) and libaom 3.15.2's aomdec and dav1d 1.5.4 (egami-avifref:2.9.2, see tools/avifref).

Outputs, committed, next to each clip in tests/video/corpus/av1:
  <clip>.frames.json        libaom's MD5 of every output frame, copied from .cache/avif/reference/av1/<clip>.ivf.json (made by
                            tools/make-av1-references.py): {"frames": [[width, height, bit depth, monochrome, md5, md5 without
                            film grain], ...]}, the MD5 over the planes Y, U and V cropped to the frame as 16-bit little-endian samples.
  <clip>.webm, <clip>.mkv   the IVF clip's packets remuxed by FFmpeg (-c copy) into WebM, and for some clips into Matroska, with
  <file>.json               ffprobe's streams and packets (as tools/video/make-corpus.py writes them).

Usage, from the repo root with Docker running, after python tools/make-av1-references.py:
  python tools/video/make-av1-corpus.py
"""
import json
import os
import shutil
import subprocess
import sys

TOOLS = "egami-video-tools"
CLIPS = "tests/video/corpus/av1"
REFERENCES = ".cache/avif/reference/av1"

# Clips also remuxed into Matroska (every clip goes into WebM).
MKV = {"tools-8bit", "tools-10bit", "tools-12bit", "monochrome", "forward-keyframes", "resize", "yuv444"}

PACKET_FIELDS = ["stream_index", "pts", "dts", "duration", "size", "pos", "flags"]
STREAM_FIELDS = ["index", "codec_type", "codec_name", "codec_tag_string", "profile", "width", "height", "pix_fmt", "color_range",
                 "color_space", "color_transfer", "color_primaries", "chroma_location", "r_frame_rate", "avg_frame_rate",
                 "time_base", "nb_frames", "duration_ts", "side_data_list"]


def run(cmd):
    return subprocess.run(cmd, shell=True, check=True, capture_output=True)


def probe(path, extra=""):
    data = json.loads(run(f"ffprobe -v error {extra} -show_streams -show_format -show_packets -of json {path}").stdout)
    trimmed = {
        "format": {k: data["format"].get(k) for k in ["format_name", "duration", "nb_streams"]},
        "streams": [{k: s[k] for k in STREAM_FIELDS if k in s} for s in data["streams"]],
        "packets": [{k: p[k] for k in PACKET_FIELDS if k in p} for p in data.get("packets", [])],
    }
    with open(path + ".json", "w", encoding="utf-8", newline="\n") as f:
        f.write('{"format":' + json.dumps(trimmed["format"]) + ',\n"streams":[\n')
        f.write(",\n".join(json.dumps(s) for s in trimmed["streams"]) + '],\n"packets":[\n')
        f.write(",\n".join(json.dumps(p) for p in trimmed["packets"]) + "]}\n")


def clips():
    return sorted(f[:-4] for f in os.listdir(CLIPS) if f.endswith(".ivf"))


def inside():
    for name in clips():
        run(f"ffmpeg -v error -y -i {CLIPS}/{name}.ivf -c copy {CLIPS}/{name}.webm")
        probe(f"{CLIPS}/{name}.webm")
        if name in MKV:
            run(f"ffmpeg -v error -y -i {CLIPS}/{name}.ivf -c copy {CLIPS}/{name}.mkv")
            probe(f"{CLIPS}/{name}.mkv")
        print(name)


def copy_references():
    for name in clips():
        source = os.path.join(REFERENCES, name + ".ivf.json")
        if not os.path.isfile(source):
            sys.exit(f"{source} is missing: run python tools/make-av1-references.py first.")
        with open(source, encoding="utf-8") as f:
            rows = json.load(f)["frames"]
        with open(os.path.join(CLIPS, name + ".frames.json"), "w", encoding="utf-8", newline="\n") as f:
            f.write('{"frames": [\n' + ",\n".join(json.dumps(r) for r in rows) + "]}\n")


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "--inside":
        inside()
        return

    copy_references()
    env = dict(os.environ, MSYS_NO_PATHCONV="1")
    subprocess.run(["docker", "run", "--rm", "-v", f"{os.getcwd()}:/work", "-w", "/work", TOOLS, "python3", "tools/video/make-av1-corpus.py",
                    "--inside"], check=True, env=env)


if __name__ == "__main__":
    main()
