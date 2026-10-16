"""Builds the Phase 2.6.5 n-colour test profiles, written byte by byte here, and checks them with Little CMS.

Profiles (tests/images/icc/ncolor, apart from the synthetic folder, which tools/make-icc-references.py reads whole):
  7clr-mab-v4.icc      a seven-ink output profile (Cyan, Magenta, Yellow, Black, Orange, Green, Violet), v4, XYZ PCS: lutAtoB
                       with dot-gain A curves and a 5-point 16-bit CLUT over the seven inks, lutBtoA with cube-root B curves
                       and a 33-point CLUT, a colorant table (clrt)
  2clr-lut16-v2.icc    a duotone output profile (Black, Warm Red), v2, XYZ PCS: lut16 both ways and a colorant table

The inks follow a simple model: each ink absorbs a share of three light bands at full tint, with a little dot gain, and the
bands multiply (as inks printed over one another do), then convert to XYZ through the sRGB primaries, paper being the D50
white. B2A separates a colour by a fixed, smooth rule (see separate()). The profiles
need not be accurate: Little CMS's reading of the tables is the reference, and Egami's own round trips are checked on them.

After writing them, the script runs Little CMS 2 (double precision, unoptimised) in Docker and prints the round-trip error:
random ink values to Lab through A2B, back to inks through B2A, and to Lab again, in CIEDE2000. Task 4 expects at most 1.

Usage, from the repo root with Docker running:
  python tools/make-ink-profiles.py
"""
import importlib.util
import math
import os
import random
import struct
import subprocess

spec = importlib.util.spec_from_file_location("icc", os.path.join(os.path.dirname(__file__), "make-icc-references.py"))
icc = importlib.util.module_from_spec(spec)
spec.loader.exec_module(icc)

OUT = "tests/images/icc/ncolor"
WORK = ".cache/refs-inks"
IMAGE = "alpine:3.20"
PACKAGES = "lcms2-dev gcc musl-dev"

# Share of the red, green and blue bands each ink absorbs at full tint.
SEVEN = [("Cyan", (0.85, 0.25, 0.05)), ("Magenta", (0.10, 0.85, 0.30)), ("Yellow", (0.02, 0.08, 0.90)), ("Black", (0.92, 0.92, 0.92)),
         ("Orange", (0.05, 0.65, 0.92)), ("Green", (0.85, 0.10, 0.80)), ("Violet", (0.60, 0.88, 0.15))]
DUO = [("Black", (0.92, 0.92, 0.92)), ("Warm Red", (0.05, 0.85, 0.75))]



def gain(t):
    return t + 0.12 * t * (1 - t)


def gain_slope(t):
    return 1 + 0.12 * (1 - 2 * t)


def xyz_of_gains(inks, gains):
    r = [1.0, 1.0, 1.0]
    for (_, absorb), g in zip(inks, gains):
        for b in range(3):
            r[b] *= 1 - g * absorb[b]
    return icc.mul(icc.SRGB_D50, r)


def lab_of(inks, tints):
    return icc.lab(xyz_of_gains(inks, [gain(t) for t in tints]))


def density(absorb, t):
    return -math.log(1 - gain(t) * absorb)


def solve(inks, target, free, tints):
    """Tints for the free inks (the others as given) that match the target band densities, by Gauss-Newton within 0..1."""
    t = list(tints)
    for _ in range(60):
        rows, residual = [], []
        for b in range(3):
            residual.append(target[b] - sum(density(inks[i][1][b], t[i]) for i in range(len(inks))))
            rows.append([inks[i][1][b] * gain_slope(t[i]) / (1 - gain(t[i]) * inks[i][1][b]) for i in free])
        n = len(free)
        jtj = [[sum(rows[b][p] * rows[b][q] for b in range(3)) + (1e-12 if p == q else 0) for q in range(n)] for p in range(n)]
        jtr = [sum(rows[b][p] * residual[b] for b in range(3)) for p in range(n)]
        step = gauss(jtj, jtr)
        moved = 0.0
        for j, i in enumerate(free):
            new = min(1.0, max(0.0, t[i] + max(-0.25, min(0.25, step[j]))))
            moved = max(moved, abs(new - t[i]))
            t[i] = new
        if moved < 1e-10:
            break
    return t


