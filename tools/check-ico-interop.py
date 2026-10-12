"""Checks that ImageMagick, Pillow and Windows read Egami's ICO and CUR files to the pixels Egami reads back.

Runs the IcoInteropFiles test with EGAMI_INTEROP_DIR=.cache/interop, which writes favicon and Windows sets, all-PNG and all-BMP
icons, a raster at its own size, a padded one and a cursor, with Egami's decode of each entry next to it as raw RGBA. Then
ImageMagick and Pillow read every entry in egami-iconref:2.3.3 (tools/iconref), and, on Windows, PowerShell reads every frame
with WIC's icon decoder (what Explorer and the shell use). Colors are compared where alpha is not 0. Known gaps are reported,
not failed: Pillow reads one image of a cursor, without alpha, and WIC has no cursor decoder. Windows skips PNG entries that are
not 8-bit RGBA, which is why the writer always writes that color type.

Usage, from the repo root with Docker running:
  python tools/check-ico-interop.py
"""
import glob
import os
import re
import subprocess
import sys

IMAGE = "egami-iconref:2.3.3"
OUT = ".cache/interop/ico"

INSIDE = r'''
import glob, re, struct, subprocess, sys
from PIL import Image, IcoImagePlugin
bad = 0
for path in sorted(glob.glob("/w/.cache/interop/ico/*.ico") + glob.glob("/w/.cache/interop/ico/*.cur")):
    refs = sorted(glob.glob(path + ".*.rgba"), key=lambda p: int(p.split(".")[-3]))
    data = open(path, "rb").read()
    count = struct.unpack_from("<H", data, 4)[0]
    offsets = [struct.unpack_from("<I", data, 6 + 16 * i + 12)[0] for i in range(count)]
    pillow = {}
    if path.endswith(".ico"):
        icofile = IcoImagePlugin.IcoFile(open(path, "rb"))
        for k, e in enumerate(icofile.entry):
            pillow[offsets.index(e.offset)] = icofile.frame(k).convert("RGBA").tobytes()
    else:
        im = Image.open(path).convert("RGBA")
        pillow = {i: im.tobytes() for i, r in enumerate(refs) if r.endswith(f".{im.width}x{im.height}.rgba")}
    for i, ref in enumerate(refs):
        mine = open(ref, "rb").read()
        magick = subprocess.run(["magick", f"{path}[{i}]", "-depth", "8", "rgba:-"], capture_output=True).stdout
        for tool, theirs in (("magick", magick), ("pillow", pillow.get(i))):
            if theirs is None:
                print(f"{path} {i} {tool}: not read (known)")
                continue
            # Pillow reads cursors as RGB, without alpha (known): only the colors of visible pixels are compared there.
            colors_only = tool == "pillow" and path.endswith(".cur")
            same = len(theirs) == len(mine) and all((colors_only or theirs[j + 3] == mine[j + 3]) and (mine[j + 3] == 0 or theirs[j:j + 3] == mine[j:j + 3]) for j in range(0, len(mine), 4))
            print(f"{path} {i} {tool}: {'same' if same else 'DIFFERS'}{' (colors; Pillow drops a cursor alpha)' if colors_only else ''}")
            bad += not same
sys.exit(1 if bad else 0)
'''

WIC = r'''
Add-Type -AssemblyName PresentationCore
$bad = 0
foreach ($file in Get-ChildItem -Path "{out}" -Filter *.ico) {
  $decoder = [System.Windows.Media.Imaging.BitmapDecoder]::Create([Uri]$file.FullName, 'PreservePixelFormat', 'OnLoad')
  $seen = @{}
  foreach ($frame in $decoder.Frames) {
    $converted = New-Object System.Windows.Media.Imaging.FormatConvertedBitmap($frame, [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)
    $stride = $converted.PixelWidth * 4
    $buffer = New-Object byte[] ($stride * $converted.PixelHeight)
    $converted.CopyPixels($buffer, $stride, 0)
    $ref = Get-ChildItem -Path "{out}" -Filter "$($file.Name).*.$($converted.PixelWidth)x$($converted.PixelHeight).rgba" | Select-Object -First 1
    $seen[$ref.Name] = $true
    $mine = [System.IO.File]::ReadAllBytes($ref.FullName)
    $same = $mine.Length -eq $buffer.Length
    for ($j = 0; $same -and $j -lt $mine.Length; $j += 4) {
      if ($mine[$j + 3] -ne $buffer[$j + 3]) { $same = $false }
      elseif ($mine[$j + 3] -ne 0 -and ($mine[$j] -ne $buffer[$j + 2] -or $mine[$j + 1] -ne $buffer[$j + 1] -or $mine[$j + 2] -ne $buffer[$j])) { $same = $false }
    }
    Write-Output "$($ref.Name) wic: $(if ($same) { 'same' } else { 'DIFFERS' })"
    if (-not $same) { $bad++ }
  }
  foreach ($ref in Get-ChildItem -Path "{out}" -Filter "$($file.Name).*.rgba") {
    if (-not $seen.ContainsKey($ref.Name)) { Write-Output "$($ref.Name) wic: NOT LISTED"; $bad++ }
  }
}
exit $bad
'''


def main():
    root = os.getcwd()
    for old in glob.glob(f"{OUT}/*"):
        os.remove(old)
    subprocess.run(["dotnet", "test", "tests/Egami.Tests", "-c", "Release", "--filter", "FullyQualifiedName~IcoInteropFiles"],
                   check=True, env={**os.environ, "EGAMI_INTEROP_DIR": os.path.join(root, ".cache", "interop")})
    subprocess.run(["docker", "build", "-q", "-t", IMAGE, "tools/iconref"], check=True, capture_output=True)
    inside = subprocess.run(["docker", "run", "--rm", "-v", f"{root}:/w", IMAGE, "python3", "-c", INSIDE],
                            env={**os.environ, "MSYS_NO_PATHCONV": "1"})
    wic = 0
    if sys.platform == "win32":
        wic = subprocess.run(["powershell", "-NoProfile", "-Command", WIC.replace("{out}", os.path.abspath(OUT))]).returncode
    sys.exit(1 if inside.returncode or wic else 0)


if __name__ == "__main__":
    main()
