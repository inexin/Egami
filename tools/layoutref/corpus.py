"""The paragraph layout corpus of Phase 2.8.2 (question 23): every case Chromium lays out for tests/reference/layout.

A case is a dict that maps one to one onto Egami's API: "fonts" is the font family list (files under tests/fonts, in fallback
order), "size" the font size in pixels, "text" the paragraph, "widths" the layout widths in pixels (None for no wrapping),
"style" ParagraphStyle fields, "spans" StyleSpans (start and length in UTF-16 units, with TextStyle fields), "placeholders"
Placeholder fields (each at a U+FFFC in the text) and "queries" what to record beyond lines and glyph positions. Style values use
Egami's member names; tools/layoutref/layout.py maps them to CSS, and the tests map them to ParagraphStyle.

Groups:
  corpus    one paragraph per script at five widths: line breaks, line boxes and glyph positions
  features  one or more samples per layout feature (question 23's "each have cases")
"""
import os

LATIN = ["NotoSans-Regular.ttf"]
SERIF = ["SourceSerif4-Regular.otf"]
ARABIC = ["NotoSans-Regular.ttf", "shaping/NotoSansArabic-Regular.ttf"]
HEBREW = ["NotoSans-Regular.ttf", "shaping/NotoSansHebrew-Regular.ttf"]
DEVANAGARI = ["NotoSans-Regular.ttf", "shaping/NotoSansDevanagari-Regular.ttf"]
THAI = ["NotoSans-Regular.ttf", "shaping/NotoSansThai-Regular.ttf"]
CJK = ["derived/NotoSansCJK-layout-subset.otf"]
BIDI = ["NotoSans-Regular.ttf", "shaping/NotoSansHebrew-Regular.ttf", "shaping/NotoSansArabic-Regular.ttf"]
EMOJI = ["NotoSans-Regular.ttf", "derived/Twemoji-COLRv0-subset.ttf"]

WIDTHS = [80, 140, 220, 340, 600]

# Texts written for these tests (the UDHR texts come from tests/reference/shaping/texts and tests/reference/layout/texts).
MIXED_BIDI = "The word שלום means peace (and so does سلام) in 2024, said the guide: \"עברית, عربي and English\"."
EMOJI_TEXT = "Launch day 🚀 went well 🎉! The team 👩‍💻 shipped it, 👍🏽 from Oslo 🇳🇴 and ❤️ from everyone 😀."
JAPANESE_KINSOKU = "ちょっと待ってください。ジャーナルのテストは々ぁぃぅぇぉっゃゅょゎゕゖァィゥェォッャュョヮヵヶ〜ーー「ここ」です。"
LONG_URL = "See https://example.com/a/very/long/path/without/any/spaces/at/all?query=value&other=thing for details."
SOFT_HYPHENS = "Hy\u00adphen\u00adation is in\u00adcom\u00adpre\u00adhen\u00adsi\u00adbil\u00adi\u00adties of ex\u00adtra\u00ador\u00addi\u00adnary words."
LIGATURES = "The office affords fine fluffy waffles, first and final."
TABS = "Name\tQty\tPrice\nApples\t12\t3.50\nPears\t7\t12.25\nFigs\t140\t0.75"
KOREAN = "모든 인간은 태어날 때부터 자유로우며 그 존엄과 권리에 있어 동등하다."
CHINESE = "人人生而自由，在尊严和权利上一律平等。他们赋有理性和良心，并应以兄弟关系的精神相对待。"
VERTICAL_MIXED = "日本語の縦書きテキスト。ABC と 2024 年、「かっこ」も含む。"


def udhr(root, name, line=None):
    """One UDHR paragraph: the given line of the text, or the first after the title lines with at least 100 characters."""
    for folder in ("shaping", "layout"):
        path = os.path.join(root, "tests", "reference", folder, "texts", f"{name}.txt")
        if os.path.exists(path):
            with open(path, encoding="utf-8") as f:
                lines = f.read().splitlines()
            if line is not None:
                return lines[line]
            return next(x for x in lines[2:] if len(x) >= 100)
    raise FileNotFoundError(name)


def case(name, fonts, text, widths=None, size=16, style=None, spans=None, placeholders=None, queries=None):
    # Spans are clipped to the text, so a span can say "the whole text" of a cut UDHR line.
    spans = [{**s, "length": min(s["length"], len(text) - s["start"])} for s in spans or []]
    return {"name": name, "fonts": fonts, "size": size, "text": text, "widths": widths or WIDTHS, "style": style or {},
            "spans": spans, "placeholders": placeholders or [], "queries": queries or {}}


def corpus(root):
    return [
        case("latin", LATIN, udhr(root, "english")),
        case("latin-serif", SERIF, udhr(root, "english", 3)),
        case("arabic", ARABIC, udhr(root, "arabic")),
        case("hebrew", HEBREW, udhr(root, "hebrew")),
        case("devanagari", DEVANAGARI, udhr(root, "hindi")),
        case("thai", THAI, udhr(root, "thai")),
        case("chinese", CJK, udhr(root, "chinese")),
        case("japanese", CJK, udhr(root, "japanese")),
        case("korean", CJK, udhr(root, "korean")),
        case("mixed-bidi", BIDI, MIXED_BIDI),
        case("emoji", EMOJI, EMOJI_TEXT),
    ]


