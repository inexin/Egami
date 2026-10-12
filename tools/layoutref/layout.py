"""Lays out the Phase 2.8.2 corpus in Chromium and writes tests/reference/layout/<group>.json.gz.

Each case of tools/layoutref/corpus.py becomes an HTML block with @font-face rules for its fonts and CSS mapped from its style
(white-space: pre-wrap, hyphens: manual and the mapping in CSS below). Per width the page records, in CSS pixels relative to the
block's top-left corner:
  height, minContent, maxContent   the block's size and its min-content and max-content inline sizes
  graphemes                        [index, x, y, width, height] of each grapheme's text rectangle (Range.getBoundingClientRect)
  lines                            per line its first UTF-16 index and the line box: [over, baseline, under] along the block
                                   axis (y, or x in vertical modes, where over is the right side), read from empty inline boxes
                                   aligned top, baseline and bottom placed in the line one line at a time (null when no place
                                   keeps the layout and the neighbouring lines do not pin it)
  carets                           [index, x, y, height] of the collapsed range at each grapheme boundary (queries.carets)
  hits                             [x, y, index] from caretPositionFromPoint on a grid over every line (queries.hits)
  selections                       [start, end, [[x, y, width, height], ...]] from Range.getClientRects (queries.selections)
Lines are told apart by baseline: each grapheme's baseline comes from a probe of the same grapheme and style beside a
zero-size inline-block, so lines with overlapping glyph boxes (tight line heights) still separate.

Chromium runs with --font-render-hinting=none, which gives unhinted advances with subpixel positions; layout values are in
LayoutUnits of 1/64 pixel.

Usage (inside the egami-layoutref image, from tools/make-layout-references.py): python /w/tools/layoutref/layout.py /w [out]
"""
import gzip
import json
import os
import sys

sys.path.insert(0, os.path.dirname(__file__))
import corpus  # noqa: E402

from playwright.sync_api import sync_playwright  # noqa: E402

ROOT = sys.argv[1] if len(sys.argv) > 1 else "/w"
OUT = sys.argv[2] if len(sys.argv) > 2 else os.path.join(ROOT, "tests", "reference", "layout")
FONTS = os.path.join(ROOT, "tests", "fonts")

ENUM_CSS = {
    "align": ("text-align", {"Start": "start", "End": "end", "Left": "left", "Right": "right", "Center": "center", "Justify": "justify"}),
    "alignLast": ("text-align-last", {"Auto": "auto", "Start": "start", "End": "end", "Left": "left", "Right": "right",
                                      "Center": "center", "Justify": "justify"}),
    "justify": ("text-justify", {"Auto": "auto", "InterWord": "inter-word", "InterCharacter": "inter-character", "None": "none"}),
    "wordBreak": ("word-break", {"Normal": "normal", "BreakAll": "break-all", "KeepAll": "keep-all"}),
    "overflowWrap": ("overflow-wrap", {"Normal": "normal", "Anywhere": "anywhere", "BreakWord": "break-word"}),
    "lineBreak": ("line-break", {"Auto": "auto", "Loose": "loose", "Normal": "normal", "Strict": "strict", "Anywhere": "anywhere"}),
    "writingMode": ("writing-mode", {"HorizontalTb": "horizontal-tb", "VerticalRl": "vertical-rl", "VerticalLr": "vertical-lr"}),
    "textOrientation": ("text-orientation", {"Mixed": "mixed", "Upright": "upright", "Sideways": "sideways"}),
    "lineFill": ("text-wrap", {"Greedy": "wrap", "Balanced": "balance"}),
}


def line_height(value):
    if value in (None, "Normal"):
        return "normal"
    if "multiple" in value:
        return repr(value["multiple"])
    return f"{value['absolute']}px"


class Fonts:
    """One @font-face family per font file."""

    def __init__(self):
        self.names = {}

    def family(self, files):
        names = []
        for f in files:
            if f not in self.names:
                self.names[f] = f"F{len(self.names)}"
            names.append(self.names[f])
        return ", ".join(names)

    def css(self):
        return "\n".join(f"@font-face {{ font-family: {name}; src: url('file://{FONTS}/{f}'); }}" for f, name in self.names.items())


