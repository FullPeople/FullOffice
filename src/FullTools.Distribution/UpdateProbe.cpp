#include "UpdateManifest.h"
#include <filesystem>
#include <fstream>
#include <iostream>
#include <string>
int wmain(int count, wchar_t** arguments) {
    if (count == 4 && std::wstring(arguments[1]) == L"--newer") {
        std::string candidate, current;
        for (const wchar_t* c = arguments[2]; *c; ++c) { if (*c > 127) return 2; candidate += static_cast<char>(*c); }
        for (const wchar_t* c = arguments[3]; *c; ++c) { if (*c > 127) return 2; current += static_cast<char>(*c); }
        return fulltools::newer_version(candidate, current) ? 0 : 1;
    }
    if (count != 3) return 2;
    std::ifstream input(std::filesystem::path(arguments[1]), std::ios::binary);
    if (!input) return 2;
    const std::string contents((std::istreambuf_iterator<char>(input)), std::istreambuf_iterator<char>());
    const std::wstring wide_product(arguments[2]);
    std::string product;
    for (wchar_t letter : wide_product) { if (letter > 127) return 2; product.push_back(static_cast<char>(letter)); }
    fulltools::UpdateManifest manifest;
    std::string error;
    const bool valid = fulltools::verify_update_manifest(contents, product, manifest, error);
    std::cout << (valid ? "VERIFIED " + manifest.product + " " + manifest.version : "REJECTED " + error) << '\n';
    return valid ? 0 : 1;
}