def gauss(a, y):
    n = len(y)
    m = [row[:] + [y[i]] for i, row in enumerate(a)]
    for c in range(n):
        p = max(range(c, n), key=lambda r: abs(m[r][c]))
        m[c], m[p] = m[p], m[c]
        if abs(m[c][c]) < 1e-12:
            continue
        for r in range(n):
            if r != c:
                f = m[r][c] / m[c][c]
                for k in range(c, n + 1):
                    m[r][k] -= f * m[c][k]
    return [m[i][n] / m[i][i] if abs(m[i][i]) > 1e-12 else 0.0 for i in range(n)]


def separate(inks, xyz):
    """XYZ to tints, smoothly, so the B2A table interpolates well. Black takes the part of the grey component above a
    threshold; with seven inks, Orange, Green and Violet then take half of what the two process inks around them share;
    Cyan, Magenta and Yellow (Warm Red for the duotone) make up the rest exactly."""
    lin = icc.mul(icc.inv(icc.SRGB_D50), xyz)
    target = [-math.log(min(1.0, max(1e-4, v))) for v in lin]
    names = [n for n, _ in inks]
    black = names.index("Black")
    t = [0.0] * len(inks)
    grey = max(0.0, min(target) - 0.25) * 0.9
    g = min(1.0, (1 - math.exp(-grey)) / inks[black][1][0])
    t[black] = (1.12 - math.sqrt(1.12 ** 2 - 4 * 0.12 * g)) / (2 * 0.12)   # gain(k) = g, solved for k
    if len(inks) == 2:
        return solve(inks, target, [1 - black], t)
    cmy = [names.index(n) for n in ("Cyan", "Magenta", "Yellow")]
    t = solve(inks, target, cmy, t)
    c, m, y = (t[i] for i in cmy)
    for name, share in (("Orange", min(m, y)), ("Green", min(c, y)), ("Violet", min(c, m))):
        t[names.index(name)] = share / 2
    return solve(inks, target, cmy, t)


def clrt(inks):
    data = b"clrt\0\0\0\0" + struct.pack(">I", len(inks))
    for name, _ in inks:
        pcs = icc.xyz_enc(xyz_of_gains(inks, [1.0 if n == name else 0.0 for n, _ in inks]))
        data += name.encode("ascii").ljust(32, b"\0") + b"".join(struct.pack(">H", int(round(v * 65535))) for v in pcs)
    return data


# Both profiles use the XYZ PCS: the model is then multilinear in the dot-gained tints, which the A curves (or lut16 input
# tables) apply, so the CLUT holds it closely. B2A spaces its grid by the cube root of XYZ, as L* does, through its B curves.
CUBE_ROOT = [i / 4095 for i in range(4096)]


def from_pcs(inks):
    return lambda v: separate(inks, icc.xyz_dec(tuple(c ** 3 for c in v)))


def seven():
    gain_curve = icc.curv_table([gain(i / 255) for i in range(256)])
    a2b = icc.lut_ab(b"mAB ", 7, 3, [icc.para(0, 1.0)] * 3, clut=icc.clut_element((5,) * 7, lambda g: icc.xyz_enc(xyz_of_gains(SEVEN, g)), 3, 2),
                     a_curves=[gain_curve] * 7)
    b2a = icc.lut_ab(b"mBA ", 3, 7, [icc.para(0, 1 / 3)] * 3, clut=icc.clut_element((33, 33, 33), from_pcs(SEVEN), 7, 2),
                     a_curves=[icc.para(0, 1.0)] * 7)
    tags = [("desc", icc.mluc("Egami synthetic 7-colour lutAtoB v4")), ("cprt", icc.mluc("Public domain")), ("wtpt", icc.xyz_type(*icc.D50)),
            ("A2B0", a2b), ("A2B1", a2b), ("B2A0", b2a), ("B2A1", b2a), ("clrt", clrt(SEVEN))]
    return icc.profile(0x04300000, "prtr", "7CLR", "XYZ ", tags)


