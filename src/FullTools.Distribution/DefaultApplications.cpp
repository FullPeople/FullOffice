#include "DefaultApplications.h"
#include <windows.h>
#include <shlobj.h>
#include <array>
#include <algorithm>
#include <cwctype>
#include <vector>

namespace fulltools {
namespace {
struct Format { const wchar_t* suffix; const wchar_t* kind; const wchar_t* title; bool requires_office = false; };
constexpr std::array formats{
    Format{L".pdf", L"Pdf", L"PDF 文档"}, Format{L".md", L"Markdown", L"Markdown 文档"},
    Format{L".markdown", L"Markdown", L"Markdown 文档"}, Format{L".txt", L"Text", L"文本文档"},
    Format{L".docx", L"Word", L"Word 文档", true}, Format{L".xlsx", L"Spreadsheet", L"Excel 工作簿", true},
    Format{L".pptx", L"Presentation", L"演示文稿", true},
    Format{L".odt", L"Word", L"OpenDocument 文档", true},
    Format{L".ods", L"Spreadsheet", L"OpenDocument 工作簿", true},
    Format{L".odp", L"Presentation", L"OpenDocument 演示文稿", true},
    Format{L".rtf", L"Word", L"RTF 文档", true}};
bool valid(const std::wstring& name) {
    return !name.empty() && name.size() < 100 && name.starts_with(L"FullOffice") &&
        std::all_of(name.begin(), name.end(), [](wchar_t c) { return std::iswalnum(c) || c == L'-' || c == L'.'; });
}
bool value(const std::wstring& path, const wchar_t* name, const std::wstring& data, fulloffice_install::Failure& failure) {
    HKEY key = nullptr;
    LSTATUS result = RegCreateKeyExW(HKEY_CURRENT_USER, path.c_str(), 0, nullptr, 0, KEY_SET_VALUE, nullptr, &key, nullptr);
    if (result == ERROR_SUCCESS) {
        result = RegSetValueExW(key, name, 0, REG_SZ, reinterpret_cast<const BYTE*>(data.c_str()),
            static_cast<DWORD>((data.size() + 1) * sizeof(wchar_t)));
        RegCloseKey(key);
    }
    if (result != ERROR_SUCCESS) { failure.set("install.default_registration", "Registry write failed: " + std::to_string(result)); return false; }
    return true;
}
bool erase(const std::wstring& path, fulloffice_install::Failure& failure) {
    const LSTATUS result = RegDeleteTreeW(HKEY_CURRENT_USER, path.c_str());
    if (result != ERROR_SUCCESS && result != ERROR_FILE_NOT_FOUND) {
        failure.set("install.default_registration", "Registry cleanup failed: " + std::to_string(result)); return false;
    }
    return true;
}
}
bool remove_default_candidate(const std::wstring& application, fulloffice_install::Failure& failure) {
    if (!valid(application)) { failure.set("install.default_registration", "Invalid application identity"); return false; }
    HKEY registered = nullptr;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, L"Software\\RegisteredApplications", 0, KEY_SET_VALUE, &registered) == ERROR_SUCCESS) {
        const LSTATUS result = RegDeleteValueW(registered, application.c_str()); RegCloseKey(registered);
        if (result != ERROR_SUCCESS && result != ERROR_FILE_NOT_FOUND) { failure.set("install.default_registration", "Cannot remove application registration"); return false; }
    }
    if (!erase(L"Software\\FullPeople\\" + application, failure)) return false;
    for (const auto& format : formats) {
        const std::wstring id = application + L"." + format.kind;
        if (!erase(L"Software\\Classes\\" + id, failure)) return false;
        HKEY open_with = nullptr;
        const std::wstring path = std::wstring(L"Software\\Classes\\") + format.suffix + L"\\OpenWithProgids";
        if (RegOpenKeyExW(HKEY_CURRENT_USER, path.c_str(), 0, KEY_SET_VALUE, &open_with) == ERROR_SUCCESS) {
            const LSTATUS result = RegDeleteValueW(open_with, id.c_str()); RegCloseKey(open_with);
            if (result != ERROR_SUCCESS && result != ERROR_FILE_NOT_FOUND) { failure.set("install.default_registration", "Cannot remove Open With entry"); return false; }
        }
    }
    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
    return true;
}
bool register_default_candidate(const std::filesystem::path& executable,
    const std::wstring& application, fulloffice_install::Failure& failure) {
    if (!valid(application) || executable.is_relative() || executable.wstring().find(L'\"') != std::wstring::npos) {
        failure.set("install.default_registration", "Invalid application identity or executable path"); return false;
    }
    if (!remove_default_candidate(application, failure)) return false;
    const std::wstring root = L"Software\\FullPeople\\" + application + L"\\Capabilities";
    std::error_code status;
    const auto engine = executable.parent_path() / L"office-engine";
    const bool office_available = std::filesystem::is_regular_file(engine / L"FullOfficeOfficeHost.exe", status) &&
        !status && std::filesystem::is_regular_file(engine / L"ascdocumentscore.dll", status) && !status;
    bool written = value(root, L"ApplicationName", application, failure) &&
        value(root, L"ApplicationDescription", office_available ? L"FullOffice：文档、表格、演示文稿、PDF 与 Markdown" : L"FullOffice：PDF 阅读与标记、Markdown 和文本编辑", failure) &&
        value(root, L"ApplicationIcon", L"\"" + executable.wstring() + L"\",0", failure);
    for (const auto& format : formats) {
        if (!written) break;
        if (format.requires_office && !office_available) continue;
        const std::wstring id = application + L"." + format.kind;
        const std::wstring classes = L"Software\\Classes\\" + id;
        written = value(classes, nullptr, format.title, failure) &&
            value(classes + L"\\DefaultIcon", nullptr, L"\"" + executable.wstring() + L"\",0", failure) &&
            value(classes + L"\\shell\\open\\command", nullptr, L"\"" + executable.wstring() + L"\" \"%1\"", failure) &&
            value(root + L"\\FileAssociations", format.suffix, id, failure);
        if (written) {
            HKEY open_with = nullptr;
            const std::wstring path = std::wstring(L"Software\\Classes\\") + format.suffix + L"\\OpenWithProgids";
            LSTATUS result = RegCreateKeyExW(HKEY_CURRENT_USER, path.c_str(), 0, nullptr, 0, KEY_SET_VALUE, nullptr, &open_with, nullptr);
            if (result == ERROR_SUCCESS) { result = RegSetValueExW(open_with, id.c_str(), 0, REG_NONE, nullptr, 0); RegCloseKey(open_with); }
            if (result != ERROR_SUCCESS) { failure.set("install.default_registration", "Cannot register Open With entry"); written = false; }
        }
    }
    if (written) written = value(L"Software\\RegisteredApplications", application.c_str(), root, failure);
    if (!written) { fulloffice_install::Failure ignored; static_cast<void>(remove_default_candidate(application, ignored)); return false; }
    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
    return true;
}
}
