#pragma once
#include <cstdint>
#include <string>
#include <string_view>
namespace fulltools {
struct UpdateManifest final {
    std::string product, version, architecture, url, sha256;
    std::uint64_t bytes = 0;
};
[[nodiscard]] bool verify_update_manifest(std::string_view envelope, std::string_view expected_product,
    UpdateManifest& manifest, std::string& error);
[[nodiscard]] bool newer_version(std::string_view candidate, std::string_view current);
}
