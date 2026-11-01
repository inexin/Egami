# Reference tool for JPEG XL extras PR C: libjxl 0.12.0's butteraugli_main (BSD-3), built from source, for the Butteraugli
# distances in tests/reference/analysis/butteraugli.json. Built by tools/make-butteraugli-references.py; the tool runs as an
# oracle only.
FROM debian@sha256:913f6706df59a68922d1dd08f78c2476560a8d367897200a6005b00e5f67c2d5
RUN apt-get update && apt-get install -y --no-install-recommends \
        build-essential cmake git ca-certificates pkg-config python3 libpng-dev libjpeg62-turbo-dev libgif-dev \
    && rm -rf /var/lib/apt/lists/*
RUN git clone --depth 1 --branch v0.12.0 --recursive --shallow-submodules https://github.com/libjxl/libjxl /src/libjxl \
    && cmake -S /src/libjxl -B /src/libjxl/build -DCMAKE_BUILD_TYPE=Release -DBUILD_TESTING=OFF \
        -DJPEGXL_ENABLE_BENCHMARK=OFF -DJPEGXL_ENABLE_EXAMPLES=OFF -DJPEGXL_ENABLE_MANPAGES=OFF -DJPEGXL_ENABLE_OPENEXR=OFF \
        -DJPEGXL_ENABLE_PLUGINS=OFF -DJPEGXL_ENABLE_DOXYGEN=OFF -DJPEGXL_ENABLE_JNI=OFF -DJPEGXL_ENABLE_SJPEG=OFF \
        -DJPEGXL_ENABLE_DEVTOOLS=ON -DBUILD_SHARED_LIBS=OFF \
    && cmake --build /src/libjxl/build -j4 --target butteraugli_main \
    && cp /src/libjxl/build/tools/butteraugli_main /usr/local/bin/ && rm -rf /src/libjxl \
    && (butteraugli_main || true) 2>&1 | grep -q Usage
WORKDIR /work