def text_css(fonts, style, base_size=None):
    """CSS for TextStyle fields."""
    css = []
    if "fonts" in style:
        css.append(f"font-family: {fonts.family(style['fonts'])}")
    if "size" in style:
        css.append(f"font-size: {style['size']}px")
    if "letterSpacing" in style:
        css.append(f"letter-spacing: {style['letterSpacing']}px")
    if "wordSpacing" in style:
        css.append(f"word-spacing: {style['wordSpacing']}px")
    if "lineHeight" in style:
        css.append(f"line-height: {line_height(style['lineHeight'])}")
    if "baselineShift" in style:
        # BaselineShift is positive downward in Egami's y-down coordinates; CSS vertical-align lengths raise.
        css.append(f"vertical-align: {-style['baselineShift']}px")
    return "; ".join(css)


def block_css(fonts, c, width):
    s = c["style"]
    vertical = s.get("writingMode", "HorizontalTb") != "HorizontalTb"
    # Egami has no CSS Text 4 punctuation trimming, so text-spacing-trim is off (Chromium 129's normal halves adjacent
    # fullwidth punctuation).
    css = ["position: absolute", "left: 20px", "top: 20px", "margin: 0", "padding: 0", "white-space: pre-wrap", "hyphens: manual",
           "text-spacing-trim: space-all",
           "font-kerning: normal", "font-synthesis: none", f"font-family: {fonts.family(c['fonts'])}", f"font-size: {c['size']}px",
           f"line-height: {line_height(s.get('lineHeight'))}"]
    css.append(f"inline-size: {'max-content' if width is None else f'{width}px'}")
    for key, (prop, values) in ENUM_CSS.items():
        if key in s:
            css.append(f"{prop}: {values[s[key]]}")
    if "letterSpacing" in s:
        css.append(f"letter-spacing: {s['letterSpacing']}px")
    if "wordSpacing" in s:
        css.append(f"word-spacing: {s['wordSpacing']}px")
    css.append(f"tab-size: {s.get('tabSize', 8)}")
    if s.get("direction", "Auto") == "Auto":
        # Direction Auto takes each paragraph's direction from its first strong character, as UAX #9's P2 and P3.
        css.append("unicode-bidi: plaintext")
    if "hyphen" in s:
        css.append(f"hyphenate-character: {json.dumps(s['hyphen'])}")
        css.append(f"-webkit-hyphenate-character: {json.dumps(s['hyphen'])}")
    if "maxLines" in s:
        css += ["display: -webkit-box", "-webkit-box-orient: vertical" if not vertical else "-webkit-box-orient: horizontal",
                f"-webkit-line-clamp: {s['maxLines']}", "overflow: hidden"]
    if "strut" in s:
        # CSS's strut is the block's own font and line height; the text then takes the case's font size through a span.
        strut = s["strut"]
        css = [x for x in css if not x.startswith(("font-family", "font-size", "line-height"))]
        css += [f"font-family: {fonts.family(strut['fonts'])}", f"font-size: {strut['size']}px",
                f"line-height: {line_height(strut.get('lineHeight'))}"]
    return "; ".join(css)


def direction_attrs(s):
    d = s.get("direction", "Auto")
    if d == "Auto":
        return 'dir="auto"'
    return 'dir="ltr"' if d == "LeftToRight" else 'dir="rtl"'


