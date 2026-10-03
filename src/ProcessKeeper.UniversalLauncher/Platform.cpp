#include "Platform.h"
#include <shellapi.h>
#include <sstream>
#include <map>
#include <stdexcept>

namespace pk {
HMODULE LoadSystemLibrary(const wchar_t* name) {
    wchar_t directory[MAX_PATH]{};
    auto length = GetSystemDirectoryW(directory, MAX_PATH);
    if (!length || length >= MAX_PATH) return nullptr;
    return LoadLibraryExW((std::wstring(directory) + L"\\" + name).c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
}
bool IsRuntimeOverride(const std::wstring& name) {
    std::wstring upper = name;
    for (auto& character : upper) if (character >= L'a' && character <= L'z') character = static_cast<wchar_t>(character - L'a' + L'A');
    return upper.rfind(L"DOTNET_", 0) == 0 || upper.rfind(L"CORECLR_", 0) == 0 || upper.rfind(L"COR_", 0) == 0 || upper.rfind(L"COMPLUS_", 0) == 0 ||
        upper == L"DEVPATH" || upper == L"APP_PATHS" || upper == L"APP_NI_PATHS" || upper == L"TRUSTED_PLATFORM_ASSEMBLIES" ||
        upper == L"PROBING_DIRECTORIES" || upper == L"NATIVE_DLL_SEARCH_DIRECTORIES" || upper == L"__COMPAT_LAYER";
}
std::vector<wchar_t> BuildChildEnvironment() {
    auto less = [](const std::wstring& left, const std::wstring& right) { return CompareStringOrdinal(left.c_str(), -1, right.c_str(), -1, TRUE) == CSTR_LESS_THAN; };
    std::map<std::wstring, std::wstring, decltype(less)> entries(less);
    auto block = GetEnvironmentStringsW();
    if (!block) throw std::runtime_error("Cannot read process environment");
    try {
        for (auto value = block; *value; value += wcslen(value) + 1) {
            std::wstring entry(value); const auto separator = entry.find(L'=', entry[0] == L'=' ? 1 : 0);
            if (separator != std::wstring::npos && !IsRuntimeOverride(entry.substr(0, separator))) entries[entry.substr(0, separator)] = entry.substr(separator + 1);
        }
    } catch (...) { FreeEnvironmentStringsW(block); throw; }
    FreeEnvironmentStringsW(block);
    wchar_t windows[MAX_PATH]{}, system[MAX_PATH]{};
    if (!GetWindowsDirectoryW(windows, MAX_PATH) || !GetSystemDirectoryW(system, MAX_PATH)) throw std::runtime_error("Cannot identify Windows directories");
    entries[L"SystemRoot"] = windows; entries[L"windir"] = windows;
    entries[L"PATH"] = std::wstring(system) + L";" + windows;
    std::vector<wchar_t> result;
    for (const auto& entry : entries) {
        const auto line = entry.first + L"=" + entry.second;
        result.insert(result.end(), line.begin(), line.end()); result.push_back(L'\0');
    }
    result.push_back(L'\0'); return result;
}
namespace {
using RtlGetVersionFn = LONG(WINAPI*)(OSVERSIONINFOEXW*);
using IsWow64Process2Fn = BOOL(WINAPI*)(HANDLE, USHORT*, USHORT*);
}

Host DetectHost() {
    Host result;
    auto ntdll = GetModuleHandleW(L"ntdll.dll");
    auto version = reinterpret_cast<RtlGetVersionFn>(GetProcAddress(ntdll, "RtlGetVersion"));
    if (!version) return result;
    OSVERSIONINFOEXW info{}; info.dwOSVersionInfoSize = sizeof(info);
    if (version(&info) < 0) return result;
    result.major = info.dwMajorVersion; result.minor = info.dwMinorVersion;
    result.build = info.dwBuildNumber; result.servicePack = info.wServicePackMajor;
    auto kernel = GetModuleHandleW(L"kernel32.dll");
    auto wow2 = reinterpret_cast<IsWow64Process2Fn>(GetProcAddress(kernel, "IsWow64Process2"));
    USHORT processMachine = 0, nativeMachine = 0;
    if (wow2 && wow2(GetCurrentProcess(), &processMachine, &nativeMachine)) result.machine = nativeMachine;
    else {
        SYSTEM_INFO system{}; GetNativeSystemInfo(&system);
        if (system.wProcessorArchitecture == PROCESSOR_ARCHITECTURE_AMD64) result.machine = IMAGE_FILE_MACHINE_AMD64;
        else if (system.wProcessorArchitecture == PROCESSOR_ARCHITECTURE_INTEL) result.machine = IMAGE_FILE_MACHINE_I386;
    }
    // The 32-bit view contains the Framework setup registration on both x86/x64 Windows.
    HKEY key{};
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\Microsoft\\NET Framework Setup\\NDP\\v4\\Full",
        0, KEY_QUERY_VALUE | KEY_WOW64_32KEY, &key) == ERROR_SUCCESS) {
        DWORD size = sizeof(DWORD), type = 0, release = 0;
        if (RegQueryValueExW(key, L"Release", nullptr, &type, reinterpret_cast<BYTE*>(&release), &size) == ERROR_SUCCESS &&
            type == REG_DWORD && size == sizeof(DWORD)) result.framework = release;
        RegCloseKey(key);
    }
    return result;
}

