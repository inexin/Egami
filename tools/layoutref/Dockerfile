# Reference layout engine for Phase 2.8.2 (question 23): Chromium 129.0.6668.29 as Playwright 1.47.0 ships it (Apache 2.0;
# Chromium BSD-3), pinned by the base image's digest. Only page layout is used: tools/layoutref/layout.py lays out each corpus
# case as HTML with the test fonts and records line breaks, line boxes, glyph positions, carets, hit tests and selection
# rectangles as JSON. Built by tools/make-layout-references.py.
FROM mcr.microsoft.com/playwright/python:v1.47.0-jammy@sha256:f3a3d2e0332df4c7b6992db0ad0687df1653efd4671612b8ca1be6ae5fc06448
RUN pip install --no-cache-dir playwright==1.47.0
ENV LANG=C.UTF-8
WORKDIR /w
