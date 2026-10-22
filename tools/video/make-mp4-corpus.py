"""Makes the MP4 and QuickTime test corpus (V4.1): short clips written by FFmpeg and GPAC MP4Box in Docker, plus an ffprobe
dump of each clip as the expected result. FFmpeg and MP4Box run only as black-box tools; their source is never read.

Usage, from the repo root:
  docker build -t egami-video-mp4:4.1 tools/video/docker-mp4
  python tools/video/make-mp4-corpus.py               all clips into tests/video/corpus/mp4 (checked in)
  python tools/video/make-mp4-corpus.py NAME ...      only the named clips
  python tools/video/make-mp4-corpus.py --probe-only  rewrite the ffprobe dumps of the existing clips

Each clip gets clip.json: the format, its tags and chapters, the streams (with tags, side data and dispositions) and every
packet as ffprobe reports them, trimmed to the fields the tests check.
"""
import json
import os
import subprocess
import sys

IMAGE = "egami-video-mp4:4.1"
OUT = "tests/video/corpus/mp4"

# Inputs: a 160x120 test pattern at 25 fps and a 440 Hz tone, both a second long unless a clip says otherwise.
V = "-f lavfi -i testsrc2=size=160x120:rate=25:duration={s}"
A = "-f lavfi -i sine=frequency=440:sample_rate={sr}:duration={s}"
FF = "ffmpeg -y -v error -nostdin"
# The metadata used by the tag, chapter and cover-art clips.
META = ("printf ';FFMETADATA1\\ntitle=Corpus clip\\nartist=Egami\\ndate=2026\\ncomment=Test clip\\n"
        "[CHAPTER]\\nTIMEBASE=1/1000\\nSTART=0\\nEND=400\\ntitle=Opening\\n"
        "[CHAPTER]\\nTIMEBASE=1/1000\\nSTART=400\\nEND=1000\\ntitle=Rest\\n' > /tmp/meta.txt"
        f" && {FF} -f lavfi -i color=red:size=32x32 -frames:v 1 /tmp/cover.jpg")
SRT = ("printf '1\\n00:00:00,100 --> 00:00:00,500\\nPlain line\\n\\n2\\n00:00:00,500 --> 00:00:00,900\\n<b>Bold</b> and <i>italic</i>\\n\\n"
       "3\\n00:00:00,900 --> 00:00:01,000\\nTwo\\nlines\\n' > /tmp/subs.srt")
H264 = "-c:v libx264 -preset ultrafast -g 12 -bf 2 -pix_fmt yuv420p"

