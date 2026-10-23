"""Checks the subtitle tracks Egami.Video writes (Video V1.2 tasks 8-10): SubtitleTrackTests.WritesFilesForTheToolCheck writes
Matroska, WebM and MP4 files with SRT, WebVTT, ASS, mov_text and wvtt tracks into .cache/video/subtitle-tracks, then FFmpeg
(ffprobe), MKVToolNix (mkvmerge -J) and GPAC MP4Box (-info) read them. A file passes when the tools print no warning or error
and ffprobe finds every cue at its start and end (MP4 tracks also have gap samples, which have no text and are left out).

Usage, from the repo root:
  docker build -t egami-video-tools tools/video/docker
  docker build -t egami-video-mp4:4.3 tools/video/docker-mp4
  python tools/video/check-subtitle-tracks.py           in Docker
  python tools/video/check-subtitle-tracks.py --local   with ffprobe, mkvmerge and MP4Box on PATH
"""
import json
import os
import subprocess
import sys

OUT = os.path.abspath(".cache/video/subtitle-tracks")

# FFmpeg 6.1 knows neither Matroska's S_TEXT/WEBVTT nor MP4's wvtt, for files from MKVToolNix and MP4Box too: it lists the track
# with an unknown codec and reads no packets. For those MKVToolNix and MP4Box are the check.
NO_FFMPEG = ("S_TEXT_WEBVTT", "S_WVTT")
LOCAL = "--local" in sys.argv


def run(image, args):
    command = args if LOCAL else ["docker", "run", "--rm", "-v", f"{OUT}:/work", "-w", "/work", image] + args
    return subprocess.run(command, cwd=OUT, capture_output=True, text=True)


def cues_found(name):
    """Returns the (start, end) of each packet ffprobe reads that holds text, in milliseconds."""
    result = run("egami-video-mp4:4.3", ["ffprobe", "-v", "warning", "-show_packets", "-show_data", "-of", "json", name])
    packets = json.loads(result.stdout or "{}").get("packets", [])
    found = []
    for p in packets:
        size = int(p.get("size", 0))
        data = p.get("data", "")
        # mov_text gaps are 2 bytes of zero length; wvtt gaps are an 8-byte vtte box.
        if size == 0 or (name.endswith(".mp4") and (size <= 2 or "vtte" in data)):
            continue
        start = round(float(p["pts_time"]) * 1000)
        found.append((start, start + round(float(p.get("duration_time", 0)) * 1000)))
    return found, result.stderr.strip()


def main():
    os.makedirs(OUT, exist_ok=True)
    for f in os.listdir(OUT):
        os.remove(os.path.join(OUT, f))
    env = dict(os.environ, EGAMI_SUBTITLE_OUT=OUT)
    subprocess.run(["dotnet", "test", "tests/Egami.Video.Tests", "-c", "Release", "--filter", "FullyQualifiedName~WritesFilesForTheToolCheck"],
                   check=True, env=env, stdout=subprocess.DEVNULL)
    failed = 0
    for name in sorted(f for f in os.listdir(OUT) if not f.endswith(".cues")):
        problems = []
        expected = [tuple(int(v) for v in line.split()) for line in open(os.path.join(OUT, name + ".cues")) if line.strip()]
        found, warnings = cues_found(name)
        if any(k in name for k in NO_FFMPEG):
            found, warnings = None, ""
        if warnings:
            problems.append("ffprobe: " + warnings)
        if name.endswith(".mp4"):
            # MP4 samples cannot overlap: overlapping cues are split, so check every cue edge is a sample edge.
            edges = {t for c in found or [] for t in c}
            missing = [c for c in expected if c[0] not in edges or c[1] not in edges]
            if found is not None and missing:
                problems.append(f"ffprobe has no sample edges at {missing}")
            info = run("egami-video-mp4:4.3", ["MP4Box", "-info", name])
            bad = [l for l in (info.stdout + info.stderr).splitlines() if any(w in l.lower() for w in ("error", "warning", "corrupt", "invalid"))]
            if info.returncode != 0 or bad:
                problems.append("MP4Box: " + "; ".join(bad or [f"exit {info.returncode}"]))
        else:
            if found is not None and found != expected:
                problems.append(f"ffprobe found {found}, expected {expected}")
            identify = run("egami-video-tools", ["mkvmerge", "-J", name])
            report = json.loads(identify.stdout or "{}")
            if identify.returncode != 0 or report.get("errors") or report.get("warnings"):
                problems.append(f"mkvmerge: {report.get('errors')} {report.get('warnings')}")
            elif not any(t.get("type") == "subtitles" for t in report.get("tracks", [])):
                problems.append("mkvmerge finds no subtitle track")
        status = "ok" if not problems else "FAILED"
        failed += bool(problems)
        print(f"{status:6} {name}" + "".join(f"\n       {p}" for p in problems))
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