def placeholder_html(p):
    """An inline-block of the placeholder's size, aligned as Egami's PlaceholderAlignment says."""
    w, h, a = p["width"], p["height"], p["alignment"]
    outer = f"display: inline-block; width: {w}px; height: {h}px; line-height: 0; font-size: 0; background: #888"
    if a == "Baseline":
        inner = f'<span style="display: inline-block; width: 0; height: {p.get("baselineOffset", h)}px"></span>'
        return f'<span class="ph" style="{outer}; vertical-align: baseline">{inner}</span>'
    if a == "AboveBaseline":
        return f'<span class="ph" style="{outer}; vertical-align: baseline"></span>'
    if a == "BelowBaseline":
        inner = '<span style="display: inline-block; width: 0; height: 0"></span>'
        return f'<span class="ph" style="{outer}; vertical-align: baseline">{inner}</span>'
    return f'<span class="ph" style="{outer}; vertical-align: {a.lower()}"></span>'


def esc(text):
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def content_html(fonts, c):
    """The paragraph as spans: one per run of equal style, placeholders as inline-blocks."""
    text = c["text"]
    cuts = {0, len(text)}
    for sp in c["spans"]:
        cuts.update((sp["start"], sp["start"] + sp["length"]))
    for i, ch in enumerate(text):
        if ch == "\ufffc":
            cuts.update((i, i + 1))
    base = {}
    if "strut" in c["style"]:
        base = {"fonts": c["fonts"], "size": c["size"]}
    if "language" in c["style"]:
        base["language"] = c["style"]["language"]
    parts = []
    placeholder = 0
    points = sorted(cuts)
    for a, b in zip(points, points[1:]):
        if text[a:b] == "\ufffc":
            parts.append(placeholder_html(c["placeholders"][placeholder]))
            placeholder += 1
            continue
        style = dict(base)
        for sp in c["spans"]:
            if sp["start"] <= a and b <= sp["start"] + sp["length"]:
                style.update(sp["style"])
        lang = f' lang="{style["language"]}"' if "language" in style else ""
        parts.append(f'<span class="t" data-start="{a}"{lang} style="{text_css(fonts, style)}">{esc(text[a:b])}</span>')
    return "".join(parts)


PAGE = """<!doctype html><html><head><meta charset="utf-8"><style>
{faces}
html, body {{ margin: 0; padding: 0; }}
.pk {{ display: inline-block; width: 0; height: 0; }}
.mk {{ font-size: 0; line-height: 0; }}
</style></head><body>{blocks}</body></html>"""