# name: shell command run in /work (the output folder). {v} and {a} are the inputs, {out} the clip.
CLIPS = {
    # QuickTime: Motion JPEG ('jpeg') with sowt, twos and lpcm audio, and the Apple Motion JPEG variants.
    "mov-mjpeg-sowt.mov": "{ff} {v} {a} -c:v mjpeg -q:v 12 -c:a pcm_s16le -shortest {out}",
    "mov-mjpeg-twos.mov": "{ff} {v} {a} -c:v mjpeg -q:v 12 -c:a pcm_s16be -shortest {out}",
    "mov-mjpeg-lpcm24.mov": "{ff} {v} {a} -c:v mjpeg -q:v 12 -c:a pcm_s24le -shortest {out}",
    "mov-mjpa.mov": "{ff} {v} -c:v mjpeg -q:v 12 -tag:v mjpa {out}",
    # QuickTime with a start timecode (tmcd track) and a rotated, mirrored picture (display matrix).
    "mov-tmcd.mov": "{ff} {v} -c:v mjpeg -q:v 12 -timecode 01:00:00:00 {out}",
    "mov-rotate90.mov": "{ff} {v} -c:v mjpeg -q:v 12 /tmp/in.mov && {ff} -display_rotation 90 -i /tmp/in.mov -c copy {out}",
    "mp4-rotate270-hflip.mp4": "{ff} {v} {h264} /tmp/in.mp4 && {ff} -display_rotation 270 -display_hflip -i /tmp/in.mp4 -c copy {out}",
    # ISO MP4 with each codec Egami.Video maps: Motion JPEG (mp4v, object type 0x6C), VP8 (by MP4Box, which FFmpeg cannot write), VP9, AV1, Opus and FLAC.
    "mp4-mjpeg.mp4": "{ff} {v} -c:v mjpeg -q:v 12 {out}",
    "mp4box-vp8.mp4": "{ff} {v} -c:v libvpx -b:v 200k /tmp/in.ivf && MP4Box -quiet -add /tmp/in.ivf -new {out}",
    "mp4-vp9-opus.mp4": "{ff} {v} {a} -c:v libvpx-vp9 -b:v 200k -deadline realtime -cpu-used 8 -color_primaries bt709 -color_trc bt709"
                        " -colorspace bt709 -c:a libopus -b:a 32k -shortest {out}",
    "mp4-av1.mp4": "{ff} {v} -c:v libaom-av1 -cpu-used 8 -b:v 100k -g 12 {out}",
    "mp4-flac.mp4": "{ff} {a} -c:a flac -strict experimental {out}",
    # Pass-through codecs: H.264 with B-frames (ctts and an edit list), HEVC (hvc1), AAC; moov first and last; audio only.
    "mp4-h264-aac.mp4": "{ff} {v} {a} {h264} -c:a aac -b:a 32k -shortest {out}",
    "mp4-h264-faststart.mp4": "{ff} {v} {a} {h264} -c:a aac -b:a 32k -shortest -movflags +faststart {out}",
    "mp4-hevc.mp4": "{ff} {v} -c:v libx265 -preset ultrafast -x265-params log-level=error -tag:v hvc1 {out}",
    "m4a-aac.m4a": "{ff} {a} -c:a aac -b:a 32k {out}",
    # Edit lists: a start trimmed off with stream copy (media_time > 0), and a delayed audio track (an empty edit).
    "mp4-editlist-trim.mp4": "{ff} {v} {a} {h264} -c:a aac -shortest /tmp/in.mp4 && {ff} -ss 0.3 -i /tmp/in.mp4 -c copy {out}",
    "mp4-editlist-delay.mp4": "{ff} {v} -itsoffset 0.2 {a} -map 0 -map 1 -c:v mjpeg -q:v 12 -c:a aac -b:a 32k {out}",
    # Fragmented MP4: FFmpeg's empty moov, CMAF with a global sidx, MP4Box fragments with mfra, and a DASH onDemand file.
    "mp4-frag.mp4": "{ff} {v} {a} {h264} -c:a aac -shortest -movflags frag_keyframe+empty_moov {out}",
    "mp4-cmaf.mp4": "{ff} {v} {h264} -movflags cmaf+global_sidx+frag_keyframe+empty_moov -frag_duration 500000 {out}",
    "mp4box-frag-mfra.mp4": "{ff} {v} {a} {h264} -c:a aac -shortest /tmp/in.mp4 && MP4Box -quiet -frag 500 -mfra /tmp/in.mp4 -out {out}",
    "mp4box-dash.mp4": "{ff} {v} {h264} /tmp/in.mp4 && cd /tmp && MP4Box -quiet -dash 500 -profile onDemand -out /tmp/d.mpd /tmp/in.mp4"
                       " && cp /tmp/in_dashinit.mp4 /work/{out}",
    # MP4Box layouts: 64-bit chunk offsets, compact sample sizes (stz2) and a QuickTime chapter track.
    "mp4box-co64.mp4": "{ff} {v} {a} {h264} -c:a aac -shortest /tmp/in.mp4 && MP4Box -quiet -co64 -add /tmp/in.mp4 -new {out}",
    "mp4box-stz2.mp4": "{ff} {a} -c:a aac -b:a 16k /tmp/in.m4a && MP4Box -quiet -add /tmp/in.m4a:stz2 -new {out}",
    "mp4box-chapqt.mp4": "{ff} {v} {h264} /tmp/in.mp4 && printf '00:00:00.000 Opening\\n00:00:00.400 Rest\\n' > /tmp/ch.txt"
                         " && MP4Box -quiet -add /tmp/in.mp4 -chapqt /tmp/ch.txt -new {out}",
    # Tags, chapters (FFmpeg writes a chapter text track and Nero chpl) and cover art.
    "mp4-metadata.mp4": "{meta} && {ff} {v} {a} -i /tmp/meta.txt -i /tmp/cover.jpg -map 0 -map 1 -map 3 -map_metadata 2 -map_chapters 2"
                        " -c:v:0 libx264 -preset ultrafast -g 12 -bf 2 -pix_fmt:v:0 yuv420p -c:a aac -c:v:1 copy -disposition:v:1 attached_pic -metadata:s:a:0 language=nor {out}",
    "mov-metadata.mov": "{meta} && {ff} {v} -i /tmp/meta.txt -map 0 -map_metadata 1 -map_chapters 1 -c:v mjpeg -q:v 12 {out}",
    # mov_text subtitles with style records.
    "mp4-movtext.mp4": "{srt} && {ff} {v} -i /tmp/subs.srt -map 0 -map 1 -c:v mjpeg -q:v 12 -c:s mov_text -metadata:s:s:0 language=eng {out}",
}

