#pragma once
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <string>

namespace fulltools {
struct ExtractionProgress final {
    std::atomic<std::uint64_t> completed{0};
    std::atomic<std::uint64_t> total{0};
    std::atomic<bool> cancelled{false};
};
[[nodiscard]] bool extract_bundle(const std::uint8_t* bytes, std::size_t length,
    const std::filesystem::path& destination, ExtractionProgress& progress, std::string& error);
[[nodiscard]] bool remove_owned_stage(const std::filesystem::path& destination,
    const std::filesystem::path& expected_parent, std::string& error);
}
