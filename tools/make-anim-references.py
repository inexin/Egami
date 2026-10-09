"""Builds the GIF and APNG corpus in tests/images/gif and tests/images/apng, and composited references in tests/reference/anim,
with Pillow and ImageMagick in Docker.

Corpus (licenses in tests/images/LICENSES), all made here from the 1.2 public-domain photos (tests/images/ops/duck.png and
egret.png) or from generated shapes:
  GIF, by Pillow: a pan across the duck with a palette per frame and with one shared palette, a moving sprite with each disposal
    method, a NETSCAPE loop count of 3 and none at all, zero delays, a comment, and a transparent still.
  GIF, by ImageMagick: sub-rectangle frames placed with -page under each disposal, the pan optimized with -layers Optimize
    (sub-rectangles and transparency), interlaced and GIF87a stills, 2 to 256 color stills (every LZW code size), and the
    256-color egret still.
  APNG, by Pillow: the pan as RGB, a sprite with partial alpha under each blend and disposal, gray and gray with alpha frames,
    and a file whose default image is not part of the animation. Pillow cannot write palette or 16-bit APNGs; the decoder tests
    build those by hand.

References (tests/reference/anim):
  <file>.pillow.rgba.gz       every frame as Pillow composites it (seek, then convert to RGBA), raw, frames one after another
  <file>.magick.rgba.gz       the same from ImageMagick's -coalesce (GIF only; ImageMagick reads only the default image of an APNG)
  manifest.json               per file: size, frame count, delays in ms, loop count, and which references exist

Where the two disagree on GIF, the GIF89a specification and browsers decide, and the decoder tests say which reference they follow.

Usage, from the repo root with Docker running:
  python tools/make-anim-references.py
The script runs alpine:3.20 and installs ImageMagick and Pillow inside it with apk.
"""
import gzip
import json
import os
import subprocess
import sys

IMAGE = "alpine:3.20"
PACKAGES = "python3 py3-pillow imagemagick"
GIF = "tests/images/gif"
APNG = "tests/images/apng"
TARGET = "tests/reference/anim"
OPS = "tests/images/ops"


def run(*args):
    subprocess.run(list(args), check=True)


def pan_frames(count=8, size=(96, 72)):
    """Crops sliding across the duck, reduced: a short video-like clip."""
    from PIL import Image
    duck = Image.open(os.path.join(OPS, "duck.png")).convert("RGB")
    frames = []
    for i in range(count):
        box = (40 + i * 24, 60 + i * 8, 40 + i * 24 + 320, 60 + i * 8 + 240)
        frames.append(duck.crop(box).resize(size, Image.LANCZOS))
    return frames


def sprite_frames(mode, count=6, size=(64, 48)):
    """A square moving over a still background: a few exact colors, transparent outside the square in RGBA."""
    from PIL import Image, ImageDraw
    frames = []
    for i in range(count):
        if mode == "RGBA":
            im = Image.new("RGBA", size, (0, 0, 0, 0))
        else:
            im = Image.new("RGB", size, (30, 60, 90))
        draw = ImageDraw.Draw(im)
        draw.rectangle((2, 2, 10, 10), fill=(250, 250, 0, 255) if mode == "RGBA" else (250, 250, 0))
        x, y = 4 + i * 8, 6 + i * 5
        draw.rectangle((x, y, x + 15, y + 11), fill=(200, 30, 30, 255) if mode == "RGBA" else (200, 30, 30))
        if mode == "RGBA":
            # A band of partial alpha, so Over blending is exercised.
            draw.rectangle((x + 4, y + 2, x + 11, y + 9), fill=(30, 200, 30, 128))
        frames.append(im)
    return frames


