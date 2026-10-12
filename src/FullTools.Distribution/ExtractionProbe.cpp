#include "BundleExtract.h"
#include <filesystem>
#include <fstream>
#include <iostream>
#include <vector>
int wmain(int count, wchar_t** arguments) {
    if (count != 3) return 2;
    const std::filesystem::path input(arguments[1]);
    const std::filesystem::path output(arguments[2]);
    std::ifstream file(input, std::ios::binary);
    if (!file) return 2;
    std::vector<std::uint8_t> bytes((std::istreambuf_iterator<char>(file)), std::istreambuf_iterator<char>());
    fulltools::ExtractionProgress progress;
    std::string error;
    const bool extracted = fulltools::extract_bundle(bytes.data(), bytes.size(), output, progress, error);
    std::cout << (extracted ? "PASS " : "REJECT ") << error << '\n';
    return extracted ? 0 : 1;
}
