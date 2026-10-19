"""A small HEIF reader and writer for building the Phase 2.9.4 corpus: items with their properties, references and data, read
from and written to ISO BMFF (ISO/IEC 14496-12, 23008-12), plus the hvcC record and NAL units of ISO/IEC 14496-15. Used by
heiccorpus.py to wrap HM's and x265's HEVC streams in HEIF files, to cut grids of phone photos down to a few tiles and to turn
items back into Annex B streams for HM and dec265. Written from the specifications; it handles what the corpus needs, nothing
more."""
import struct


def box(kind, body):
    return struct.pack(">I4s", 8 + len(body), kind.encode("latin-1")) + body


def full_box(kind, version, flags, body):
    return box(kind, struct.pack(">I", version << 24 | flags) + body)


def boxes(data, start, end):
    """Yields (type, body start, box end) for the boxes in data[start:end]."""
    pos = start
    while pos + 8 <= end:
        size, kind = struct.unpack(">I4s", data[pos:pos + 8])
        head = 8
        if size == 1:
            size, head = struct.unpack(">Q", data[pos + 8:pos + 16])[0], 16
        elif size == 0:
            size = end - pos
        if size < head or pos + size > end:
            raise ValueError(f"bad box {kind!r} at {pos}")
        yield kind.decode("latin-1"), pos + head, pos + size
        pos += size


def uint(data, pos, n):
    return int.from_bytes(data[pos:pos + n], "big") if n else 0


def cstring(data, pos, end):
    stop = data.find(b"\0", pos, end)
    stop = end if stop < 0 else stop
    return data[pos:stop].decode("utf-8", "replace"), stop + 1


class Item:
    def __init__(self, item_id, item_type, data=b"", name="", hidden=False, content_type=None, content_encoding=None,
                 uri=None, idat=False):
        self.id, self.type, self.data, self.name, self.hidden = item_id, item_type, data, name, hidden
        self.content_type, self.content_encoding, self.uri, self.idat = content_type, content_encoding, uri, idat
        self.props = []  # (whole property box, essential)
        self.refs = {}  # reference type -> [item ids], in order

    def prop(self, kind):
        """The body of the first associated property of this type, or None."""
        for raw, _ in self.props:
            if raw[4:8].decode("latin-1") == kind:
                return raw[8:]
        return None

    def set_prop(self, raw, essential=False):
        """Replaces the first property of raw's type, or appends raw."""
        kind = raw[4:8]
        for i, (old, e) in enumerate(self.props):
            if old[4:8] == kind:
                self.props[i] = (raw, e)
                return
        self.props.append((raw, essential))


class Heif:
    def __init__(self, major="heic", minor=0, compatible=("mif1", "heic")):
        self.major, self.minor, self.compatible = major, minor, list(compatible)
        self.items = []
        self.primary = None
        self.extra_meta = []  # whole boxes kept as they are (grpl, ...)
        self.extra_top = []  # whole top-level boxes kept as they are (moov for sequences, ...), written after meta

    def item(self, item_id):
        return next(i for i in self.items if i.id == item_id)

    def next_id(self):
        return max([i.id for i in self.items], default=0) + 1

    def add(self, item_type, data=b"", **kwargs):
        item = Item(self.next_id(), item_type, data, **kwargs)
        self.items.append(item)
        return item

    def remove(self, ids):
        ids = set(ids)
        self.items = [i for i in self.items if i.id not in ids]
        for i in self.items:
            i.refs = {k: [t for t in v if t not in ids] for k, v in i.refs.items()}
            i.refs = {k: v for k, v in i.refs.items() if v}


