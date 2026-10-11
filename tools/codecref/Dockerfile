# Reference tools for JBIG2 and JPEG 2000 (Phase 2.3.4): jbig2dec 0.20 and PDFium (through pypdfium2) decode JBIG2,
# OpenJPEG 2.5.4 decodes JPEG 2000, jbig2enc and opj_compress generate the corpus. Built by tools/make-codec-references.py.
# jbig2dec is AGPL-3.0 and is only run here as a command-line tool; its code is never read.
FROM alpine@sha256:020dfcbaaf4cc1078bf2d9c7ba31a8466e334061dcd2f248001d68f79e52c000
RUN apk add --no-cache \
        jbig2dec=0.20-r0 jbig2enc=0.30-r0 openjpeg-tools=2.5.4-r2 imagemagick=7.1.2.32-r0 \
        py3-pillow=12.3.0-r1 py3-numpy=2.5.3-r0 python3=3.14.8-r0 py3-pip=26.1.2-r0 qpdf=12.4.2-r0 \
    && pip install --no-cache-dir --break-system-packages pypdfium2==5.3.0
WORKDIR /work
