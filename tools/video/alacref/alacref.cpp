// alacref: a small driver for Apple's reference ALAC codec (https://github.com/macosforge/alac, Apache-2.0), built by
// tools/video/alacref/build.sh or the Dockerfile next to it. Egami.Video's gates use it to check that its encoder writes
// the same packets as Apple's and that Apple's decoder reads them. This file is Egami's (MIT); it only calls Apple's API.
//
//   alacref encode <in.raw> <out.alac> <rate> <channels> <bits> <fast 0|1> [frame size]
//   alacref decode <in.alac> <out.raw>
//
// Raw PCM is interleaved little-endian: 16-bit samples in 2 bytes, 20 and 24-bit in 3 (20-bit left-justified, the low 4
// bits zero, as Apple's encoder takes them), 32-bit in 4. A .alac file is the 24-byte ALACSpecificConfig, then each packet
// as a 4-byte little-endian length and its bytes.
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <vector>
#include "ALACEncoder.h"
#include "ALACDecoder.h"
#include "ALACBitUtilities.h"

static void put32(FILE* f, uint32_t v)
{
    uint8_t b[4] = { (uint8_t)v, (uint8_t)(v >> 8), (uint8_t)(v >> 16), (uint8_t)(v >> 24) };
    fwrite(b, 1, 4, f);
}

static bool get32(FILE* f, uint32_t* v)
{
    uint8_t b[4];
    if (fread(b, 1, 4, f) != 4) return false;
    *v = b[0] | (b[1] << 8) | (b[2] << 16) | ((uint32_t)b[3] << 24);
    return true;
}

static int encode(int argc, char** argv)
{
    if (argc < 8) return 2;
    FILE* in = fopen(argv[2], "rb");
    FILE* out = fopen(argv[3], "wb");
    if (!in || !out) return 1;
    uint32_t rate = atoi(argv[4]), channels = atoi(argv[5]), bits = atoi(argv[6]);
    bool fast = atoi(argv[7]) != 0;
    uint32_t frameSize = argc > 8 ? atoi(argv[8]) : kALACDefaultFramesPerPacket;
    uint32_t bytes = bits == 16 ? 2 : bits == 32 ? 4 : 3;

    AudioFormatDescription input = {};
    input.mSampleRate = rate;
    input.mFormatID = kALACFormatLinearPCM;
    input.mFormatFlags = kALACFormatFlagIsSignedInteger | kALACFormatFlagIsPacked;
    input.mBytesPerPacket = input.mBytesPerFrame = bytes * channels;
    input.mFramesPerPacket = 1;
    input.mChannelsPerFrame = channels;
    input.mBitsPerChannel = bits;
    AudioFormatDescription output = {};
    output.mSampleRate = rate;
    output.mFormatID = kALACFormatAppleLossless;
    output.mFormatFlags = bits == 16 ? 1 : bits == 20 ? 2 : bits == 24 ? 3 : 4;
    output.mFramesPerPacket = frameSize;
    output.mChannelsPerFrame = channels;

    ALACEncoder encoder;
    encoder.SetFrameSize(frameSize);
    encoder.SetFastMode(fast);
    encoder.InitializeEncoder(output);
    ALACSpecificConfig config;
    encoder.GetConfig(config);
    fwrite(&config, 1, sizeof(config), out);

    std::vector<uint8_t> readBuffer(frameSize * input.mBytesPerFrame);
    std::vector<uint8_t> writeBuffer(frameSize * channels * 5 + 64);
    for (;;)
    {
        size_t got = fread(readBuffer.data(), 1, readBuffer.size(), in);
        got -= got % input.mBytesPerFrame;
        if (got == 0) break;
        int32_t numBytes = (int32_t)got;
        if (encoder.Encode(input, input, readBuffer.data(), writeBuffer.data(), &numBytes) != 0) return 1;
        put32(out, numBytes);
        fwrite(writeBuffer.data(), 1, numBytes, out);
    }

    fclose(in);
    fclose(out);
    return 0;
}

static int decode(int argc, char** argv)
{
    if (argc < 4) return 2;
    FILE* in = fopen(argv[2], "rb");
    FILE* out = fopen(argv[3], "wb");
    if (!in || !out) return 1;
    uint8_t cookie[24];
    if (fread(cookie, 1, 24, in) != 24) return 1;
    ALACDecoder decoder;
    if (decoder.Init(cookie, 24) != 0) return 1;
    uint32_t frameSize = decoder.mConfig.frameLength, channels = decoder.mConfig.numChannels, bits = decoder.mConfig.bitDepth;
    uint32_t bytes = bits == 16 ? 2 : bits == 32 ? 4 : 3;
    std::vector<uint8_t> samples(frameSize * channels * bytes);
    uint32_t length;
    while (get32(in, &length))
    {
        std::vector<uint8_t> packet(length + 16);
        if (fread(packet.data(), 1, length, in) != length) return 1;
        BitBuffer buffer;
        BitBufferInit(&buffer, packet.data(), length);
        uint32_t count = 0;
        if (decoder.Decode(&buffer, samples.data(), frameSize, channels, &count) != 0) return 1;
        fwrite(samples.data(), 1, count * channels * bytes, out);
    }

    fclose(in);
    fclose(out);
    return 0;
}

int main(int argc, char** argv)
{
    if (argc > 1 && strcmp(argv[1], "encode") == 0) return encode(argc, argv);
    if (argc > 1 && strcmp(argv[1], "decode") == 0) return decode(argc, argv);
    fprintf(stderr, "usage: alacref encode <in.raw> <out.alac> <rate> <channels> <bits> <fast 0|1> [frame size]\n"
                    "       alacref decode <in.alac> <out.raw>\n");
    return 2;
}