Route ChooseRoute(const Host& host, bool forceLegacy) {
    // The x86 outer executable is supported by Windows on Arm emulation. Only the
    // architecture reported by IsWow64Process2 may select the native Arm64 payload.
    if (host.machine == IMAGE_FILE_MACHINE_ARM64)
        return !forceLegacy && (host.major > 10 || host.major == 10 && host.build >= 19041)
            ? Route::ModernArm64 : Route::Unsupported;
    if (host.machine != IMAGE_FILE_MACHINE_I386 && host.machine != IMAGE_FILE_MACHINE_AMD64) return Route::Unsupported;
    const bool win7 = host.major == 6 && host.minor == 1;
    if (host.major < 6 || host.major == 6 && host.minor < 1 || win7 && host.servicePack < 1) return Route::Unsupported;
    // Windows 8 RTM cannot install Framework 4.6.2. Do not send it to an incompatible installer.
    if (host.major == 6 && host.minor == 2) return Route::Unsupported;
    if (!forceLegacy && host.machine == IMAGE_FILE_MACHINE_AMD64 && (host.major > 10 || host.major == 10 && host.build >= 19041))
        return Route::ModernX64;
    return host.framework >= 394802 ? Route::Legacy : Route::MissingFramework;
}
bool IsModernRoute(Route route) { return route == Route::ModernX64 || route == Route::ModernArm64; }
bool CanOfferLegacy(Route detected, bool forceLegacy, bool childRunning) { return detected == Route::ModernX64 && !forceLegacy && !childRunning; }
bool IsApplicationWindowIdentity(const std::wstring& className, const std::wstring& title) {
    return title == L"Process Keeper" && (className == L"WinUIDesktopWin32WindowClass" || className.rfind(L"HwndWrapper[", 0) == 0 && className.back() == L']');
}

bool IsAdministrator() {
    SID_IDENTIFIER_AUTHORITY authority = SECURITY_NT_AUTHORITY;
    PSID administrators = nullptr; BOOL member = FALSE;
    if (!AllocateAndInitializeSid(&authority, 2, SECURITY_BUILTIN_DOMAIN_RID, DOMAIN_ALIAS_RID_ADMINS,
        0, 0, 0, 0, 0, 0, &administrators)) return false;
    if (!CheckTokenMembership(nullptr, administrators, &member)) member = FALSE;
    FreeSid(administrators); return member != FALSE;
}

std::wstring FrameworkUrl(const Host& host) {
    if (host.major == 10 && host.build < 14393)
        return L"https://dotnet.microsoft.com/download/dotnet-framework/net462";
    return L"https://dotnet.microsoft.com/download/dotnet-framework/net48";
}

std::wstring ErrorText(DWORD error) {
    wchar_t* buffer = nullptr;
    auto length = FormatMessageW(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
        nullptr, error, 0, reinterpret_cast<wchar_t*>(&buffer), 0, nullptr);
    std::wstring text = length && buffer ? std::wstring(buffer, length) : L"Windows error";
    if (buffer) LocalFree(buffer);
    return text + L" | " + std::to_wstring(error);
}

void WriteDiagnostic(const std::wstring& value) {
    auto output = GetStdHandle(STD_OUTPUT_HANDLE);
    if (output == nullptr || output == INVALID_HANDLE_VALUE) return;
    auto bytes = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (bytes <= 0) return;
    std::string utf8(static_cast<size_t>(bytes), '\0');
    WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), &utf8[0], bytes, nullptr, nullptr);
    DWORD written; WriteFile(output, utf8.data(), static_cast<DWORD>(utf8.size()), &written, nullptr);
}
}
