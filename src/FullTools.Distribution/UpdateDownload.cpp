#include "UpdateDownload.h"
#include "core/sha256.h"
#include <windows.h>
#include <winhttp.h>
#include <array>
#include <vector>

namespace fulltools {
namespace {
struct Internet { HINTERNET value = nullptr; ~Internet() { if (value) WinHttpCloseHandle(value); } };
struct File { HANDLE value = INVALID_HANDLE_VALUE; ~File() { if (value != INVALID_HANDLE_VALUE) CloseHandle(value); } };
bool request(const std::wstring& url, Internet& session, Internet& connection, Internet& response, std::string& error) {
    URL_COMPONENTS parts{}; parts.dwStructSize = sizeof(parts); parts.dwHostNameLength = static_cast<DWORD>(-1);
    parts.dwUrlPathLength = static_cast<DWORD>(-1); parts.dwExtraInfoLength = static_cast<DWORD>(-1);
    if (url.find_first_of(L"\r\n\0", 0, 3) != std::wstring::npos ||
        !WinHttpCrackUrl(url.c_str(), 0, ICU_REJECT_USERPWD, &parts) || parts.nScheme != INTERNET_SCHEME_HTTPS ||
        parts.dwUserNameLength != 0 || parts.dwPasswordLength != 0) { error = "Update URL must use HTTPS without credentials"; return false; }
    const std::wstring host(parts.lpszHostName, parts.dwHostNameLength);
    std::wstring path(parts.lpszUrlPath, parts.dwUrlPathLength);
    if (path.empty()) path = L"/";
    if (parts.dwExtraInfoLength) path.append(parts.lpszExtraInfo, parts.dwExtraInfoLength);
    session.value = WinHttpOpen(L"FullTools-Update/1.0", WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!session.value || !WinHttpSetTimeouts(session.value, 10000, 10000, 20000, 10000)) { error = "Cannot initialize update network request"; return false; }
    connection.value = WinHttpConnect(session.value, host.c_str(), parts.nPort, 0);
    if (!connection.value) { error = "Cannot connect to update server"; return false; }
    response.value = WinHttpOpenRequest(connection.value, L"GET", path.c_str(), nullptr, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, WINHTTP_FLAG_SECURE);
    DWORD redirects = WINHTTP_OPTION_REDIRECT_POLICY_DISALLOW_HTTPS_TO_HTTP;
    if (!response.value || !WinHttpSetOption(response.value, WINHTTP_OPTION_REDIRECT_POLICY, &redirects, sizeof(redirects)) ||
        !WinHttpSendRequest(response.value, WINHTTP_NO_ADDITIONAL_HEADERS, 0, WINHTTP_NO_REQUEST_DATA, 0, 0, 0) ||
        !WinHttpReceiveResponse(response.value, nullptr)) { error = "Update server did not respond"; return false; }
    DWORD status = 0, size = sizeof(status);
    if (!WinHttpQueryHeaders(response.value, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, WINHTTP_HEADER_NAME_BY_INDEX, &status, &size, WINHTTP_NO_HEADER_INDEX) || status != 200) {
        error = "Update server returned HTTP " + std::to_string(status); return false;
    }
    return true;
}
}
bool fetch_update_text(const std::wstring& url, std::string& text, std::string& error) {
    Internet session, connection, response;
    if (!request(url, session, connection, response, error)) return false;
    text.clear(); std::array<char, 8192> buffer{};
    for (;;) {
        DWORD count = 0;
        if (!WinHttpReadData(response.value, buffer.data(), static_cast<DWORD>(buffer.size()), &count)) { error = "Cannot read update manifest"; return false; }
        if (!count) break;
        if (text.size() + count > 65536) { error = "Update manifest exceeds the allowed size"; return false; }
        text.append(buffer.data(), count);
    }
    return true;
}
bool download_update(const std::wstring& url, const std::filesystem::path& destination, const std::uint64_t expected_size,
    const std::string& expected_digest, DownloadProgress& progress, std::string& error) {
    if (expected_size == 0 || expected_size > 2ULL * 1024 * 1024 * 1024 || expected_digest.size() != 64 ||
        expected_digest.find_first_not_of("0123456789abcdef") != std::string::npos) {
        error = "Update download contract is invalid"; return false;
    }
    if (progress.cancelled.load()) { error = "Update download cancelled"; return false; }
    Internet session, connection, response;
    if (!request(url, session, connection, response, error)) return false;
    std::error_code status;
    if (destination.empty() || destination.is_relative() || std::filesystem::exists(destination, status)) { error = "Update cache path must be new and absolute"; return false; }
    File file;
    file.value = CreateFileW(destination.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_WRITE_THROUGH, nullptr);
    if (file.value == INVALID_HANDLE_VALUE) { error = "Cannot create update download cache"; return false; }
    progress.total.store(expected_size); progress.received.store(0);
    std::array<std::uint8_t, 65536> buffer{};
    for (;;) {
        if (progress.cancelled.load()) { error = "Update download cancelled"; return false; }
        DWORD count = 0;
        if (!WinHttpReadData(response.value, buffer.data(), static_cast<DWORD>(buffer.size()), &count)) { error = "Update download was interrupted"; return false; }
        if (!count) break;
        if (progress.received.load() + count > expected_size) { error = "Update download exceeds the signed size"; return false; }
        DWORD written = 0;
        if (!WriteFile(file.value, buffer.data(), count, &written, nullptr) || written != count) { error = "Cannot write update cache"; return false; }
        progress.received.fetch_add(count);
    }
    if (progress.received.load() != expected_size || !FlushFileBuffers(file.value)) { error = "Update download is incomplete"; return false; }
    CloseHandle(file.value); file.value = INVALID_HANDLE_VALUE;
    fulloffice_install::Failure failure;
    std::string actual;
    if (!fulloffice_install::sha256_file_hex(destination, actual, failure) || actual != expected_digest) { error = "Downloaded update does not match the signed digest"; return false; }
    return true;
}
}
