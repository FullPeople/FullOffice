#include "BundleExtract.h"
#include "BundleConfig.h"
#include "core/sha256.h"
#include <windows.h>
#include <commctrl.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <wrl/client.h>
#include <atomic>
#include <filesystem>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace {
HWND window = nullptr, path_edit = nullptr, status_label = nullptr, progress_bar = nullptr, install_button = nullptr, close_button = nullptr;
HFONT font = nullptr;
fulltools::ExtractionProgress progress;
std::atomic<int> phase{0};
std::atomic<DWORD> result_code{0};
std::mutex message_mutex;
std::wstring final_message;
std::thread worker;
std::filesystem::path install_root;
std::filesystem::path default_install_root;
std::filesystem::path test_shortcut;
std::filesystem::path test_evidence;
std::wstring test_registry;
bool quiet = false;
std::wstring quote(const std::wstring& text) {
    std::wstring output = L"\""; size_t slash = 0;
    for (wchar_t c : text) {
        if (c == L'\\') { ++slash; continue; }
        output.append(c == L'\"' ? slash * 2 + 1 : slash, L'\\'); slash = 0; output += c;
    }
    output.append(slash * 2, L'\\'); output += L'\"'; return output;
}
void finish(DWORD code, const std::wstring& message) {
    { std::lock_guard lock(message_mutex); final_message = message; }
    result_code.store(code); phase.store(3);
    if (window) PostMessageW(window, WM_APP + 1, 0, 0);
}
std::wstring wide(const std::string& text) {
    if (text.empty()) return {};
    const int count = MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0);
    std::wstring result(static_cast<size_t>(count), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), result.data(), count); return result;
}
void install() {
    const HRSRC resource = FindResourceW(nullptr, MAKEINTRESOURCEW(101), RT_RCDATA);
    const HGLOBAL loaded = resource ? LoadResource(nullptr, resource) : nullptr;
    const auto* bytes = loaded ? static_cast<const std::uint8_t*>(LockResource(loaded)) : nullptr;
    const DWORD length = resource ? SizeofResource(nullptr, resource) : 0;
    fulloffice_install::Failure failure;
    std::string digest;
    if (!bytes || !fulloffice_install::sha256_hex(bytes, length, digest, failure) || digest != payload_sha256) {
        finish(2, L"安装包校验失败，尚未更改已安装的软件。"); return;
    }
    wchar_t temporary[32768]{};
    GUID identifier{};
    wchar_t id[40]{};
    if (!GetTempPathW(32768, temporary) || FAILED(CoCreateGuid(&identifier)) || !StringFromGUID2(identifier, id, 40)) {
        finish(2, L"无法建立安装暂存目录。"); return;
    }
    const auto base = std::filesystem::path(temporary);
    const auto stage = base / (L"FullTools-stage-" + std::wstring(id));
    std::string error;
    if (!fulltools::extract_bundle(bytes, length, stage, progress, error)) {
        std::string cleanup; static_cast<void>(fulltools::remove_owned_stage(stage, base, cleanup));
        finish(2, L"准备安装失败：" + wide(error)); return;
    }
    auto package = stage;
    std::error_code status;
    if (!std::filesystem::is_regular_file(package / backend_name, status)) {
        for (const auto& entry : std::filesystem::directory_iterator(stage, status)) {
            if (entry.is_directory(status) && std::filesystem::is_regular_file(entry.path() / backend_name, status)) { package = entry.path(); break; }
        }
    }
    if (status || !std::filesystem::is_regular_file(package / backend_name, status)) {
        std::string cleanup; static_cast<void>(fulltools::remove_owned_stage(stage, base, cleanup));
        finish(2, L"安装包缺少安装程序。"); return;
    }
    phase.store(2);
    const auto executable = package / backend_name;
    std::wstring command = quote(executable.wstring()) + L" --quiet";
    const auto argument = [&](const wchar_t* name, const std::wstring& value) {
        return std::wstring(L" ") + name + (backend_uses_equals ? L"=" : L" ") + quote(value);
    };
    if constexpr (backend_uses_equals) {
        if (!test_registry.empty()) command += L" --diagnostic-install" + argument(L"--package-root", package.wstring()) +
            argument(L"--install-root", install_root.wstring()) + argument(L"--evidence-root", test_evidence.wstring());
    } else {
        command += argument(L"--package-root", package.wstring()) + argument(L"--install-root", install_root.wstring()) + L" --no-launch";
    }
    if (!test_registry.empty()) command += argument(L"--registry-subkey", test_registry);
    if (!test_shortcut.empty()) command += argument(L"--shortcut-path", test_shortcut.wstring());
    STARTUPINFOW startup{}; startup.cb = sizeof(startup); PROCESS_INFORMATION process{};
    DWORD code = 2;
    if (CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, package.c_str(), &startup, &process)) {
        CloseHandle(process.hThread);
        const DWORD waited = WaitForSingleObject(process.hProcess, INFINITE);
        if (waited == WAIT_OBJECT_0 && !GetExitCodeProcess(process.hProcess, &code)) code = 2;
        CloseHandle(process.hProcess);
    }
    std::string cleanup;
    if (!fulltools::remove_owned_stage(stage, base, cleanup) && code == 0) {
        finish(0, L"安装完成。暂存目录清理未完成，下次安装可重试。"); return;
    }
    finish(code, code == 0 ? L"安装完成。现在可以从开始菜单打开软件。" : L"安装未完成。请先保存并退出正在运行的软件，然后重试。");
}
void begin() {
    if (phase.load() == 1 || phase.load() == 2) return;
    if (worker.joinable()) worker.join();
    if (path_edit) {
        const int length = GetWindowTextLengthW(path_edit);
        std::wstring text(static_cast<size_t>(length) + 1, L'\0'); GetWindowTextW(path_edit, text.data(), length + 1); text.resize(static_cast<size_t>(length)); install_root = text;
    }
    if (install_root.empty() || install_root.is_relative()) { result_code.store(1); if (!quiet) MessageBoxW(window, L"请选择完整的安装路径。", product_title, MB_OK | MB_ICONINFORMATION); return; }
    if constexpr (backend_uses_equals) {
        if (test_registry.empty() && install_root != default_install_root) {
            result_code.store(1); if (!quiet) MessageBoxW(window, L"此产品使用固定的用户安装目录。", product_title, MB_OK | MB_ICONINFORMATION); return;
        }
    }
    progress.cancelled.store(false); phase.store(1);
    if (install_button) EnableWindow(install_button, FALSE);
    if (path_edit) EnableWindow(path_edit, FALSE);
    if (status_label) SetWindowTextW(status_label, L"正在准备安装…");
    worker = std::thread(install);
}
LRESULT CALLBACK procedure(HWND owner, UINT message, WPARAM wparam, LPARAM lparam) {
    if (message == WM_COMMAND && LOWORD(wparam) == 1001) { begin(); return 0; }
    if (message == WM_TIMER) {
        const auto total = progress.total.load();
        if (total) SendMessageW(progress_bar, PBM_SETPOS, static_cast<WPARAM>(progress.completed.load() * 100 / total), 0);
        if (phase.load() == 2) { SetWindowTextW(status_label, L"正在安装并注册软件，请稍候…"); EnableWindow(close_button, FALSE); }
        return 0;
    }
    if (message == WM_APP + 1) {
        if (worker.joinable()) worker.join();
        std::wstring text; { std::lock_guard lock(message_mutex); text = final_message; }
        SetWindowTextW(status_label, text.c_str()); EnableWindow(close_button, TRUE); SetWindowTextW(close_button, L"关闭");
        if (result_code.load() != 0) { EnableWindow(install_button, TRUE); EnableWindow(path_edit, !backend_uses_equals); }
        return 0;
    }
    if (message == WM_COMMAND && LOWORD(wparam) == 1002) { SendMessageW(owner, WM_CLOSE, 0, 0); return 0; }
    if (message == WM_CLOSE) {
        if (phase.load() == 2) return 0;
        if (phase.load() == 1) { progress.cancelled.store(true); SetWindowTextW(status_label, L"正在取消并清理暂存文件…"); return 0; }
        DestroyWindow(owner); return 0;
    }
    if (message == WM_DESTROY) { PostQuitMessage(static_cast<int>(result_code.load())); return 0; }
    return DefWindowProcW(owner, message, wparam, lparam);
}
}
int wmain(int argc, wchar_t** argv) {
    const HRESULT initialized = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(initialized)) return 1;
    wchar_t data[32768]{};
    const DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", data, 32768);
    if (length == 0 || length >= 32768) { CoUninitialize(); return 1; }
    install_root = std::filesystem::path(data) / L"Programs" / product_id;
    if constexpr (backend_uses_equals) {
        PWSTR local = nullptr;
        if (FAILED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &local))) { CoUninitialize(); return 1; }
        install_root = std::filesystem::path(local) / L"Programs" / product_id; CoTaskMemFree(local);
    }
    default_install_root = install_root;
    for (int i = 1; i < argc; ++i) {
        const std::wstring argument = argv[i];
        if (argument == L"--quiet") quiet = true;
        else if (argument == L"--install-root" && i + 1 < argc) install_root = argv[++i];
        else if (argument == L"--registry-subkey" && i + 1 < argc) test_registry = argv[++i];
        else if (argument == L"--shortcut-path" && i + 1 < argc) test_shortcut = argv[++i];
        else if (argument == L"--evidence-root" && i + 1 < argc) test_evidence = argv[++i];
        else { CoUninitialize(); return 1; }
    }
    if constexpr (backend_uses_equals) {
        if (!test_registry.empty() && (test_evidence.empty() || test_shortcut.empty())) { CoUninitialize(); return 1; }
    }
    if (quiet) { begin(); if (worker.joinable()) worker.join(); CoUninitialize(); return static_cast<int>(result_code.load()); }
    INITCOMMONCONTROLSEX controls{sizeof(controls), ICC_PROGRESS_CLASS}; InitCommonControlsEx(&controls);
    WNDCLASSEXW cls{}; cls.cbSize = sizeof(cls); cls.hInstance = GetModuleHandleW(nullptr); cls.lpfnWndProc = procedure;
    cls.hIcon = LoadIconW(cls.hInstance, MAKEINTRESOURCEW(1)); cls.hIconSm = cls.hIcon; cls.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    cls.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1); cls.lpszClassName = L"FullTools.InstallWizard";
    if (!RegisterClassExW(&cls)) { CoUninitialize(); return 1; }
    window = CreateWindowExW(0, cls.lpszClassName, (std::wstring(L"安装 ") + product_title).c_str(), WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU,
        CW_USEDEFAULT, CW_USEDEFAULT, 670, 360, nullptr, nullptr, cls.hInstance, nullptr);
    if (!window) { CoUninitialize(); return 1; }
    font = CreateFontW(-16, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, L"Microsoft YaHei UI");
    const auto child = [&](const wchar_t* type, const wchar_t* text, DWORD style, int x, int y, int width, int height, int id) {
        HWND control = CreateWindowExW(0, type, text, WS_CHILD | WS_VISIBLE | style, x, y, width, height, window,
            reinterpret_cast<HMENU>(static_cast<INT_PTR>(id)), cls.hInstance, nullptr);
        if (control) SendMessageW(control, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE); return control;
    };
    child(L"STATIC", product_title, 0, 28, 25, 610, 28, 0);
    child(L"STATIC", L"安装位置", 0, 28, 76, 610, 25, 0);
    path_edit = child(L"EDIT", install_root.c_str(), WS_BORDER | ES_AUTOHSCROLL | WS_TABSTOP, 28, 110, 610, 30, 1003);
    if constexpr (backend_uses_equals) SendMessageW(path_edit, EM_SETREADONLY, TRUE, 0);
    progress_bar = child(PROGRESS_CLASSW, L"", 0, 28, 160, 610, 20, 1004); SendMessageW(progress_bar, PBM_SETRANGE32, 0, 100);
    status_label = child(L"STATIC", L"安装到当前用户目录，无需管理员权限。", 0, 28, 199, 610, 45, 1005);
    install_button = child(L"BUTTON", L"安装", BS_DEFPUSHBUTTON | WS_TABSTOP, 421, 263, 103, 32, 1001);
    close_button = child(L"BUTTON", L"取消", WS_TABSTOP, 535, 263, 103, 32, 1002);
    SetTimer(window, 1, 100, nullptr); ShowWindow(window, SW_SHOW);
    MSG message{}; while (GetMessageW(&message, nullptr, 0, 0) > 0) { if (!IsDialogMessageW(window, &message)) { TranslateMessage(&message); DispatchMessageW(&message); } }
    if (worker.joinable()) worker.join(); if (font) DeleteObject(font); CoUninitialize(); return static_cast<int>(result_code.load());
}
