#include "image_cache.h"
#include <algorithm>

namespace GlideCache {

void DecodedCache::Add(const std::wstring& key, const GlideDecode::DecodedImage& decoded, std::size_t maxItems) {
    if (FAILED(decoded.hr) || decoded.pixels.empty() || key.empty()) return;
    auto existing = items_.find(key);
    if (existing != items_.end() && !existing->second.preview && decoded.preview) return;
    items_[key] = CachedPixels{decoded.width, decoded.height,
                               decoded.sourceWidth ? decoded.sourceWidth : decoded.width,
                               decoded.sourceHeight ? decoded.sourceHeight : decoded.height,
                               decoded.stride, decoded.preview, decoded.pixels};
    order_.erase(std::remove(order_.begin(), order_.end(), key), order_.end());
    order_.push_back(key);
    while (order_.size() > std::max<std::size_t>(2, maxItems)) {
        const std::wstring victim = order_.front();
        order_.pop_front();
        items_.erase(victim);
    }
}

CachedPixels* DecodedCache::Find(const std::wstring& key) {
    auto it = items_.find(key);
    return it == items_.end() ? nullptr : &it->second;
}
const CachedPixels* DecodedCache::Find(const std::wstring& key) const {
    auto it = items_.find(key);
    return it == items_.end() ? nullptr : &it->second;
}
bool DecodedCache::Contains(const std::wstring& key) const { return items_.find(key) != items_.end(); }
void DecodedCache::Clear() { items_.clear(); order_.clear(); }

} // namespace GlideCache
