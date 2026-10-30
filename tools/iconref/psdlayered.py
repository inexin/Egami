"""Builds the layered PSD files of the corpus (tests/images/psd/layered-*) and their references, inside egami-iconref.

Called by psdcorpus.py. Every file is written byte by byte from Adobe's Photoshop File Format Specification by the writer here and
in psdcorpus.py, from ops/duck.png (public domain). The files:
  - layered-rgb8.psd: a background, layers partly off the canvas, a user mask (default color 255), a disabled mask, fill opacity,
    a hidden layer, an empty layer, clipping (two layers clipped to a base), an isolated group with its own mode and opacity, a
    pass-through group with opacity, and every channel compression (raw, PackBits, Deflate, Deflate with prediction) in turn;
  - layered-rgb8.psb, layered-rgb16.psd (layers in Lr16), layered-rgb32.psd (Lr32), layered-gray8.psd, layered-gray16.psd,
    layered-cmyk8.psd, layered-cmyk16.psd, layered-lab8.psd: fewer layers in the other modes, depths and PSB;
  - layered-mode-<name>.psd: one per Photoshop blend mode, a layer with transparency at opacity 80 % over the background;
  - layered-fill.psd: a solid color fill layer (SoCo) and a shape layer (SoCo bounded by a vector mask, vmsk), without pixels;
  - layered-nocomposite.psd: layers without a real composite (resource 1057), its stored image a white placeholder.

References, per file, in tests/reference/psd:
  - <file>.layerpixels.raw.gz and layered.json ("layers"): each layer's pixels as ReadLayers gives them (the natural type with
    alpha, little-endian), checked here against psd-tools' own decode of the layer;
  - <file>.flat.f32.gz and layered.json ("flat"): the flattened image as straight RGBA (gray as RGBA too) 32-bit floats, from
    psd-tools' composite() ("psd-tools") or from the ink math below for CMYK ("ink"); the file's stored composite is the same
    image, quantized, so the stored and the flattened composite agree.
"""
import gzip
import json
import struct
import zlib

import numpy as np
from PIL import Image

import psdcorpus as pc

W, H = pc.W, pc.H
CHANNELS = {"rgb": 3, "gray": 1, "cmyk": 4, "lab": 3}
MODE_KEYS = {
    "normal": b"norm", "dissolve": b"diss", "darken": b"dark", "multiply": b"mul ", "colorburn": b"idiv", "linearburn": b"lbrn",
    "darkercolor": b"dkCl", "lighten": b"lite", "screen": b"scrn", "colordodge": b"div ", "lineardodge": b"lddg",
    "lightercolor": b"lgCl", "overlay": b"over", "softlight": b"sLit", "hardlight": b"hLit", "vividlight": b"vLit",
    "linearlight": b"lLit", "pinlight": b"pLit", "hardmix": b"hMix", "difference": b"diff", "exclusion": b"smud",
    "subtract": b"fsub", "divide": b"fdiv", "hue": b"hue ", "saturation": b"sat ", "color": b"colr", "luminosity": b"lum ",
}


def ramp_image(w, h, seed):
    """A smooth color image and a transparency with a soft hole and edge, values in 0..1."""
    yy, xx = np.mgrid[0:h, 0:w]
    u, v = (xx + 0.5) / w, (yy + 0.5) / h
    rgb = np.stack([(0.5 + 0.5 * np.sin(6.0 * u + seed)), v, (0.5 + 0.5 * np.cos(4.0 * v + 3 * u + seed))], axis=2)
    d = np.hypot(u - 0.5, v - 0.5)
    alpha = np.clip(np.minimum((0.5 - d) * 6, (d - 0.08) * 8), 0, 1)
    return rgb, alpha


def duck(w, h, flip=False):
    rgb = pc.RGB.astype(np.float64) / 255
    if flip:
        rgb = rgb[::-1, ::-1]
    return np.ascontiguousarray(np.resize(rgb, (h, w, 3)) if (h, w) != rgb.shape[:2] else rgb)


def quantize(x, depth):
    """0..1 floats to stored samples: 8 and 16-bit integers, or 32-bit floats."""
    if depth == 8:
        return np.round(np.clip(x, 0, 1) * 255).astype(np.int64)
    if depth == 16:
        return np.round(np.clip(x, 0, 1) * 65535).astype(np.int64)
    return x.astype(np.float32)


def unit(x, depth):
    return x.astype(np.float64) / (255 if depth == 8 else 65535 if depth == 16 else 1)


