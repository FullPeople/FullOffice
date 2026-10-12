#include "UpdateManifest.h"
#include "UpdateDownload.h"
#include "core/sha256.h"
#include <windows.h>
#include <commctrl.h>
#include <tlhelp32.h>
#include <objbase.h>
#include <shellapi.h>
#include <shlobj.h>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <string>
#include <thread>

namespace {
HWND window = nullptr, label = nullptr, action = nullptr, bar = nullptr;
std::string product, current_version;
std::filesystem::path install_root, stage, installer;
std::wstring manifest_url;
fulltools::UpdateManifest manifest;
fulltools::DownloadProgress progress;
std::atomic<int> phase{0}; // 1 checking, 2 available, 3 downloading, 4 verified, 5 installing, 6 finished, 7 failed
std::atomic<bool> busy{false};
std::mutex mutex;
std::wstring status_text;
std::thread worker;
bool close_requested = false;
std::filesystem::path self() {
    std::wstring text(32768, L'\0');
    const DWORD length = GetModuleFileNameW(nullptr, text.data(), static_cast<DWORD>(text.size()));
    if (!length || length == text.size()) return {};
    text.resize(length); return text;
}
std::wstring quote(const std::wstring& value) {
    std::wstring result = L"\""; size_t slashes = 0;
    for (wchar_t c : value) {
        if (c == L'\\') { ++slashes; continue; }
        result.append(c == L'\"' ? slashes * 2 + 1 : slashes, L'\\'); slashes = 0; result += c;
    }
    result.append(slashes * 2, L'\\'); return result + L'\"';
}
std::wstring wide(const std::string& text) {
    const int size = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), nullptr, 0);
    if (size <= 0) return L"无法显示错误详情";
    std::wstring value(static_cast<size_t>(size), L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), value.data(), size); return value;
}
bool ascii(const std::wstring& value, std::string& result) {
    result.clear();
    for (wchar_t c : value) { if (c > 127) return false; result += static_cast<char>(c); }
    return true;
}
void finish(int state, std::wstring text) {
    { std::lock_guard lock(mutex); status_text = std::move(text); }
    phase.store(state); busy.store(false);
    if (window) PostMessageW(window, WM_APP + 1, 0, 0);
}
bool process_running(const std::filesystem::path& executable) {
    const HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) return true;
    PROCESSENTRY32W entry{}; entry.dwSize = sizeof(entry);
    bool running = false;
    if (Process32FirstW(snapshot, &entry)) do {
        if (_wcsicmp(entry.szExeFile, executable.filename().c_str()) != 0) continue;
        const HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, entry.th32ProcessID);
        if (!process) { running = true; break; }
        std::wstring name(32768, L'\0'); DWORD length = static_cast<DWORD>(name.size());
        const BOOL read = QueryFullProcessImageNameW(process, 0, name.data(), &length); CloseHandle(process);
        if (!read) { running = true; break; }
        name.resize(length);
        if (_wcsicmp(name.c_str(), executable.c_str()) == 0) { running = true; break; }
    } while (Process32NextW(snapshot, &entry));
    CloseHandle(snapshot); return running;
}
std::filesystem::path product_executable() {
    const wchar_t* name = product == "MusicBoard" ? L"MusicBoard.exe" : product == "DesktopDice" ? L"DesktopDice.exe" : L"FullOffice.exe";
    return product == "DesktopDice" ? install_root / name : install_root / L"current" / name;
}
void check() {
    std::string envelope, error;
    if (!fulltools::fetch_update_text(manifest_url, envelope, error) || !fulltools::verify_update_manifest(envelope, product, manifest, error)) {
        finish(7, L"更新检查未完成：" + wide(error) + L"。当前软件可以继续使用。"); return;
    }
    if (!fulltools::newer_version(manifest.version, current_version)) { finish(6, L"当前版本已是最新版本。"); return; }
    finish(2, L"发现新版本 " + wide(manifest.version) + L"（约 " + std::to_wstring((manifest.bytes + 1048575) / 1048576) + L" MB）。下载完成后，请保存并退出软件再安装。");
}
void download() {
    std::string error;
    installer = stage / L"update-installer.exe";
    std::error_code status;
    if (std::filesystem::exists(installer, status) && !std::filesystem::remove(installer, status)) {
        finish(7, L"上一次下载文件无法清理，请关闭更新窗口后重试。"); return;
    }
    if (!fulltools::download_update(wide(manifest.url), installer, manifest.bytes, manifest.sha256, progress, error)) {
        const bool removed = !std::filesystem::exists(installer, status) || std::filesystem::remove(installer, status);
        finish(7, L"下载未完成：" + wide(error) + (removed ? L"。可稍后重试。" : L"。暂存文件清理失败。")); return;
    }
    finish(4, L"下载和校验完成。请保存并退出软件，然后点击“安装更新”。文档、曲库和设置会保留。");
}
void install() {
    if (process_running(product_executable())) { finish(4, L"软件仍在运行。请先保存并退出，再点击“安装更新”。"); return; }
    // Revalidate the downloaded file immediately before executing the signed package.
    // The installer also validates every payload file before replacing the current installation.
    HANDLE guard = CreateFileW(installer.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (guard == INVALID_HANDLE_VALUE) { finish(7, L"无法锁定更新文件，请重新下载。"); return; }
    LARGE_INTEGER size{}; fulloffice_install::Failure failure; std::string digest;
    if (!GetFileSizeEx(guard, &size) || static_cast<std::uint64_t>(size.QuadPart) != manifest.bytes ||
        !fulloffice_install::sha256_file_hex(installer, digest, failure) || digest != manifest.sha256) {
        CloseHandle(guard); finish(7, L"更新文件发生变化，已停止安装。请重新下载。"); return;
    }
    std::wstring command = quote(installer.wstring()) + L" --quiet --install-root " + quote(install_root.wstring());
    STARTUPINFOW startup{}; startup.cb = sizeof(startup); PROCESS_INFORMATION process{};
    const BOOL opened = CreateProcessW(installer.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, stage.c_str(), &startup, &process);
    if (!opened) { CloseHandle(guard); finish(7, L"无法启动更新安装程序，Windows 错误 " + std::to_wstring(GetLastError()) + L"。"); return; }
    CloseHandle(process.hThread);
    const DWORD waited = WaitForSingleObject(process.hProcess, INFINITE);
    DWORD code = 1; const BOOL read = GetExitCodeProcess(process.hProcess, &code);
    CloseHandle(process.hProcess); CloseHandle(guard);
    finish(waited == WAIT_OBJECT_0 && read && code == 0 ? 6 : 7,
        waited == WAIT_OBJECT_0 && read && code == 0 ? L"更新完成。可以从开始菜单重新打开软件。" : L"更新未完成，安装程序已保留可恢复的版本。请退出软件后重试。");
}
void begin(int state, void(*operation)(), const wchar_t* text) {
    if (busy.exchange(true)) return;
    if (worker.joinable()) worker.join();
    phase.store(state); progress.cancelled.store(false); SetWindowTextW(label, text); EnableWindow(action, FALSE);
    worker = std::thread(operation);
}
LRESULT CALLBACK procedure(HWND owner, UINT message, WPARAM wparam, LPARAM lparam) {
    if (message == WM_COMMAND && LOWORD(wparam) == 1) {
        if (phase.load() == 2) begin(3, download, L"正在下载更新…");
        else if (phase.load() == 4) begin(5, install, L"正在安装更新，请稍候…");
        else if (phase.load() == 7) begin(1, check, L"正在重新检查更新…");
        return 0;
    }
    if (message == WM_TIMER) {
        const auto total = progress.total.load();
        if (total) SendMessageW(bar, PBM_SETPOS, static_cast<WPARAM>(progress.received.load() * 100 / total), 0);
        return 0;
    }
    if (message == WM_APP + 1) {
        if (worker.joinable()) worker.join();
        if (close_requested) { DestroyWindow(owner); return 0; }
        { std::lock_guard lock(mutex); SetWindowTextW(label, status_text.c_str()); }
        const int state = phase.load();
        SetWindowTextW(action, state == 2 ? L"下载更新" : state == 4 ? L"安装更新" : L"重试");
        EnableWindow(action, state == 2 || state == 4 || state == 7); return 0;
    }
    if (message == WM_CLOSE) {
        if (phase.load() == 5) return 0;
        if (busy.load()) { close_requested = true; progress.cancelled.store(true); SetWindowTextW(label, L"正在结束下载并清理，请稍候…"); return 0; }
        DestroyWindow(owner); return 0;
    }
    if (message == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(owner, message, wparam, lparam);
}
bool launch_worker(const std::wstring& args) {
    wchar_t temp[32768]{}, identifier[40]{}; GUID guid{};
    if (!GetTempPathW(32768, temp) || FAILED(CoCreateGuid(&guid)) || !StringFromGUID2(guid, identifier, 40)) return false;
    stage = std::filesystem::path(temp) / (L"FullTools-update-" + std::wstring(identifier));
    if (!CreateDirectoryW(stage.c_str(), nullptr)) return false;
    const auto copy = stage / L"FullToolsUpdater.exe";
    if (!CopyFileW(self().c_str(), copy.c_str(), TRUE)) return false;
    std::wstring command = quote(copy.wstring()) + args + L" --worker";
    STARTUPINFOW startup{}; startup.cb = sizeof(startup); PROCESS_INFORMATION process{};
    if (!CreateProcessW(copy.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, stage.c_str(), &startup, &process)) return false;
    CloseHandle(process.hThread); CloseHandle(process.hProcess); return true;
}
void schedule_cleanup() {
    // No recursive deletion: the worker owns exactly these files in its unique temporary directory.
    const auto script = stage / L"cleanup.ps1";
    std::ofstream output(script, std::ios::binary);
    output << R"PS(param([int]$OwnerPid)
$ErrorActionPreference='Stop'
$stage=[IO.Path]::GetFullPath($PSScriptRoot)
$temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
if(([IO.Path]::GetDirectoryName($stage) -ne $temp) -or ([IO.Path]::GetFileName($stage) -notlike 'FullTools-update-*')){exit 2}
if((Get-Item -LiteralPath $stage).Attributes -band [IO.FileAttributes]::ReparsePoint){exit 3}
$owner=Get-Process -Id $OwnerPid -ErrorAction SilentlyContinue
if($owner -and -not $owner.WaitForExit(30000)){exit 4}
Set-Location -LiteralPath $temp
foreach($name in @('FullToolsUpdater.exe','update-installer.exe')){
  $file=Join-Path $stage $name
  if(Test-Path -LiteralPath $file){Remove-Item -LiteralPath $file -Force}
}
Remove-Item -LiteralPath $PSCommandPath -Force
Remove-Item -LiteralPath $stage
)PS";
    output.close();
    wchar_t system[32768]{};
    if (!output || !GetSystemDirectoryW(system, 32768)) return;
    const auto shell = std::filesystem::path(system) / L"WindowsPowerShell/v1.0/powershell.exe";
    std::wstring args = L"-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File " + quote(script.wstring()) + L" -OwnerPid " + std::to_wstring(GetCurrentProcessId());
    std::wstring command = quote(shell.wstring()) + L" " + args;
    STARTUPINFOW startup{}; startup.cb = sizeof(startup); PROCESS_INFORMATION process{};
    if (CreateProcessW(shell.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, stage.parent_path().c_str(), &startup, &process)) {
        CloseHandle(process.hThread); CloseHandle(process.hProcess);
    }
}
bool background_due() {
    wchar_t local[32768]{};
    const DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", local, 32768);
    if (!length || length >= 32768) return false;
    const auto folder = std::filesystem::path(local) / L"FullPeople" / L"UpdateChecks";
    std::error_code error; std::filesystem::create_directories(folder, error);
    if (error) return false;
    const auto marker = folder / (wide(product) + L".timestamp");
    HANDLE file = CreateFileW(marker.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    ULARGE_INTEGER now{}; FILETIME utc{}; GetSystemTimeAsFileTime(&utc); now.LowPart = utc.dwLowDateTime; now.HighPart = utc.dwHighDateTime;
    std::uint64_t previous = 0; DWORD read = 0, written = 0;
    const BOOL loaded = ReadFile(file, &previous, sizeof(previous), &read, nullptr);
    const bool due = loaded && (read != sizeof(previous) || now.QuadPart < previous || now.QuadPart - previous >= 24ULL * 60 * 60 * 10000000);
    if (due) {
        LARGE_INTEGER zero{};
        if (!SetFilePointerEx(file, zero, nullptr, FILE_BEGIN) || !WriteFile(file, &now.QuadPart, sizeof(now.QuadPart), &written, nullptr) || written != sizeof(now.QuadPart)) {
            CloseHandle(file); return false;
        }
    }
    CloseHandle(file); return due;
}
}
int wmain(int argc, wchar_t** argv) {
    bool staged = false, background = false;
    for (int i = 1; i < argc; ++i) {
        const std::wstring arg = argv[i];
        if (arg == L"--worker") staged = true;
        else if (arg == L"--background") background = true;
        else if (arg == L"--product" && i + 1 < argc) { if (!ascii(argv[++i], product)) return 2; }
        else if (arg == L"--version" && i + 1 < argc) { if (!ascii(argv[++i], current_version)) return 2; }
        else if (arg == L"--install-root" && i + 1 < argc) install_root = argv[++i];
        else return 2;
    }
    if ((product != "FullOffice" && product != "MusicBoard" && product != "DesktopDice") || current_version.empty()) return 2;
    if (install_root.empty()) {
        bool installed = false;
        if (product == "DesktopDice") {
            PWSTR local = nullptr;
            if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &local))) {
                const auto expected = std::filesystem::path(local) / L"Programs" / L"DesktopDice";
                installed = _wcsicmp(self().parent_path().c_str(), expected.c_str()) == 0;
                if (installed) install_root = expected;
                CoTaskMemFree(local);
            }
        } else if (self().parent_path().filename() == L"current") {
            installed = true; install_root = self().parent_path().parent_path();
        }
        if (!installed) {
            if (!background) MessageBoxW(nullptr, L"便携版请从官网下载新版。安装版支持在这里检查和安装更新。", L"软件更新", MB_OK | MB_ICONINFORMATION);
            return 2;
        }
    }
    std::error_code filesystem_error;
    if (install_root.is_relative() || !std::filesystem::is_regular_file(product_executable(), filesystem_error) || filesystem_error) return 2;
    if (!staged) {
        if (background && !background_due()) return 0;
        std::wstring args = L" --product " + quote(wide(product)) + L" --version " + quote(wide(current_version)) + L" --install-root " + quote(install_root.wstring());
        if (background) args += L" --background";
        if (!launch_worker(args)) { MessageBoxW(nullptr, L"无法启动更新检查，请稍后重试。", L"软件更新", MB_OK | MB_ICONERROR); return 2; }
        return 0;
    }
    stage = self().parent_path();
    wchar_t temporary[32768]{};
    if (!GetTempPathW(32768, temporary) || !stage.filename().native().starts_with(L"FullTools-update-")) return 2;
    const auto actual_parent = std::filesystem::weakly_canonical(stage.parent_path(), filesystem_error);
    const auto expected_parent = std::filesystem::weakly_canonical(temporary, filesystem_error);
    const DWORD attributes = GetFileAttributesW(stage.c_str());
    if (filesystem_error || actual_parent != expected_parent || attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_REPARSE_POINT)) return 2;
    manifest_url = L"https://dnd.center/downloads/updates/" + wide(product) + L"-windows-x64.manifest";
    if (background) {
        check();
        if (phase.load() != 2) { schedule_cleanup(); return 0; }
    }
    INITCOMMONCONTROLSEX controls{sizeof(controls), ICC_PROGRESS_CLASS}; InitCommonControlsEx(&controls);
    const auto instance = GetModuleHandleW(nullptr);
    HICON icon = nullptr; ExtractIconExW(product_executable().c_str(), 0, &icon, nullptr, 1);
    WNDCLASSW cls{}; cls.hInstance = instance; cls.lpfnWndProc = procedure; cls.lpszClassName = L"FullTools.Update";
    cls.hCursor = LoadCursorW(nullptr, IDC_ARROW); cls.hIcon = icon; cls.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
    if (!RegisterClassW(&cls)) { schedule_cleanup(); return 2; }
    const std::wstring title = wide(product) + L" · 软件更新";
    window = CreateWindowExW(0, cls.lpszClassName, title.c_str(), WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU, CW_USEDEFAULT, CW_USEDEFAULT, 570, 260, nullptr, nullptr, instance, nullptr);
    label = CreateWindowW(L"STATIC", L"正在检查更新…", WS_CHILD | WS_VISIBLE, 28, 28, 500, 100, window, nullptr, instance, nullptr);
    bar = CreateWindowW(PROGRESS_CLASSW, nullptr, WS_CHILD | WS_VISIBLE, 28, 138, 500, 18, window, nullptr, instance, nullptr);
    action = CreateWindowW(L"BUTTON", L"下载更新", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON, 400, 175, 126, 30, window, reinterpret_cast<HMENU>(1), instance, nullptr);
    if (!window || !label || !bar || !action) { if (window) DestroyWindow(window); schedule_cleanup(); return 2; }
    const HFONT font = CreateFontW(-16, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Microsoft YaHei UI");
    for (HWND control : {label, action}) SendMessageW(control, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
    EnableWindow(action, FALSE); SetTimer(window, 1, 100, nullptr); ShowWindow(window, SW_SHOWNORMAL);
    if (background) PostMessageW(window, WM_APP + 1, 0, 0); else begin(1, check, L"正在检查更新…");
    MSG message{};
    while (GetMessageW(&message, nullptr, 0, 0) > 0) { if (!IsDialogMessageW(window, &message)) { TranslateMessage(&message); DispatchMessageW(&message); } }
    if (worker.joinable()) worker.join();
    if (font) DeleteObject(font); if (icon) DestroyIcon(icon);
    schedule_cleanup(); return 0;
}