def duo():
    a2b = icc.lut16(2, 3, 17, lambda g: icc.xyz_enc(xyz_of_gains(DUO, g)), lambda ch, x: gain(x), icc.identity_curve)
    b2a = icc.lut16(3, 2, 33, from_pcs(DUO), lambda ch, x: x ** (1 / 3), icc.identity_curve, in_entries=4096)
    tags = [("desc", icc.desc_v2("Egami synthetic duotone lut16 v2")), ("cprt", icc.text_type("Public domain")), ("wtpt", icc.xyz_type(*icc.D50)),
            ("A2B0", a2b), ("A2B1", a2b), ("B2A0", b2a), ("B2A1", b2a), ("clrt", clrt(DUO))]
    return icc.profile(0x02400000, "prtr", "2CLR", "XYZ ", tags)


HARNESS = r'''
#include <stdio.h>
#include <stdlib.h>
#include <lcms2.h>
/* argv: profile channels count inputs.f64 lab.f64 back.f64 relab.f64 -- ink values 0..1 in, Lab, inks, Lab out (relative colorimetric). */
int main(int argc, char **argv) {
  int n = atoi(argv[2]), count = atoi(argv[3]);
  cmsHPROFILE p = cmsOpenProfileFromFile(argv[1], "r"), lab = cmsCreateLab4Profile(NULL);  /* Lab D50 */
  if (!p) { fprintf(stderr, "cannot open %s\n", argv[1]); return 1; }
  cmsUInt32Number ink = FLOAT_SH(1) | COLORSPACE_SH(PT_MCH2 + n - 2) | CHANNELS_SH(n) | BYTES_SH(0);
  cmsHTRANSFORM to = cmsCreateTransform(p, ink, lab, TYPE_Lab_DBL, 1, cmsFLAGS_NOOPTIMIZE | cmsFLAGS_NOCACHE);
  cmsHTRANSFORM from = cmsCreateTransform(lab, TYPE_Lab_DBL, p, ink, 1, cmsFLAGS_NOOPTIMIZE | cmsFLAGS_NOCACHE);
  if (!to || !from) { fprintf(stderr, "no transform\n"); return 1; }
  double *a = malloc(sizeof(double) * count * n), *l = malloc(sizeof(double) * count * 3), *b = malloc(sizeof(double) * count * n), *m = malloc(sizeof(double) * count * 3);
  FILE *f = fopen(argv[4], "rb"); fread(a, sizeof(double), count * n, f); fclose(f);
  double scale = n >= 5 ? 100 : 1;   /* Little CMS reads floats of 5 to 15 inks as 0..100, of fewer as 0..1 */
  for (int i = 0; i < count * n; i++) a[i] *= scale;
  cmsDoTransform(to, a, l, count); cmsDoTransform(from, l, b, count); cmsDoTransform(to, b, m, count);
  for (int i = 0; i < count * n; i++) b[i] /= scale;
  f = fopen(argv[5], "wb"); fwrite(l, sizeof(double), count * 3, f); fclose(f);
  f = fopen(argv[6], "wb"); fwrite(b, sizeof(double), count * n, f); fclose(f);
  f = fopen(argv[7], "wb"); fwrite(m, sizeof(double), count * 3, f); fclose(f);
  return 0;
}
'''


def de2000(lab1, lab2):
    L1, a1, b1 = lab1
    L2, a2, b2 = lab2
    c1, c2 = math.hypot(a1, b1), math.hypot(a2, b2)
    cm = (c1 + c2) / 2
    g = 0.5 * (1 - math.sqrt(cm ** 7 / (cm ** 7 + 25 ** 7)))
    a1p, a2p = a1 * (1 + g), a2 * (1 + g)
    c1p, c2p = math.hypot(a1p, b1), math.hypot(a2p, b2)
    h1p = math.degrees(math.atan2(b1, a1p)) % 360
    h2p = math.degrees(math.atan2(b2, a2p)) % 360
    dl, dc = L2 - L1, c2p - c1p
    if c1p * c2p == 0:
        dh = 0.0
    elif abs(h2p - h1p) <= 180:
        dh = h2p - h1p
    else:
        dh = h2p - h1p - 360 if h2p > h1p else h2p - h1p + 360
    dH = 2 * math.sqrt(c1p * c2p) * math.sin(math.radians(dh / 2))
    lm, cmp = (L1 + L2) / 2, (c1p + c2p) / 2
    if c1p * c2p == 0:
        hm = h1p + h2p
    elif abs(h1p - h2p) <= 180:
        hm = (h1p + h2p) / 2
    else:
        hm = (h1p + h2p + 360) / 2 if h1p + h2p < 360 else (h1p + h2p - 360) / 2
    t = 1 - 0.17 * math.cos(math.radians(hm - 30)) + 0.24 * math.cos(math.radians(2 * hm)) + 0.32 * math.cos(math.radians(3 * hm + 6)) - 0.20 * math.cos(math.radians(4 * hm - 63))
    sl = 1 + 0.015 * (lm - 50) ** 2 / math.sqrt(20 + (lm - 50) ** 2)
    sc, sh = 1 + 0.045 * cmp, 1 + 0.015 * cmp * t
    rt = -2 * math.sqrt(cmp ** 7 / (cmp ** 7 + 25 ** 7)) * math.sin(math.radians(60 * math.exp(-(((hm - 275) / 25) ** 2))))
    return math.sqrt((dl / sl) ** 2 + (dc / sc) ** 2 + (dH / sh) ** 2 + rt * (dc / sc) * (dH / sh))