def pillow_gifs():
    from PIL import Image
    pan = pan_frames()
    durations = [40, 60, 80, 100, 120, 140, 160, 180]
    pan[0].save(os.path.join(GIF, "pan-local.gif"), save_all=True, append_images=pan[1:], duration=durations, loop=0)

    palette = pan[0].quantize(256, method=Image.Quantize.MEDIANCUT)
    shared = [f.quantize(palette=palette, dither=Image.Dither.NONE) for f in pan]
    shared[0].save(os.path.join(GIF, "pan-global.gif"), save_all=True, append_images=shared[1:], duration=50, loop=0)

    sprite = [f.convert("P", palette=Image.Palette.ADAPTIVE, colors=8) for f in sprite_frames("RGB")]
    for disposal in (0, 1, 2, 3):
        sprite[0].save(os.path.join(GIF, f"sprite-dispose{disposal}.gif"), save_all=True, append_images=sprite[1:],
                       duration=70, loop=0, disposal=disposal)
    sprite[0].save(os.path.join(GIF, "loop-3.gif"), save_all=True, append_images=sprite[1:], duration=70, loop=3)
    sprite[0].save(os.path.join(GIF, "no-netscape.gif"), save_all=True, append_images=sprite[1:], duration=70)
    sprite[0].save(os.path.join(GIF, "zero-delay.gif"), save_all=True, append_images=sprite[1:], duration=0, loop=0)
    sprite[0].save(os.path.join(GIF, "comment.gif"), comment=b"Made for Egami's tests")

    still = sprite_frames("RGBA", count=1)[0]
    still.save(os.path.join(GIF, "transparent-still.gif"))


def magick_gifs():
    duck = os.path.join(OPS, "duck.png")
    egret = os.path.join(OPS, "egret.png")
    # Sub-rectangle frames over a 64 x 48 canvas, each placed with -page, under every disposal method.
    for dispose in ("None", "Background", "Previous"):
        run("magick", "-dispose", "None", "-delay", "10", "-size", "64x48", "xc:#1e3c5a",
            "-dispose", dispose,
            "(", "-size", "16x12", "xc:#c81e1e", "-repage", "+4+6", ")",
            "(", "-size", "16x12", "xc:#1ec81e", "-repage", "+20+16", ")",
            "(", "-size", "20x10", "xc:#1e1ec8", "-repage", "+40+30", ")",
            "(", "-size", "8x8", "xc:#f0f000", "-repage", "+2+2", ")",
            "-loop", "0", os.path.join(GIF, f"page-dispose-{dispose.lower()}.gif"))
    # A transparent canvas, so Background disposal shows through.
    run("magick", "-dispose", "Background", "-delay", "10", "-size", "48x32", "xc:none",
        "(", "-size", "12x12", "xc:#c81e1e", "-repage", "+4+4", ")",
        "(", "-size", "12x12", "xc:#1ec81e", "-repage", "+20+10", ")",
        "(", "-size", "12x12", "xc:#1e1ec8", "-repage", "+30+16", ")",
        "-loop", "0", os.path.join(GIF, "transparent-background.gif"))

    pan_dir = "/tmp/pan"
    os.makedirs(pan_dir, exist_ok=True)
    names = []
    for i, frame in enumerate(pan_frames()):
        name = os.path.join(pan_dir, f"{i}.png")
        frame.save(name)
        names.append(name)
    run("magick", "-delay", "8", *names, "-layers", "Optimize", "-loop", "0", os.path.join(GIF, "pan-optimized.gif"))

    run("magick", duck, "-resize", "96x72", "-colors", "64", "-interlace", "GIF", os.path.join(GIF, "interlaced.gif"))
    run("magick", duck, "-resize", "80x60", "-colors", "32", "GIF87:" + os.path.join(GIF, "gif87a.gif"))
    for colors in (2, 4, 8, 16, 32, 64, 128):
        run("magick", duck, "-resize", "64x48", "+dither", "-colors", str(colors), os.path.join(GIF, f"colors-{colors}.gif"))
    run("magick", egret, "-resize", "240x320", "-colors", "256", os.path.join(GIF, "egret-256.gif"))


