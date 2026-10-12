# Reference tools for fonts (Phase 2.7.1): fontTools 4.55.0 (MIT) dumps tables, character maps, kerning pairs and outlines,
# builds the derived test fonts (collection, bare CFF, CID subset, symbol and Mac cmaps) and checks subsets; FreeType (through
# freetype-py) opens subsets as a second reader. FreeType runs only as a library here; its code is never read.
# Built by tools/make-font-references.py.
FROM python:3.12-slim
RUN pip install --no-cache-dir fonttools==4.55.0 freetype-py==2.5.1
WORKDIR /w
