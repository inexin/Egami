"""Builds the Phase 2.1.2 ICC references: synthetic profiles written byte by byte here, and Little CMS's reading of every profile.

Profiles (tests/images/icc):
  compact/*.icc        Clinton Ingram's Compact ICC Profiles (CC0), copied unchanged: matrix-shaper RGB and gray, v2 and v4,
                       curv tables, para curves, chad tags, and the "micro" and "nano" files that push the format's limits
  synthetic/*.icc      written by this script: every LUT tag type (lut8, lut16, lutAtoB, lutBtoA) with Lab and XYZ PCS, CLUTs
                       with unequal grids and one- and two-byte entries, CMYK, gray, para curves of all five types, a chad
                       tag, distinct perceptual and colorimetric tables, and a v2 input profile whose white is not D50

References (tests/reference/icc):
  manifest.json        for each profile, what Little CMS reports (version, class, color space, PCS, matrix-shaper or LUT,
                       which intents have tables, media white, detected black points, tag list), and the transforms below
  inputs-<model>.f32   the inputs: little-endian float32 samples, 0..1 for every model (CMYK too)
  <profile>.f32.gz     the outputs of every transform for that profile, concatenated in manifest order: float32 samples, Lab
                       as L 0..100 and a, b about -128..127, XYZ relative to D50 white Y = 1, CMYK 0..1

Each profile is transformed to Lab (D50) and to sRGB under all four intents, and from sRGB under perceptual and relative
colorimetric, with and without black point compensation; a profile with no table for a direction has no cases for it. Little CMS runs unoptimized (cmsFLAGS_NOOPTIMIZE), in double precision.

Usage, from the repo root with Docker running:
  python tools/make-icc-references.py
The script runs alpine:3.20 and installs lcms2 and gcc inside it with apk.
"""
import gzip
import json
import math
import os
import struct
import subprocess

IMAGE = "alpine:3.20"
PACKAGES = "lcms2-dev gcc musl-dev"
ICC = "tests/images/icc"
REF = "tests/reference/icc"
WORK = ".cache/refs-icc"

D50 = (0.9642, 1.0, 0.8249)
D65 = (0.95047, 1.0, 1.08883)
# The sRGB colorants adapted to D50 (Bradford), as ICC sRGB profiles store them.
SRGB_D50 = ((0.4360747, 0.3850649, 0.1430804), (0.2225045, 0.7168786, 0.0606169), (0.0139322, 0.0971045, 0.7141733))

