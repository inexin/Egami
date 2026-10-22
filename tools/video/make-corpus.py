"""Makes the test corpus: short clips with known content, written by FFmpeg and MKVToolNix in Docker, plus an ffprobe dump of
each clip as the expected result. FFmpeg and MKVToolNix run only as black-box tools; their source is never read.

Usage, from the repo root:
  docker build -t egami-video-tools tools/video/docker
  python tools/video/make-corpus.py            small clips into tests/video/corpus (checked in)
  python tools/video/make-corpus.py --probe-only   rewrite the ffprobe dumps of the existing clips
  python tools/video/make-corpus.py --large    also an OpenDML AVI over 1 GB and its MKV remux into .cache/video/corpus-large (not checked in)

Each clip.ext gets clip.ext.json: the streams and every packet as ffprobe reports them, trimmed to the fields the tests check.
"""
import json
import os
import subprocess
import sys

IMAGE = "egami-video-tools"
SRC = "-f lavfi -i testsrc2=size={size}:rate={rate}:duration={seconds}"
SINE = "-f lavfi -i sine=frequency=440:sample_rate={sr}:duration={seconds}"

# name: shell command run in /work (the output folder). {v} and {a} are the lavfi video and audio inputs.
CLIPS = {
    # AVI written by FFmpeg: idx1 index, MJPG video, PCM or MP3 audio.
    "avi-mjpeg-420-pcm.avi": "ffmpeg {v} {a} -c:v mjpeg -pix_fmt yuvj420p -q:v 5 -huffman default -c:a pcm_s16le -shortest {out}",
    "avi-mjpeg-422-mp3-ntsc.avi": "ffmpeg {v} {a} -c:v mjpeg -pix_fmt yuvj422p -q:v 4 -c:a libmp3lame -b:a 64k -ac 2 -shortest {out}",
    "avi-mjpeg-444-noaudio.avi": "ffmpeg {v} -c:v mjpeg -pix_fmt yuvj444p -q:v 3 {out}",
    # Matroska written by FFmpeg: V_MJPEG, SimpleBlocks, Cues at the end.
    "mkv-mjpeg-pcm.mkv": "ffmpeg {v} {a} -c:v mjpeg -pix_fmt yuvj420p -q:v 5 -c:a pcm_s16le -shortest {out}",
    # Live Matroska through a pipe: unknown-size Segment and Clusters, no Cues.
    "mkv-mjpeg-live.mkv": "ffmpeg {v} -c:v mjpeg -pix_fmt yuvj420p -q:v 5 -f matroska -live 1 pipe:1 > {out}",
    # Matroska written by mkvmerge from an AVI: V_MS/VFW/FOURCC compatibility mode, laced audio.
    "mkv-mjpeg-vfw.mkv": "ffmpeg {v} {a} -c:v mjpeg -pix_fmt yuvj420p -q:v 5 -c:a pcm_s16le -shortest /tmp/in.avi && mkvmerge -q -o {out} /tmp/in.avi",
    "mkv-mp3-nocues.mkv": "ffmpeg {v} {a} -c:v mjpeg -q:v 5 -c:a libmp3lame -b:a 48k -shortest /tmp/in.mkv && mkvmerge -q --no-cues -o {out} /tmp/in.mkv",
    "mkv-vorbis-xiph-laced.mkv": "ffmpeg {v} {a} -c:v mjpeg -q:v 5 -c:a libvorbis -q:a 2 -shortest /tmp/in.mkv && mkvmerge -q -o {out} /tmp/in.mkv",
    "mkv-ac3.mkv": "ffmpeg {v} {a} -c:v mjpeg -q:v 5 -c:a ac3 -b:a 96k -shortest /tmp/in.mkv && mkvmerge -q -o {out} /tmp/in.mkv",
    # WebM: VP8 and Opus (packets only in V1).
    "webm-vp8-opus.webm": "ffmpeg {v} {a} -c:v libvpx -b:v 200k -c:a libopus -b:a 32k -shortest {out}",
    # YUV4MPEG2 at several layouts and depths, and uncompressed AVI and Matroska tracks (task 16).
    "y4m-420jpeg.y4m": "ffmpeg {v} -pix_fmt yuv420p -strict -1 {out}",
    "y4m-422.y4m": "ffmpeg {v} -pix_fmt yuv422p -strict -1 {out}",
    "y4m-444p10.y4m": "ffmpeg {v} -pix_fmt yuv444p10le -strict -1 {out}",
    "y4m-420p12.y4m": "ffmpeg {v} -pix_fmt yuv420p12le -strict -1 {out}",
    "y4m-mono.y4m": "ffmpeg {v} -pix_fmt gray -strict -1 {out}",
    "y4m-444alpha.y4m": "ffmpeg {v} -pix_fmt yuva444p -strict -1 {out}",
    "avi-raw-yuy2.avi": "ffmpeg {v} -c:v rawvideo -pix_fmt yuyv422 {out}",
    "avi-raw-bgr24.avi": "ffmpeg {v} -c:v rawvideo -pix_fmt bgr24 {out}",
    "avi-v210.avi": "ffmpeg {v} -c:v v210 {out}",
    "mkv-raw-i420.mkv": "ffmpeg {v} -c:v rawvideo -pix_fmt yuv420p {out}",
    "mkv-raw-uyvy.mkv": "ffmpeg {v} -c:v rawvideo -pix_fmt uyvy422 {out}",
    # Tags, chapters and attachments (task 17): an ffmetadata file gives the chapters, a small JPEG is the cover art.
    "mkv-metadata.mkv": "printf ';FFMETADATA1\ntitle=Corpus clip\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=800\ntitle=Opening\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=800\nEND=2000\ntitle=Rest\n' > /tmp/meta.txt"
                        " && ffmpeg -y -v error -f lavfi -i color=red:size=32x32 -frames:v 1 /tmp/cover.jpg"
                        " && ffmpeg {v} {a} -i /tmp/meta.txt -map 0 -map 1 -map_metadata 2 -map_chapters 2 -c:v mjpeg -q:v 5 -c:a pcm_s16le -shortest"
                        " -metadata artist=Egami -metadata comment='Test clip' -metadata:s:a:0 language=nor -metadata:s:a:0 title=Tone"
                        " -attach /tmp/cover.jpg -metadata:s:t:0 mimetype=image/jpeg -metadata:s:t:0 filename=cover.jpg {out}",
    "avi-tags.avi": "ffmpeg {v} -c:v mjpeg -q:v 5 -metadata title='Corpus clip' -metadata artist=Egami -metadata comment='Test clip' -metadata copyright=MIT {out}",
}

