#include "UpdateDownload.h"
#include <windows.h>
#include <iostream>
#include <string>
#include <thread>
int wmain(int count, wchar_t** arguments) {
    if (count == 3 && std::wstring(arguments[1]) == L"--fetch") {
        std::string contents, error;
        const bool result = fulltools::fetch_update_text(arguments[2], contents, error);
        std::cout << (result ? "FETCHED " + std::to_string(contents.size()) : "REJECTED " + error) << '\n';
        return result ? 0 : 1;
    }
    if (count != 5) return 2;
    const std::uint64_t size = std::wcstoull(arguments[3], nullptr, 10);
    std::string digest;
    for (const wchar_t* c = arguments[4]; *c; ++c) { if (*c > 127) return 2; digest += static_cast<char>(*c); }
    fulltools::DownloadProgress progress;
    std::string error;
    const bool result = fulltools::download_update(arguments[1], std::filesystem::path(arguments[2]), size, digest, progress, error);
    std::cout << (result ? "VERIFIED " : "REJECTED ") << progress.received.load() << " " << error << '\n';
    return result ? 0 : 1;
}