SCRIPT = r"""
(args) => {
  const block = document.getElementById(args.id);
  const vertical = getComputedStyle(block).writingMode !== 'horizontal-tb';
  const origin = block.getBoundingClientRect();
  const text = args.text;
  const r = v => v;  // values are LayoutUnits already
  const rel = q => [r(q.x - origin.x), r(q.y - origin.y), r(q.width), r(q.height)];

  // UTF-16 index -> [node, offset] for text, or the placeholder element.
  function nodes(root) {
    const map = new Array(text.length + 1);
    // map gives the position where a character starts (the later node at a boundary), map.end where one ends.
    map.end = new Array(text.length + 1);
    const placeholders = [];
    for (const el of root.children) {
      if (el.classList.contains('mk')) continue;
      if (el.classList.contains('ph')) { placeholders.push(el); continue; }
      const start = +el.dataset.start; const t = el.firstChild;
      for (let i = 0; i <= t.length; i++) map[start + i] = [t, i];
      for (let i = 0; i <= t.length; i++) if (i > 0 || !map.end[start]) map.end[start + i] = [t, i];
    }
    let p = 0;
    for (let i = 0; i < text.length; i++) if (text[i] === '\ufffc') { const el = placeholders[p++]; map[i] = [el.parentNode, Array.prototype.indexOf.call(el.parentNode.childNodes, el)]; map[i].el = el; map[i + 1] = map[i + 1] || [el.parentNode, map[i][1] + 1]; map.end[i + 1] = [el.parentNode, map[i][1] + 1]; }
    return map;
  }

  const seg = new Intl.Segmenter('en', { granularity: 'grapheme' });
  const graphemes = [...seg.segment(text)].map(s => [s.index, s.index + s.segment.length]);

  function rangeOf(map, a, b) {
    const range = document.createRange();
    if (map[a].el && b === a + 1) { range.selectNode(map[a].el); return range; }
    range.setStart(map[a][0], map[a][1]);
    const e = map.end[b] || map[b];
    range.setEnd(e[0], e[1]);
    return range;
  }

  // A grapheme's rectangle; when Chromium gives several (the hyphen of a taken soft hyphen comes with the next character),
  // the last, which is the character's own.
  function rectOf(map, a, b) {
    let rects = rangeOf(map, a, b).getClientRects();
    if (rects.length === 0) return null;
    if (b === graphemes.find(g => g[0] === a)?.[1] && rects.length > 1) rects = [rects[rects.length - 1]];
    let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
    for (const q of rects) { x0 = Math.min(x0, q.left); y0 = Math.min(y0, q.top); x1 = Math.max(x1, q.right); y1 = Math.max(y1, q.bottom); }
    return { x: x0, y: y0, width: x1 - x0, height: y1 - y0 };
  }

  // The baseline of the line a grapheme sits on, along the block axis: its rectangle plus the offset a probe measures.
  const probeCache = new Map();
  function baselineOffset(map, a, b) {
    const host = map[a].el ? map[a].el : map[a][0].parentNode;
    const key = host.getAttribute('style') + '|' + text.slice(a, b);
    if (probeCache.has(key)) return probeCache.get(key);
    const probe = block.cloneNode(false);
    probe.removeAttribute('id');
    probe.style.inlineSize = 'max-content';
    probe.style.left = '0px'; probe.style.top = '0px';
    probe.style.display = 'block'; probe.style.webkitLineClamp = 'none';
    const inner = host.cloneNode(true);
    if (!map[a].el) inner.textContent = text.slice(a, b);
    const mark = document.createElement('span'); mark.className = 'pk';
    probe.appendChild(inner); probe.appendChild(mark);
    document.body.appendChild(probe);
    const q = map[a].el ? inner.getBoundingClientRect() : (() => { const rg = document.createRange(); rg.selectNodeContents(inner); return rg.getBoundingClientRect(); })();
    const m = mark.getBoundingClientRect();
    const value = vertical ? m.x - q.x : m.y - q.y;
    probe.remove();
    probeCache.set(key, value);
    return value;
  }

  // Default ignorables (soft hyphen, ZWSP, ZWJ, WJ) have no box of their own to place.
  // Only segment breaks force a line in CSS (U+2028, U+2029, NEL, VT and FF do not, in Chromium).
  const HARD = new Set(['\u000a', '\u000d', '\u000d\u000a']);
  const IGNORABLE = new Set(['\u00ad', '\u200b', '\u200c', '\u200d', '\u2060', '\ufeff']);
  function lineStarts(root) {
    const map = nodes(root);
    const starts = []; const baselines = []; let last = null; let forced = true; let band = null;
    const rects = [];
    for (const [a, b] of graphemes) {
      const q = rectOf(map, a, b);
      rects.push(q);
      const g = text.slice(a, b);
      if (forced) { starts.push(a); forced = false; last = null; band = null; }
      if (HARD.has(g)) { forced = true; continue; }
      if (q === null || IGNORABLE.has(g)) continue;
      const lo = vertical ? q.x : q.y, hi = lo + (vertical ? q.width : q.height);
      if (map[a].el) {
        // A placeholder is placed by its box, not a baseline: it starts a line when it misses the current line's band of
        // glyph boxes.
        if (band !== null && (hi <= band[0] + 0.01 || lo >= band[1] - 0.01) && starts[starts.length - 1] !== a) { starts.push(a); last = null; band = null; }
        continue;
      }
      const base = lo + baselineOffset(map, a, b);
      if (baselines.length < starts.length) baselines.push(null);
      if (last !== null && Math.abs(base - last) > 0.01 && starts[starts.length - 1] !== a) { starts.push(a); band = null; baselines.push(null); }
      if (baselines[starts.length - 1] === null) baselines[starts.length - 1] = base;
      last = base;
      band = band === null ? [lo, hi] : [Math.min(band[0], lo), Math.max(band[1], hi)];
    }
    while (baselines.length < starts.length) baselines.push(null);
    return { map, starts, rects, baselines };
  }

  const out = { height: r(vertical ? origin.width : origin.height) };
  const first = lineStarts(block);
  out.graphemes = graphemes.map(([a], i) => first.rects[i] === null ? [a, null] : [a, ...rel(first.rects[i])]);

  // Carets, hit tests and selections, before the marker pass changes the DOM.
  if (args.queries.carets) {
    out.carets = [];
    const bounds = [...graphemes.map(g => g[0]), text.length];
    for (const i of bounds) {
      const range = document.createRange();
      range.setStart(first.map[i][0], first.map[i][1]);
      const q = range.getClientRects()[0] || range.getBoundingClientRect();
      out.carets.push([i, r(q.x - origin.x), r(q.y - origin.y), r(vertical ? q.width : q.height)]);
    }
  }
  if (args.queries.selections) {
    out.selections = args.queries.selections.map(([s, e]) => {
      e = Math.min(e, text.length);
      return [s, e, [...rangeOf(first.map, s, e).getClientRects()].map(rel)];
    });
  }
  function indexAt(x, y) {
    const pos = document.caretPositionFromPoint(x, y);
    if (!pos) return null;
    if (pos.offsetNode.nodeType === Node.ELEMENT_NODE && pos.offsetNode.classList.contains('t')) return +pos.offsetNode.dataset.start + (pos.offset > 0 ? pos.offsetNode.textContent.length : 0);
    for (let i = 0; i <= text.length; i++) {
      const m = first.map[i];
      if (m && m[0] === pos.offsetNode && m[1] === pos.offset) return i;
    }
    return null;
  }

  if (args.queries.hits) {
    out.hits = [];
    const lineRects = new Map();
    graphemes.forEach(([a], i) => {
      const q = first.rects[i]; if (q === null) return;
      let line = 0; for (let k = 0; k < first.starts.length; k++) if (first.starts[k] <= a) line = k;
      const cur = lineRects.get(line);
      lineRects.set(line, cur ? { x0: Math.min(cur.x0, q.x), y0: Math.min(cur.y0, q.y), x1: Math.max(cur.x1, q.x + q.width), y1: Math.max(cur.y1, q.y + q.height), qs: [...cur.qs, q] }
                              : { x0: q.x, y0: q.y, x1: q.x + q.width, y1: q.y + q.height, qs: [q] });
    });
    for (const [, L] of lineRects) {
      if (!vertical) {
        const y = (L.y0 + L.y1) / 2;
        const xs = [L.x0 - 6, L.x1 + 6];
        for (const q of L.qs) xs.push(q.x + q.width * 0.25, q.x + q.width * 0.75);
        for (const x of xs) out.hits.push([r(x - origin.x), r(y - origin.y), indexAt(x, y)]);
      } else {
        const x = (L.x0 + L.x1) / 2;
        const ys = [L.y0 - 6, L.y1 + 6];
        for (const q of L.qs) ys.push(q.y + q.height * 0.25, q.y + q.height * 0.75);
        for (const y of ys) out.hits.push([r(x - origin.x), r(y - origin.y), indexAt(x, y)]);
      }
    }
  }

  // Intrinsic sizes.
  const probe = block.cloneNode(true);
  probe.removeAttribute('id');
  probe.style.left = '0px'; probe.style.top = '0px';
  document.body.appendChild(probe);
  probe.style.inlineSize = 'min-content';
  let b = probe.getBoundingClientRect(); out.minContent = vertical ? b.height : b.width;
  probe.style.inlineSize = 'max-content';
  b = probe.getBoundingClientRect(); out.maxContent = vertical ? b.height : b.width;
  probe.remove();
  // Line boxes: empty inline boxes of font size 0 aligned top, baseline and bottom at one line start at a time; empty inline
  // boxes add no break opportunity, which inline-blocks would. They go after the line's first grapheme (before a hard break),
  // or, when that changes the layout (CJK punctuation spacing can), before it. The block's size and the boxes of the line's
  // first and last graphemes and of the next line's first must not move, or that line's box is left out.
  const starts = first.starts;
  const pristine = block.cloneNode(true);
  const index = new Map(graphemes.map(([a, b], k) => [a, k]));
  // Splitting a text item can move later fragments by a LayoutUnit along the line, so only the block axis must stay exact.
  function same(q, p) {
    if (q === null || p === null) return q === p;
    const [qi, pi, qb, pb] = vertical ? [q.y, p.y, q.x, p.x] : [q.x, p.x, q.y, p.y];
    return Math.abs(qi - pi) < 0.5 && Math.abs(qb - pb) < 0.001;
  }
  function tryLine(k, at) {
    const target = pristine.cloneNode(true);
    document.getElementById(args.id).replaceWith(target);
    const map = nodes(target);
    const [node, offset] = map[at];
    const frag = document.createDocumentFragment();
    const made = [];
    for (const align of ['top', 'baseline', 'bottom']) {
      const s = document.createElement('span'); s.className = 'mk'; s.style.verticalAlign = align; frag.appendChild(s); made.push(s);
    }
    // At the root inline box, so a shifted span never moves the markers: a span is split where they go.
    if (node.nodeType === Node.TEXT_NODE) {
      const span = node.parentNode;
      if (offset === 0) target.insertBefore(frag, span);
      else if (offset >= node.length) target.insertBefore(frag, span.nextSibling);
      else {
        const rest = span.cloneNode(false);
        rest.dataset.start = +span.dataset.start + offset;
        rest.appendChild(node.splitText(offset));
        target.insertBefore(rest, span.nextSibling);
        target.insertBefore(frag, rest);
      }
    } else {
      node.insertBefore(frag, node.childNodes[offset] || null);
    }
    const box = target.getBoundingClientRect();
    if (Math.abs(box.width - origin.width) > 0.001 || Math.abs(box.height - origin.height) > 0.001) return null;
    const g = index.get(starts[k]);
    const next = k + 1 < starts.length ? index.get(starts[k + 1]) : graphemes.length;
    const before = graphemes.findIndex(([a, b]) => b === at);
    const check = [g, next - 1, next, before, before + 1].filter(x => x >= 0 && x < graphemes.length);
    const fresh = nodes(target);
    for (const x of check) if (!same(first.rects[x], rectOf(fresh, graphemes[x][0], graphemes[x][1]))) return null;
    const values = made.map(m => { const q = m.getBoundingClientRect(); return vertical ? q.x - box.x : q.y - box.y; });
    // The baseline marker must sit on the line's own baseline from the first pass.
    const base = first.baselines[k];
    if (base !== null && Math.abs(values[1] - (base - (vertical ? origin.x : origin.y))) > 0.01) return null;
    if (base === null && !HARD.has(text.slice(starts[k], graphemes[g][1]))) return null;
    return values;
  }
  function lineBox(k) {
    const g = index.get(starts[k]);
    const next = k + 1 < starts.length ? index.get(starts[k + 1]) : graphemes.length;
    // Candidates: after each of the line's first four graphemes, then before the first; a hard break only before itself.
    const at = [];
    if (!HARD.has(text.slice(graphemes[g][0], graphemes[g][1]))) for (let x = g; x < Math.min(g + 4, next); x++) at.push(graphemes[x][1]);
    at.push(starts[k]);
    for (const p of at) { const v = tryLine(k, p); if (v) return v; }
    return null;
  }
  const boxes = starts.map((i, k) => lineBox(k));
  // Line boxes stack without gaps, so a line whose markers could not be placed takes its over and under edges from its
  // neighbours (or the block's edges) and its baseline from the first pass. Boxes are [over, baseline, under] along the block
  // axis: over is the top in horizontal layouts and the right in vertical ones, where vertical-lr stacks lines rightwards.
  const extent = vertical ? origin.width : origin.height;
  const wm = getComputedStyle(block).writingMode;
  for (let k = 0; k < boxes.length; k++) {
    if (boxes[k] !== null || first.baselines[k] === null) continue;
    const prev = k === 0 ? null : boxes[k - 1], next = k === boxes.length - 1 ? null : boxes[k + 1];
    let over, under;
    if (wm === 'vertical-lr') {
      over = next ? next[2] : (k === boxes.length - 1 ? extent : undefined);
      under = prev ? prev[0] : (k === 0 ? 0 : undefined);
    } else {
      over = prev ? prev[2] : (k === 0 ? (wm === 'vertical-rl' ? extent : 0) : undefined);
      under = next ? next[0] : (k === boxes.length - 1 ? (wm === 'vertical-rl' ? 0 : extent) : undefined);
    }
    if (over !== undefined && under !== undefined) boxes[k] = [over, first.baselines[k] - (vertical ? origin.x : origin.y), under];
  }
  out.lines = starts.map((i, k) => ({ start: i, box: boxes[k] }));
  document.getElementById(args.id).replaceWith(pristine);
  return out;
}
"""


