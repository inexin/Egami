"""Checks the MP4 files Egami.Video writes (V4.1 mux gate): the writer tests remux every clip of tests/video/corpus/mp4 three
ways (moov first in place, moov first from a temporary file, moov at the end) into .cache/video/mp4-mux, then FFmpeg (ffprobe
and a stream copy) and GPAC MP4Box (-info) read each one in Docker. A file passes when they print no warning or error that
they do not also print for the clip it was made from (FFmpeg warns about its own chapter, timecode and cover art tracks).

Usage, from the repo root:
  docker build -t egami-video-mp4:4.1 tools/video/docker-mp4
  python tools/video/check-mp4-mux.py
"""
import os
import re
import subprocess
import sys

IMAGE = "egami-video-mp4:4.1"
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
              "MP4Box -info \"$f\" 2>&1 | grep -iE 'error|warning|corrupt|invalid' | sed \"s/^/$f|/\"; done")
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
    subprocess.run(["dotnet", "test", "tests/Egami.Video.Tests", "--filter", "FullyQualifiedName~Mp4WriterTests.ReadWriteRead"],
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
    print(f"{len(files)} files, {len(new)} new complaints")
    sys.exit(1 if new else 0)


if __name__ == "__main__":
    main()
