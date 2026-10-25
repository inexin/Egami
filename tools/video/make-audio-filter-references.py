"""Makes the V9.1 audio filter references: short test signals and FFmpeg's output of the matching filters, as 24-bit FLAC in
tests/video/corpus/audio/filters (checked in), with manifest.json naming the input and the FFmpeg filter of each reference.
FFmpeg runs only as a black-box tool in Egami's audio reference image; its source is never read.

Usage, from the repo root:
  docker build -t egami-audioref:v5.0 tools/video/audioref
  python tools/video/make-audio-filter-references.py           FFmpeg in Docker
  python tools/video/make-audio-filter-references.py --local   FFmpeg from the PATH

Each filter runs on F32 planar samples (aformat=fltp first), the format Egami.Video's filters use, so the references match to
rounding. AudioFilterReferenceTests applies Egami.Video's filter to the same input and checks the difference is under -90 dBFS RMS.
"""
import json
import os
import subprocess
import sys

IMAGE = "egami-audioref:v5.0"
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, "tests", "video", "corpus", "audio", "filters")
LOCAL = "--local" in sys.argv

# name: lavfi source. Stereo tones with a little high content, and a mono "voice" of two tones with a slow tremolo.
INPUTS = {
    "tone": "aevalsrc='0.5*sin(2*PI*440*t)+0.1*sin(2*PI*3100*t)|0.4*sin(2*PI*997*t)+0.05*sin(2*PI*7000*t)':s=48000:d=0.8",
    "tone2": "aevalsrc='0.3*sin(2*PI*660*t)|0.35*sin(2*PI*1320*t+1)':s=48000:d=0.6",
    "voice": "aevalsrc='(0.4+0.2*sin(2*PI*3*t))*(sin(2*PI*220*t)+0.3*sin(2*PI*1760*t))/1.3':s=48000:d=0.8:c=mono",
}

# name: (inputs, filter). One input runs through -af, two through -filter_complex.
REFERENCES = {
    "gain-minus6db": (["tone"], "volume=-6dB"),
    "gain-half": (["tone"], "volume=0.5"),
    "fade-in-linear": (["tone"], "afade=t=in:st=0.1:d=0.3:curve=tri"),
    "fade-out-equalpower": (["tone"], "afade=t=out:st=0.4:d=0.3:curve=qsin"),
    "fade-in-log": (["tone"], "afade=t=in:st=0.1:d=0.3:curve=log"),
    "fade-in-exp": (["tone"], "afade=t=in:st=0.1:d=0.3:curve=exp"),
    "fade-out-scurve": (["tone"], "afade=t=out:st=0.2:d=0.5:curve=hsin"),
    "crossfade-equalpower": (["tone", "tone2"], "acrossfade=d=0.2:c1=qsin:c2=qsin"),
    "crossfade-linear": (["tone", "tone2"], "acrossfade=d=0.25:c1=tri:c2=tri"),
    "pan-mono-centre": (["voice"], "pan=stereo|c0=0.70710678*c0|c1=0.70710678*c0"),
    "mix-weighted": (["tone", "tone2"], "amix=inputs=2:weights=1 0.5:normalize=0:duration=longest"),
    "delay-per-channel": (["tone"], "adelay=delays=4800S|12000S"),
}


def ffmpeg(arguments):
    if LOCAL:
        subprocess.run(f"ffmpeg -v error -y {arguments}", shell=True, check=True, cwd=OUT)
    else:
        subprocess.run(["docker", "run", "--rm", "-v", f"{OUT}:/work", "-w", "/work", IMAGE, "sh", "-c", f"ffmpeg -v error -y {arguments}"],
                       check=True)


def main():
    os.makedirs(OUT, exist_ok=True)
    flac = "-c:a flac -sample_fmt s32 -compression_level 8"
    for name, source in INPUTS.items():
        ffmpeg(f"-f lavfi -i \"{source}\" {flac} {name}.flac")

    manifest = {"tool": "FFmpeg (run as a tool only)", "inputs": INPUTS, "references": {}}
    for name, (inputs, graph) in REFERENCES.items():
        sources = " ".join(f"-i {i}.flac" for i in inputs)
        if len(inputs) == 1:
            ffmpeg(f"{sources} -af \"aformat=sample_fmts=fltp,{graph}\" {flac} {name}.flac")
        else:
            pads = "".join(f"[{i}:a]aformat=sample_fmts=fltp[i{i}];" for i in range(len(inputs)))
            joined = "".join(f"[i{i}]" for i in range(len(inputs)))
            ffmpeg(f"{sources} -filter_complex \"{pads}{joined}{graph}\" {flac} {name}.flac")
        manifest["references"][name] = {"inputs": inputs, "filter": graph}

    with open(os.path.join(OUT, "manifest.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")
    print(f"wrote {len(INPUTS)} inputs and {len(REFERENCES)} references to {OUT}")


if __name__ == "__main__":
    main()