def features(root):
    english = udhr(root, "english")
    interaction = {"carets": True, "hits": True, "selections": [[4, 20], [10, 40], [0, 9999]]}
    cases = [
        # Line breaking switches (question 5).
        case("word-break-break-all", LATIN, english, widths=[90, 170], style={"wordBreak": "BreakAll"}),
        case("word-break-keep-all", CJK, KOREAN, widths=[90, 170], style={"wordBreak": "KeepAll"}),
        case("word-break-keep-all-chinese", CJK, CHINESE, widths=[90, 170], style={"wordBreak": "KeepAll"}),
        case("overflow-wrap-normal", LATIN, LONG_URL, widths=[120, 200]),
        case("overflow-wrap-anywhere", LATIN, LONG_URL, widths=[120, 200], style={"overflowWrap": "Anywhere"}),
        case("overflow-wrap-break-word", LATIN, LONG_URL, widths=[120, 200], style={"overflowWrap": "BreakWord"}),
        case("line-break-loose", CJK, JAPANESE_KINSOKU, widths=[70, 110, 150], style={"lineBreak": "Loose", "language": "ja"}),
        case("line-break-normal", CJK, JAPANESE_KINSOKU, widths=[70, 110, 150], style={"lineBreak": "Normal", "language": "ja"}),
        case("line-break-strict", CJK, JAPANESE_KINSOKU, widths=[70, 110, 150], style={"lineBreak": "Strict", "language": "ja"}),
        case("line-break-anywhere", LATIN, english, widths=[90, 170], style={"lineBreak": "Anywhere"}),
        # Soft hyphens and hard breaks (question 7).
        case("soft-hyphens", LATIN, SOFT_HYPHENS, widths=[60, 90, 130]),
        case("soft-hyphens-custom", LATIN, SOFT_HYPHENS, widths=[60, 90], style={"hyphen": "="}),
        case("hard-breaks", LATIN, "First line\nSecond line\u2028third after LS\n\nafter a blank line\n", widths=[300, 60]),
        # Spacing and tabs (question 11).
        case("letter-spacing", SERIF, LIGATURES, widths=[200, None], style={}, spans=[{"start": 0, "length": len(LIGATURES), "style": {"letterSpacing": 2}}]),
        case("letter-spacing-negative", LATIN, english[:120], widths=[200], spans=[{"start": 0, "length": 120, "style": {"letterSpacing": -0.5}}]),
        case("letter-spacing-arabic", ARABIC, udhr(root, "arabic")[:120], widths=[200], spans=[{"start": 0, "length": 120, "style": {"letterSpacing": 3}}]),
        case("word-spacing", LATIN, english[:160], widths=[200, None], spans=[{"start": 0, "length": 160, "style": {"wordSpacing": 6}}]),
        case("tab-size", LATIN, TABS, widths=[400], style={"tabSize": 4}),
        case("tab-size-default", LATIN, TABS, widths=[400]),
        # Line height and struts (question 10).
        case("line-height-multiple", LATIN, english, widths=[220], style={"lineHeight": {"multiple": 1.5}}),
        case("line-height-absolute", LATIN, english, widths=[220], style={"lineHeight": {"absolute": 30}}),
        case("line-height-tight", LATIN, english, widths=[220], style={"lineHeight": {"multiple": 0.8}}),
        case("line-height-mixed-sizes", LATIN, english[:200], widths=[220],
             spans=[{"start": 20, "length": 30, "style": {"size": 28}}, {"start": 90, "length": 20, "style": {"size": 10}}]),
        case("line-height-mixed-fonts", BIDI, MIXED_BIDI, widths=[180], size=20),
        case("strut", LATIN, english[:200], size=12, widths=[220],
             style={"strut": {"fonts": LATIN, "size": 20, "lineHeight": {"multiple": 1.4}}}),
        case("baseline-shift", LATIN, "H2O and E=mc2 with x10 raised.", widths=[300],
             spans=[{"start": 1, "length": 1, "style": {"baselineShift": -4, "size": 11}},
                    {"start": 12, "length": 1, "style": {"baselineShift": 6, "size": 11}},
                    {"start": 20, "length": 2, "style": {"baselineShift": 6, "size": 11}}]),
        # Alignment and justification (question 8).
        case("align-start", LATIN, english, widths=[220]),
        case("align-end", LATIN, english, widths=[220], style={"align": "End"}),
        case("align-center", LATIN, english, widths=[220], style={"align": "Center"}),
        case("align-left-rtl", ARABIC, udhr(root, "arabic"), widths=[220], style={"align": "Left"}),
        case("align-right", LATIN, english, widths=[220], style={"align": "Right"}),
        case("align-start-rtl", HEBREW, udhr(root, "hebrew"), widths=[220]),
        case("justify", LATIN, english, widths=[180, 260], style={"align": "Justify"}),
        case("justify-serif", SERIF, udhr(root, "english", 3), widths=[180, 260], style={"align": "Justify"}),
        case("justify-arabic", ARABIC, udhr(root, "arabic"), widths=[180, 260], style={"align": "Justify"}),
        case("justify-bidi", BIDI, MIXED_BIDI, widths=[180], style={"align": "Justify"}),
        case("justify-chinese", CJK, udhr(root, "chinese"), widths=[150, 230], style={"align": "Justify"}),
        case("justify-inter-character", LATIN, english, widths=[180], style={"align": "Justify", "justify": "InterCharacter"}),
        case("justify-last-justify", LATIN, english, widths=[180], style={"align": "Justify", "alignLast": "Justify"}),
        case("justify-last-end", LATIN, english, widths=[180], style={"align": "Justify", "alignLast": "End"}),
        case("justify-hard-breaks", LATIN, english[:90] + "\n" + english[90:200], widths=[180], style={"align": "Justify"}),
        case("trailing-spaces", LATIN, "Spaces   at   the   end     of   lines   hang   outside     the   box.", widths=[90, 140],
             style={"align": "End"}),
        # Balanced filling (question 6).
        case("balanced", LATIN, english[:160], widths=[200, 300], style={"lineFill": "Balanced"}),
        # Max lines and ellipsis (question 12).
        case("clamp-latin", LATIN, english, widths=[200], style={"maxLines": 3, "ellipsis": "\u2026"}),
        case("clamp-arabic", ARABIC, udhr(root, "arabic"), widths=[200], style={"maxLines": 2, "ellipsis": "\u2026"}),
        case("clamp-chinese", CJK, udhr(root, "chinese"), widths=[160], style={"maxLines": 2, "ellipsis": "\u2026"}),
        # Placeholders (question 15): each U+FFFC in the text is the next placeholder.
        case("placeholders", LATIN, "Base \ufffc above \ufffc below \ufffc top \ufffc bottom \ufffc middle \ufffc end.", widths=[400, 120],
             placeholders=[{"width": 20, "height": 30, "alignment": "Baseline", "baselineOffset": 22},
                           {"width": 12, "height": 12, "alignment": "AboveBaseline"},
                           {"width": 12, "height": 12, "alignment": "BelowBaseline"},
                           {"width": 16, "height": 40, "alignment": "Top"},
                           {"width": 16, "height": 40, "alignment": "Bottom"},
                           {"width": 16, "height": 10, "alignment": "Middle"}]),
        # Rich text with several styles (questions 3 and 4).
        case("rich-fonts", LATIN, english[:200], widths=[200],
             spans=[{"start": 10, "length": 40, "style": {"fonts": SERIF}}, {"start": 70, "length": 30, "style": {"fonts": SERIF, "size": 20}}]),
        # Vertical lines (question 17).
        case("vertical-rl-mixed", CJK, VERTICAL_MIXED, widths=[120, 200], style={"writingMode": "VerticalRl"}),
        case("vertical-lr-mixed", CJK, VERTICAL_MIXED, widths=[120], style={"writingMode": "VerticalLr"}),
        case("vertical-rl-upright", CJK, VERTICAL_MIXED, widths=[120], style={"writingMode": "VerticalRl", "textOrientation": "Upright"}),
        case("vertical-rl-sideways", CJK, VERTICAL_MIXED, widths=[120], style={"writingMode": "VerticalRl", "textOrientation": "Sideways"}),
        case("vertical-japanese", CJK, udhr(root, "japanese"), widths=[160, 300], style={"writingMode": "VerticalRl"}),
        # Hit testing, carets and selection (question 16).
        case("interaction-latin", SERIF, LIGATURES + " " + english[:120], widths=[160], queries=interaction),
        case("interaction-bidi", BIDI, MIXED_BIDI, widths=[160, None], queries=interaction),
        case("interaction-arabic", ARABIC, udhr(root, "arabic")[:150], widths=[160], queries=interaction),
        case("interaction-rtl-paragraph", HEBREW, udhr(root, "hebrew")[:150], widths=[160], style={"direction": "RightToLeft"}, queries=interaction),
        case("interaction-emoji", EMOJI, EMOJI_TEXT, widths=[160], queries=interaction),
        case("interaction-hard-break", LATIN, "One line\n\nthree lines\n", widths=[300], queries=interaction),
        case("interaction-clamp", LATIN, english, widths=[200], style={"maxLines": 2, "ellipsis": "\u2026"}, queries=interaction),
        case("interaction-vertical", CJK, VERTICAL_MIXED, widths=[120], style={"writingMode": "VerticalRl"}, queries=interaction),
        # Intrinsic widths with Anywhere lowering min-content (question 5).
        case("intrinsic-anywhere", LATIN, LONG_URL, widths=[None], style={"overflowWrap": "Anywhere"}),
    ]
    return cases


def groups(root):
    return {"corpus": corpus(root), "features": features(root)}


def all_text(root):
    """Every character the corpus uses, for cutting the CJK font down."""
    return "".join(c["text"] for cases in groups(root).values() for c in cases)
