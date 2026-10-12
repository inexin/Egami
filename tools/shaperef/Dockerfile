# Reference shaper for Phase 2.8.1: HarfBuzz 14.5.1 (MIT) built from its release tarball, whose SHA-256
# tools/make-shaping-references.py checks before the build. Only hb-shape and hb-guess (which prints the script and direction
# HarfBuzz guesses for a line) are run; Egami's shaper is checked against their output. Built by
# tools/make-shaping-references.py, which copies the tarball, hb-guess.c and this file into .cache/shaperef as the context.
FROM python:3.12-slim-bookworm
RUN apt-get update && apt-get install -y --no-install-recommends build-essential meson ninja-build pkg-config libglib2.0-dev \
    libfreetype-dev xz-utils && rm -rf /var/lib/apt/lists/*
COPY harfbuzz-14.5.1.tar.xz hb-guess.c /tmp/
RUN cd /tmp && tar xf harfbuzz-14.5.1.tar.xz && cd harfbuzz-14.5.1 \
    && meson setup build --buildtype=release -Dtests=disabled -Ddocs=disabled -Dcairo=disabled -Dchafa=disabled -Dicu=disabled \
       -Dgraphite2=disabled -Dglib=enabled -Dfreetype=enabled -Dutilities=enabled -Dbenchmark=disabled -Dintrospection=disabled \
    && ninja -C build install && ldconfig \
    && cc -O2 -o /usr/local/bin/hb-guess /tmp/hb-guess.c $(pkg-config --cflags --libs harfbuzz) \
    && cd / && rm -rf /tmp/harfbuzz-14.5.1 /tmp/harfbuzz-14.5.1.tar.xz /tmp/hb-guess.c
ENV LANG=C.UTF-8
WORKDIR /w
