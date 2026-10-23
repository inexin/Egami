"""Builds the references of egami-video's picture commands in tests/reference/video-cli: contact sheets and a thumbnail that the
built tool makes from clips of the video corpus (tests/video/corpus). They pin the tool's own output, written with Egami's
resampling and the bundled Liberation Sans, so a change that moves pixels shows up in PictureTests, which runs the same
arguments and compares with a small tolerance. After a deliberate change, run this script again and look at the images before
committing them.

  sheet-4x4.png                    sheet webm-vp8-opus.webm --width 480
  sheet-3x2-timestamps-title.png   sheet avi-mjpeg-420-pcm.avi --grid 3x2 --width 720 --timestamps --title
  sheet-5x1-timestamps.png         sheet y4m-420jpeg.y4m --grid 5x1 --width 640 --timestamps
  thumbnail-120.png                thumbnail webm-vp8-opus.webm --size 120x

Usage, from the repo root (needs the .NET SDK):
  python tools/make-video-cli-references.py
"""
import os
import shutil
import subprocess
import sys
import tempfile

CORPUS = os.path.join("tests", "video", "corpus")
TARGET = os.path.join("tests", "reference", "video-cli")
PROJECT = os.path.join("src", "Egami.Video.Cli", "Egami.Video.Cli.csproj")

CASES = [
    ("sheet-4x4", "webm-vp8-opus.webm", ["sheet", "--width", "480"]),
    ("sheet-3x2-timestamps-title", "avi-mjpeg-420-pcm.avi", ["sheet", "--grid", "3x2", "--width", "720", "--timestamps", "--title"]),
    ("sheet-5x1-timestamps", "y4m-420jpeg.y4m", ["sheet", "--grid", "5x1", "--width", "640", "--timestamps"]),
    ("thumbnail-120", "webm-vp8-opus.webm", ["thumbnail", "--size", "120x"]),
]


def main():
    if not os.path.isfile(PROJECT):
        sys.exit("Run this from the repository's root.")
    # The Debug build, as the tests run it (the step catalog reads Egami's documentation from its own bin folder).
    subprocess.run(["dotnet", "build", PROJECT, "--nologo", "-v", "q"], check=True)
    tool = os.path.abspath(os.path.join("src", "Egami.Video.Cli", "bin", "Debug", "net10.0", "Egami.Video.Cli.dll"))
    os.makedirs(TARGET, exist_ok=True)
    with tempfile.TemporaryDirectory() as work:
        for name, clip, args in CASES:
            # The clip is copied in and named bare, as the tests do, so a sheet's title shows the same name.
            shutil.copyfile(os.path.join(CORPUS, clip), os.path.join(work, clip))
            command, options = args[0], args[1:]
            output = os.path.join(work, name + ".png")
            subprocess.run(["dotnet", tool, command, clip, "-o", output, *options, "--quiet"], cwd=work, check=True)
            shutil.copyfile(output, os.path.join(TARGET, name + ".png"))
            print(f"wrote {TARGET}/{name}.png")


if __name__ == "__main__":
    main()