# A small C harness. "info <icc>" prints what Little CMS reads from a profile as one JSON object; "jobs <file>" runs one transform
# a line: source target intent bpc count input output, where source and target are a profile path, "lab" or "xyz".
HARNESS = r'''
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <lcms2.h>
static cmsHPROFILE open(const char *p) {
  if (!strcmp(p, "lab")) return cmsCreateLab4Profile(NULL);
  if (!strcmp(p, "xyz")) return cmsCreateXYZProfile();
  return cmsOpenProfileFromFile(p, "r");
}
static cmsUInt32Number format(cmsHPROFILE h) {
  switch (cmsGetColorSpace(h)) {
    case cmsSigRgbData: return TYPE_RGB_DBL;
    case cmsSigGrayData: return TYPE_GRAY_DBL;
    case cmsSigCmykData: return TYPE_CMYK_DBL;
    case cmsSigLabData: return TYPE_Lab_DBL;
    case cmsSigXYZData: return TYPE_XYZ_DBL;
    default: return 0;
  }
}
static void sig(char *out, cmsUInt32Number s) { out[0] = s >> 24; out[1] = s >> 16; out[2] = s >> 8; out[3] = s; out[4] = 0; }
static void info(const char *path) {
  cmsHPROFILE h = cmsOpenProfileFromFile(path, "r");
  if (!h) { printf("{\"error\":\"open\"}\n"); return; }
  char a[5], b[5], c[5]; wchar_t wdesc[256]; char desc[256];
  sig(a, cmsGetDeviceClass(h)); sig(b, cmsGetColorSpace(h)); sig(c, cmsGetPCS(h));
  cmsGetProfileInfoASCII(h, cmsInfoDescription, "en", "US", desc, sizeof desc);
  for (char *q = desc; *q; q++) if (*q == '"' || *q == '\\' || *q < 32) *q = ' ';
  printf("{\"version\":%.2f,\"class\":\"%s\",\"colorSpace\":\"%s\",\"pcs\":\"%s\",\"headerIntent\":%u,\"description\":\"%s\",\"matrixShaper\":%d",
         cmsGetProfileVersion(h), a, b, c, cmsGetHeaderRenderingIntent(h), desc, cmsIsMatrixShaper(h));
  printf(",\"clutAsInput\":[%d,%d,%d,%d]", cmsIsCLUT(h, 0, LCMS_USED_AS_INPUT), cmsIsCLUT(h, 1, LCMS_USED_AS_INPUT), cmsIsCLUT(h, 2, LCMS_USED_AS_INPUT), cmsIsCLUT(h, 3, LCMS_USED_AS_INPUT));
  printf(",\"intentSupportedAsInput\":[%d,%d,%d,%d]", cmsIsIntentSupported(h, 0, LCMS_USED_AS_INPUT), cmsIsIntentSupported(h, 1, LCMS_USED_AS_INPUT), cmsIsIntentSupported(h, 2, LCMS_USED_AS_INPUT), cmsIsIntentSupported(h, 3, LCMS_USED_AS_INPUT));
  cmsCIEXYZ *w = (cmsCIEXYZ *)cmsReadTag(h, cmsSigMediaWhitePointTag);
  if (w) printf(",\"mediaWhite\":[%.7f,%.7f,%.7f]", w->X, w->Y, w->Z);
  for (int i = 0; i < 2; i++) {
    cmsCIEXYZ bp; int ok = cmsDetectBlackPoint(&bp, h, i, 0);
    if (ok) printf(",\"blackPoint%s\":[%.7f,%.7f,%.7f]", i ? "Relative" : "Perceptual", bp.X, bp.Y, bp.Z);
  }
  printf(",\"tags\":[");
  int n = cmsGetTagCount(h);
  for (int i = 0; i < n; i++) { char t[5]; sig(t, cmsGetTagSignature(h, i)); printf("%s\"%s\"", i ? "," : "", t); }
  printf("]}\n");
  cmsCloseProfile(h);
}
static int channels(cmsUInt32Number f) { return T_CHANNELS(f); }
static int run(char *line) {
  char src[512], dst[512], in[512], out[512]; int intent, bpc, count;
  if (sscanf(line, "%511s %511s %d %d %d %511s %511s", src, dst, &intent, &bpc, &count, in, out) != 7) return 0;
  cmsHPROFILE s = open(src), d = open(dst);
  cmsUInt32Number fi = format(s), fo = format(d);
  cmsHTRANSFORM x = cmsCreateTransform(s, fi, d, fo, intent, cmsFLAGS_NOOPTIMIZE | cmsFLAGS_NOCACHE | (bpc ? cmsFLAGS_BLACKPOINTCOMPENSATION : 0));
  if (!x) return 0;   /* the profile has no table for that direction: no output file */
  int ci = channels(fi), co = channels(fo);
  double *a = malloc(sizeof(double) * count * ci), *b = malloc(sizeof(double) * count * co);
  float *f = malloc(sizeof(float) * count * (ci > co ? ci : co));
  FILE *fin = fopen(in, "rb"); fread(f, sizeof(float), count * ci, fin); fclose(fin);
  for (int i = 0; i < count * ci; i++) a[i] = f[i] * (T_COLORSPACE(fi) == PT_CMYK ? 100.0 : 1.0);
  cmsDoTransform(x, a, b, count);
  for (int i = 0; i < count * co; i++) f[i] = (float)(b[i] / (T_COLORSPACE(fo) == PT_CMYK ? 100.0 : 1.0));
  FILE *fout = fopen(out, "wb"); fwrite(f, sizeof(float), count * co, fout); fclose(fout);
  cmsDeleteTransform(x); cmsCloseProfile(s); cmsCloseProfile(d); free(a); free(b); free(f);
  return 0;
}
int main(int argc, char **argv) {
  if (!strcmp(argv[1], "info")) { for (int i = 2; i < argc; i++) info(argv[i]); return 0; }
  FILE *j = fopen(argv[2], "r"); char line[2048]; int bad = 0;
  while (fgets(line, sizeof line, j)) bad |= run(line);
  return bad;
}
'''


# ---------------------------------------------------------------------------------------------------------------------------
# ICC writing


def s15(v):
    return struct.pack(">i", int(round(v * 65536)))


def xyz_type(x, y, z):
    return b"XYZ \0\0\0\0" + s15(x) + s15(y) + s15(z)


def sf32_type(values):
    return b"sf32\0\0\0\0" + b"".join(s15(v) for v in values)


