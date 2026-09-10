#include "image_prefetch.h"
#include <algorithm>

namespace GlidePrefetch {

std::vector<std::filesystem::path> BuildRapidPlan(const std::vector<std::filesystem::path>& files,
                                                  std::size_t currentIndex,
                                                  bool haveIndex,
                                                  int direction,
                                                  int forwardDepth,
                                                  int reverseDepth) {
    std::vector<std::filesystem::path> out;
    if (!haveIndex || files.empty() || direction == 0 || currentIndex >= files.size()) return out;
    const int dir = direction > 0 ? 1 : -1;
    auto add = [&](long long index) {
        if (index >= 0 && index < static_cast<long long>(files.size())) out.push_back(files[static_cast<std::size_t>(index)]);
    };
    for (int d = 1; d <= std::max(0, forwardDepth); ++d) add(static_cast<long long>(currentIndex) + dir * d);
    for (int d = 1; d <= std::max(0, reverseDepth); ++d) add(static_cast<long long>(currentIndex) - dir * d);
    return out;
}

std::vector<std::filesystem::path> BuildNearbyPlan(const std::vector<std::filesystem::path>& files,
                                                   std::size_t currentIndex,
                                                   bool haveIndex,
                                                   int depth) {
    std::vector<std::filesystem::path> out;
    if (!haveIndex || files.empty() || currentIndex >= files.size()) return out;
    depth = std::max(0, depth);
    for (int d = 1; d <= depth; ++d) {
        if (currentIndex + static_cast<std::size_t>(d) < files.size()) out.push_back(files[currentIndex + static_cast<std::size_t>(d)]);
        if (currentIndex >= static_cast<std::size_t>(d)) out.push_back(files[currentIndex - static_cast<std::size_t>(d)]);
    }
    return out;
}

} // namespace GlidePrefetch
