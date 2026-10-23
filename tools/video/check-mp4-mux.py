"""Checks the MP4 files Egami.Video writes (V4.1 and V4.3 mux gates): the writer tests remux every clip of
tests/video/corpus/mp4 five ways (moov first in place, moov first from a temporary file, moov at the end, and fragmented to a
seekable and to a forward-only stream) into .cache/video/mp4-mux, then FFmpeg (ffprobe and a stream copy), GPAC MP4Box (-info)
and Bento4 mp4info read each one in Docker. A file passes when they print no warning or error that they do not also print for
the clip it was made from (FFmpeg warns about its own chapter, timecode and cover art tracks). Fragmented files must also give
ffprobe's packet list of their source: each stream's sizes, timestamps and keyframe flags.

Usage, from the repo root:
  docker build -t egami-video-mp4:4.3 tools/video/docker-mp4
  python tools/video/check-mp4-mux.py
"""
import json
import os
import re
import subprocess
import sys

IMAGE = "egami-video-mp4:4.3"
OUT = os.path.abspath(".cache/video/mp4-mux")
CORPUS = os.path.abspath("tests/video/corpus/mp4")

# What FFmpeg prints for every QuickTime chapter text track, its own included (as for mp4-metadata.mp4), whose samples it does
# not decode; we write that track (V4.1 Design Q8) for sources that had only Nero chapters too.
KNOWN = [re.compile(r"Unsupported codec with id 98314 for input stream \d+")]


def complaints(folder, files):
    """Runs ffprobe, a stream copy and MP4Box -info on each file; returns its warnings, without the addresses FFmpeg prints."""
    script = ("for f in " + " ".join(f"'{f}'" for f in files) + "; do "
              "ffprobe -v warning -show_packets -of compact \"$f\" 2>&1 >/dev/null | sed \"s/^/$f|/\"; "
              "ffmpeg -v warning -nostdin -i \"$f\" -map 0 -c copy -f null - 2>&1 | sed \"s/^/$f|/\"; "
              "MP4Box -info \"$f\" 2>&1 | grep -iE 'error|warning|corrupt|invalid' | sed \"s/^/$f|/\"; "
              "mp4info \"$f\" 2>&1 >/dev/null | sed \"s/^/$f|/\"; mp4info \"$f\" >/dev/null 2>&1 || echo \"$f|mp4info failed\"; done")
    result = subprocess.run(["docker", "run", "--rm", "-v", f"{folder}:/work", IMAGE, "sh", "-c", script], capture_output=True, text=True)
    found = {}
    for line in (result.stdout + result.stderr).splitlines():
        if "|" in line:
            name, text = line.split("|", 1)
            found.setdefault(name, []).append(re.sub(r" @ 0x[0-9a-f]+", "", text).strip())
    return found


def main():
    os.makedirs(OUT, exist_ok=True)
    env = dict(os.environ, EGAMI_MP4_OUT=OUT)
    subprocess.run(["dotnet", "test", "tests/Egami.Video.Tests", "--filter", "FullyQualifiedName~Mp4WriterTests.ReadWriteRead|FullyQualifiedName~Mp4FragmentWriterTests.ReadWriteRead"],
                   check=True, env=env, stdout=subprocess.DEVNULL)
    files = sorted(f for f in os.listdir(OUT) if f.endswith(".mp4"))
    sources = {os.path.splitext(f)[0]: f for f in os.listdir(CORPUS) if not f.endswith(".json")}
    source_of = {f: sources[f.split(".")[0]] for f in files}
    ours = complaints(OUT, files)
    theirs = complaints(CORPUS, sorted(set(source_of.values())))
    new = [(f, line) for f in files for line in ours.get(f, [])
           if line not in theirs.get(source_of[f], []) and not any(k.fullmatch(line) for k in KNOWN)]
    for f, line in new:
        print(f"{f}: {line}")
    fragmented = [f for f in files if ".fragmented-" in f]
    differ = []
    for f in fragmented:
        theirs_packets = packets(CORPUS, source_of[f])
        ours_packets = packets(OUT, f)
        if theirs_packets != ours_packets:
            differ.append(f)
            print(f"{f}: ffprobe packets differ from {source_of[f]}: {first_difference(theirs_packets, ours_packets)}")
    print(f"{len(files)} files, {len(new)} new complaints; {len(fragmented)} fragmented, {len(differ)} with other packets")
    sys.exit(1 if new or differ else 0)


def packets(folder, name):
    """ffprobe's packets of a file, a list per stream of (size, pts, dts, keyframe), with times in seconds."""
    result = subprocess.run(["docker", "run", "--rm", "-v", f"{folder}:/work", IMAGE, "ffprobe", "-v", "error", "-show_packets",
                             "-show_streams", "-of", "json", name], capture_output=True, text=True, check=True)
    data = json.loads(result.stdout)
    # Chapter text tracks and cover art are not tracks of ours, and fragmented files leave the chapter track out.
    kept = {s["index"]: s for s in data["streams"] if s["codec_type"] in ("video", "audio", "subtitle")
            and not s.get("disposition", {}).get("attached_pic")}
    order = {index: n for n, index in enumerate(sorted(kept))}
    streams = [[] for _ in kept]
    for p in data["packets"]:
        if p["stream_index"] in kept:
            streams[order[p["stream_index"]]].append((p["size"], p.get("pts_time"), p.get("dts_time"), "K" in p["flags"]))
    return streams


def first_difference(a, b):
    if len(a) != len(b):
        return f"{len(b)} streams, {len(a)} before"
    for s, (x, y) in enumerate(zip(a, b)):
        if len(x) != len(y):
            return f"stream {s}: {len(y)} packets, {len(x)} before"
        for i, (p, q) in enumerate(zip(x, y)):
            if p != q:
                return f"stream {s} packet {i}: {q}, before {p}"
    return "?"


if __name__ == "__main__":
    main()
