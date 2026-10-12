#include "install/install_transaction.h"
#include "install/product_registration.h"
#include "core/path_utf8.h"
#include "DefaultApplications.h"
#include <windows.h>
#include <objbase.h>
#include <shellapi.h>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <string>

using namespace fulloffice_install;
namespace {
std::filesystem::path self_path() {
    std::wstring path(32768, L'\0');
    const DWORD count = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    if (count == 0 || count == path.size()) return {};
    path.resize(count); return std::filesystem::path(path);
}
void show_error(const Failure& error, bool quiet) {
    std::cerr << error.to_string() << '\n';
    if (!quiet) { std::wstring text; Failure conversion; if (utf8_to_wide(error.to_string(), text, conversion)) MessageBoxW(nullptr, text.c_str(), L"FullOffice 安装", MB_OK | MB_ICONERROR); }
}
bool schedule_temporary_cleanup(const std::filesystem::path& executable) {
    wchar_t temporary[32768]{};
    wchar_t system[32768]{};
    if (!GetTempPathW(32768, temporary) || !GetSystemDirectoryW(system, 32768)) return false;
    std::error_code status;
    if (std::filesystem::weakly_canonical(executable.parent_path(), status) != std::filesystem::weakly_canonical(std::filesystem::path(temporary), status) ||
        status || !executable.filename().native().starts_with(L"FullOffice-Uninstall-")) return false;
    const auto script = std::filesystem::path(temporary) / (L"FullOffice-cleanup-" + std::to_wstring(GetCurrentProcessId()) + L".ps1");
    std::ofstream output(script, std::ios::binary);
    output << R"PS(param([int]$WaitProcessId,[string]$DeleteFile)
$ErrorActionPreference='Stop'
$target=[IO.Path]::GetFullPath($DeleteFile)
$temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
if(([IO.Path]::GetDirectoryName($target) -ne $temp) -or ([IO.Path]::GetFileName($target) -notlike 'FullOffice-Uninstall-*.exe')){exit 2}
$owner=Get-Process -Id $WaitProcessId -ErrorAction SilentlyContinue
if($owner -and -not $owner.WaitForExit(30000)){exit 3}
for($attempt=0;$attempt -lt 30;$attempt++){
  try {Remove-Item -LiteralPath $target -Force -ErrorAction Stop;Remove-Item -LiteralPath $PSCommandPath -Force;exit 0}
  catch {Start-Sleep -Milliseconds 100}
}
[IO.File]::WriteAllText($target+'.cleanup-failed.txt','Temporary uninstaller cleanup failed')
exit 4
)PS";
    output.close(); if (!output) return false;
    const auto shell = std::filesystem::path(system) / L"WindowsPowerShell/v1.0/powershell.exe";
    std::wstring command = L"\"" + shell.wstring() + L"\" -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script.wstring() +
        L"\" -WaitProcessId " + std::to_wstring(GetCurrentProcessId()) + L" -DeleteFile \"" + executable.wstring() + L"\"";
    STARTUPINFOW startup{}; startup.cb = sizeof(startup); PROCESS_INFORMATION process{};
    if (!CreateProcessW(shell.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &startup, &process)) return false;
    CloseHandle(process.hThread); CloseHandle(process.hProcess); return true;
}
}
int wmain(int argc, wchar_t** argv) {
    const HRESULT com = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(com)) return 1;
    struct Com final { ~Com() { CoUninitialize(); } } apartment;
    bool quiet = false, uninstall = false, launch = true;
    DWORD wait_pid = 0;
    std::filesystem::path package = self_path().parent_path();
    std::filesystem::path root = default_install_root();
    std::filesystem::path shortcut;
    std::wstring registry;
    for (int i = 1; i < argc; ++i) {
        const std::wstring argument = argv[i];
        if (argument == L"--quiet") quiet = true;
        else if (argument == L"--uninstall") uninstall = true;
        else if (argument == L"--no-launch") launch = false;
        else if (argument == L"--package-root" && i + 1 < argc) package = argv[++i];
        else if (argument == L"--install-root" && i + 1 < argc) root = argv[++i];
        else if (argument == L"--shortcut-path" && i + 1 < argc) shortcut = argv[++i];
        else if (argument == L"--registry-subkey" && i + 1 < argc) registry = argv[++i];
        else if (argument == L"--wait-pid" && i + 1 < argc) wait_pid = static_cast<DWORD>(std::wcstoul(argv[++i], nullptr, 10));
        else return 1;
    }
    if (wait_pid) {
        const HANDLE previous = OpenProcess(SYNCHRONIZE, FALSE, wait_pid);
        if (previous) { const DWORD waited = WaitForSingleObject(previous, 30000); CloseHandle(previous); if (waited != WAIT_OBJECT_0) return 1; }
    }
    if (root.empty() || self_path().empty()) return 1;
    ProductRegistrationOptions registration;
    registration.install_root = root;
    registration.product_name = "FullOffice";
    registration.publisher = "FullPeople";
    registration.start_menu_shortcut = shortcut;
    registration.uninstall_registry_subkey = registry;
    registration.uninstaller_path = root / L"current" / L"FullOfficeSetup.exe";
    registration.executable_path = root / L"current" / L"FullOffice.exe";
    registration.executable_relative = L"current\\FullOffice.exe";
    Failure error;
    if (uninstall) {
        const auto current_self = self_path();
        std::error_code status;
        if (std::filesystem::equivalent(current_self, registration.uninstaller_path, status)) {
            wchar_t temporary[32768]{};
            if (!GetTempPathW(32768, temporary)) return 1;
            const auto copy = std::filesystem::path(temporary) / (L"FullOffice-Uninstall-" + std::to_wstring(GetCurrentProcessId()) + L".exe");
            std::filesystem::copy_file(current_self, copy, std::filesystem::copy_options::none, status);
            if (status) return 1;
            std::wstring args = L"\"" + copy.wstring() + L"\" --uninstall --install-root \"" + root.wstring() + L"\" --wait-pid " + std::to_wstring(GetCurrentProcessId());
            if (quiet) args += L" --quiet";
            if (!registry.empty()) args += L" --registry-subkey \"" + registry + L"\"";
            if (!shortcut.empty()) args += L" --shortcut-path \"" + shortcut.wstring() + L"\"";
            STARTUPINFOW startup{}; startup.cb = sizeof(startup); PROCESS_INFORMATION process{};
            if (!CreateProcessW(copy.c_str(), args.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &startup, &process)) return 1;
            CloseHandle(process.hThread); CloseHandle(process.hProcess); return 0;
        }
        UninstallOptions options; options.install_root = root;
        UninstallResult result;
        if (!uninstall_product(options, result, error) || !remove_product_registration(registration, error) ||
            !fulltools::remove_default_candidate(registry.empty() ? L"FullOffice" : registry, error)) { show_error(error, quiet); return 2; }
        if (!quiet) MessageBoxW(nullptr, L"FullOffice 已卸载。文档、设置和阅读记录已保留。", L"FullOffice", MB_OK | MB_ICONINFORMATION);
        if (wait_pid && !schedule_temporary_cleanup(current_self)) return 6;
        return 0;
    }
    VerifiedPackage verified;
    VerifyOptions verify; verify.package_root = package;
    if (!verify_install_package(verify, verified, error)) { show_error(error, quiet); return 2; }
    InstallOptions options; options.package = verified; options.install_root = root;
    InstallResult result;
    if (!install_product(options, result, error)) { show_error(error, quiet); return 3; }
    registration.product_version = verified.version;
    registration.estimated_size_kib = static_cast<std::uint32_t>(verified.total_bytes / 1024);
    if (!apply_product_registration(registration, error)) { show_error(error, quiet); return 4; }
    if (!fulltools::register_default_candidate(registration.executable_path, registry.empty() ? L"FullOffice" : registry, error)) { show_error(error, quiet); return 4; }
    if (launch) {
        const auto executable = result.installed_executable;
        const HINSTANCE opened = ShellExecuteW(nullptr, L"open", executable.c_str(), nullptr, executable.parent_path().c_str(), SW_SHOWNORMAL);
        if (reinterpret_cast<INT_PTR>(opened) <= 32) return 5;
    }
    if (!quiet) MessageBoxW(nullptr, L"FullOffice 安装完成。", L"FullOffice", MB_OK | MB_ICONINFORMATION);
    return 0;
}
