"""Makes the FFmpeg subtitle conversion set (Video V1.2 task 12): 25 small source files written here (10 SRT, 10 WebVTT, 5 ASS,
each on one feature), each converted by FFmpeg to the other two formats, 50 conversions in all. Sources go to
tests/video/corpus/subtitles/references/sources and FFmpeg's files to .../ffmpeg (checked in), with manifest.txt naming the
FFmpeg version. SubtitleReferenceTests converts the sources with Egami.Video and compares the cues (times and text without
tags) with FFmpeg's, apart from the known differences listed in the test. FFmpeg runs only as a black-box program.

Usage, from the repo root:
  docker build -t egami-video-tools tools/video/docker
  python tools/video/make-subtitle-references.py           FFmpeg in Docker
  python tools/video/make-subtitle-references.py --local   ffmpeg on PATH (the checked-in files: FFmpeg 6.1.1, Ubuntu 24.04)
"""
import os
import shutil
import subprocess
import sys

ROOT = os.path.abspath("tests/video/corpus/subtitles/references")
SOURCES = os.path.join(ROOT, "sources")
FFMPEG = os.path.join(ROOT, "ffmpeg")
TOOLS = "egami-video-tools"
LOCAL = "--local" in sys.argv


def srt(*cues):
    return "".join(f"{i + 1}\n{a} --> {b}\n{t}\n\n" for i, (a, b, t) in enumerate(cues))


def vtt(*blocks):
    return "WEBVTT\n\n" + "".join(block + "\n\n" for block in blocks)


ASS_HEADER = ("[Script Info]\nScriptType: v4.00+\nPlayResX: 384\nPlayResY: 288\n\n[V4+ Styles]\n"
              "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, "
              "StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n"
              "Style: Default,Arial,20,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,2,2,10,10,10,1\n"
              "Style: Sign,Arial,24,&H0000FFFF,&H000000FF,&H00000000,&H00000000,-1,0,0,0,100,100,0,0,1,2,2,8,10,10,10,1\n\n"
              "[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n")


def ass(*events):
    return ASS_HEADER + "".join(f"{kind}: {layer},{a},{b},{style},,0,0,0,,{t}\n" for kind, layer, a, b, style, t in events)


def d(a, b, t, style="Default", layer=0, kind="Dialogue"):
    return (kind, layer, a, b, style, t)