SAMPLE_RATES = {"mp4-vp9-opus.mp4": 48000, "mp4-flac.mp4": 44100}

PACKET_FIELDS = ["stream_index", "pts", "dts", "duration", "size", "pos", "flags"]
STREAM_FIELDS = ["index", "codec_type", "codec_name", "codec_tag_string", "width", "height", "pix_fmt", "color_space",
                 "color_transfer", "color_primaries", "color_range", "sample_aspect_ratio", "r_frame_rate", "avg_frame_rate",
                 "time_base", "start_pts", "duration_ts", "sample_rate", "channels", "bits_per_sample", "nb_frames", "extradata_size"]


def docker(command):
    subprocess.run(["docker", "run", "--rm", "-v", f"{os.path.abspath(OUT)}:/work", IMAGE, "sh", "-c", command], check=True)


def make(name, command):
    sr = SAMPLE_RATES.get(name, 8000)
    seconds = 1
    command = command.format(ff=FF, v=V.format(s=seconds), a=A.format(s=seconds, sr=sr), h264=H264, meta=META, srt=SRT, out=name)
    docker("set -e; " + command)
    probe(name)
    print(f"{name}: {os.path.getsize(os.path.join(OUT, name))} bytes")


def probe(name):
    out = subprocess.run(["docker", "run", "--rm", "-v", f"{os.path.abspath(OUT)}:/work", IMAGE, "ffprobe", "-v", "error",
                          "-show_streams", "-show_format", "-show_packets", "-show_chapters", "-show_data_hash", "MD5",
                          "-of", "json", name], check=True, capture_output=True, text=True).stdout
    data = json.loads(out)

    def stream(s):
        d = {k: s[k] for k in STREAM_FIELDS if k in s}
        disposition = {k: v for k, v in s.get("disposition", {}).items() if v and k in ("default", "attached_pic", "timed_thumbnails")}
        if disposition:
            d["disposition"] = disposition
        if s.get("tags"):
            d["tags"] = {k: v for k, v in s["tags"].items() if k not in ("creation_time",)}
        if s.get("side_data_list"):
            d["side_data"] = [{k: v for k, v in sd.items() if k != "displaymatrix"} for sd in s["side_data_list"]]
        return d

    trimmed = {
        "format": {k: data["format"].get(k) for k in ["format_name", "duration", "nb_streams", "start_time"]},
        "tags": {k: v for k, v in data["format"].get("tags", {}).items() if k != "creation_time"},
        "chapters": [{"start": c["start"], "end": c["end"], "time_base": c["time_base"], "title": c.get("tags", {}).get("title")}
                     for c in data.get("chapters", [])],
        "streams": [stream(s) for s in data["streams"]],
        "packets": [{**{k: p[k] for k in PACKET_FIELDS if k in p}, "md5": p.get("data_hash", "")[4:]} for p in data.get("packets", [])],
    }
    # One stream or packet per line keeps the dumps small and their diffs readable.
    with open(os.path.join(OUT, name + ".json"), "w", encoding="utf-8", newline="\n") as f:
        f.write('{"format":' + json.dumps(trimmed["format"]) + ',\n"tags":' + json.dumps(trimmed["tags"]) + ',\n"chapters":'
                + json.dumps(trimmed["chapters"]) + ',\n"streams":[\n')
        f.write(",\n".join(json.dumps(s) for s in trimmed["streams"]) + '],\n"packets":[\n')
        f.write(",\n".join(json.dumps(p) for p in trimmed["packets"]) + "]}\n")


def main():
    os.makedirs(OUT, exist_ok=True)
    only = [a for a in sys.argv[1:] if not a.startswith("--")]
    for name, command in CLIPS.items():
        if only and name not in only:
            continue
        if "--probe-only" in sys.argv:
            probe(name)
        else:
            make(name, command)


if __name__ == "__main__":
    main()
