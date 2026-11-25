"""Word boundary references of 2.8.5 (task 7): Chromium's Intl.Segmenter (word granularity) over the Thai, Lao, Khmer and
Myanmar UDHR texts, as the pinned Chromium splits them with its ICU and dictionaries.

Runs in the egami-layoutref image (tools/layoutref/Dockerfile); tools/make-word-references.py builds the image and runs it.
Writes tests/reference/layout/words.json: per text the language, the whole text and the start of every segment.
"""
import json
import os
import sys

from playwright.sync_api import sync_playwright

ROOT = sys.argv[1] if len(sys.argv) > 1 else "/w"
OUT = sys.argv[2] if len(sys.argv) > 2 else os.path.join(ROOT, "tests", "reference", "layout", "words.json")
TEXTS = [("thai", "th"), ("lao", "lo"), ("khmer", "km"), ("burmese", "my")]
SEGMENT = "([text, language]) => [...new Intl.Segmenter(language, {granularity: 'word'}).segment(text)].map(s => s.index)"


def main():
    with sync_playwright() as p:
        browser = p.chromium.launch()
        page = browser.new_page()
        texts = []
        for name, language in TEXTS:
            with open(os.path.join(ROOT, "tests", "reference", "shaping", "texts", f"{name}.txt"), encoding="utf-8") as f:
                text = f.read()
            starts = page.evaluate(SEGMENT, [text, language])
            texts.append({"name": name, "language": language, "text": text, "starts": starts})
            print(f"  {name}: {len(starts)} segments", flush=True)
        result = {"chromium": browser.version, "texts": texts}
        browser.close()
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        json.dump(result, f, ensure_ascii=False, separators=(",", ":"))
        f.write("\n")


if __name__ == "__main__":
    main()
