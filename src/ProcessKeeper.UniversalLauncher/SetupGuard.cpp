#include "SetupGuard.h"
#include "LaunchContext.h"
#include <shlobj.h>
#include <sddl.h>
#include <set>

namespace pk { namespace {
struct FindHandle {
    HANDLE value; explicit FindHandle(HANDLE handle) : value(handle) {} ~FindHandle() { if (value != INVALID_HANDLE_VALUE) FindClose(value); }
    FindHandle(const FindHandle&) = delete; FindHandle& operator=(const FindHandle&) = delete;
};
bool Hash(const std::wstring& value) { return value.size() == 64 && value.find_first_not_of(L"0123456789abcdefABCDEF") == std::wstring::npos; }
bool Id(const std::wstring& value) { return value.size() == 32 && value.find_first_not_of(L"0123456789abcdef") == std::wstring::npos; }
bool Sid(const std::wstring& value) { PSID sid = nullptr; const bool valid = ConvertStringSidToSidW(value.c_str(), &sid) && IsValidSid(sid); if (sid) LocalFree(sid); return valid; }
std::wstring Decode(const std::wstring& value) {
    if (value.size() > 131068 || value.size() % 4) throw Failure(L"Malformed setup session path.");
    std::wstring result; result.reserve(value.size() / 4);
    auto nibble = [](wchar_t c) -> unsigned { if (c >= L'0' && c <= L'9') return c - L'0'; if (c >= L'a' && c <= L'f') return c - L'a' + 10; throw Failure(L"Malformed setup session path."); };
    for (size_t index = 0; index < value.size(); index += 4) {
        const auto character = static_cast<wchar_t>((nibble(value[index]) * 16 + nibble(value[index + 1])) | ((nibble(value[index + 2]) * 16 + nibble(value[index + 3])) << 8));
        if (!character) throw Failure(L"Malformed setup session path."); result += character;
    }
    return result;
}
ULONGLONG Number(const std::wstring& text) { if (text.empty() || text.size() > 20 || text.find_first_not_of(L"0123456789") != std::wstring::npos) throw Failure(L"Malformed setup process identity."); return std::stoull(text); }
bool Equal(const std::wstring& a, const std::wstring& b) { return CompareStringOrdinal(a.c_str(), -1, b.c_str(), -1, TRUE) == CSTR_EQUAL; }
Handle Open(const std::wstring& path, bool directory, bool fixture) {
    Handle file(CreateFileW(path.c_str(), GENERIC_READ | READ_CONTROL, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
        FILE_FLAG_OPEN_REPARSE_POINT | (directory ? FILE_FLAG_BACKUP_SEMANTICS : 0), nullptr));
    if (!file.valid()) Fail(L"Setup cannot verify the existing application sessions.");
    VerifyHandlePath(file.get(), path, directory); if (!fixture) VerifySecurity(file.get(), directory, true); return file;
}
struct Record { std::vector<std::wstring> fields; ULONGLONG created = 0; };
Record Read(const std::wstring& path, bool fixture) {
    auto file = Open(path, false, fixture); LARGE_INTEGER size{};
    if (!GetFileSizeEx(file.get(), &size) || size.QuadPart <= 0 || size.QuadPart > 300000) throw Failure(L"Unbounded setup session record.");
    FILETIME created{}; if (!GetFileTime(file.get(), &created, nullptr, nullptr)) Fail(L"Cannot verify setup session time.");
    Record result; result.created = (static_cast<ULONGLONG>(created.dwHighDateTime) << 32) | created.dwLowDateTime;
    std::string bytes(static_cast<size_t>(size.QuadPart), '\0'); DWORD count = 0;
    if (!ReadFile(file.get(), &bytes[0], static_cast<DWORD>(bytes.size()), &count, nullptr) || count != bytes.size()) Fail(L"Cannot verify setup session record.");
    std::wstring line;
    for (unsigned char c : bytes) { if (c == '\n') { result.fields.push_back(line); line.clear(); } else if (c == 0 || c > 127) throw Failure(L"Malformed setup session encoding."); else if (c != '\r') line += c; }
    if (!line.empty()) result.fields.push_back(line); return result;
}
ULONGLONG Created(HANDLE process) { FILETIME start{}, end{}, kernel{}, user{}; if (!GetProcessTimes(process, &start, &end, &kernel, &user)) Fail(L"Cannot verify running app identity."); return (static_cast<ULONGLONG>(start.dwHighDateTime) << 32) | start.dwLowDateTime; }
int Live(ULONGLONG pid, ULONGLONG created, const std::wstring& image, ULONGLONG markerTime = 0) {
    if (pid <= 4 || pid > MAXDWORD) throw Failure(L"Invalid running app identity.");
    Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, static_cast<DWORD>(pid)));
    if (!process.valid()) return GetLastError() == ERROR_INVALID_PARAMETER ? 0 : 2;
    const auto state = WaitForSingleObject(process.get(), 0); if (state == WAIT_OBJECT_0) return 0; if (state != WAIT_TIMEOUT) return 2;
    const auto started = Created(process.get()); if (markerTime ? started < created || started > markerTime : started != created) return 0;
    wchar_t path[32768]{}; DWORD size = 32768;
    if (!QueryFullProcessImageNameW(process.get(), 0, path, &size)) return 2;
    return Equal(FullPath(std::wstring(path, size)), image) ? 1 : 0;
}
bool PayloadPath(const std::wstring& root, const std::wstring& path, bool helper) {
    if (path.size() <= root.size() + 65 || !Equal(path.substr(0, root.size() + 1), root + L"\\")) return false;
    const auto relative = path.substr(root.size() + 1);
    if (!Hash(relative.substr(0, 64))) return false;
    const auto remainder = relative.substr(64); const auto name = helper ? L"ProcessKeeper.Updater.exe" : L"ProcessKeeper.exe";
    return Equal(remainder, L"-modern\\modern\\" + std::wstring(name)) || Equal(remainder, L"-legacy\\legacy\\" + std::wstring(name)) || Equal(remainder, L"-arm64\\modern\\arm64\\" + std::wstring(name));
}
int Session(const std::wstring& root, const std::wstring& sid, const std::wstring& directory, const std::wstring& id, const std::wstring& original, bool fixture, unsigned& total) {
    if (++total > 2048) return 2;
    auto lock = Open(directory, true, fixture); const auto record = Read(directory + L"\\context.txt", fixture); const auto& fields = record.fields;
    if ((fields.size() < 12 || fields.size() > 14) || fields[0] != L"PKLC1" || fields[1] != id || Decode(fields[11]) != sid || !Hash(fields[3]) || !Hash(fields[6]) || !Hash(fields[8])) return 2;
    const auto recordedOriginal = FullPath(Decode(fields[2]));
    if (!Equal(recordedOriginal, original)) return 0;
    const auto image = FullPath(Decode(fields[5])), helper = FullPath(Decode(fields[7]));
    if (!PayloadPath(root, image, false) || !PayloadPath(root, helper, true) || !Equal(image.substr(0, image.find_last_of(L'\\')), helper.substr(0, helper.find_last_of(L'\\')))) return 2;
    const auto created = Number(fields[10]); const auto running = Live(Number(fields[9]), created, image); if (running != 0) return running;
    // Helpers may outlive the managed UI. Bind their PID/image/start time to a protected
    // ready marker created after helper startup; a later reused PID cannot match it.
    WIN32_FIND_DATAW job{}; const FindHandle search(FindFirstFileW((directory + L"\\job-*").c_str(), &job));
    if (search.value == INVALID_HANDLE_VALUE) return GetLastError() == ERROR_FILE_NOT_FOUND ? 0 : 2;
    unsigned jobs = 0; int result = 0;
    do {
        if (++jobs > 64) { result = 2; break; }
        const auto name = std::wstring(job.cFileName);
        if (!(job.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) || job.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT || name.size() != 36 || !Id(name.substr(4))) { result = 2; break; }
        const auto path = directory + L"\\" + name + L"\\ready.txt";
        if (GetFileAttributesW(path.c_str()) == INVALID_FILE_ATTRIBUTES) { if (GetLastError() == ERROR_FILE_NOT_FOUND || GetLastError() == ERROR_PATH_NOT_FOUND) continue; result = 2; break; }
        const auto ready = Read(path, fixture);
        if (ready.fields.size() != 2 || ready.fields[0] != L"PKREADY1") { result = 2; break; }
        result = Live(Number(ready.fields[1]), created, helper, ready.created); if (result != 0) break;
    } while (FindNextFileW(search.value, &job));
    if (result == 0 && GetLastError() != ERROR_NO_MORE_FILES) result = 2; return result;
}
int Check(const std::wstring& cache, const std::wstring& original, bool fixture) {
    if (GetFileAttributesW(cache.c_str()) == INVALID_FILE_ATTRIBUTES) return GetLastError() == ERROR_FILE_NOT_FOUND || GetLastError() == ERROR_PATH_NOT_FOUND ? 0 : 2;
    auto parents = LockParents(cache + L"\\guard", false); auto locked = Open(cache, true, fixture);
    WIN32_FIND_DATAW account{}; const FindHandle search(FindFirstFileW((cache + L"\\*").c_str(), &account)); if (search.value == INVALID_HANDLE_VALUE) return 2;
    unsigned accounts = 0, total = 0; int result = 0;
    do {
        if (wcscmp(account.cFileName, L".") == 0 || wcscmp(account.cFileName, L"..") == 0) continue;
        if (++accounts > 64 || !(account.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) || account.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT || !Sid(account.cFileName)) { result = 2; break; }
        const auto root = cache + L"\\" + account.cFileName, sessions = root + L"\\sessions";
        auto sidLock = Open(root, true, fixture);
        if (GetFileAttributesW(sessions.c_str()) == INVALID_FILE_ATTRIBUTES) { if (GetLastError() == ERROR_FILE_NOT_FOUND) continue; result = 2; break; }
        auto sessionLock = Open(sessions, true, fixture);
        WIN32_FIND_DATAW session{}; const FindHandle children(FindFirstFileW((sessions + L"\\*").c_str(), &session));
        if (children.value == INVALID_HANDLE_VALUE) { result = 2; break; }
        do {
                if (wcscmp(session.cFileName, L".") == 0 || wcscmp(session.cFileName, L"..") == 0) continue;
                if (!(session.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) || session.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT || !Id(session.cFileName)) { result = 2; break; }
                result = Session(root, account.cFileName, sessions + L"\\" + session.cFileName, session.cFileName, original, fixture, total); if (result != 0) break;
        } while (FindNextFileW(children.value, &session));
        if (result == 0 && GetLastError() != ERROR_NO_MORE_FILES) result = 2;
        if (result != 0) break;
    } while (FindNextFileW(search.value, &account));
    if (result == 0 && GetLastError() != ERROR_NO_MORE_FILES) result = 2; return result;
}
}
int CheckInstalledSessions(const std::wstring& original) {
    try { wchar_t data[MAX_PATH]{}; if (FAILED(SHGetFolderPathW(nullptr, CSIDL_COMMON_APPDATA, nullptr, SHGFP_TYPE_CURRENT, data))) return 2; return Check(std::wstring(data) + L"\\ProcessKeeper\\Universal", FullPath(original), false); } catch (...) { return 2; }
}
int CheckPortableFile(const std::wstring& path, const std::wstring& expectedHash) {
    try {
        if (!Hash(expectedHash)) return 2; const auto full = FullPath(path); auto parents = LockParents(full, false); auto file = Open(full, false, true);
        BY_HANDLE_FILE_INFORMATION info{}; if (!GetFileInformationByHandle(file.get(), &info) || info.nNumberOfLinks != 1 || !Equal(Hex(HashFile(file.get())), expectedHash)) return 2;
        LARGE_INTEGER zero{}; if (!SetFilePointerEx(file.get(), zero, nullptr, FILE_BEGIN)) return 2;
        IMAGE_DOS_HEADER dos{}; DWORD bytes = 0;
        if (!ReadFile(file.get(), &dos, sizeof(dos), &bytes, nullptr) || bytes != sizeof(dos) || dos.e_magic != IMAGE_DOS_SIGNATURE || dos.e_lfanew < sizeof(dos) || dos.e_lfanew > 1024 * 1024) return 2;
        LARGE_INTEGER offset{}; offset.QuadPart = dos.e_lfanew; DWORD signature = 0; IMAGE_FILE_HEADER header{};
        if (!SetFilePointerEx(file.get(), offset, nullptr, FILE_BEGIN) || !ReadFile(file.get(), &signature, sizeof(signature), &bytes, nullptr) || bytes != sizeof(signature) || signature != IMAGE_NT_SIGNATURE ||
            !ReadFile(file.get(), &header, sizeof(header), &bytes, nullptr) || bytes != sizeof(header) || header.Machine != IMAGE_FILE_MACHINE_I386 || header.Characteristics & IMAGE_FILE_DLL) return 2;
        return 0;
    } catch (...) { return 2; }
}
int GetInstalledPackageTarget(const std::wstring& path) {
    try {
        const auto full = FullPath(path); auto parents = LockParents(full, false); auto file = Open(full, false, true);
        BY_HANDLE_FILE_INFORMATION info{}; if (!GetFileInformationByHandle(file.get(), &info) || info.nNumberOfLinks != 1) return -1;
        return static_cast<int>(ValidateInstalledBundle(full));
    } catch (...) { return -1; }
}
int CheckInstalledPackage(const std::wstring& path) { return GetInstalledPackageTarget(path) >= 0 ? 0 : 2; }
#ifdef PK_FIXTURE_BUILD
int CheckInstalledSessionFixture(const std::wstring& cache, const std::wstring& original) { try { return Check(FullPath(cache), FullPath(original), true); } catch (...) { return 2; } }
#endif
}
#ifdef PK_SETUP_GUARD_BUILD
extern "C" __declspec(dllexport) int __stdcall CheckInstalledSessionW(const wchar_t* original) { return original ? pk::CheckInstalledSessions(original) : 2; }
extern "C" __declspec(dllexport) int __stdcall CheckPortableFileW(const wchar_t* path, const wchar_t* expectedHash) { return path && expectedHash ? pk::CheckPortableFile(path, expectedHash) : 2; }
extern "C" __declspec(dllexport) int __stdcall CheckInstalledPackageW(const wchar_t* path) { return path ? pk::CheckInstalledPackage(path) : 2; }
extern "C" __declspec(dllexport) int __stdcall GetInstalledPackageTargetW(const wchar_t* path) { return path ? pk::GetInstalledPackageTarget(path) : -1; }
#endif
