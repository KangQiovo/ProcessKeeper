#include "InstalledRegistration.h"
namespace pk { namespace {
const wchar_t* TargetKey(PackageTarget target) {
    if (target == PackageTarget::Windows7Compat) return L"Windows7Compat";
    if (target == PackageTarget::Windows10x64) return L"Windows10x64";
    if (target == PackageTarget::Windows10arm64) return L"Windows10arm64"; return L"";
}
std::wstring Value(HKEY key, const wchar_t* name) {
    DWORD kind = 0, bytes = 0;
    if (RegQueryValueExW(key, name, nullptr, &kind, nullptr, &bytes) != ERROR_SUCCESS || kind != REG_SZ || bytes < sizeof(wchar_t) || bytes > 65536 || bytes % sizeof(wchar_t)) return L"";
    std::vector<wchar_t> text(bytes / sizeof(wchar_t));
    if (RegQueryValueExW(key, name, nullptr, &kind, reinterpret_cast<BYTE*>(text.data()), &bytes) != ERROR_SUCCESS || text.back() != 0) return L"";
    std::wstring result(text.data()); if ((result.size() + 1) * sizeof(wchar_t) != bytes) return L""; return result;
}
std::wstring Marker(const std::wstring& path, const wchar_t* key) {
    wchar_t value[512]{}; const auto count = GetPrivateProfileStringW(L"Installation", key, L"", value, 512, path.c_str());
    return count >= 511 ? L"" : std::wstring(value, count);
}
}
bool MatchesInstallation(const InstallationRecord& record, const std::wstring& original, PackageTarget target) {
    const auto full = FullPath(original); const auto separator = full.find_last_of(L'\\');
    if (separator == std::wstring::npos || CompareStringOrdinal(full.substr(separator + 1).c_str(), -1, L"ProcessKeeper.exe", -1, TRUE) != CSTR_EQUAL || target == PackageTarget::Universal) return false;
    const auto directory = full.substr(0, separator); const auto wantedTarget = std::wstring(TargetKey(target));
    auto equal = [&](const wchar_t* name, const std::wstring& wanted, bool path = false) {
        const auto found = record.text.find(name); return found != record.text.end() && (path ? CompareStringOrdinal(found->second.c_str(), -1, wanted.c_str(), -1, TRUE) == CSTR_EQUAL : found->second == wanted);
    };
    return record.contract == 1 && record.markerContract == L"1" && record.markerRepository == L"KangQiovo/ProcessKeeper" && record.markerTarget == wantedTarget &&
        equal(L"DisplayName", L"Process Keeper") && equal(L"Publisher", L"KangQi") && equal(L"ProcessKeeperRepository", L"KangQiovo/ProcessKeeper") &&
        equal(L"ProcessKeeperPackageTarget", wantedTarget) && equal(L"InstallLocation", directory, true) && equal(L"UninstallString", L"\"" + directory + L"\\Uninstall.exe\"", true) &&
        equal(L"DisplayIcon", L"\"" + full + L"\",0", true) && equal(L"URLInfoAbout", L"https://github.com/KangQiovo/ProcessKeeper");
}
std::wstring RefreshInstalledRegistration(const std::wstring& original, const std::wstring& sha256, const std::wstring& version, PackageTarget target) {
    HKEY key = nullptr;
    const auto opened = RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\ProcessKeeper", 0, KEY_QUERY_VALUE | KEY_SET_VALUE | KEY_WOW64_32KEY, &key);
    if (opened == ERROR_FILE_NOT_FOUND) return L"absent"; if (opened != ERROR_SUCCESS) return L"conflict";
    try {
        InstallationRecord record;
        for (const auto* name : {L"DisplayName", L"Publisher", L"ProcessKeeperRepository", L"ProcessKeeperPackageTarget", L"InstallLocation", L"UninstallString", L"DisplayIcon", L"URLInfoAbout"}) record.text.emplace(name, Value(key, name));
        DWORD type = 0, size = sizeof(record.contract);
        if (RegQueryValueExW(key, L"ProcessKeeperInstallerContract", nullptr, &type, reinterpret_cast<BYTE*>(&record.contract), &size) != ERROR_SUCCESS || type != REG_DWORD || size != sizeof(record.contract)) record.contract = 0;
        const auto full = FullPath(original), directory = full.substr(0, full.find_last_of(L'\\')), marker = directory + L"\\install.ini";
        auto parents = LockParents(full, false);
        Handle image(CreateFileW(full.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
        Handle uninstaller(CreateFileW((directory + L"\\Uninstall.exe").c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
        Handle ini(CreateFileW(marker.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
        if (!image.valid() || !uninstaller.valid() || !ini.valid()) { RegCloseKey(key); return L"conflict"; }
        VerifyHandlePath(image.get(), full, false); VerifyHandlePath(uninstaller.get(), directory + L"\\Uninstall.exe", false); VerifyHandlePath(ini.get(), marker, false);
        if (Hex(HashFile(image.get())) != sha256) throw Failure(L"Installed application changed before registration refresh.");
        record.markerRepository = Marker(marker, L"Repository"); record.markerTarget = Marker(marker, L"PackageTarget"); record.markerContract = Marker(marker, L"Contract");
        if (!MatchesInstallation(record, full, target)) { RegCloseKey(key); return L"conflict"; }
        const auto result = RegSetValueExW(key, L"DisplayVersion", 0, REG_SZ, reinterpret_cast<const BYTE*>(version.c_str()), static_cast<DWORD>((version.size() + 1) * sizeof(wchar_t)));
        RegCloseKey(key); return result == ERROR_SUCCESS ? L"updated" : L"conflict";
    } catch (...) { RegCloseKey(key); throw; }
}
}
