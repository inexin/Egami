// OpenEXR references for Egami's EXR tests: every channel of every part as OpenEXR 3.5 decodes it, and the RGBA image its
// RgbaInputFile makes (luminance-chroma converted, missing channels filled) for each layer of the first part.
//
//   exrref in.exr out.ref     writes int32 JSON length, the JSON, then the samples the JSON points into
//   exrref time in.exr runs [threads]
//                             decodes every channel of the first part runs times (with that many threads, default
//                             none), prints the best time in milliseconds
//   exrref yca in.exr out.exr compression
//                             rewrites a file's RGBA as luminance and subsampled chroma (Y, RY, BY), as RgbaOutputFile does
#include <ImfChannelList.h>
#include <ImfChromaticitiesAttribute.h>
#include <ImfFrameBuffer.h>
#include <ImfHeader.h>
#include <ImfInputPart.h>
#include <ImfMultiPartInputFile.h>
#include <ImfRgbaFile.h>
#include <ImfStandardAttributes.h>
#include <ImfTileDescriptionAttribute.h>
#include <ImfThreading.h>
#include <ImfPartType.h>
#include <ImfCompression.h>

#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <set>
#include <sstream>
#include <string>
#include <vector>

using namespace Imf;
using namespace Imath;

static std::string escape(const std::string& s)
{
    std::string o;
    for (char c : s)
    {
        if (c == '"' || c == '\\') o += '\\';
        o += c;
    }
    return o;
}

static int sampleBytes(PixelType t) { return t == HALF ? 2 : 4; }

// Counts the multiples of s in [lo, hi].
static int sampled(int lo, int hi, int s)
{
    int first = lo % s == 0 ? lo : lo + (s - ((lo % s) + s) % s);
    if (first > hi) return 0;
    return (hi - first) / s + 1;
}

