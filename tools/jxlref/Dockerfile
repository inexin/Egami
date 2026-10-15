# Reference tools for Phase 2.9.3 (JPEG XL): libjxl 0.12.0 (BSD-3) built from source for djxl, cjxl and jxlinfo; jxl-oxide-cli
# 0.12.6 (MIT or Apache-2.0); ExifTool; and Squoosh's CLI 0.7.3 (Apache-2.0, its JPEG XL encoder is an older libjxl built to
# WebAssembly) for encoder variety. Built by tools/make-jxl-references.py; the tools run as oracles only.
FROM rust@sha256:2752b332db73fdbb7dc576f06c82ed1f312005784ef913d7e04a28f5f55dc581 AS oxide
RUN cargo install --locked jxl-oxide-cli --version 0.12.6 --root /opt/oxide

FROM node@sha256:89061680359f5d9103d24f0066e2c96a8018c24d5c4fe8eb8d59d16dd4ab64ba AS squoosh
RUN npm install -g @squoosh/cli@0.7.3

FROM debian@sha256:913f6706df59a68922d1dd08f78c2476560a8d367897200a6005b00e5f67c2d5
RUN apt-get update && apt-get install -y --no-install-recommends \
        build-essential cmake git ca-certificates pkg-config python3 python3-numpy \
        libpng-dev libjpeg62-turbo-dev libgif-dev libimage-exiftool-perl \
    && rm -rf /var/lib/apt/lists/*
RUN git clone --depth 1 --branch v0.12.0 --recursive --shallow-submodules https://github.com/libjxl/libjxl /src/libjxl \
    && cmake -S /src/libjxl -B /src/libjxl/build -DCMAKE_BUILD_TYPE=Release -DBUILD_TESTING=OFF \
        -DJPEGXL_ENABLE_BENCHMARK=OFF -DJPEGXL_ENABLE_EXAMPLES=OFF -DJPEGXL_ENABLE_MANPAGES=OFF -DJPEGXL_ENABLE_OPENEXR=OFF \
        -DJPEGXL_ENABLE_PLUGINS=OFF -DJPEGXL_ENABLE_DOXYGEN=OFF -DJPEGXL_ENABLE_JNI=OFF -DJPEGXL_ENABLE_SJPEG=OFF \
    && cmake --build /src/libjxl/build -j8 && cmake --install /src/libjxl/build && ldconfig && rm -rf /src/libjxl/build
# ExifTool reads brob (Brotli-compressed) boxes with this module.
RUN apt-get update && apt-get install -y --no-install-recommends libio-compress-brotli-perl && rm -rf /var/lib/apt/lists/*
COPY --from=oxide /opt/oxide/bin/jxl-oxide /usr/local/bin/jxl-oxide
COPY --from=squoosh /usr/local/bin/node /usr/local/bin/node
COPY --from=squoosh /usr/local/lib/node_modules /usr/local/lib/node_modules
RUN ln -s /usr/local/lib/node_modules/@squoosh/cli/src/index.js /usr/local/bin/squoosh-cli \
    && djxl --version && jxl-oxide --version && exiftool -ver && squoosh-cli --help > /dev/null
WORKDIR /work