FILES = {
    "srt-plain.srt": srt(("00:00:01,000", "00:00:02,500", "Hello."), ("00:00:03,000", "00:00:04,000", "Goodbye.")),
    "srt-tags.srt": srt(("00:00:01,000", "00:00:02,000", "<i>italic</i> <b>bold</b> <u>under</u>"),
                        ("00:00:03,000", "00:00:04,000", "<b><i>both</i></b> plain")),
    "srt-color.srt": srt(("00:00:01,000", "00:00:02,000", '<font color="#ff0000">red</font> and <font color="#00ff00">green</font>')),
    "srt-position.srt": srt(("00:00:01,000", "00:00:02,000", "{\\an8}At the top"), ("00:00:03,000", "00:00:04,000", "At the bottom")),
    "srt-lines.srt": srt(("00:00:01,000", "00:00:03,000", "Line one\nLine two\nLine three")),
    "srt-overlap.srt": srt(("00:00:01,000", "00:00:05,000", "Long cue"), ("00:00:02,000", "00:00:03,000", "Inside it")),
    "srt-hours.srt": srt(("02:59:58,123", "02:59:59,989", "Near three hours"), ("10:00:00,000", "10:00:01,000", "Ten hours")),
    "srt-unicode.srt": srt(("00:00:01,000", "00:00:02,000", "Grüße, 日本語, emoji \U0001F600"), ("00:00:03,000", "00:00:04,000", "שלום עולם")),
    "srt-crlf.srt": srt(("00:00:01,000", "00:00:02,000", "Windows"), ("00:00:03,000", "00:00:04,000", "line ends")).replace("\n", "\r\n"),
    "srt-ampersand.srt": srt(("00:00:01,000", "00:00:02,000", "Tom & Jerry < 3 > 2")),
    "vtt-plain.vtt": vtt("00:01.000 --> 00:02.500\nHello.", "00:03.000 --> 00:04.000\nGoodbye."),
    "vtt-tags.vtt": vtt("00:01.000 --> 00:02.000\n<i>italic</i> <b>bold</b> <u>under</u>"),
    "vtt-voice.vtt": vtt("00:01.000 --> 00:02.000\n<v Alice>Hi Bob</v>", "00:03.000 --> 00:04.000\n<v.loud Bob>Hi Alice</v>"),
    "vtt-class.vtt": vtt("00:01.000 --> 00:02.000\n<c.yellow>yellow</c> and <c.blue.bg>blue</c>"),
    "vtt-settings.vtt": vtt("00:01.000 --> 00:02.000 line:0 align:start\nTop left", "00:03.000 --> 00:04.000 position:80% size:30%\nRight"),
    "vtt-ids.vtt": vtt("first\n00:01.000 --> 00:02.000\nOne", "second\n00:03.000 --> 00:04.000\nTwo"),
    "vtt-karaoke.vtt": vtt("00:01.000 --> 00:03.000\nKa<00:00:01.500>ra<00:00:02.000>oke"),
    "vtt-entities.vtt": vtt("00:01.000 --> 00:02.000\nTom &amp; Jerry &lt;3 &gt; 2 &nbsp;end"),
    "vtt-note.vtt": vtt("NOTE a comment\nover two lines", "00:01.000 --> 00:02.000\nAfter the note"),
    "vtt-hours.vtt": vtt("02:59:58.123 --> 02:59:59.989\nNear three hours", "10:00:00.000 --> 10:00:01.000\nTen hours"),
    "ass-plain.ass": ass(d("0:00:01.00", "0:00:02.50", "Hello."), d("0:00:03.00", "0:00:04.00", "Goodbye.")),
    "ass-tags.ass": ass(d("0:00:01.00", "0:00:02.00", "{\\i1}italic{\\i0} {\\b1}bold{\\b0} {\\u1}under{\\u0}"),
                        d("0:00:03.00", "0:00:04.00", "{\\an8}Top {\\c&H0000FF&}red")),
    "ass-lines.ass": ass(d("0:00:01.00", "0:00:03.00", "Line one\\NLine two\\nsoft")),
    "ass-styles.ass": ass(d("0:00:01.00", "0:00:02.00", "Sign text", style="Sign", layer=1), d("0:00:01.00", "0:00:02.00", "Dialogue under it"),
                          d("0:00:02.00", "0:00:03.00", "A comment", kind="Comment")),
    "ass-karaoke.ass": ass(d("0:00:01.00", "0:00:03.00", "{\\k50}Ka{\\k50}ra{\\k100}oke")),
}

TARGETS = {".srt": "srt", ".vtt": "webvtt", ".ass": "ass"}


def main():
    for folder in (SOURCES, FFMPEG):
        shutil.rmtree(folder, ignore_errors=True)
        os.makedirs(folder)
    for name, text in FILES.items():
        with open(os.path.join(SOURCES, name), "w", encoding="utf-8", newline="") as f:
            f.write(text)
    count = 0
    for name in FILES:
        stem, extension = os.path.splitext(name)
        for target, muxer in TARGETS.items():
            if target == extension:
                continue
            out = f"{stem}{target}"
            args = ["ffmpeg", "-v", "error", "-y", "-i", f"sources/{name}", "-f", muxer, f"ffmpeg/{out}"]
            command = args if LOCAL else ["docker", "run", "--rm", "-v", f"{ROOT}:/work", "-w", "/work", TOOLS] + args
            subprocess.run(command, cwd=ROOT, check=True)
            count += 1
    version = subprocess.run(["ffmpeg", "-version"] if LOCAL else ["docker", "run", "--rm", TOOLS, "ffmpeg", "-version"],
                             capture_output=True, text=True, check=True).stdout.splitlines()[0]
    with open(os.path.join(ROOT, "manifest.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write(f"{version}\n{len(FILES)} sources, {count} conversions: ffmpeg -i sources/<file> -f <srt|webvtt|ass> ffmpeg/<file>\n")
    print(f"{count} conversions in {FFMPEG}")


if __name__ == "__main__":
    main()
