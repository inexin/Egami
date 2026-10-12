# Reference tools for ICO, CUR and PSD (Phase 2.3.3): ImageMagick and Pillow decode both formats and write part of the corpus,
# exiftool reads PSD metadata, psd-tools (MIT) reads PSD layer records. Built by tools/make-ico-references.py and
# tools/make-psd-references.py. psd-tools is installed without its compositing dependencies, which reading records does not use.
FROM alpine@sha256:020dfcbaaf4cc1078bf2d9c7ba31a8466e334061dcd2f248001d68f79e52c000
RUN apk add --no-cache \
        imagemagick=7.1.2.32-r0 py3-pillow=12.3.0-r1 py3-numpy=2.5.3-r0 python3=3.14.8-r0 py3-pip=26.1.2-r0 exiftool gcc g++ musl-dev python3-dev \
    && pip install --no-cache-dir --break-system-packages --no-deps psd-tools==1.10.9 attrs==25.3.0
WORKDIR /work
