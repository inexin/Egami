#!/bin/sh
# Builds alacref against Apple's ALAC source (Apache-2.0), fetched at a fixed commit into a work folder.
# Usage: tools/video/alacref/build.sh <work folder>   (the binary lands in <work folder>/alacref)
set -e
WORK=${1:-.cache/alacref}
HERE=$(cd "$(dirname "$0")" && pwd)
COMMIT=c38887c5c5e64a4b31108733bd79ca9b2496d987
mkdir -p "$WORK"
if [ ! -d "$WORK/alac" ]; then
  git clone -q https://github.com/macosforge/alac.git "$WORK/alac"
fi
git -C "$WORK/alac" checkout -q $COMMIT
C="$WORK/alac/codec"
g++ -O2 -w -I"$C" -o "$WORK/alacref" "$HERE/alacref.cpp" "$C/ALACEncoder.cpp" "$C/ALACDecoder.cpp" "$C/ALACBitUtilities.c" \
  "$C/EndianPortable.c" "$C/ag_enc.c" "$C/ag_dec.c" "$C/dp_enc.c" "$C/dp_dec.c" "$C/matrix_enc.c" "$C/matrix_dec.c"
echo "$WORK/alacref"