def read(data):
    """Reads a HEIF file's ftyp and meta box into a Heif (items with their data). Raises on what it cannot read."""
    top = list(boxes(data, 0, len(data)))
    heif = Heif()
    meta = None
    for kind, s, e in top:
        if kind == "ftyp":
            heif.major, heif.minor = data[s:s + 4].decode("latin-1"), uint(data, s + 4, 4)
            heif.compatible = [data[p:p + 4].decode("latin-1") for p in range(s + 8, e, 4)]
        elif kind == "meta":
            meta = (s, e)
        elif kind not in ("mdat", "free", "skip"):
            heif.extra_top.append(data[s - 8:e])
    if meta is None:
        raise ValueError("no meta box")
    parts = {}
    for kind, s, e in boxes(data, meta[0] + 4, meta[1]):
        if kind in ("hdlr", "pitm", "iinf", "iloc", "iref", "iprp", "idat", "dinf"):
            parts[kind] = (s, e)
        else:
            heif.extra_meta.append(data[s - 8:e])
    s, e = parts["pitm"]
    heif.primary = uint(data, s + 4, 2 if data[s] == 0 else 4)
    by_id = {}
    s, e = parts["iinf"]
    for kind, bs, be in boxes(data, s + 4 + (2 if data[s] == 0 else 4), e):
        v = data[bs]
        if kind != "infe" or v < 2:
            continue
        p = bs + 4
        n = 2 if v == 2 else 4
        item = Item(uint(data, p, n), data[p + n + 2:p + n + 6].decode("latin-1"), hidden=bool(data[bs + 3] & 1))
        item.name, p = cstring(data, p + n + 6, be)
        if item.type == "mime":
            item.content_type, p = cstring(data, p, be)
            if p < be:
                item.content_encoding, p = cstring(data, p, be)
        elif item.type == "uri ":
            item.uri, p = cstring(data, p, be)
        by_id[item.id] = item
        heif.items.append(item)
    idat = data[parts["idat"][0]:parts["idat"][1]] if "idat" in parts else b""
    s, e = parts["iloc"]
    v = data[s]
    p = s + 4
    off_size, len_size = data[p] >> 4, data[p] & 15
    base_size, index_size = data[p + 1] >> 4, (data[p + 1] & 15) if v in (1, 2) else 0
    p += 2
    n = 2 if v < 2 else 4
    count = uint(data, p, n)
    p += n
    for _ in range(count):
        item_id = uint(data, p, n)
        p += n
        method = 0
        if v in (1, 2):
            method = uint(data, p, 2) & 15
            p += 2
        p += 2
        base = uint(data, p, base_size)
        p += base_size
        extents = []
        extent_count = uint(data, p, 2)
        p += 2
        for _ in range(extent_count):
            p += index_size
            off = uint(data, p, off_size)
            p += off_size
            length = uint(data, p, len_size)
            p += len_size
            extents.append((base + off, length))
        if item_id in by_id:
            source = idat if method == 1 else data
            by_id[item_id].data = b"".join(source[o:o + ln] if ln else source[o:] for o, ln in extents)
            by_id[item_id].idat = method == 1
    if "iref" in parts:
        s, e = parts["iref"]
        n = 2 if data[s] == 0 else 4
        for kind, bs, be in boxes(data, s + 4, e):
            src = uint(data, bs, n)
            targets = [uint(data, bs + n + 2 + i * n, n) for i in range(uint(data, bs + n, 2))]
            if src in by_id:
                by_id[src].refs.setdefault(kind, []).extend(targets)
    if "iprp" in parts:
        s, e = parts["iprp"]
        sub = list(boxes(data, s, e))
        ipco = next((bs, be) for k, bs, be in sub if k == "ipco")
        props = [data[bs - 8:be] for _, bs, be in boxes(data, *ipco)]
        for k, bs, be in sub:
            if k != "ipma":
                continue
            v, flags = data[bs], uint(data, bs + 1, 3)
            p = bs + 8
            for _ in range(uint(data, bs + 4, 4)):
                item_id = uint(data, p, 2 if v < 1 else 4)
                p += 2 if v < 1 else 4
                n_assoc = data[p]
                p += 1
                for _ in range(n_assoc):
                    if flags & 1:
                        raw = uint(data, p, 2)
                        essential, index = raw >> 15, raw & 0x7FFF
                        p += 2
                    else:
                        essential, index = data[p] >> 7, data[p] & 0x7F
                        p += 1
                    if index and item_id in by_id:
                        by_id[item_id].props.append((props[index - 1], bool(essential)))
    return heif


