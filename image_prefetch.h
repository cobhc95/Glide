#pragma once
#include <cstddef>
#include <filesystem>
#include <vector>

namespace GlidePrefetch {

std::vector<std::filesystem::path> BuildRapidPlan(const std::vector<std::filesystem::path>& files,
                                                  std::size_t currentIndex,
                                                  bool haveIndex,
                                                  int direction,
                                                  int forwardDepth = 8,
                                                  int reverseDepth = 2);
std::vector<std::filesystem::path> BuildNearbyPlan(const std::vector<std::filesystem::path>& files,
                                                   std::size_t currentIndex,
                                                   bool haveIndex,
                                                   int depth);

} // namespace GlidePrefetch