int main(int argc, char** argv)
{
    if ((argc == 4 || argc == 5) && strcmp(argv[1], "time") == 0)
    {
        int runs = atoi(argv[3]);
        setGlobalThreadCount(argc == 5 ? atoi(argv[4]) : 0);
        double best = 1e30;
        for (int r = 0; r < runs; r++)
        {
            auto t0 = std::chrono::steady_clock::now();
            MultiPartInputFile file(argv[2]);
            InputPart part(file, 0);
            const Header& h = part.header();
            Box2i dw = h.dataWindow();
            std::vector<std::vector<char>> buffers;
            FrameBuffer fb;
            for (ChannelList::ConstIterator c = h.channels().begin(); c != h.channels().end(); ++c)
            {
                int w = sampled(dw.min.x, dw.max.x, c.channel().xSampling);
                int hh = sampled(dw.min.y, dw.max.y, c.channel().ySampling);
                int b = sampleBytes(c.channel().type);
                buffers.emplace_back((size_t)w * hh * b);
                fb.insert(c.name(), Slice::Make(c.channel().type, buffers.back().data(), dw, b, (size_t)w * b,
                                                c.channel().xSampling, c.channel().ySampling));
            }
            part.setFrameBuffer(fb);
            part.readPixels(dw.min.y, dw.max.y);
            double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count();
            if (ms < best) best = ms;
        }
        printf("%s %.3f\n", argv[2], best);
        return 0;
    }
    if (argc == 5 && strcmp(argv[1], "yca") == 0)
    {
        RgbaInputFile in(argv[2]);
        Box2i dw = in.dataWindow();
        int w = dw.max.x - dw.min.x + 1, hh = dw.max.y - dw.min.y + 1;
        std::vector<Rgba> pixels((size_t)w * hh);
        in.setFrameBuffer(pixels.data() - dw.min.x - (size_t)dw.min.y * w, 1, w);
        in.readPixels(dw.min.y, dw.max.y);
        Header h(in.displayWindow(), dw);
        h.compression() = (Compression)atoi(argv[4]);
        RgbaOutputFile out(argv[3], h, WRITE_YCA);
        out.setFrameBuffer(pixels.data() - dw.min.x - (size_t)dw.min.y * w, 1, w);
        out.writePixels(hh);
        return 0;
    }
    if (argc != 3)
    {
        fprintf(stderr, "usage: exrref in.exr out.ref | exrref time in.exr runs\n");
        return 2;
    }

    MultiPartInputFile file(argv[1]);
    std::ostringstream json;
    std::vector<char> blob;
    json << "{\"parts\":[";
    for (int p = 0; p < file.parts(); p++)
    {
        const Header& h = file.header(p);
        Box2i dw = h.dataWindow(), dispw = h.displayWindow();
        std::string type = h.hasType() ? h.type() : (h.hasTileDescription() ? TILEDIMAGE : SCANLINEIMAGE);
        if (p) json << ",";
        json << "{\"name\":\"" << escape(h.hasName() ? h.name() : "") << "\",\"type\":\"" << type << "\",\"compression\":" << (int)h.compression()
             << ",\"dataWindow\":[" << dw.min.x << "," << dw.min.y << "," << dw.max.x << "," << dw.max.y << "]"
             << ",\"displayWindow\":[" << dispw.min.x << "," << dispw.min.y << "," << dispw.max.x << "," << dispw.max.y << "]"
             << ",\"lineOrder\":" << (int)h.lineOrder() << ",\"pixelAspectRatio\":" << h.pixelAspectRatio();
        if (h.hasTileDescription())
        {
            const TileDescription& t = h.tileDescription();
            json << ",\"tiles\":[" << t.xSize << "," << t.ySize << "," << (int)t.mode << "," << (int)t.roundingMode << "]";
        }
        if (hasChromaticities(h))
        {
            const Chromaticities& c = chromaticities(h);
            json << ",\"chromaticities\":[" << c.red.x << "," << c.red.y << "," << c.green.x << "," << c.green.y << "," << c.blue.x << ","
                 << c.blue.y << "," << c.white.x << "," << c.white.y << "]";
        }
        json << ",\"channels\":[";
        bool deep = type == DEEPSCANLINE || type == DEEPTILE;
        std::vector<std::vector<char>> buffers;
        FrameBuffer fb;
        bool first = true;
        for (ChannelList::ConstIterator c = h.channels().begin(); c != h.channels().end(); ++c)
        {
            const Channel& ch = c.channel();
            int w = sampled(dw.min.x, dw.max.x, ch.xSampling);
            int hh = sampled(dw.min.y, dw.max.y, ch.ySampling);
            int b = sampleBytes(ch.type);
            if (!first) json << ",";
            first = false;
            json << "{\"name\":\"" << escape(c.name()) << "\",\"type\":" << (int)ch.type << ",\"xSampling\":" << ch.xSampling
                 << ",\"ySampling\":" << ch.ySampling << ",\"pLinear\":" << (ch.pLinear ? "true" : "false") << ",\"width\":" << w
                 << ",\"height\":" << hh << ",\"offset\":" << (blob.size() + [&] { size_t s = 0; for (auto& v : buffers) s += v.size(); return s; }()) << "}";
            buffers.emplace_back(deep ? 0 : (size_t)w * hh * b);
            if (!deep)
            {
                fb.insert(c.name(), Slice::Make(ch.type, buffers.back().data(), dw, b, (size_t)w * b, ch.xSampling, ch.ySampling));
            }
        }
        json << "]";
        if (!deep)
        {
            InputPart part(file, p);
            part.setFrameBuffer(fb);
            part.readPixels(dw.min.y, dw.max.y);
        }
        for (auto& v : buffers) blob.insert(blob.end(), v.begin(), v.end());

        // RgbaInputFile for the first part: the default layer and every layer prefix with color channels.
        if (p == 0 && !deep)
        {
            std::set<std::string> layers;
            layers.insert("");
            for (ChannelList::ConstIterator c = h.channels().begin(); c != h.channels().end(); ++c)
            {
                std::string n = c.name();
                size_t dot = n.rfind('.');
                if (dot != std::string::npos)
                {
                    std::string last = n.substr(dot + 1);
                    if (last == "R" || last == "G" || last == "B" || last == "Y" || last == "A") layers.insert(n.substr(0, dot));
                }
            }
            json << ",\"rgba\":[";
            bool firstLayer = true;
            for (const std::string& layer : layers)
            {
                RgbaInputFile rgba(argv[1], layer);
                if (!(rgba.channels() & (WRITE_RGB | WRITE_Y | WRITE_A))) continue;
                int w = dw.max.x - dw.min.x + 1, hh = dw.max.y - dw.min.y + 1;
                std::vector<Rgba> pixels((size_t)w * hh);
                rgba.setFrameBuffer(pixels.data() - dw.min.x - (size_t)dw.min.y * w, 1, w);
                rgba.readPixels(dw.min.y, dw.max.y);
                if (!firstLayer) json << ",";
                firstLayer = false;
                json << "{\"layer\":\"" << escape(layer) << "\",\"channels\":" << (int)rgba.channels() << ",\"offset\":" << blob.size() << "}";
                const char* bytes = reinterpret_cast<const char*>(pixels.data());
                blob.insert(blob.end(), bytes, bytes + pixels.size() * sizeof(Rgba));
            }
            json << "]";
        }
        json << "}";
    }
    json << "]}";

    std::string text = json.str();
    FILE* out = fopen(argv[2], "wb");
    int length = (int)text.size();
    fwrite(&length, 4, 1, out);
    fwrite(text.data(), 1, text.size(), out);
    fwrite(blob.data(), 1, blob.size(), out);
    fclose(out);
    return 0;
}
