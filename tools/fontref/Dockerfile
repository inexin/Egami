# Reference tools for fonts (Phases 2.7.1 and 2.7.2): fontTools 4.55.0 (MIT) dumps tables, character maps, kerning pairs and
# outlines, builds the derived test fonts (collection, bare CFF, CID subset, symbol and Mac cmaps, WOFF and WOFF2 files, an avar 2
# font, an sbix and an SVG font) and checks subsets and instances; Brotli (MIT) lets fontTools read and write WOFF2; Google's woff2
# tools (MIT, Debian's package) compress the WOFF2 collection; FreeType (through freetype-py) opens subsets as a second reader.
# FreeType runs only as a library here; its code is never read. Built by tools/make-font-references.py.
FROM python:3.12-slim
RUN apt-get update && apt-get install -y --no-install-recommends woff2 && rm -rf /var/lib/apt/lists/*
RUN pip install --no-cache-dir fonttools==4.55.0 freetype-py==2.5.1 brotli==1.1.0 pillow==11.0.0 lxml==5.3.0
WORKDIR /w
