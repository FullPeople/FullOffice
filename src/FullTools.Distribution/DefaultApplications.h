#pragma once
#include "core/failure.h"
#include <filesystem>
#include <string>
namespace fulltools {
[[nodiscard]] bool register_default_candidate(const std::filesystem::path& executable,
    const std::wstring& application, fulloffice_install::Failure& failure);
[[nodiscard]] bool remove_default_candidate(const std::wstring& application,
    fulloffice_install::Failure& failure);
}
