#pragma once
#define NOMINMAX
#include "image_decode.h"
#include <windows.h>
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <climits>
#include <cwctype>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

namespace GlideDecode::Extended {

namespace fs = std::filesystem;

inline std::wstring Lower(std::wstring s) {
    std::transform(s.begin(), s.end(), s.begin(), [](wchar_t c){ return static_cast<wchar_t>(towlower(c)); });
    return s;
}

inline DecodedImage MakeResultBase(const DecodeRequest& req) {
    DecodedImage r;
    r.path=req.path;
    r.generation=req.generation;
    r.currentRequest=req.currentRequest;
    r.preview=req.preview;
    r.refinement=req.refinement;
    return r;
}

inline bool PrepareBgra(DecodedImage& r, UINT w, UINT h) {
    if (!w || !h) return false;
    const uint64_t stride64=static_cast<uint64_t>(w)*4ull;
    const uint64_t bytes64=stride64*static_cast<uint64_t>(h);
    if (stride64>UINT_MAX || bytes64>UINT_MAX || bytes64>static_cast<uint64_t>(SIZE_MAX)) return false;
    r.sourceWidth=w;
    r.sourceHeight=h;
    r.width=w;
    r.height=h;
    r.stride=static_cast<UINT>(stride64);
    r.pixels.resize(static_cast<size_t>(bytes64));
    return true;
}

inline void PutPremulBgra(BYTE* d, BYTE red, BYTE green, BYTE blue, BYTE alpha=255) {
    d[0]=BYTE((static_cast<uint16_t>(blue)*alpha+127)/255);
    d[1]=BYTE((static_cast<uint16_t>(green)*alpha+127)/255);
    d[2]=BYTE((static_cast<uint16_t>(red)*alpha+127)/255);
    d[3]=alpha;
}

inline bool ReadWholeFileBytes(const std::wstring& path, std::vector<BYTE>& out) {
    std::ifstream f(fs::path(path), std::ios::binary);
    if (!f) return false;
    f.seekg(0, std::ios::end);
    const auto n = f.tellg();
    if (n <= 0 || n > static_cast<std::streamoff>(512ull * 1024ull * 1024ull)) return false;
    f.seekg(0, std::ios::beg);
    out.resize(static_cast<size_t>(n));
    f.read(reinterpret_cast<char*>(out.data()), static_cast<std::streamsize>(n));
    return f.good() || f.eof();
}

inline uint16_t Be16(const BYTE* p) { return static_cast<uint16_t>((uint16_t(p[0]) << 8) | p[1]); }
inline uint32_t Be32(const BYTE* p) { return (uint32_t(p[0])<<24)|(uint32_t(p[1])<<16)|(uint32_t(p[2])<<8)|uint32_t(p[3]); }
inline uint16_t Le16(const BYTE* p) { return static_cast<uint16_t>(uint16_t(p[0]) | (uint16_t(p[1])<<8)); }
inline uint32_t Le32(const BYTE* p) { return uint32_t(p[0])|(uint32_t(p[1])<<8)|(uint32_t(p[2])<<16)|(uint32_t(p[3])<<24); }

inline bool ReadAsciiLine(const BYTE*& p, const BYTE* end, std::string& line) {
    line.clear();
    if (p>=end) return false;
    while(p<end && *p!='\n') {
        if(*p!='\r') line.push_back(static_cast<char>(*p));
        ++p;
    }
    if(p<end && *p=='\n')++p;
    return true;
}

DecodedImage DecodeTga(const DecodeRequest& req);
DecodedImage DecodeDib(const DecodeRequest& req);
DecodedImage DecodePnm(const DecodeRequest& req);
DecodedImage DecodeQoi(const DecodeRequest& req);
DecodedImage DecodeFarbfeld(const DecodeRequest& req);
DecodedImage DecodePcx(const DecodeRequest& req);
DecodedImage DecodeRadianceHdr(const DecodeRequest& req);
DecodedImage DecodeDds(const DecodeRequest& req);
DecodedImage DecodeWbmp(const DecodeRequest& req);
DecodedImage DecodePfm(const DecodeRequest& req);
DecodedImage DecodeXbm(const DecodeRequest& req);

} // namespace GlideDecode::Extended