def curv_gamma(g):
    return b"curv\0\0\0\0" + struct.pack(">IH", 1, int(round(g * 256))) + b"\0\0"


def curv_table(values):
    data = b"curv\0\0\0\0" + struct.pack(">I", len(values)) + b"".join(struct.pack(">H", min(65535, max(0, int(round(v * 65535))))) for v in values)
    return pad4(data)


def para(kind, *params):
    return b"para\0\0\0\0" + struct.pack(">HH", kind, 0) + b"".join(s15(p) for p in params)


def pad4(data):
    return data + b"\0" * (-len(data) % 4)


def desc_v2(text):
    a = text.encode("ascii") + b"\0"
    return pad4(b"desc\0\0\0\0" + struct.pack(">I", len(a)) + a + struct.pack(">II", 0, 0) + struct.pack(">HB", 0, 0) + b"\0" * 67)


def mluc(text):
    u = text.encode("utf-16-be")
    return pad4(b"mluc\0\0\0\0" + struct.pack(">II", 1, 12) + b"enUS" + struct.pack(">II", len(u), 28) + u)


def text_type(text):
    return pad4(b"text\0\0\0\0" + text.encode("ascii") + b"\0")


def profile(version, cls, space, pcs, tags, intent=0):
    """An ICC profile: a 128-byte header, the tag table, and the tags (shared bytes stored once), 4-byte aligned."""
    count = len(tags)
    offset = 128 + 4 + 12 * count
    table, body, seen = b"", b"", {}
    for signature, data in tags:
        data = pad4(data)
        if data not in seen:
            seen[data] = offset + len(body)
            body += data
        table += signature.encode("ascii") + struct.pack(">II", seen[data], len(data))
    size = offset + len(body)
    header = struct.pack(">I", size) + b"\0\0\0\0" + struct.pack(">I", version) + cls.encode() + space.encode() + pcs.encode()
    header += struct.pack(">6H", 2026, 10, 10, 0, 0, 0) + b"acsp" + b"\0" * 4 + struct.pack(">I", 0) + b"\0" * 8 + b"\0" * 8
    header += struct.pack(">I", intent) + s15(D50[0]) + s15(D50[1]) + s15(D50[2]) + b"\0" * 4 + b"\0" * 16 + b"\0" * 28
    assert len(header) == 128
    return header + struct.pack(">I", count) + table + body


# ---------------------------------------------------------------------------------------------------------------------------
# Color models for the synthetic tables. They need not be accurate: Little CMS's reading of the tables is the reference.


def mul(m, v):
    return tuple(sum(m[r][c] * v[c] for c in range(3)) for r in range(3))


def inv(m):
    a, b, c = m[0]
    d, e, f = m[1]
    g, h, i = m[2]
    det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g)
    return ((( e * i - f * h) / det, (c * h - b * i) / det, (b * f - c * e) / det),
            ((f * g - d * i) / det, (a * i - c * g) / det, (c * d - a * f) / det),
            ((d * h - e * g) / det, (b * g - a * h) / det, (a * e - b * d) / det))


def lab(xyz):
    def f(t):
        return t ** (1 / 3) if t > 216 / 24389 else (24389 / 27 * t + 16) / 116
    fx, fy, fz = (f(xyz[i] / D50[i]) for i in range(3))
    return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz))


def xyz_of_lab(L, a, b):
    fy = (L + 16) / 116
    fx, fz = fy + a / 500, fy - b / 200

    def g(t):
        return t ** 3 if t ** 3 > 216 / 24389 else (116 * t - 16) / (24389 / 27)
    return (g(fx) * D50[0], g(fy) * D50[1], g(fz) * D50[2])


def rgb_to_xyz(rgb, gamma=2.2):
    lin = tuple(max(0.0, c) ** gamma for c in rgb)
    return mul(SRGB_D50, lin)


def xyz_to_rgb(xyz, gamma=2.2):
    lin = mul(inv(SRGB_D50), xyz)
    return tuple(min(1.0, max(0.0, c)) ** (1 / gamma) for c in lin)


def cmyk_to_xyz(cmyk):
    c, m, y, k = cmyk
    # Ink with a little dot gain, multiplied together, then a 1.8 gamma to light.
    rgb = tuple((1 - (1 - (1 - t) ** 1.25)) * (1 - k ** 1.1) * 0.97 + 0.015 for t in (c, m, y))
    return rgb_to_xyz(rgb, 1.8)


