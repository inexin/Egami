# Reference tools for ICO, CUR and PSD (Phase 2.3.3, 2.3.10): ImageMagick and Pillow decode both formats and write part of the
# corpus, exiftool reads PSD metadata, psd-tools (MIT) reads PSD layer records and pixels and composites layered files (with
# scipy, scikit-image and aggdraw, its compositing dependencies). Built by tools/make-ico-references.py and
# tools/make-psd-references.py.
FROM alpine@sha256:020dfcbaaf4cc1078bf2d9c7ba31a8466e334061dcd2f248001d68f79e52c000
RUN apk add --no-cache \
        imagemagick=7.1.2.34-r0 py3-pillow=13.0.0-r0 py3-numpy=2.5.4-r0 py3-scipy=1.18.1-r0 python3=3.14.10-r1 py3-pip=26.2.1-r0 \
        exiftool gcc g++ musl-dev python3-dev freetype-dev \
    && pip install --no-cache-dir --break-system-packages --no-deps psd-tools==1.10.9 attrs==25.3.0 docopt==0.6.2 \
    && pip install --no-cache-dir --break-system-packages scikit-image==0.26.0 aggdraw==1.4.1
WORKDIR /work