def write(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    text = json.dumps(data, ensure_ascii=False, separators=(",", ":"))
    with open(path, "wb") as raw:
        with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0, compresslevel=9) as f:
            f.write(text.encode("utf-8"))


SEGMENT = r"""
(args) => {
  // A zero width space at every word boundary between two letters of the language's script, from ICU's dictionary.
  const seg = new Intl.Segmenter(args.language, { granularity: 'word' });
  let out = '';
  let prev = '';
  for (const s of seg.segment(args.text)) {
    const first = s.segment[0];
    if (out.length > 0 && /\p{L}|\p{M}/u.test(prev) && /\p{L}/u.test(first) && s.isWordLike) out += String.fromCharCode(0x200B);
    out += s.segment;
    prev = s.segment[s.segment.length - 1];
  }
  return out;
}
"""


def run_group(page, cases):
    results = []
    for c in cases:
        if "segment" in c:
            # Words found by Chromium's own dictionary, marked with zero width spaces, so a breaker without a dictionary
            # can match its breaks.
            page.set_content("<!doctype html><html></html>")
            c = {**c, "text": page.evaluate(SEGMENT, {"language": c["segment"], "text": c["text"]})}
        fonts = Fonts()
        blocks = []
        for k, width in enumerate(c["widths"]):
            blocks.append((f"b{k}", width, block_css(fonts, c, width)))
        path = "/tmp/layout.html"
        # Each width is laid out alone, so blocks never interact.
        layouts = []
        for bid, width, css in blocks:
            body = (f'<div id="{bid}" {direction_attrs(c["style"])} '
                    f'{"lang=" + json.dumps(c["style"]["language"]) if "language" in c["style"] else ""} '
                    f'style="{css}">{content_html(fonts, c)}</div>')
            with open(path, "w", encoding="utf-8") as f:
                f.write(PAGE.format(faces=fonts.css(), blocks=body))
            page.goto("file://" + path)
            page.evaluate("document.fonts.ready.then(() => document.fonts.size)")
            page.evaluate("Promise.all([...document.fonts].map(f => f.load()))")
            result = page.evaluate(SCRIPT, {"id": bid, "text": c["text"], "queries": c["queries"]})
            result["width"] = width
            layouts.append(result)
        results.append({"case": c, "layouts": layouts})
        print(f"  {c['name']}: {', '.join(str(len(x['lines'])) for x in layouts)} lines", flush=True)
    return results


def main():
    with sync_playwright() as p:
        browser = p.chromium.launch(args=["--font-render-hinting=none"])
        page = browser.new_page(viewport={"width": 2000, "height": 2000}, device_scale_factor=1)
        for name, cases in corpus.groups(ROOT).items():
            print(f"{name} ({len(cases)} cases)", flush=True)
            results = run_group(page, cases)
            write(os.path.join(OUT, f"{name}.json.gz"), {"chromium": browser.version, "cases": results})
        browser.close()


if __name__ == "__main__":
    main()