def xyz_to_cmyk(xyz):
    r, g, b = xyz_to_rgb(xyz, 1.8)
    r, g, b = ((t - 0.015) / 0.97 for t in (r, g, b))
    k = max(0.0, min(1.0, 1 - max(r, g, b))) * 0.7
    ink = 1 - k ** 1.1
    cmy = []
    for t in (r, g, b):
        remain = min(1.0, max(0.0, t / ink if ink > 0 else 0.0))
        cmy.append(1 - remain ** (1 / 1.25))
    return (cmy[0], cmy[1], cmy[2], k)


def saturate(lab_value, s):
    return (lab_value[0], lab_value[1] * s, lab_value[2] * s)


# PCS encodings: Little CMS's normalized 0..1 values.
def lab_v2(v):      # lut16 (and every v2 table): L 0..100 to 0..0xFF00, a and b with 0x8000 at zero
    L, a, b = v
    return (L * 0xFF00 / 100 / 65535, (a + 128) * 256 / 65535, (b + 128) * 256 / 65535)


def lab_v2_inv(n):
    return (n[0] * 65535 * 100 / 0xFF00, n[1] * 65535 / 256 - 128, n[2] * 65535 / 256 - 128)


def lab_v4(v):      # lutAtoB and lutBtoA: L 0..100 to 0..1, a and b -128..127 to 0..1
    L, a, b = v
    return (L / 100, (a + 128) / 255, (b + 128) / 255)


def lab_v4_inv(n):
    return (n[0] * 100, n[1] * 255 - 128, n[2] * 255 - 128)


def xyz_enc(v):     # u1Fixed15: 1.0 is 0x8000
    return tuple(min(1.0, max(0.0, c * 32768 / 65535)) for c in v)


def xyz_dec(n):
    return tuple(c * 65535 / 32768 for c in n)


def clamp01(v):
    return tuple(min(1.0, max(0.0, c)) for c in v)


def grid(points, fn, out_channels):
    """CLUT samples, first input channel slowest; fn takes normalized inputs and gives normalized outputs."""
    samples = []

    def walk(prefix):
        if len(prefix) == len(points):
            out = fn(tuple(prefix))
            assert len(out) == out_channels
            samples.extend(clamp01(out))
            return
        n = points[len(prefix)]
        for i in range(n):
            walk(prefix + [i / (n - 1)])
    walk([])
    return samples


def lut16(inputs, outputs, points, fn, in_curve, out_curve, matrix=None, in_entries=256, out_entries=256):
    m = matrix or ((1, 0, 0), (0, 1, 0), (0, 0, 1))
    data = b"mft2\0\0\0\0" + struct.pack(">BBBB", inputs, outputs, points, 0) + b"".join(s15(m[r][c]) for r in range(3) for c in range(3))
    data += struct.pack(">HH", in_entries, out_entries)
    for ch in range(inputs):
        data += b"".join(struct.pack(">H", int(round(in_curve(ch, i / (in_entries - 1)) * 65535))) for i in range(in_entries))
    data += b"".join(struct.pack(">H", int(round(v * 65535))) for v in grid([points] * inputs, fn, outputs))
    for ch in range(outputs):
        data += b"".join(struct.pack(">H", int(round(out_curve(ch, i / (out_entries - 1)) * 65535))) for i in range(out_entries))
    return data


def lut8(inputs, outputs, points, fn, in_curve, out_curve, matrix=None):
    m = matrix or ((1, 0, 0), (0, 1, 0), (0, 0, 1))
    data = b"mft1\0\0\0\0" + struct.pack(">BBBB", inputs, outputs, points, 0) + b"".join(s15(m[r][c]) for r in range(3) for c in range(3))
    for ch in range(inputs):
        data += bytes(int(round(in_curve(ch, i / 255) * 255)) for i in range(256))
    data += bytes(int(round(v * 255)) for v in grid([points] * inputs, fn, outputs))
    for ch in range(outputs):
        data += bytes(int(round(out_curve(ch, i / 255) * 255)) for i in range(256))
    return data


def clut_element(points, fn, outputs, precision):
    data = bytes(points) + b"\0" * (16 - len(points)) + bytes([precision, 0, 0, 0])
    values = grid(points, fn, outputs)
    data += bytes(int(round(v * 255)) for v in values) if precision == 1 else b"".join(struct.pack(">H", int(round(v * 65535))) for v in values)
    return pad4(data)


