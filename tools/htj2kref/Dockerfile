# Reference tools for High-Throughput JPEG 2000 (DICOM Build Plan task 23): OpenJPH 0.33.0 (BSD-2) encodes the HT corpus
# with ojph_compress, and OpenJPEG 2.5.4 (BSD-2) decodes it with opj_decompress as the oracle. Both run only as command-line
# tools; their code is never read. Built by tools/make-htj2k-references.py.
FROM alpine@sha256:020dfcbaaf4cc1078bf2d9c7ba31a8466e334061dcd2f248001d68f79e52c000
RUN apk add --no-cache openjpeg-tools=2.5.4-r2 imagemagick python3 py3-numpy py3-pillow build-base cmake git tiff-dev \
    && git clone --depth 1 --branch 0.33.0 https://github.com/aous72/OpenJPH.git /tmp/ojph \
    && cmake -S /tmp/ojph -B /tmp/ojph/build -DCMAKE_BUILD_TYPE=Release -DOJPH_ENABLE_TIFF_SUPPORT=ON -DOJPH_BUILD_TESTS=OFF \
    && cmake --build /tmp/ojph/build -j 4 \
    && cmake --install /tmp/ojph/build \
    && rm -rf /tmp/ojph
WORKDIR /work