# Uncompressed clips are small pictures and a few frames, so the corpus stays small.
RAW = {"size": "48x32", "rate": "25", "sr": 8000}

SETTINGS = {
    "avi-mjpeg-422-mp3-ntsc.avi": {"size": "176x144", "rate": "30000/1001", "sr": 22050},
    "avi-mjpeg-444-noaudio.avi": {"size": "96x64", "rate": "10", "sr": 8000},
}

PACKET_FIELDS = ["stream_index", "pts", "dts", "duration", "size", "pos", "flags"]
STREAM_FIELDS = ["index", "codec_type", "codec_name", "codec_tag_string", "width", "height", "pix_fmt", "r_frame_rate",
                 "avg_frame_rate", "time_base", "sample_rate", "channels", "bits_per_sample", "nb_frames", "duration_ts"]


def docker(command, outdir):
    subprocess.run(["docker", "run", "--rm", "-v", f"{os.path.abspath(outdir)}:/work", IMAGE, "sh", "-c", command], check=True)


def make(name, command, outdir, seconds=2):
    raw = name.startswith("y4m-") or "-raw-" in name or "v210" in name
    settings = {"size": "160x120", "rate": "25", "sr": 8000, **(RAW if raw else {}), **SETTINGS.get(name, {})}
    seconds = 0.2 if raw else seconds
    v = SRC.format(seconds=seconds, **settings)
    a = SINE.format(seconds=seconds, **settings)
    docker("set -e; " + command.format(v=v, a=a, out=name).replace("ffmpeg ", "ffmpeg -y -v error -nostdin ", 2), outdir)
    probe(name, outdir)
    print(f"{name}: {os.path.getsize(os.path.join(outdir, name))} bytes")


def probe(name, outdir):
    out = subprocess.run(["docker", "run", "--rm", "-v", f"{os.path.abspath(outdir)}:/work", IMAGE, "ffprobe", "-v", "error", "-show_streams",
                          "-show_format", "-show_packets", "-of", "json", name], check=True, capture_output=True, text=True).stdout
    data = json.loads(out)
    trimmed = {
        "format": {k: data["format"].get(k) for k in ["format_name", "duration", "nb_streams"]},
        "streams": [{**{k: s[k] for k in STREAM_FIELDS if k in s}, **({"attached_pic": 1} if s.get("disposition", {}).get("attached_pic") else {})}
                    for s in data["streams"]],
        "packets": [{k: p[k] for k in PACKET_FIELDS if k in p} for p in data.get("packets", [])],
    }
    # One stream or packet per line keeps the dumps small and their diffs readable.
    with open(os.path.join(outdir, name + ".json"), "w", encoding="utf-8", newline="\n") as f:
        f.write('{"format":' + json.dumps(trimmed["format"]) + ',\n"streams":[\n')
        f.write(",\n".join(json.dumps(s) for s in trimmed["streams"]) + '],\n"packets":[\n')
        f.write(",\n".join(json.dumps(p) for p in trimmed["packets"]) + "]}\n")


def main():
    outdir = "tests/video/corpus"
    os.makedirs(outdir, exist_ok=True)
    only = [a for a in sys.argv[1:] if not a.startswith("--")]
    for name, command in CLIPS.items():
        if "--probe-only" in sys.argv:
            probe(name, outdir)
        elif not only or name in only:
            make(name, command, outdir)
    if "--large" in sys.argv:
        large = ".cache/video/corpus-large"
        os.makedirs(large, exist_ok=True)
        # Noise makes every frame about 300 KB, so 4,500 frames pass 1 GB and FFmpeg switches to OpenDML (AVIX lists, indx).
        docker("set -e; ffmpeg -y -v error -nostdin -f lavfi -i nullsrc=size=1280x720:rate=25,geq=random(1)*255:128:128 -t 180 "
               "-f lavfi -i sine=sample_rate=8000:duration=180 -c:v mjpeg -q:v 2 -c:a pcm_s16le -shortest opendml.avi", large)
        probe("opendml.avi", large)
        print(f"opendml.avi: {os.path.getsize(os.path.join(large, 'opendml.avi'))} bytes")


if __name__ == "__main__":
    main()