def write(heif):
    """Writes a Heif: ftyp, meta (hdlr, pitm, iinf, iloc, iref, iprp, idat and the kept boxes), the kept top-level boxes, mdat."""
    big = any(i.id > 0xFFFF for i in heif.items)
    ftyp = box("ftyp", heif.major.encode() + struct.pack(">I", heif.minor) + b"".join(c.encode() for c in heif.compatible))
    hdlr = full_box("hdlr", 0, 0, b"\0" * 4 + b"pict" + b"\0" * 12 + b"\0")
    pitm = full_box("pitm", 1 if big else 0, 0, struct.pack(">I" if big else ">H", heif.primary))
    infes = b""
    for i in heif.items:
        body = struct.pack(">I" if big else ">H", i.id) + b"\0\0" + i.type.encode("latin-1") + i.name.encode() + b"\0"
        if i.type == "mime":
            body += (i.content_type or "").encode() + b"\0"
            if i.content_encoding:
                body += i.content_encoding.encode() + b"\0"
        elif i.type == "uri ":
            body += (i.uri or "").encode() + b"\0"
        infes += full_box("infe", 3 if big else 2, 1 if i.hidden else 0, body)
    iinf = full_box("iinf", 0, 0, struct.pack(">H", len(heif.items)) + infes) if len(heif.items) < 0x10000 else \
        full_box("iinf", 1, 0, struct.pack(">I", len(heif.items)) + infes)
    refs = b""
    for i in heif.items:
        for kind, targets in i.refs.items():
            fmt = ">I" if big else ">H"
            refs += box(kind, struct.pack(fmt, i.id) + struct.pack(">H", len(targets)) + b"".join(struct.pack(fmt, t) for t in targets))
    iref = full_box("iref", 1 if big else 0, 0, refs) if refs else b""
    unique, assoc = [], b""
    for i in heif.items:
        indices = []
        for raw, essential in i.props:
            if raw not in unique:
                unique.append(raw)
            indices.append((unique.index(raw) + 1, essential))
        assoc += (struct.pack(">I", i.id) if big else struct.pack(">H", i.id)) + bytes([len(indices)])
        assoc += b"".join(struct.pack(">H", (0x8000 if e else 0) | n) for n, e in indices)
    ipma = full_box("ipma", 1 if big else 0, 1, struct.pack(">I", len(heif.items)) + assoc)
    iprp = box("iprp", box("ipco", b"".join(unique)) + ipma)
    idat_body = b""
    places = {}
    for i in heif.items:
        if i.idat:
            places[i.id] = (1, len(idat_body))
            idat_body += i.data
    idat = box("idat", idat_body) if idat_body else b""
    extra = b"".join(heif.extra_meta)
    tail = b"".join(heif.extra_top)

    def iloc(mdat_start):
        body = bytes([0x44, 0x00]) + struct.pack(">H", len(heif.items))
        pos = mdat_start
        for i in heif.items:
            method, offset = places.get(i.id, (0, None))
            if method == 0:
                offset, pos = pos, pos + len(i.data)
            body += struct.pack(">I" if big else ">H", i.id) + struct.pack(">HHH", method, 0, 1) + struct.pack(">II", offset, len(i.data))
        return full_box("iloc", 2 if big else 1, 0, body)

    head = lambda start: ftyp + box("meta", struct.pack(">I", 0) + hdlr + pitm + iloc(start) + iinf + iref + iprp + idat + extra) + tail
    start = len(head(0)) + 8
    mdat = b"".join(i.data for i in heif.items if not i.idat)
    return head(start) + box("mdat", mdat)


# ---------------------------------------------------------------- properties

def ispe(width, height):
    return full_box("ispe", 0, 0, struct.pack(">II", width, height))


def pixi(*depths):
    return full_box("pixi", 0, 0, bytes([len(depths), *depths]))


