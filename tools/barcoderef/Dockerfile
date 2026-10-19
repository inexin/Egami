# Reference tools for Egami.Barcodes (Phase 2.11): zint's backend library (BSD-3) makes reference module grids through
# zintdump.c, our own program on zint's public API; zint's GPL frontend and GUI are not built. zxing-cpp (Apache-2.0) reads back
# the PNGs Egami writes. Built by tools/make-barcode-references.py.
FROM debian@sha256:a29215f6a35e51e22adffa17f89e9d2ef06214e64a2bad10d765c46aea49f11f
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl gcc g++ libc6-dev cmake make python3 python3-pip \
    && rm -rf /var/lib/apt/lists/*
RUN curl -fsSL -o /tmp/zint.tar.gz https://github.com/zint/zint/archive/refs/tags/2.16.0.tar.gz \
    && echo "e1077fd5846bdd0128fef3ab8c3c28e93e450d2efaf1424d0aa96e0c9e514591  /tmp/zint.tar.gz" | sha256sum -c \
    && tar xzf /tmp/zint.tar.gz -C /tmp \
    && cmake -S /tmp/zint-2.16.0 -B /tmp/zint-build -DZINT_FRONTEND=OFF -DZINT_USE_QT=OFF -DZINT_USE_PNG=OFF -DZINT_USE_GS1SE=OFF \
        -DZINT_SHARED=OFF -DZINT_STATIC=ON -DZINT_UNINSTALL=OFF -DCMAKE_BUILD_TYPE=Release \
    && cmake --build /tmp/zint-build -j \
    && cmake --install /tmp/zint-build \
    && rm -rf /tmp/zint.tar.gz /tmp/zint-2.16.0 /tmp/zint-build
RUN pip install --no-cache-dir --break-system-packages zxing-cpp==3.1.1 pillow==12.3.0
COPY zintdump.c /src/zintdump.c
RUN gcc -O2 -o /usr/local/bin/zintdump /src/zintdump.c $(ls /usr/local/lib/libzint*.a /usr/local/lib/*/libzint*.a 2>/dev/null | head -1) -lm
WORKDIR /work