def be_f64(v):
    return struct.pack(">d", v)


def unicode_string(text):
    u = (text + "\0").encode("utf-16-be")
    return pc.be32(len(u) // 2) + u


def key_id(key):
    return pc.be32(0) + key if len(key) == 4 else pc.be32(len(key)) + key


def descriptor(cls, items):
    """An action descriptor: name, class id and items, each (key, type, value)."""
    out = unicode_string("") + key_id(cls) + pc.be32(len(items))
    for key, kind, value in items:
        out += key_id(key) + kind
        if kind == b"doub":
            out += be_f64(value)
        elif kind == b"long":
            out += struct.pack(">i", value)
        elif kind == b"bool":
            out += bytes([1 if value else 0])
        elif kind == b"Objc":
            out += descriptor(*value)
        else:
            raise ValueError(kind)
    return out


def soco(color, mode):
    """A solid color fill block's data: version 16 and a descriptor with the color in the document's mode."""
    if mode == "rgb":
        clr = (b"RGBC", [(b"Rd  ", b"doub", color[0] * 255), (b"Grn ", b"doub", color[1] * 255), (b"Bl  ", b"doub", color[2] * 255)])
    elif mode == "gray":
        clr = (b"Grsc", [(b"Gry ", b"doub", (1 - color[0]) * 100)])
    else:
        clr = (b"CMYC", [(b"Cyn ", b"doub", color[0] * 100), (b"Mgnt", b"doub", color[1] * 100), (b"Ylw ", b"doub", color[2] * 100), (b"Blck", b"doub", color[3] * 100)])
    return pc.be32(16) + descriptor(b"null", [(b"Clr ", b"Objc", clr)])


def vmsk(points):
    """A vector mask (version 3) of one closed subpath through points (x, y) in pixels, its knots corners."""
    def fixed(v):
        return struct.pack(">i", int(round(v * (1 << 24))))
    rec = lambda selector, body: pc.be16(selector) + body + b"\0" * (24 - len(body))
    out = pc.be32(3) + pc.be32(0)
    out += rec(6, b"") + rec(8, pc.be16(0)) + rec(0, pc.be16(len(points)))
    for x, y in points:
        p = fixed(y / H) + fixed(x / W)
        out += rec(2, p + p + p)
    return out


def block(key, data):
    if len(data) % 2:
        data += b"\0"
    return b"8BIM" + key + pc.be32(len(data)) + data


class Layer:
    def __init__(self, name, rect=None, color=None, alpha=None, *, blend=b"norm", opacity=255, clipping=0, hidden=False, fill=None,
                 mask=None, mask_rect=None, mask_default=0, mask_disabled=False, section=None, section_blend=None, blocks=b"", compression=1,
                 density=None, ranges=None, adjust=None):
        self.name, self.rect, self.color, self.alpha = name, rect, color, alpha
        self.blend, self.opacity, self.clipping, self.hidden, self.fill = blend, opacity, clipping, hidden, fill
        self.mask, self.mask_rect, self.mask_default, self.mask_disabled = mask, mask_rect, mask_default, mask_disabled
        self.section, self.section_blend, self.blocks, self.compression = section, section_blend, blocks, compression
        self.density, self.ranges, self.adjust = density, ranges, adjust


def record_and_data(layer, depth, version, counter):
    """A layer record and its channel data; each channel takes the next compression of the counter when the layer asks for 'mix'."""
    top, left, bottom, right = layer.rect if layer.rect else (0, 0, 0, 0)
    planes = []
    if layer.color is not None:
        planes += [(c, p) for c, p in enumerate(layer.color)]
    if layer.alpha is not None:
        planes.append((-1, layer.alpha))
    if layer.mask is not None:
        planes.append((-2, layer.mask))
    channels = []
    for cid, plane in planes:
        comp = layer.compression if layer.compression != "mix" else counter.pop(0)
        counter.append(comp)
        data = pc.image_data([plane], depth, comp, version, plane.shape[1])
        channels.append((cid, data))
    out = struct.pack(">iiii", top, left, bottom, right) + pc.be16(len(channels))
    for cid, data in channels:
        out += struct.pack(">h", cid) + (pc.be32(len(data)) if version == 1 else struct.pack(">Q", len(data)))
    flags = (0x02 if layer.hidden else 0) | 0x08
    blend = layer.blend
    out += b"8BIM" + blend + bytes([layer.opacity, layer.clipping, flags, 0])
    if layer.mask is not None:
        mt, ml, mb, mr = layer.mask_rect
        flags = (0x02 if layer.mask_disabled else 0) | (0x10 if layer.density is not None else 0)
        mask = struct.pack(">iiii", mt, ml, mb, mr) + bytes([layer.mask_default, flags])
        mask += bytes([1, layer.density]) if layer.density is not None else b"\0\0"
    else:
        mask = b""
    ranges = b"".join(pc.be32(r) for r in layer.ranges) if layer.ranges else b""
    blocks = layer.blocks
    u = layer.name.encode("utf-16-be")
    blocks += block(b"luni", pc.be32(len(u) // 2) + u + b"\0" * (len(u) % 4 and 4 - len(u) % 4))
    if layer.fill is not None:
        blocks += block(b"iOpa", bytes([layer.fill, 0, 0, 0]))
    if layer.section is not None:
        body = pc.be32(layer.section)
        if layer.section_blend is not None:
            body += b"8BIM" + layer.section_blend
        blocks += block(b"lsct", body)
    extra = pc.be32(len(mask)) + mask + pc.be32(len(ranges)) + ranges + pc.pascal(layer.name.encode("ascii", "replace").decode("latin-1")[:31], 4) + blocks
    return out + pc.be32(len(extra)) + extra, b"".join(d for _, d in channels)


def write(path, mode, depth, layers, composite, *, version=1, real=True, resources=b""):
    """Writes a layered file; composite is the stored image's planes (color, then transparency when there is one)."""
    counter = [0, 1, 2, 3]
    records = [record_and_data(layer, depth, version, counter) for layer in layers]
    has_alpha = len(composite) > CHANNELS[mode]
    count = -len(records) if has_alpha else len(records)
    block_key = {16: b"Lr16", 32: b"Lr32"}.get(depth)
    pc.psd(path, mode, depth, composite, version=version, layers=records, count=count, layers_in_block=block_key, real=real, resources=resources)


def stored_composite(mode, depth, color, alpha):
    """The stored planes of a flattened image: color flattened onto white where it is transparent, then the transparency."""
    a = alpha[..., 0]
    opaque = bool(np.all(a >= 1 - 1e-6))
    if mode == "cmyk":
        planes = [quantize(1 - (color[..., c] * a), depth) for c in range(4)]
    else:
        planes = [quantize(color[..., c] * a + (1 - a), depth) for c in range(color.shape[2])]
    if not opaque:
        planes.append(quantize(a, depth))
    return planes


def layer_truth(layer, mode, depth):
    """The layer's pixels as ReadLayers gives them: (layout, little-endian bytes), or None without pixels."""
    if layer.color is None or layer.rect is None or layer.section is not None:
        return None
    h, w = layer.color[0].shape
    alpha = layer.alpha if layer.alpha is not None else None
    if mode == "cmyk":
        full = 255 if depth == 8 else 65535
        a = alpha if alpha is not None else np.full((h, w), full, np.int64)
        planes = [((full - c) * a + full // 2) // full for c in layer.color] + [a]
        dtype = np.uint8 if depth == 8 else "<u2"
        return ("Ink8" if depth == 8 else "Ink16"), np.stack(planes, axis=0).astype(dtype)
    if mode == "lab":
        rgb = np.zeros((h, w, 3))
        for y in range(h):
            for x in range(w):
                rgb[y, x] = pc.lab_to_srgb(layer.color[0][y, x] * 100.0 / 255, layer.color[1][y, x] - 128.0, layer.color[2][y, x] - 128.0)
        a = alpha if alpha is not None else np.full((h, w), 255)
        return "Rgba8", np.dstack([pc.unorm(rgb, 255), a]).astype(np.uint8)
    if mode == "gray" and depth != 32:
        full = 255 if depth == 8 else 65535
        a = alpha if alpha is not None else np.full((h, w), full)
        return ("GrayAlpha8" if depth == 8 else "GrayAlpha16"), np.dstack([layer.color[0], a]).astype(np.uint8 if depth == 8 else "<u2")
    if depth == 32:
        a = alpha if alpha is not None else np.ones((h, w), np.float32)
        colors = layer.color if mode == "rgb" else [layer.color[0]] * 3
        return "RgbaF32", np.dstack(list(colors) + [a]).astype("<f4")
    full = 255 if depth == 8 else 65535
    a = alpha if alpha is not None else np.full((h, w), full)
    return ("Rgba8" if depth == 8 else "Rgba16"), np.dstack(list(layer.color) + [a]).astype(np.uint8 if depth == 8 else "<u2")


def psd_tools_layers(path):
    from psd_tools import PSDImage
    doc = PSDImage.open(path)
    return {layer.name: layer for layer in doc.descendants()}


def check_against_psd_tools(path, layers, mode, depth):
    """psd-tools must read every layer's pixels as written (8 and 16-bit RGB and gray)."""
    if mode not in ("rgb", "gray") or depth == 32:
        return
    found = psd_tools_layers(path)
    full = 255 if depth == 8 else 65535
    for layer in layers:
        if layer.color is None or layer.section is not None:
            continue
        theirs = found[layer.name].numpy()
        mine = np.dstack(list(layer.color) + ([layer.alpha] if layer.alpha is not None else [])).astype(np.float64) / full
        if layer.alpha is None:
            theirs = theirs[..., : mine.shape[2]]
        if theirs.shape != mine.shape or np.abs(theirs - mine).max() > 0.5 / full:
            raise SystemExit(f"psd-tools reads layer {layer.name} of {path} differently")


def psd_tools_composite(path):
    from psd_tools import PSDImage
    from psd_tools.composite import composite
    color, _, alpha = composite(PSDImage.open(path))
    if color.shape[2] == 1:
        color = np.repeat(color, 3, axis=2)
    return np.ascontiguousarray(color.astype(np.float64)), np.ascontiguousarray(alpha.astype(np.float64))


def ink_composite(layers, depth):
    """Normal-mode layers over transparent paper on ink planes, premultiplied, bottom to top: ink = s + d(1 - αs)."""
    full = 255.0 if depth == 8 else 65535.0
    ink = np.zeros((H, W, 4))
    alpha = np.zeros((H, W, 1))
    for layer in layers:
        if layer.color is None or layer.hidden:
            continue
        top, left, bottom, right = layer.rect
        a = (layer.alpha / full if layer.alpha is not None else np.ones(layer.color[0].shape)) * layer.opacity / 255
        if layer.mask is not None:
            # The user mask on the layer's own grid: its values inside its rectangle, the default color outside.
            m = np.full(a.shape, layer.mask_default / 255.0)
            mt, ml, mb, mr = layer.mask_rect
            for yy in range(a.shape[0]):
                for xx in range(a.shape[1]):
                    cy, cx = top + yy, left + xx
                    if mt <= cy < mb and ml <= cx < mr:
                        m[yy, xx] = layer.mask[cy - mt, cx - ml] / full
            a = a * m
        s = np.stack([(full - c) / full for c in layer.color], axis=2) * a[..., None]
        y0, y1, x0, x1 = max(top, 0), min(bottom, H), max(left, 0), min(right, W)
        sv, av = s[y0 - top:y1 - top, x0 - left:x1 - left], a[y0 - top:y1 - top, x0 - left:x1 - left, None]
        ink[y0:y1, x0:x1] = sv + ink[y0:y1, x0:x1] * (1 - av)
        alpha[y0:y1, x0:x1] = av + alpha[y0:y1, x0:x1] * (1 - av)
    straight = np.where(alpha > 0, ink / np.maximum(alpha, 1e-12), 0)
    return straight, alpha


def range_weight(r, v):
    """A blending range's weight at values v (0 to 255): black low and high, white low and high, one byte each from the top."""
    b0, b1, w0, w1 = (r >> 24) & 255, (r >> 16) & 255, (r >> 8) & 255, r & 255
    out = np.ones_like(v)
    out = np.where(v < b0, 0, out)
    if b1 > b0:
        out = np.where((v >= b0) & (v < b1), (v - b0) / (b1 - b0), out)
    if w1 > w0:
        out = np.where((v > w0) & (v < w1), (w1 - v) / (w1 - w0), out)
    out = np.where(v >= w1, np.where(v <= w0, 1, 0), out)
    return out


def own_composite(layers, depth):
    """Egami's model, written out for the files psd-tools does not composite as Photoshop does: normal and multiply layers, a
    user mask with density, blending ranges, and clipping that keeps the base's coverage (the clipped layers blend onto the base
    as if it were opaque, then the result takes the base's alpha)."""
    full = 255.0 if depth == 8 else 65535.0
    color = np.zeros((H, W, 3))
    alpha = np.zeros((H, W, 1))

    def place(layer):
        top, left, bottom, right = layer.rect
        c = np.zeros((H, W, 3))
        a = np.zeros((H, W, 1))
        y0, y1, x0, x1 = max(top, 0), min(bottom, H), max(left, 0), min(right, W)
        lc = np.dstack(layer.color) / full
        la = (layer.alpha / full if layer.alpha is not None else np.ones(layer.color[0].shape))[..., None]
        c[y0:y1, x0:x1] = lc[y0 - top:y1 - top, x0 - left:x1 - left]
        a[y0:y1, x0:x1] = la[y0 - top:y1 - top, x0 - left:x1 - left]
        if layer.mask is not None:
            m = np.full((H, W, 1), layer.mask_default / 255.0)
            mt, ml, mb, mr = layer.mask_rect
            m[max(mt, 0):min(mb, H), max(ml, 0):min(mr, W)] = (layer.mask / full)[max(mt, 0) - mt:min(mb, H) - mt, max(ml, 0) - ml:min(mr, W) - ml, None]
            d = (layer.density if layer.density is not None else 255) / 255.0
            a = a * (1 - d * (1 - m))
        return c, a

    def blend(bc, ba, sc, sa, key):
        f = sc if key == b"norm" else bc * sc
        ra = sa + ba * (1 - sa)
        rc = np.where(ra > 0, (sc * sa * (1 - ba) + bc * ba * (1 - sa) + f * sa * ba) / np.maximum(ra, 1e-12), 0)
        return rc, ra

    i = 0
    while i < len(layers):
        layer = layers[i]
        if layer.adjust is not None:
            # An adjustment layer: the adjusted color mixed in by the layer's opacity; alpha stays.
            color = color + (layer.adjust(color) - color) * (layer.opacity / 255)
            i += 1
            continue
        c, a = place(layer)
        if layer.ranges:
            gray = lambda x: (0.3 * x[..., 0] + 0.59 * x[..., 1] + 0.11 * x[..., 2])[..., None]
            w = range_weight(layer.ranges[0], gray(c) * 255) * range_weight(layer.ranges[1], gray(color) * 255)
            for ch in range(3):
                w = w * range_weight(layer.ranges[2 + 2 * ch], c[..., ch:ch + 1] * 255) * range_weight(layer.ranges[3 + 2 * ch], color[..., ch:ch + 1] * 255)
            a = a * w
        # Clipped layers above this one blend onto it as if it were opaque, then take its coverage.
        j = i + 1
        gc = c.copy()
        while j < len(layers) and layers[j].clipping:
            cc, ca = place(layers[j])
            gc, _ = blend(gc, np.ones_like(a), cc, ca * layers[j].opacity / 255, layers[j].blend)
            j += 1
        color, alpha = blend(color, alpha, gc, a * layer.opacity / 255, layer.blend)
        i = j
    return color, alpha


def finish(name, mode, depth, layers, *, version=1, flat="psd-tools", real=True, resources=b""):
    """Writes the file with a placeholder, composites it, writes it again with the composite stored, and its references."""
    path = f"{pc.CORPUS}/{name}"
    channels = CHANNELS[mode]
    placeholder = [quantize(np.ones((H, W)), depth) for _ in range(channels)]
    write(path, mode, depth, layers, placeholder, version=version, real=real, resources=resources)
    entry = {"mode": mode, "depth": depth, "layers": []}
    if flat == "psd-tools":
        color, alpha = psd_tools_composite(path)
    elif flat == "ink":
        color, alpha = ink_composite(layers, depth)
    elif flat == "own":
        color, alpha = own_composite(layers, depth)
    else:
        color, alpha = None, None
    if color is not None:
        entry["flat"] = flat
        stored = stored_composite(mode, depth, color[..., :channels] if mode != "cmyk" else color, alpha)
        if real:
            write(path, mode, depth, layers, stored, version=version, real=real, resources=resources)
        if mode == "cmyk":
            rgba = np.dstack([color, alpha])
        else:
            rgba = np.dstack([color[..., :3], alpha])
        pc.write_gz(f"{pc.TARGET}/{name}.flat.f32.gz", rgba.astype("<f4").tobytes())
    check_against_psd_tools(path, layers, mode, depth)
    blobs = []
    for layer in layers:
        t = layer_truth(layer, mode, depth)
        if t is None:
            continue
        layout, pixels = t
        top, left, _, _ = layer.rect
        entry["layers"].append({"name": layer.name, "x": left, "y": top, "width": layer.color[0].shape[1], "height": layer.color[0].shape[0], "layout": layout})
        blobs.append(np.ascontiguousarray(pixels).tobytes())
    pc.write_gz(f"{pc.TARGET}/{name}.layerpixels.raw.gz", b"".join(blobs))
    return entry


def planes_of(rgb, alpha, mode, depth):
    """Stored color planes and transparency of a 0..1 RGB image in a mode: gray by Rec. 601 weights, CMYK by the naive formula."""
    if mode == "gray":
        color = [quantize(rgb @ np.array([0.299, 0.587, 0.114]), depth)]
    elif mode == "cmyk":
        k = 1 - rgb.max(axis=2)
        cmy = [(1 - rgb[..., c] - k) / np.maximum(1 - k, 1e-9) for c in range(3)]
        color = [quantize(1 - v, depth) for v in cmy + [k]]
    elif mode == "lab":
        lab = np.array(Image.fromarray(np.round(rgb * 255).astype(np.uint8)).convert("LAB"), dtype=np.int64)
        color = [lab[..., 0], lab[..., 1], lab[..., 2]]
    else:
        color = [quantize(rgb[..., c], depth) for c in range(3)]
    return color, (quantize(alpha, depth) if alpha is not None else None)


def build():
    manifest = {}

    def rect(top, left, h, w):
        return (top, left, top + h, left + w)

    def full_layers(mode, depth, rich):
        bg_color, _ = planes_of(duck(W, H), None, mode, depth)
        layers = [Layer("Background", rect(0, 0, H, W), bg_color, None, compression="mix")]
        rgb, alpha = ramp_image(40, 30, 0.0)
        c, a = planes_of(rgb, alpha, mode, depth)
        layers.append(Layer("Off canvas", rect(10, -6, 30, 40), c, a, blend=b"mul " if rich else b"norm", compression="mix"))
        rgb, alpha = ramp_image(24, 20, 1.0)
        c, a = planes_of(rgb, alpha, mode, depth)
        mask = quantize(np.tile(np.linspace(0, 1, 20)[None, :], (14, 1)), depth)
        layers.append(Layer("Masked", rect(4, 30, 20, 24), c, a, opacity=204, mask=mask, mask_rect=rect(8, 26, 14, 20), mask_default=255, compression="mix"))
        if not rich:
            return layers
        rgb, alpha = ramp_image(30, 16, 2.0)
        c, a = planes_of(rgb, alpha, mode, depth)
        layers.append(Layer("Disabled mask", rect(30, 2, 16, 30), c, a, mask=quantize(np.zeros((8, 8)), depth), mask_rect=rect(30, 2, 8, 8), mask_disabled=True, compression="mix"))
        layers.append(Layer("Fill 50", rect(20, 20, 16, 30), c, a, fill=128, blend=b"scrn", compression="mix"))
        layers.append(Layer("Hidden", rect(0, 0, 16, 30), c, a, hidden=True))
        layers.append(Layer("Empty"))
        # Clipping: a base with two layers clipped to it.
        # The base is opaque where it is at all: psd-tools composites clipped layers over a partly transparent base differently
        # from Photoshop's clipping (the base's coverage kept), which Egami's own tests check by hand.
        rgb, alpha = ramp_image(20, 20, 3.0)
        c, a = planes_of(rgb, (alpha > 0.5).astype(np.float64), mode, depth)
        layers.append(Layer("Clip base", rect(24, 40, 20, 20), c, a, opacity=230))
        stripes = np.tile((np.arange(26) // 3 % 2).astype(np.float64)[None, :], (26, 1))
        c2, _ = planes_of(np.dstack([stripes, 1 - stripes, stripes * 0.5]), None, mode, depth)
        layers.append(Layer("Clipped 1", rect(21, 37, 26, 26), c2, None, clipping=1, blend=b"mul "))
        c3, a3 = planes_of(np.full((10, 30, 3), 0.8), np.full((10, 30), 0.6), mode, depth)
        layers.append(Layer("Clipped 2", rect(30, 34, 10, 30), c3, a3, clipping=1))
        # An isolated group with a mode and opacity, and a pass-through group with opacity.
        layers.append(Layer("</Layer group>", section=3))
        rgb, alpha = ramp_image(36, 24, 4.0)
        c, a = planes_of(rgb, alpha, mode, depth)
        layers.append(Layer("Iso child 1", rect(2, 2, 24, 36), c, a, blend=b"over"))
        layers.append(Layer("Iso child 2", rect(12, 10, 24, 36), c, a, opacity=180))
        layers.append(Layer("Isolated", blend=b"mul ", opacity=178, section=1, section_blend=b"mul "))
        layers.append(Layer("</Layer group>", section=3))
        layers.append(Layer("Pass child", rect(6, 34, 24, 36), c, a))
        layers.append(Layer("Pass", blend=b"pass", opacity=153, section=2, section_blend=b"pass"))
        return layers

    manifest["layered-rgb8.psd"] = finish("layered-rgb8.psd", "rgb", 8, full_layers("rgb", 8, True))
    manifest["layered-rgb8.psb"] = finish("layered-rgb8.psb", "rgb", 8, full_layers("rgb", 8, False), version=2)
    manifest["layered-rgb16.psd"] = finish("layered-rgb16.psd", "rgb", 16, full_layers("rgb", 16, True))
    manifest["layered-rgb32.psd"] = finish("layered-rgb32.psd", "rgb", 32, full_layers("rgb", 32, False))
    manifest["layered-gray8.psd"] = finish("layered-gray8.psd", "gray", 8, full_layers("gray", 8, True))
    manifest["layered-gray16.psd"] = finish("layered-gray16.psd", "gray", 16, full_layers("gray", 16, False))
    manifest["layered-cmyk8.psd"] = finish("layered-cmyk8.psd", "cmyk", 8, full_layers("cmyk", 8, False), flat="ink")
    manifest["layered-cmyk16.psd"] = finish("layered-cmyk16.psd", "cmyk", 16, full_layers("cmyk", 16, False), flat="ink")
    manifest["layered-lab8.psd"] = finish("layered-lab8.psd", "lab", 8, full_layers("lab", 8, False)[:2], flat=None)
    manifest["layered-nocomposite.psd"] = finish("layered-nocomposite.psd", "rgb", 8, full_layers("rgb", 8, False), real=False)

    # One file per blend mode: a layer with transparency at 80 % over the background.
    bg_color, _ = planes_of(duck(W, H), None, "rgb", 8)
    rgb, alpha = ramp_image(W - 8, H - 6, 5.0)
    c, a = planes_of(rgb, alpha, "rgb", 8)
    for name, key in MODE_KEYS.items():
        if name == "dissolve":
            continue
        layers = [Layer("Background", (0, 0, H, W), bg_color, None), Layer(name, (3, 4, H - 3, W - 4), c, a, blend=key, opacity=204)]
        manifest[f"layered-mode-{name}.psd"] = finish(f"layered-mode-{name}.psd", "rgb", 8, layers)

    # A solid color fill layer and a shape layer, neither with pixels.
    layers = [
        Layer("Background", (0, 0, H, W), bg_color, None),
        Layer("Solid fill", None, None, None, opacity=128, blocks=block(b"SoCo", soco((0.9, 0.2, 0.1), "rgb"))),
        Layer("Shape", None, None, None, blend=b"mul ", blocks=block(b"SoCo", soco((0.1, 0.3, 0.8), "rgb")) + block(b"vmsk", vmsk([(10, 6), (54, 14), (24, 42)]))),
    ]
    manifest["layered-fill.psd"] = finish("layered-fill.psd", "rgb", 8, layers)

    # Clipping onto a partly transparent base, a mask with density, and blending ranges, against Egami's model written out.
    rgb, alpha = ramp_image(40, 30, 6.0)
    c, a = planes_of(rgb, alpha, "rgb", 8)
    stripes = np.tile((np.arange(48) // 4 % 2).astype(np.float64)[None, :], (36, 1))
    c2, _ = planes_of(np.dstack([stripes, 0.4 + 0.2 * stripes, 1 - stripes]), None, "rgb", 8)
    c3, a3 = planes_of(np.full((20, 64, 3), 0.7), np.full((20, 64), 0.6), "rgb", 8)
    layers = [
        Layer("Background", (0, 0, H, W), bg_color, None),
        Layer("Base", (8, 12, 38, 52), c, a, opacity=230),
        Layer("Clipped normal", (6, 8, 42, 56), c2, None, clipping=1, opacity=153),
        Layer("Clipped multiply", (20, 0, 40, 64), c3, a3, clipping=1, blend=b"mul "),
    ]
    manifest["layered-clip.psd"] = finish("layered-clip.psd", "rgb", 8, layers, flat="own")

    rgb, alpha = ramp_image(W, H, 7.0)
    c, a = planes_of(rgb, alpha, "rgb", 8)
    mask = quantize(np.tile(np.linspace(0, 1, W)[None, :], (H, 1)), 8)
    flipped, _ = planes_of(duck(W, H, flip=True), None, "rgb", 8)
    split = lambda b0, b1, w0, w1: (b0 << 24) | (b1 << 16) | (w0 << 8) | w1
    layers = [
        Layer("Background", (0, 0, H, W), bg_color, None),
        Layer("Density", (0, 0, H, W), c, a, mask=mask, mask_rect=(0, 0, H, W), density=128, opacity=220),
        # Gray: this layer from 40 (fading in to 90) to 255, the underlying to 150 (fading out to 220); green: this layer from
        # 20 (fading in to 60).
        Layer("Blend if", (0, 0, H, W), flipped, None, ranges=[split(40, 90, 255, 255), split(0, 0, 150, 220), 0x0000FFFF, 0x0000FFFF,
                                                                 split(20, 60, 255, 255), 0x0000FFFF, 0x0000FFFF, 0x0000FFFF]),
    ]
    manifest["layered-blendif.psd"] = finish("layered-blendif.psd", "rgb", 8, layers, flat="own")

    # Adjustment layers: levels, curves and invert, against the formulas written out.
    def levels_block(records):
        out = pc.be16(2)
        for k in range(29):
            r = records.get(k, (0, 255, 0, 255, 100))
            out += b"".join(pc.be16(v) for v in r)
        return block(b"levl", out)

    def level(r, v):
        x = np.clip((v * 255 - r[0]) / (r[1] - r[0]), 0, 1) ** (100.0 / r[4])
        return (r[2] + x * (r[3] - r[2])) / 255

    master, red = (20, 230, 10, 245, 140), (0, 200, 0, 255, 80)
    def apply_levels(c):
        out = c.copy()
        out[..., 0] = level(red, out[..., 0])
        return level(master, out)

    def curves_block(curves):
        bits = sum(1 << k for k in curves)
        out = bytes([0]) + pc.be16(1) + pc.be32(bits)
        for k in sorted(curves):
            out += pc.be16(len(curves[k])) + b"".join(pc.be16(o) + pc.be16(i) for i, o in curves[k])
        return block(b"curv", out)

    def spline(points, v):
        """A natural cubic spline through (input, output) points on 0..255, flat outside them."""
        xs = np.array([p[0] for p in points], float)
        ys = np.array([p[1] for p in points], float)
        n = len(xs)
        m = np.zeros(n)
        if n > 2:
            a = np.zeros((n, n))
            rhs = np.zeros(n)
            a[0, 0] = a[-1, -1] = 1
            for k in range(1, n - 1):
                a[k, k - 1] = xs[k] - xs[k - 1]
                a[k, k] = 2 * (xs[k + 1] - xs[k - 1])
                a[k, k + 1] = xs[k + 1] - xs[k]
                rhs[k] = 6 * ((ys[k + 1] - ys[k]) / (xs[k + 1] - xs[k]) - (ys[k] - ys[k - 1]) / (xs[k] - xs[k - 1]))
            m = np.linalg.solve(a, rhs)
        x = np.clip(v * 255, xs[0], xs[-1])
        k = np.clip(np.searchsorted(xs, x) , 1, n - 1)
        h = xs[k] - xs[k - 1]
        t0, t1 = (xs[k] - x) / h, (x - xs[k - 1]) / h
        y = t0 * ys[k - 1] + t1 * ys[k] + ((t0 ** 3 - t0) * m[k - 1] + (t1 ** 3 - t1) * m[k]) * h * h / 6
        return np.clip(y, 0, 255) / 255

    composite_curve, blue_curve = [(0, 0), (64, 40), (180, 210), (255, 255)], [(0, 30), (255, 220)]
    def apply_curves(c):
        out = c.copy()
        out[..., 2] = spline(blue_curve, out[..., 2])
        return spline(composite_curve, out)

    for kind, blocks, fn in (("levels", levels_block({0: master, 1: red}), apply_levels),
                             ("curves", curves_block({0: composite_curve, 3: blue_curve}), apply_curves),
                             ("invert", block(b"nvrt", b""), lambda c: 1 - c)):
        layers = [Layer("Background", (0, 0, H, W), bg_color, None), Layer(kind, None, None, None, opacity=204, blocks=blocks, adjust=fn)]
        manifest[f"layered-adjust-{kind}.psd"] = finish(f"layered-adjust-{kind}.psd", "rgb", 8, layers, flat="own")

    with open(f"{pc.TARGET}/layered.json", "w") as f:
        json.dump(manifest, f, indent=1, sort_keys=True)
        f.write("\n")
