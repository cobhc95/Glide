#pragma once
#define NOMINMAX
#include <windows.h>
#include <cstddef>
#include <deque>
#include <string>
#include <unordered_map>
#include <vector>
#include "image_decode.h"

namespace GlideCache {

struct CachedPixels {
    UINT width{};
    UINT height{};
    UINT sourceWidth{};
    UINT sourceHeight{};
    UINT stride{};
    bool preview{};
    std::vector<BYTE> pixels;
};

class DecodedCache {
public:
    void Add(const std::wstring& key, const GlideDecode::DecodedImage& decoded, std::size_t maxItems);
    CachedPixels* Find(const std::wstring& key);
    const CachedPixels* Find(const std::wstring& key) const;
    bool Contains(const std::wstring& key) const;
    void Clear();
    std::size_t Size() const { return items_.size(); }

private:
    std::unordered_map<std::wstring, CachedPixels> items_;
    std::deque<std::wstring> order_;
};

} // namespace GlideCache
