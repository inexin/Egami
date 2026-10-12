# Reference tools for Phase 2.9.1 (HDR): OpenEXR 3.5.2 (its C++ library, exrheader and friends, and the OpenEXR Python
# bindings), stb_image (stb at a pinned commit), libultrahdr 2.0.2 and its ultrahdr_app, ExifTool and
# ImageMagick. Built by tools/make-hdr-references.py. OpenEXR and libultrahdr are read and run; ImageMagick only for behavior.
FROM debian@sha256:913f6706df59a68922d1dd08f78c2476560a8d367897200a6005b00e5f67c2d5
RUN apt-get update && apt-get install -y --no-install-recommends \
        build-essential cmake git ca-certificates curl pkg-config python3 python3-pip python3-numpy \
        libjpeg62-turbo-dev libimage-exiftool-perl imagemagick \
    && rm -rf /var/lib/apt/lists/*
RUN pip install --no-cache-dir --break-system-packages OpenEXR==3.5.2
RUN git clone --depth 1 --branch v3.5.2 https://github.com/AcademySoftwareFoundation/openexr /src/openexr \
    && cmake -S /src/openexr -B /src/openexr/build -DCMAKE_BUILD_TYPE=Release -DOPENEXR_BUILD_TOOLS=ON \
        -DBUILD_TESTING=OFF -DOPENEXR_INSTALL_EXAMPLES=OFF -DCMAKE_INSTALL_PREFIX=/usr/local \
    && cmake --build /src/openexr/build -j8 && cmake --install /src/openexr/build && ldconfig && rm -rf /src/openexr/build
RUN git clone https://github.com/google/libultrahdr /src/libultrahdr && cd /src/libultrahdr && git checkout e5f5a022fe96fc4dc2ee35c19f733a50df807abe \
    && cmake -S . -B build -DCMAKE_BUILD_TYPE=Release -DUHDR_BUILD_EXAMPLES=ON -DUHDR_BUILD_TESTS=OFF \
    && cmake --build build -j8 && cp build/ultrahdr_app /usr/local/bin/ && cmake --install build && ldconfig
RUN mkdir -p /opt/stb && curl -fsSL -o /opt/stb/stb_image.h \
        https://raw.githubusercontent.com/nothings/stb/2c980bb59875b0d32144a71867fbdebb2f77cd20/stb_image.h \
    && curl -fsSL -o /opt/stb/stb_image_write.h \
        https://raw.githubusercontent.com/nothings/stb/2c980bb59875b0d32144a71867fbdebb2f77cd20/stb_image_write.h
COPY stbhdr.c /opt/stb/stbhdr.c
RUN gcc -O2 -o /usr/local/bin/stbhdr /opt/stb/stbhdr.c -lm
COPY exrref.cpp /opt/exrref.cpp
RUN g++ -O2 -std=c++17 -o /usr/local/bin/exrref /opt/exrref.cpp -I/usr/local/include/OpenEXR -I/usr/local/include/Imath         -L/usr/local/lib -lOpenEXR -lImath -lIex -lIlmThread
COPY uhdrref.c /opt/uhdrref.c
RUN gcc -O2 -o /usr/local/bin/uhdrref /opt/uhdrref.c -I/src/libultrahdr -L/usr/local/lib -luhdr && ldconfig
WORKDIR /work