def pillow_apngs():
    from PIL import Image
    pan = pan_frames()
    pan[0].save(os.path.join(APNG, "pan-rgb.png"), save_all=True, append_images=pan[1:], duration=[40, 50, 60, 70, 80, 90, 100, 33],
                loop=0)
    sprite = sprite_frames("RGBA")
    for blend, blend_name in ((0, "source"), (1, "over")):
        for disposal, disposal_name in ((0, "none"), (1, "background"), (2, "previous")):
            sprite[0].save(os.path.join(APNG, f"sprite-{blend_name}-{disposal_name}.png"), save_all=True,
                           append_images=sprite[1:], duration=70, loop=2, blend=blend, disposal=disposal)
    gray = [f.convert("L") for f in pan[:4]]
    gray[0].save(os.path.join(APNG, "gray.png"), save_all=True, append_images=gray[1:], duration=100)
    gray_alpha = [f.convert("LA") for f in sprite[:4]]
    gray_alpha[0].save(os.path.join(APNG, "gray-alpha.png"), save_all=True, append_images=gray_alpha[1:], duration=100,
                       blend=1)
    # The default image (IDAT) is a fallback for old viewers and not part of the animation.
    fallback = Image.new("RGBA", sprite[0].size, (255, 0, 255, 255))
    fallback.save(os.path.join(APNG, "default-image.png"), save_all=True, append_images=sprite[:3], duration=70,
                  default_image=True)


def frames_of(path, tool):
    """Every composited frame as raw RGBA bytes, by Pillow or ImageMagick."""
    from PIL import Image
    if tool == "pillow":
        im = Image.open(path)
        out = []
        for i in range(getattr(im, "n_frames", 1)):
            im.seek(i)
            out.append(im.convert("RGBA").tobytes())
        return out, im.size
    raw = "/tmp/frames.rgba"
    run("magick", path, "-coalesce", "-depth", "8", "rgba:" + raw)
    with open(raw, "rb") as f:
        data = f.read()
    with Image.open(path) as im:
        size = im.size
    frame = size[0] * size[1] * 4
    return [data[i:i + frame] for i in range(0, len(data), frame)], size


def describe(path):
    from PIL import Image
    im = Image.open(path)
    delays = []
    for i in range(getattr(im, "n_frames", 1)):
        im.seek(i)
        delays.append(im.info.get("duration", 0))
    return {"frames": getattr(im, "n_frames", 1), "delays": delays, "loop": im.info.get("loop")}


def inside():
    for folder in (GIF, APNG, TARGET):
        os.makedirs(folder, exist_ok=True)
    pillow_gifs()
    magick_gifs()
    pillow_apngs()

    manifest = {}
    for folder, tools in ((GIF, ("pillow", "magick")), (APNG, ("pillow",))):
        for name in sorted(os.listdir(folder)):
            if not name.endswith((".gif", ".png")):
                continue
            path = os.path.join(folder, name)
            entry = describe(path)
            entry["references"] = []
            for tool in tools:
                frames, size = frames_of(path, tool)
                entry["width"], entry["height"] = size
                with open(os.path.join(TARGET, f"{name}.{tool}.rgba.gz"), "wb") as raw, gzip.GzipFile(fileobj=raw, mode="wb", mtime=0) as f:
                    f.write(b"".join(frames))
                entry["references"].append({"tool": tool, "frames": len(frames)})
            manifest[f"{os.path.basename(folder)}/{name}"] = entry
    with open(os.path.join(TARGET, "manifest.json"), "w") as f:
        json.dump(manifest, f, indent=1, sort_keys=True)
    print(f"{len(manifest)} files")


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "--inside":
        inside()
        return

    repo = os.getcwd()
    subprocess.run(["docker", "run", "--rm", "-v", f"{repo}:/work", "-w", "/work", IMAGE, "sh", "-c",
                    f"apk add --no-cache {PACKAGES} > /dev/null && python3 tools/make-anim-references.py --inside"], check=True)


if __name__ == "__main__":
    main()
