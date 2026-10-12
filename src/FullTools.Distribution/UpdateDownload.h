#pragma once
#include <atomic>
#include <filesystem>
#include <string>
#include <cstdint>
namespace fulltools {
struct DownloadProgress {
    std::atomic<std::uint64_t> received{0};
    std::atomic<std::uint64_t> total{0};
    std::atomic<bool> cancelled{false};
};
[[nodiscard]] bool fetch_update_text(const std::wstring& url, std::string& text, std::string& error);
[[nodiscard]] bool download_update(const std::wstring& url, const std::filesystem::path& destination,
    std::uint64_t expected_size, const std::string& expected_digest, DownloadProgress& progress, std::string& error);
}