def lut_ab(tag, inputs, outputs, b_curves, matrix=None, m_curves=None, clut=None, a_curves=None):
    """lutAtoBType (tag "mAB ") or lutBtoAType ("mBA "): the elements in file order B, matrix, M, CLUT, A; offsets 0 when absent."""
    data = bytearray(tag + b"\0\0\0\0" + struct.pack(">BBH", inputs, outputs, 0) + b"\0" * 20)
    offsets = []
    for element in (b"".join(b_curves), matrix and b"".join(s15(v) for v in matrix), m_curves and b"".join(m_curves), clut, a_curves and b"".join(a_curves)):
        if element:
            offsets.append(len(data))
            data += pad4(element)
        else:
            offsets.append(0)
    struct.pack_into(">5I", data, 12, *offsets)
    return bytes(data)


def identity_curve(ch, x):
    return x


# ---------------------------------------------------------------------------------------------------------------------------
# The synthetic profiles


def synthetic():
    out = {}
    v2, v4 = 0x02100000, 0x04300000
    srgb_curve = para(3, 2.4, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045)
    colorants = [("rXYZ", 0), ("gXYZ", 1), ("bXYZ", 2)]

    # Matrix-shaper, v4: every para type, one per curve, and a chad tag for a D55 display.
    d55 = (0.95682, 1.0, 0.92149)
    chad = ((1.0219, 0.0167, -0.0198), (0.0193, 0.9949, -0.0064), (-0.0040, 0.0063, 0.8966))
    tags = [("desc", mluc("Egami synthetic matrix-shaper v4, para curves")), ("cprt", mluc("Public domain")), ("wtpt", xyz_type(*D50)),
            ("chad", sf32_type([chad[r][c] for r in range(3) for c in range(3)]))]
    tags += [(s, xyz_type(*(SRGB_D50[r][i] for r in range(3)))) for s, i in colorants]
    tags += [("rTRC", para(0, 2.2)), ("gTRC", para(1, 2.0, 1.02, -0.02)), ("bTRC", para(4, 2.4, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045, 0.001, 0.002))]
    out["matrix-para-v4"] = profile(v4, "mntr", "RGB ", "XYZ ", tags)

    # Matrix-shaper, v2: curv tables of different lengths and a single gamma (v2 has no para curves).
    tags = [("desc", desc_v2("Egami synthetic matrix-shaper v2, curv tables")), ("cprt", text_type("Public domain")), ("wtpt", xyz_type(*D50))]
    tags += [(s, xyz_type(*(SRGB_D50[r][i] for r in range(3)))) for s, i in colorants]
    tags += [("rTRC", curv_table([(i / 1023) ** 2.2 for i in range(1024)])), ("gTRC", curv_gamma(1.8)),
             ("bTRC", curv_table([(i / 16) ** 2.6 for i in range(17)]))]
    out["matrix-curv-v2"] = profile(v2, "mntr", "RGB ", "XYZ ", tags)

    # RGB, v2, lut16 with Lab PCS, an input class profile whose white is D65 (so absolute colorimetric differs), distinct
    # perceptual (less saturated) and colorimetric tables, and the reverse tables.
    def a2b(sat):
        return lambda rgb: lab_v2(saturate(lab(rgb_to_xyz(rgb, 1.0)), sat))

    def b2a(sat):
        return lambda n: xyz_to_rgb(xyz_of_lab(*saturate(lab_v2_inv(n), 1 / sat)), 1.0)
    gamma_in = lambda ch, x: x ** (2.2 + 0.1 * ch)                     # noqa: E731
    gamma_out = lambda ch, x: x ** (1 / (2.2 + 0.1 * ch))              # noqa: E731
    tags = [("desc", desc_v2("Egami synthetic RGB lut16 v2")), ("cprt", text_type("Public domain")), ("wtpt", xyz_type(*D65)),
            ("A2B0", lut16(3, 3, 17, a2b(0.85), gamma_in, identity_curve)), ("A2B1", lut16(3, 3, 17, a2b(1.0), gamma_in, identity_curve)),
            ("B2A0", lut16(3, 3, 17, b2a(0.85), identity_curve, gamma_out, out_entries=4096)),
            ("B2A1", lut16(3, 3, 17, b2a(1.0), identity_curve, gamma_out, out_entries=4096))]
    out["rgb-lut16-v2"] = profile(v2, "scnr", "RGB ", "Lab ", tags, intent=1)

    # RGB, v2, lut8 with XYZ PCS: the B2A table has a matrix (lut8 and lut16 apply it only to XYZ input).
    xyz_matrix = ((0.9, 0.05, 0.05), (0.05, 0.9, 0.05), (0.02, 0.03, 0.95))
    tags = [("desc", desc_v2("Egami synthetic RGB lut8 v2")), ("cprt", text_type("Public domain")), ("wtpt", xyz_type(*D50)),
            ("A2B0", lut8(3, 3, 9, lambda rgb: xyz_enc(rgb_to_xyz(rgb, 1.0)), lambda ch, x: x ** 2.2, identity_curve)),
            ("B2A0", lut8(3, 3, 9, lambda n: xyz_to_rgb(mul(inv(xyz_matrix), xyz_dec(n)), 1.0), identity_curve,
                          lambda ch, x: x ** (1 / 2.2), matrix=xyz_matrix))]
    out["rgb-lut8-v2"] = profile(v2, "mntr", "RGB ", "XYZ ", tags)

    # RGB, v4, lutAtoB / lutBtoA with Lab PCS: A curves, a two-byte CLUT with unequal grids, M curves, a matrix with offsets, B
    # curves; perceptual and colorimetric differ.
    def mab(sat, precision):
        return lut_ab(b"mAB ", 3, 3, [para(0, 1.0)] * 3, matrix=(0.98, 0.01, 0.01, 0.0, 0.97, 0.03, 0.02, 0.0, 0.98, 0.0, 0.004, -0.004),
                      m_curves=[para(0, 1.1), para(0, 0.95), para(0, 1.0)],
                      clut=clut_element((9, 11, 7), lambda rgb: lab_v4(saturate(lab(rgb_to_xyz(rgb, 1.0)), sat)), 3, precision),
                      a_curves=[srgb_curve, para(2, 2.2, 1.0, 0.0, 0.0), para(1, 2.4, 1.0, 0.0)])

    def mba(sat):
        return lut_ab(b"mBA ", 3, 3, [para(0, 1.0)] * 3, clut=clut_element((17, 17, 17), lambda n: xyz_to_rgb(xyz_of_lab(*saturate(lab_v4_inv(n), 1 / sat)), 1.0), 3, 2),
                      a_curves=[curv_table([(i / 255) ** (1 / 2.2) for i in range(256)])] * 3)
    tags = [("desc", mluc("Egami synthetic RGB lutAtoB v4")), ("cprt", mluc("Public domain")), ("wtpt", xyz_type(*D50)),
            ("A2B0", mab(0.85, 2)), ("A2B1", mab(1.0, 2)), ("B2A0", mba(0.85)), ("B2A1", mba(1.0))]
    out["rgb-mab-v4"] = profile(v4, "mntr", "RGB ", "Lab ", tags)

    # RGB, v4, lutAtoB with XYZ PCS and a one-byte CLUT; no M curves or matrix. No B2A: an input-only profile.
    tags = [("desc", mluc("Egami synthetic RGB lutAtoB v4, XYZ PCS")), ("cprt", mluc("Public domain")), ("wtpt", xyz_type(*D50)),
            ("A2B0", lut_ab(b"mAB ", 3, 3, [curv_gamma(1.0)] * 3, clut=clut_element((5, 6, 7), lambda rgb: xyz_enc(rgb_to_xyz(rgb, 2.0)), 3, 1), a_curves=[para(0, 1.1)] * 3))]
    out["rgb-mab-xyz-v4"] = profile(v4, "scnr", "RGB ", "XYZ ", tags)

    # CMYK, v2, lut16 with Lab PCS: 9-point four-dimensional A2B tables, 17-point B2A, perceptual and colorimetric distinct.
    def cmyk_a2b(sat):
        return lambda cmyk: lab_v2(saturate(lab(cmyk_to_xyz(cmyk)), sat))

    def cmyk_b2a(sat):
        return lambda n: xyz_to_cmyk(xyz_of_lab(*saturate(lab_v2_inv(n), 1 / sat)))
    tags = [("desc", desc_v2("Egami synthetic CMYK lut16 v2")), ("cprt", text_type("Public domain")), ("wtpt", xyz_type(*cmyk_to_xyz((0, 0, 0, 0))))]
    tags += [("A2B0", lut16(4, 3, 9, cmyk_a2b(0.9), identity_curve, identity_curve)), ("A2B1", lut16(4, 3, 9, cmyk_a2b(1.0), identity_curve, identity_curve)),
             ("A2B2", lut16(4, 3, 9, cmyk_a2b(1.1), identity_curve, identity_curve)),
             ("B2A0", lut16(3, 4, 17, cmyk_b2a(0.9), identity_curve, identity_curve)), ("B2A1", lut16(3, 4, 17, cmyk_b2a(1.0), identity_curve, identity_curve)),
             ("B2A2", lut16(3, 4, 17, cmyk_b2a(1.1), identity_curve, identity_curve))]
    out["cmyk-lut16-v2"] = profile(v2, "prtr", "CMYK", "Lab ", tags)

    # CMYK, v4, lutAtoB / lutBtoA: A curves on the inks, a one-byte CLUT, no matrix; B2A with a two-byte CLUT and A curves out.
    def cmyk_mab(sat):
        return lut_ab(b"mAB ", 4, 3, [para(0, 1.0)] * 3, clut=clut_element((7, 7, 7, 9), lambda cmyk: lab_v4(saturate(lab(cmyk_to_xyz(cmyk)), sat)), 3, 1),
                      a_curves=[para(0, 1.0), para(0, 1.05), para(0, 0.95), para(0, 1.0)])

    def cmyk_mba(sat):
        return lut_ab(b"mBA ", 3, 4, [para(0, 1.0)] * 3, clut=clut_element((13, 15, 15), lambda n: xyz_to_cmyk(xyz_of_lab(*saturate(lab_v4_inv(n), 1 / sat))), 4, 2),
                      a_curves=[para(0, 1.0)] * 4)
    tags = [("desc", mluc("Egami synthetic CMYK lutAtoB v4")), ("cprt", mluc("Public domain")), ("wtpt", xyz_type(*D50))]
    tags += [("A2B0", cmyk_mab(0.9)), ("A2B1", cmyk_mab(1.0)), ("B2A0", cmyk_mba(0.9)), ("B2A1", cmyk_mba(1.0))]
    out["cmyk-mab-v4"] = profile(v4, "prtr", "CMYK", "Lab ", tags)

    # Gray: a v4 para type 3 curve, and a v2 table with a D65 white on an input class profile.
    tags = [("desc", mluc("Egami synthetic gray v4, para")), ("cprt", mluc("Public domain")), ("wtpt", xyz_type(*D50)), ("kTRC", para(3, 2.2, 0.95, 0.05, 1 / 10, 0.08))]
    out["gray-para-v4"] = profile(v4, "mntr", "GRAY", "XYZ ", tags)
    tags = [("desc", desc_v2("Egami synthetic gray v2, table")), ("cprt", text_type("Public domain")), ("wtpt", xyz_type(*D65)), ("kTRC", curv_table([(i / 99) ** 1.9 for i in range(100)]))]
    out["gray-curv-v2"] = profile(v2, "scnr", "GRAY", "XYZ ", tags)

    # Gray with a lut16 A2B0 and Lab PCS: one input channel.
    tags = [("desc", desc_v2("Egami synthetic gray lut16 v2")), ("cprt", text_type("Public domain")), ("wtpt", xyz_type(*D50)),
            ("A2B0", lut16(1, 3, 33, lambda g: lab_v2((lab((0, g[0] ** 2.2, 0))[0], 0.0, 0.0)), identity_curve, identity_curve)),
            ("B2A0", lut16(3, 1, 33, lambda n: (min(1.0, xyz_of_lab(lab_v2_inv(n)[0], 0, 0)[1]) ** (1 / 2.2),), identity_curve, identity_curve))]
    out["gray-lut16-v2"] = profile(v2, "mntr", "GRAY", "Lab ", tags)
    return out