def check(name, inks):
    """Round trips inside the gamut: random colours of the model (process inks 5% to 95%, Black up to 60%) separated as the
    B2A table does, kept when separating them again reproduces them, so every sample is a colour the table can reach. Near the
    gamut's edges an interpolated B2A cannot be exact (a cell there mixes colours it can and cannot reach), hence the margin."""
    channels = len(inks)
    rng = random.Random(2605)
    samples = []
    while len(samples) < 2000:
        t = [0.05 + 0.9 * rng.random() for _ in inks]
        t = [0.6 * v if n == "Black" else v if n in ("Cyan", "Magenta", "Yellow", "Warm Red") else 0.0 for (n, _), v in zip(inks, t)]
        separated = separate(inks, xyz_of_gains(inks, [gain(v) for v in t]))
        xyz = xyz_of_gains(inks, [gain(v) for v in separated])
        if de2000(icc.lab(xyz), lab_of(inks, separate(inks, xyz))) < 0.01:
            samples.append(separated)
    with open(os.path.join(WORK, f"{name}.in.f64"), "wb") as f:
        f.write(b"".join(struct.pack("<d", v) for s in samples for v in s))
    script = (f"cd /repo && apk add -q {PACKAGES} >/dev/null && gcc -O2 -o /tmp/h {WORK}/harness.c -llcms2 && /tmp/h {OUT}/{name}.icc {channels} "
              f"{len(samples)} {WORK}/{name}.in.f64 {WORK}/{name}.lab.f64 {WORK}/{name}.back.f64 {WORK}/{name}.relab.f64")
    subprocess.run(["docker", "run", "--rm", "-v", f"{os.getcwd()}:/repo", IMAGE, "sh", "-c", script], check=True)

    def read(suffix, width):
        data = open(os.path.join(WORK, f"{name}.{suffix}.f64"), "rb").read()
        values = struct.unpack(f"<{len(data) // 8}d", data)
        return [values[i:i + width] for i in range(0, len(values), width)]
    lab1, lab2 = read("lab", 3), read("relab", 3)
    model = [lab_of(inks, s) for s in samples]
    trip = sorted(de2000(a, b) for a, b in zip(lab1, lab2))
    table = sorted(de2000(a, b) for a, b in zip(model, lab1))
    print(f"{name}: round trip dE2000 max {trip[-1]:.3f}, 99th {trip[int(len(trip) * 0.99)]:.3f}, mean {sum(trip) / len(trip):.3f}; "
          f"A2B table against the model max {table[-1]:.3f}, mean {sum(table) / len(table):.3f}")


def main():
    os.makedirs(WORK, exist_ok=True)
    os.makedirs(OUT, exist_ok=True)
    with open(os.path.join(OUT, "7clr-mab-v4.icc"), "wb") as f:
        f.write(seven())
    with open(os.path.join(OUT, "2clr-lut16-v2.icc"), "wb") as f:
        f.write(duo())
    with open(os.path.join(WORK, "harness.c"), "w", newline="\n") as f:
        f.write(HARNESS)
    check("7clr-mab-v4", SEVEN)
    check("2clr-lut16-v2", DUO)


if __name__ == "__main__":
    main()