def nclx(primaries, transfer, matrix, full_range):
    return box("colr", b"nclx" + struct.pack(">HHHB", primaries, transfer, matrix, 0x80 if full_range else 0))


def icc(profile):
    return box("colr", b"prof" + profile)


def irot(quarter_turns):
    return box("irot", bytes([quarter_turns & 3]))


def imir(axis):
    return box("imir", bytes([axis & 1]))


def clap(width, height, horiz_off=(0, 1), vert_off=(0, 1)):
    """Clean aperture of width x height, centered with the given offsets (fractions) from the image center."""
    return box("clap", struct.pack(">8i", width, 1, height, 1, horiz_off[0], horiz_off[1], vert_off[0], vert_off[1]))


def auxc(urn, subtype=b""):
    return full_box("auxC", 0, 0, urn.encode() + b"\0" + subtype)


def grid_data(rows, columns, width, height):
    wide = width > 0xFFFF or height > 0xFFFF
    return bytes([0, 1 if wide else 0, rows - 1, columns - 1]) + struct.pack(">II" if wide else ">HH", width, height)


def read_grid(data):
    wide = data[1] & 1
    w, h = struct.unpack(">II" if wide else ">HH", data[4:12 if wide else 8])
    return data[2] + 1, data[3] + 1, w, h


# ---------------------------------------------------------------- HEVC NAL units and hvcC

def split_annexb(stream):
    """Splits an Annex B byte stream into NAL units (start codes removed)."""
    out, pos, n = [], 0, len(stream)
    starts = []
    while True:
        k = stream.find(b"\0\0\1", pos)
        if k < 0:
            break
        starts.append(k + 3)
        pos = k + 3
    for i, s in enumerate(starts):
        end = starts[i + 1] - 3 if i + 1 < len(starts) else n
        while end > s and stream[end - 1] == 0:
            end -= 1
        out.append(stream[s:end])
    return out


def nal_type(nal):
    return nal[0] >> 1 & 63


def rbsp(nal):
    """The NAL unit's payload with emulation prevention bytes removed (header kept)."""
    out, zeros = bytearray(), 0
    for b in nal:
        if zeros >= 2 and b == 3:
            zeros = 0
            continue
        out.append(b)
        zeros = zeros + 1 if b == 0 else 0
    return bytes(out)


class Bits:
    def __init__(self, data, pos=0):
        self.data, self.pos = data, pos * 8

    def u(self, n):
        v = 0
        for _ in range(n):
            v = v << 1 | (self.data[self.pos >> 3] >> (7 - (self.pos & 7)) & 1)
            self.pos += 1
        return v

    def ue(self):
        zeros = 0
        while self.u(1) == 0:
            zeros += 1
        return (1 << zeros) - 1 + self.u(zeros)


def sps_info(sps):
    """Reads the fields hvcC repeats from an SPS NAL unit: the general profile_tier_level bytes, chroma format, bit depths, size."""
    data = rbsp(sps)
    b = Bits(data, 2)
    b.u(4)
    sub_layers = b.u(3)
    b.u(1)
    ptl = data[3:15]  # profile space, tier, idc, compatibility flags, constraint flags, level: 12 bytes
    b.pos = 15 * 8
    if sub_layers:
        present = [(b.u(1), b.u(1)) for _ in range(sub_layers)]
        for _ in range(sub_layers, 8):
            b.u(2)
        for profile, level in present:
            if profile:
                b.u(88)
            if level:
                b.u(8)
    b.ue()
    chroma = b.ue()
    if chroma == 3:
        b.u(1)
    width, height = b.ue(), b.ue()
    if b.u(1):
        sub_w = 2 if chroma in (1, 2) else 1
        sub_h = 2 if chroma == 1 else 1
        left, right, top, bottom = b.ue(), b.ue(), b.ue(), b.ue()
        width -= sub_w * (left + right)
        height -= sub_h * (top + bottom)
    return {"ptl": ptl, "chroma": chroma, "luma_depth": b.ue() + 8, "chroma_depth": b.ue() + 8, "width": width,
            "height": height, "sub_layers": sub_layers + 1}