# ---------------------------------------------------------------------------------------------------------------------------


def inputs():
    """The samples each model is transformed from: grids with the corners, and pseudo-random points."""
    state = 12345

    def rand():
        nonlocal state
        state = (state * 1103515245 + 12345) & 0x7FFFFFFF
        return state / 0x7FFFFFFF
    rgb = [(r / 5, g / 5, b / 5) for r in range(6) for g in range(6) for b in range(6)] + [(rand(), rand(), rand()) for _ in range(40)]
    gray = [(i / 63,) for i in range(64)]
    cmyk = [(c / 3, m / 3, y / 3, k / 3) for c in range(4) for m in range(4) for y in range(4) for k in range(4)] + [(rand(), rand(), rand(), rand()) for _ in range(44)]
    return {"RGB ": rgb, "GRAY": gray, "CMYK": cmyk}


MODEL = {"RGB ": "rgb", "GRAY": "gray", "CMYK": "cmyk"}


def main():
    os.makedirs(os.path.join(ICC, "synthetic"), exist_ok=True)
    os.makedirs(REF, exist_ok=True)
    os.makedirs(WORK, exist_ok=True)
    for stale in os.listdir(WORK):
        if stale.endswith(".f32"):
            os.remove(os.path.join(WORK, stale))
    for name, data in synthetic().items():
        with open(os.path.join(ICC, "synthetic", name + ".icc"), "wb") as f:
            f.write(data)

    samples = inputs()
    for space, values in samples.items():
        with open(os.path.join(REF, f"inputs-{MODEL[space]}.f32"), "wb") as f:
            f.write(b"".join(struct.pack("<f", v) for point in values for v in point))
    with open(os.path.join(WORK, "harness.c"), "w", newline="\n") as f:
        f.write(HARNESS)

    profiles = sorted(f"{d}/{n}" for d in ("compact", "synthetic") for n in os.listdir(os.path.join(ICC, d)) if n.endswith(".icc"))
    srgb = f"{ICC}/compact/sRGB-v4.icc"

    # First pass: what Little CMS reads from each profile.
    script = f"cd /repo && apk add -q {PACKAGES} >/dev/null && gcc -O2 -o /tmp/h {WORK}/harness.c -llcms2 && /tmp/h info " + " ".join(f"{ICC}/{p}" for p in profiles)
    script += f" > {WORK}/info.jsonl && apk list -I lcms2 > {WORK}/version.txt"
    repo = os.getcwd()
    subprocess.run(["docker", "run", "--rm", "-v", f"{repo}:/repo", IMAGE, "sh", "-c", script], check=True)
    infos = [json.loads(line) for line in open(os.path.join(WORK, "info.jsonl"))]

    # Second pass: the transforms.
    manifest = {"tool": open(os.path.join(WORK, "version.txt")).read().split()[0], "flags": "cmsFLAGS_NOOPTIMIZE | cmsFLAGS_NOCACHE, double", "profiles": []}
    jobs = []
    for path, info in zip(profiles, infos):
        space = info["colorSpace"]
        model = MODEL[space]
        count = len(samples[space])
        cases = []
        for intent in range(4):
            cases.append({"source": path, "target": "lab", "intent": intent, "bpc": False, "inputs": model})
            cases.append({"source": path, "target": "srgb", "intent": intent, "bpc": False, "inputs": model})
        for intent in (0, 1):
            cases.append({"source": path, "target": "srgb", "intent": intent, "bpc": True, "inputs": model})
            for bpc in (False, True):
                cases.append({"source": "srgb", "target": path, "intent": intent, "bpc": bpc, "inputs": "rgb"})
        name = path.replace("/", "-")[:-4]
        for i, case in enumerate(cases):
            src = srgb if case["source"] == "srgb" else f"{ICC}/{case['source']}"
            dst = "lab" if case["target"] == "lab" else srgb if case["target"] == "srgb" else f"{ICC}/{case['target']}"
            n = len(samples["RGB "]) if case["inputs"] == "rgb" else count
            jobs.append(f"{src} {dst} {case['intent']} {int(case['bpc'])} {n} {REF}/inputs-{case['inputs']}.f32 {WORK}/{name}.{i}.f32")
            case["count"] = n
        manifest["profiles"].append({"file": path, "lcms": info, "outputs": f"{name}.f32.gz", "cases": cases})
    with open(os.path.join(WORK, "jobs.txt"), "w", newline="\n") as f:
        f.write("\n".join(jobs) + "\n")
    script = f"cd /repo && apk add -q {PACKAGES} >/dev/null && gcc -O2 -o /tmp/h {WORK}/harness.c -llcms2 && /tmp/h jobs {WORK}/jobs.txt"
    subprocess.run(["docker", "run", "--rm", "-v", f"{repo}:/repo", IMAGE, "sh", "-c", script], check=True)

    for entry in manifest["profiles"]:
        name = entry["outputs"][:-7]
        data, kept = b"", []
        for i, case in enumerate(entry["cases"]):
            path = os.path.join(WORK, f"{name}.{i}.f32")
            if os.path.exists(path):
                with open(path, "rb") as f:
                    data += f.read()
                kept.append(case)
        entry["cases"] = kept
        with gzip.GzipFile(os.path.join(REF, entry["outputs"]), "wb", 9, mtime=0) as f:
            f.write(data)
    with open(os.path.join(REF, "manifest.json"), "w", newline="\n") as f:
        json.dump(manifest, f, indent=1)
    print("Little CMS:", manifest["tool"])
    print("Wrote", len(manifest["profiles"]), "profiles,", sum(len(p["cases"]) for p in manifest["profiles"]), "transforms to", REF)


if __name__ == "__main__":
    main()
