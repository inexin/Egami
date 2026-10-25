"""Makes the V9 Editing stage A references: small test clips and FFmpeg's output of the matching edits, in
tests/video/corpus/editing (checked in), with manifest.json. FFmpeg runs only as a black-box tool in Egami's audio reference
image (it has FFmpeg); its source is never read, only its filter documentation.

Usage, from the repo root:
  docker build -t egami-audioref:v5.0 tools/video/audioref
  python tools/video/make-edit-references.py           FFmpeg in Docker
  python tools/video/make-edit-references.py --local   FFmpeg from the PATH

Clips are raw BGR24 video with PCM audio in AVI, so no codec loss or YUV conversion gets in the way, and the filters run in RGB. Each picture reference is FFmpeg's
output as raw RGB24 frames, gzipped (<name>.rgb.gz); manifest.json gives its size and frame count. Joins only compare frame
counts, so they store counts alone. EditReferenceTests renders the same edits with Egami.Video and checks them.
"""
import gzip
import json
import os
import subprocess
import sys

IMAGE = "egami-audioref:v5.0"
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, "tests", "video", "corpus", "editing")
LOCAL = "--local" in sys.argv
W, H, RATE = 96, 64, 10

# name: (video source, audio source or None)
CLIPS = {
    "e1": ("testsrc2=s=96x64:r=10:d=2", "sine=frequency=440:sample_rate=48000:duration=2"),
    "e2": ("smptebars=s=96x64:r=10:d=2", "sine=frequency=660:sample_rate=48000:duration=2"),
    "e3": ("testsrc=s=128x72:r=25:d=1.5", "sine=frequency=880:sample_rate=44100:duration=1.5"),
    "e4": ("mandelbrot=s=80x80:r=15", None),
    "e5": ("rgbtestsrc=s=64x96:r=30:d=1.2", "sine=frequency=300:sample_rate=32000:duration=1.2"),
    "logo": ("testsrc2=s=32x24:r=10:d=2", None),
}

# Joins: the clips in order; FFmpeg fits each to the canvas (scale, pad, fps) and concatenates them.
JOINS = [["e1", "e2"], ["e1", "e3"], ["e3", "e1"], ["e1", "e4"], ["e4", "e3"], ["e1", "e5"], ["e5", "e2", "e3"],
         ["e3", "e4", "e5"], ["e1", "e2", "e3", "e4"], ["e5", "e4", "e3", "e2", "e1"]]

# Pictures: name -> (inputs, filter graph ending in [out]).
PICTURES = {
    "fade": (["e1"], "[0:v]fade=t=in:st=0:d=0.5,fade=t=out:st=1.5:d=0.5[out]"),
    "xfade-fade": (["e1", "e2"], "[0:v]format=gbrp[a];[1:v]format=gbrp[b];[a][b]xfade=transition=fade:duration=0.5:offset=1.5[out]"),
    "xfade-wipeleft": (["e1", "e2"], "[0:v]format=gbrp[a];[1:v]format=gbrp[b];[a][b]xfade=transition=wipeleft:duration=0.5:offset=1.5[out]"),
    "xfade-wiperight": (["e1", "e2"], "[0:v]format=gbrp[a];[1:v]format=gbrp[b];[a][b]xfade=transition=wiperight:duration=0.5:offset=1.5[out]"),
    "xfade-wipeup": (["e1", "e2"], "[0:v]format=gbrp[a];[1:v]format=gbrp[b];[a][b]xfade=transition=wipeup:duration=0.5:offset=1.5[out]"),
    "xfade-wipedown": (["e1", "e2"], "[0:v]format=gbrp[a];[1:v]format=gbrp[b];[a][b]xfade=transition=wipedown:duration=0.5:offset=1.5[out]"),
    "overlay": (["e1", "logo"], "[0:v][1:v]overlay=x=10:y=5:format=rgb[out]"),
    "hstack": (["e1", "e2"], "[0:v][1:v]hstack=inputs=2[out]"),
    "vstack": (["e1", "e2"], "[0:v][1:v]vstack=inputs=2[out]"),
    "xstack": (["e1", "e2", "e2", "e1"], "[0:v][1:v][2:v][3:v]xstack=inputs=4:grid=2x2[out]"),
}


def ffmpeg(arguments, capture=False):
    command = f"ffmpeg -v error -y {arguments}"
    if LOCAL:
        return subprocess.run(command, shell=True, check=True, cwd=OUT, capture_output=capture).stdout
    return subprocess.run(["docker", "run", "--rm", "-v", f"{OUT}:/work", "-w", "/work", IMAGE, "sh", "-c", command],
                          check=True, capture_output=capture).stdout


def main():
    os.makedirs(OUT, exist_ok=True)
    for name, (video, audio) in CLIPS.items():
        length = "-t 1 " if "mandelbrot" in video else ""
        sources = f"-f lavfi {length}-i \"{video}\"" + (f" -f lavfi -i \"{audio}\" -map 0:v -map 1:a -c:a pcm_s16le" if audio else "")
        ffmpeg(f"{sources} -c:v rawvideo -pix_fmt bgr24 {name}.avi")

    manifest = {"tool": "FFmpeg (run as a tool only)", "canvas": [W, H, RATE], "clips": {k: v[0] for k, v in CLIPS.items()}, "joins": [], "pictures": {}}
    for clips in JOINS:
        inputs = " ".join(f"-i {c}.avi" for c in clips)
        fit = "".join(f"[{i}:v]scale={W}:{H}:force_original_aspect_ratio=decrease,pad={W}:{H}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={RATE}[v{i}];"
                      for i in range(len(clips)))
        joined = "".join(f"[v{i}]" for i in range(len(clips)))
        raw = ffmpeg(f"{inputs} -filter_complex \"{fit}{joined}concat=n={len(clips)}:v=1:a=0[out]\" -map \"[out]\" -f rawvideo -pix_fmt rgb24 -", capture=True)
        manifest["joins"].append({"clips": clips, "frames": len(raw) // (W * H * 3)})

    for name, (inputs, graph) in PICTURES.items():
        sources = " ".join(f"-i {c}.avi" for c in inputs)
        raw = ffmpeg(f"{sources} -filter_complex \"{graph}\" -map \"[out]\" -f rawvideo -pix_fmt rgb24 -", capture=True)
        probe = ffmpeg(f"{sources} -filter_complex \"{graph}\" -map \"[out]\" -frames:v 1 -f image2pipe -c:v ppm -", capture=True)
        width, height = (int(v) for v in probe.split(b"\n")[1].split())
        with open(os.path.join(OUT, f"{name}.rgb.gz"), "wb") as f:
            f.write(gzip.compress(raw, compresslevel=9, mtime=0))
        manifest["pictures"][name] = {"inputs": inputs, "filter": graph, "width": width, "height": height, "frames": len(raw) // (width * height * 3)}

    with open(os.path.join(OUT, "manifest.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")
    print(f"wrote {len(CLIPS)} clips, {len(JOINS)} joins and {len(PICTURES)} pictures to {OUT}")


if __name__ == "__main__":
    main()