def hvcc(params, length_size=4):
    """Builds an hvcC property from parameter set NAL units (VPS, SPS, PPS and any prefix SEI), in that order."""
    sps = next(n for n in params if nal_type(n) == 33)
    info = sps_info(sps)
    body = bytes([1]) + info["ptl"][:1] + info["ptl"][1:5] + info["ptl"][5:11] + info["ptl"][11:12]
    body += struct.pack(">H", 0xF000) + bytes([0xFC, 0xFC | info["chroma"], 0xF8 | info["luma_depth"] - 8,
                                               0xF8 | info["chroma_depth"] - 8])
    body += struct.pack(">H", 0) + bytes([info["sub_layers"] << 3 | 1 << 2 | (length_size - 1)])
    arrays = []
    for t in (32, 33, 34, 39):
        group = [n for n in params if nal_type(n) == t]
        if group:
            arrays.append(bytes([0x80 | t]) + struct.pack(">H", len(group)) + b"".join(struct.pack(">H", len(n)) + n for n in group))
    return box("hvcC", body + bytes([len(arrays)]) + b"".join(arrays))


def read_hvcc(body):
    """Reads an hvcC body: (length size, NAL units in its arrays, fields)."""
    fields = {"profile_space": body[1] >> 6, "tier": body[1] >> 5 & 1, "profile": body[1] & 31,
              "compatibility": uint(body, 2, 4), "constraints": body[6:12].hex(), "level": body[12],
              "chroma_format": body[16] & 3, "luma_depth": (body[17] & 7) + 8, "chroma_depth": (body[18] & 7) + 8,
              "temporal_layers": body[21] >> 3 & 7, "length_size": (body[21] & 3) + 1}
    nals, p = [], 23
    for _ in range(body[22]):
        count = uint(body, p + 1, 2)
        p += 3
        for _ in range(count):
            n = uint(body, p, 2)
            nals.append(body[p + 2:p + 2 + n])
            p += 2 + n
    return fields["length_size"], nals, fields


def length_prefixed(nals, length_size=4):
    return b"".join(len(n).to_bytes(length_size, "big") + n for n in nals)


def split_length_prefixed(data, length_size=4):
    out, p = [], 0
    while p + length_size <= len(data):
        n = uint(data, p, length_size)
        out.append(data[p + length_size:p + length_size + n])
        p += length_size + n
    return out


def item_annexb(item):
    """An hvc1 item as an Annex B stream: its hvcC parameter sets, then its NAL units."""
    length_size, params, _ = read_hvcc(item.prop("hvcC"))
    nals = params + split_length_prefixed(item.data, length_size)
    return b"".join(b"\0\0\0\1" + n for n in nals)


def wrap_stream(stream, width=None, height=None, extra_props=()):
    """A HEIF file with one hvc1 item holding an Annex B stream's first picture (parameter sets into hvcC, the rest as data)."""
    nals = split_annexb(stream)
    params = [n for n in nals if nal_type(n) in (32, 33, 34)]
    picture = []
    for n in nals:
        t = nal_type(n)
        if t < 32 or t in (39, 40):
            if t < 32 and picture and any(nal_type(m) < 32 for m in picture) and (rbsp(n)[2] >> 7):
                break  # first_slice_segment_in_pic_flag of the next picture
            picture.append(n)
    info = sps_info(next(n for n in params if nal_type(n) == 33))
    heif = Heif()
    item = heif.add("hvc1", length_prefixed(picture))
    item.props = [(hvcc(params), True), (ispe(width or info["width"], height or info["height"]), False)]
    if info["chroma"] == 0:
        item.props.append((pixi(info["luma_depth"]), False))
    else:
        item.props.append((pixi(info["luma_depth"], info["chroma_depth"], info["chroma_depth"]), False))
    item.props += [(p, e) for p, e in extra_props]
    heif.primary = item.id
    return heif
