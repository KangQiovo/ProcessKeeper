#include "CacheCleanup.h"
#include <algorithm>
#include <fstream>
#include <set>
#include <tlhelp32.h>

namespace pk { namespace {
std::wstring RouteKey(Route route) { return route == Route::ModernArm64 ? L"arm64" : PayloadDirectory(route); }
std::vector<std::wstring> Receipt(const Manifest& manifest, Route route) {
    std::vector<std::wstring> lines{L"PKCACHE1", manifest.identity, RouteKey(route)};
    size_t size = 80;
    for (const auto& pair : manifest.files) if (IsPayloadFile(pair.first, route)) {
        const auto line = pair.first + L"\t" + std::to_wstring(pair.second.length) + L"\t" + pair.second.hash;
        size += line.size() + 1; if (size > 290000) throw Failure(L"Old cache manifest exceeds the bounded cleanup receipt.");
        lines.push_back(line);
    }
    if (lines.size() <= 3) throw Failure(L"Old cache has no owned application files."); return lines;
}
Route ReceiptRoute(const std::wstring& key) {
    if (key == L"modern") return Route::ModernX64; if (key == L"legacy") return Route::Legacy;
    if (key == L"arm64") return Route::ModernArm64; throw Failure(L"Unknown cache cleanup route.");
}
Manifest ReadReceipt(const std::vector<std::wstring>& lines, Route& route) {
    if (lines.size() < 5 || lines[0] != L"PKCACHE1" || !ValidSha256(lines[1])) throw Failure(L"Invalid cache cleanup receipt.");
    route = ReceiptRoute(lines[2]); const auto selected = PayloadDirectory(route);
    Manifest result; result.identity = lines[1];
    for (size_t index = 3; index < lines.size(); ++index) {
        const auto first = lines[index].find(L'\t'), last = lines[index].find_last_of(L'\t');
        if (first == std::wstring::npos || first == last) throw Failure(L"Invalid owned cache file.");
        const auto path = lines[index].substr(0, first), number = lines[index].substr(first + 1, last - first - 1), hash = lines[index].substr(last + 1);
        ValidateRelativePath(path); if (!IsPayloadFile(path, route) || !ValidSha256(hash) || number.empty() || number.size() > 10) throw Failure(L"Invalid owned cache file.");
        ULONGLONG length = 0; for (auto digit : number) { if (digit < L'0' || digit > L'9') throw Failure(L"Invalid cache file length."); length = length * 10 + digit - L'0'; }
        if (length > 1024ull * 1024 * 1024 || !result.files.emplace(path, PayloadFile{path, hash, length}).second) throw Failure(L"Duplicate or oversized owned cache file.");
    }
    if (!result.files.count(selected + L"/ProcessKeeper.exe") || !result.files.count(selected + L"/ProcessKeeper.Updater.exe")) throw Failure(L"Incomplete owned cache identity.");
    return result;
}
std::wstring FilePath(const std::wstring& root, std::wstring relative) { if (relative.empty()) return root; std::replace(relative.begin(), relative.end(), L'/', L'\\'); return root + L"\\" + relative; }
Handle OpenOwned(const std::wstring& path, bool directory, bool fixture) {
    Handle file(CreateFileW(path.c_str(), GENERIC_READ | READ_CONTROL | DELETE, directory ? FILE_SHARE_READ | FILE_SHARE_WRITE : 0, nullptr, OPEN_EXISTING,
        FILE_FLAG_OPEN_REPARSE_POINT | (directory ? FILE_FLAG_BACKUP_SEMANTICS : 0), nullptr));
    if (!file.valid()) Fail(L"Old cache remains in use or cannot be verified.");
    VerifyHandlePath(file.get(), path, directory); if (!fixture) VerifySecurity(file.get(), directory, true); return file;
}
void Collect(const Manifest& manifest, const std::wstring& root, const std::wstring& relative,
    std::vector<Handle>& files, std::vector<Handle>& directories, std::set<std::wstring, OrdinalIgnoreCase>& seen, bool fixture) {
    auto directory = OpenOwned(FilePath(root, relative), true, fixture);
    WIN32_FIND_DATAW item{}; auto search = FindFirstFileW((FilePath(root, relative) + L"\\*").c_str(), &item);
    if (search == INVALID_HANDLE_VALUE) { if (GetLastError() == ERROR_FILE_NOT_FOUND) { directories.push_back(std::move(directory)); return; } Fail(L"Cannot inspect old cache."); }
    try {
        do {
            if (wcscmp(item.cFileName, L".") == 0 || wcscmp(item.cFileName, L"..") == 0) continue;
            if (item.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) throw Failure(L"Old cache contains an unowned link.");
            const auto name = relative.empty() ? std::wstring(item.cFileName) : relative + L"/" + item.cFileName;
            ValidateRelativePath(name);
            if (item.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
                const auto prefix = name + L"/";
                if (std::count(name.begin(), name.end(), L'/') > 32 || std::none_of(manifest.files.begin(), manifest.files.end(), [&](const auto& pair) { return pair.first.rfind(prefix, 0) == 0; })) throw Failure(L"Old cache contains an unowned directory.");
                Collect(manifest, root, name, files, directories, seen, fixture);
            } else {
                const auto expected = manifest.files.find(name);
                if (expected == manifest.files.end() || !seen.insert(name).second) throw Failure(L"Old cache contains an unowned file.");
                auto file = OpenOwned(FilePath(root, name), false, fixture); LARGE_INTEGER length{};
                if (!GetFileSizeEx(file.get(), &length) || static_cast<ULONGLONG>(length.QuadPart) != expected->second.length || Hex(HashFile(file.get())) != expected->second.hash) throw Failure(L"Old cache ownership digest changed.");
                files.push_back(std::move(file));
            }
        } while (FindNextFileW(search, &item));
        if (GetLastError() != ERROR_NO_MORE_FILES) Fail(L"Cannot completely inspect old cache.");
    } catch (...) { FindClose(search); throw; }
    FindClose(search); directories.push_back(std::move(directory));
}
bool HasRunningImage(const std::wstring& root) {
    Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)); if (!snapshot.valid()) return true;
    PROCESSENTRY32W entry{sizeof(entry)}; const auto prefix = root + L"\\";
    if (!Process32FirstW(snapshot.get(), &entry)) return true;
    do {
        Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, entry.th32ProcessID)); if (!process.valid()) continue;
        wchar_t image[32768]{}; DWORD length = 32768;
        if (QueryFullProcessImageNameW(process.get(), 0, image, &length) && length >= prefix.size() &&
            CompareStringOrdinal(image, static_cast<int>(prefix.size()), prefix.c_str(), static_cast<int>(prefix.size()), TRUE) == CSTR_EQUAL) return true;
    } while (Process32NextW(snapshot.get(), &entry));
    return false;
}
void DeleteOwned(HANDLE file) { FILE_DISPOSITION_INFO disposal{TRUE}; if (!SetFileInformationByHandle(file, FileDispositionInfo, &disposal, sizeof(disposal))) Fail(L"Old cache is still in use; its remaining files were retained."); }
void Schedule(const std::wstring& cache, const std::vector<std::wstring>& lines, bool fixture) {
    Route route{}; const auto manifest = ReadReceipt(lines, route);
    const auto path = cache + L"\\cleanup-" + manifest.identity + L"-" + RouteKey(route) + L".txt";
    if (GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES) return;
    if (!fixture) WriteProtectedLines(path, lines);
    else { std::ofstream file(path, std::ios::binary); for (const auto& line : lines) { for (auto c : line) file.put(static_cast<char>(c)); file.put('\n'); } if (!file.good()) throw Failure(L"Cannot write cache cleanup fixture."); }
}
PayloadCleanupResult Run(const std::wstring& cache, const std::wstring& activeIdentity, bool fixture, unsigned maximum = 2) {
    PayloadCleanupResult result;
    auto parents = LockParents(cache + L"\\cleanup-scope", !fixture);
    WIN32_FIND_DATAW item{}; auto search = FindFirstFileW((cache + L"\\cleanup-*.txt").c_str(), &item); if (search == INVALID_HANDLE_VALUE) return result;
    unsigned examined = 0;
    do {
        ++result.retained;
        if (++examined > maximum) continue;
        try {
            if (item.dwFileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY)) continue;
            const auto receiptPath = cache + L"\\" + item.cFileName;
            auto receipt = OpenOwned(receiptPath, false, fixture); LARGE_INTEGER size{};
            if (!GetFileSizeEx(receipt.get(), &size) || size.QuadPart <= 0 || size.QuadPart > 300000) continue;
            std::string text(static_cast<size_t>(size.QuadPart), '\0'); DWORD read = 0;
            if (!ReadFile(receipt.get(), &text[0], static_cast<DWORD>(text.size()), &read, nullptr) || read != text.size()) continue;
            std::vector<std::wstring> lines; std::wstring line;
            for (unsigned char c : text) { if (c == '\n') { lines.push_back(line); line.clear(); } else if (c == 0 || c > 127 || c == '\r') throw Failure(L"Invalid cache cleanup encoding."); else line += c; }
            if (!line.empty()) lines.push_back(line);
            Route route{}; const auto manifest = ReadReceipt(lines, route);
            if (std::wstring(item.cFileName) != L"cleanup-" + manifest.identity + L"-" + RouteKey(route) + L".txt" || manifest.identity == activeIdentity) continue;
            const auto root = cache + L"\\" + manifest.identity + L"-" + RouteKey(route);
            Handle mutex(CreateMutexW(nullptr, FALSE, (L"Local\\ProcessKeeper.Universal." + UserSid() + L"." + manifest.identity).c_str()));
            if (!mutex.valid()) continue; const auto state = WaitForSingleObject(mutex.get(), 0); if (state != WAIT_OBJECT_0 && state != WAIT_ABANDONED) continue;
            try {
                if (GetFileAttributesW(root.c_str()) == INVALID_FILE_ATTRIBUTES && GetLastError() == ERROR_FILE_NOT_FOUND) { DeleteOwned(receipt.get()); --result.retained; ReleaseMutex(mutex.get()); continue; }
                if (HasRunningImage(root)) { ReleaseMutex(mutex.get()); continue; }
                std::vector<Handle> files, directories; std::set<std::wstring, OrdinalIgnoreCase> seen;
                Collect(manifest, root, L"", files, directories, seen, fixture);
                if (HasRunningImage(root)) throw Failure(L"Old cache remains in use.");
                // All paths and digests are verified and every leaf is locked before deletion begins.
                for (auto& file : files) { LARGE_INTEGER bytes{}; if (!GetFileSizeEx(file.get(), &bytes)) Fail(L"Cannot report old cache size."); DeleteOwned(file.get()); file.reset(); ++result.removedFiles; result.removedBytes += static_cast<ULONGLONG>(bytes.QuadPart); }
                for (auto& directory : directories) { DeleteOwned(directory.get()); directory.reset(); }
                DeleteOwned(receipt.get()); --result.retained; ReleaseMutex(mutex.get());
            } catch (...) { ReleaseMutex(mutex.get()); }
        } catch (...) { /* An unverified/busy cache is retained; it never prevents app startup. */ }
    } while (FindNextFileW(search, &item));
    FindClose(search); return result;
}
}
PayloadCleanupPlan CapturePayloadCleanup(const LaunchContext& context) {
    PayloadCleanupPlan result;
    auto parents = LockParents(context.original, false); Handle original(CreateFileW(context.original.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    if (!original.valid()) Fail(L"Cannot verify the old payload ownership."); VerifyHandlePath(original.get(), context.original, false);
    if (Hex(HashFile(original.get())) != context.originalHash) throw Failure(L"Old wrapper changed before cache capture.");
    auto module = LoadLibraryExW(context.original.c_str(), nullptr, LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE); if (!module) Fail(L"Cannot inspect the old cache manifest.");
    try {
        const auto resource = FindResourceW(module, MAKEINTRESOURCEW(101), RT_RCDATA); const auto size = resource ? SizeofResource(module, resource) : 0;
        const auto bytes = resource ? LockResource(LoadResource(module, resource)) : nullptr;
        if (!bytes || !size || size > 16 * 1024 * 1024) throw Failure(L"Old wrapper has no bounded cache manifest.");
        const auto manifest = ParseManifest(std::string(static_cast<const char*>(bytes), size)); const auto cache = ProtectedCacheRoot();
        for (auto route : {Route::ModernX64, Route::Legacy, Route::ModernArm64}) {
            const auto root = cache + L"\\" + manifest.identity + L"-" + RouteKey(route);
            if (CompareStringOrdinal(context.payload.c_str(), -1, FilePath(root, PayloadDirectory(route) + L"/ProcessKeeper.exe").c_str(), -1, TRUE) == CSTR_EQUAL) { result.receipt = Receipt(manifest, route); break; }
        }
        if (result.receipt.empty()) throw Failure(L"The old payload is outside its exact owned cache."); FreeLibrary(module); return result;
    } catch (...) { FreeLibrary(module); throw; }
}
void SchedulePayloadCleanup(const PayloadCleanupPlan& plan) { if (!plan.receipt.empty()) Schedule(ProtectedCacheRoot(), plan.receipt, false); }
PayloadCleanupResult RunPendingPayloadCleanup(const std::wstring& activeIdentity) { try { return Run(ProtectedCacheRoot(), activeIdentity, false); } catch (...) { return {}; } }
PayloadCleanupResult ClearPendingPayloadCache(const LaunchContext& context, const std::wstring& request) {
    if (!ValidContextId(request)) throw Failure(L"Invalid cache cleanup request."); auto caller = OpenContextProcess(context);
    const auto root = ProtectedCacheRoot();
    // A valid current payload path contains exactly its manifest hash and known route suffix.
    if (context.payload.rfind(root + L"\\", 0) != 0) throw Failure(L"Unknown active payload cache.");
    const auto relative = context.payload.substr(root.size() + 1); const auto separator = relative.find(L'\\');
    if (separator == std::wstring::npos || separator < 65 || !ValidSha256(relative.substr(0, 64))) throw Failure(L"Unknown active payload cache.");
    const auto result = Run(root, relative.substr(0, 64), false, 8);
    WriteProtectedLines(context.directory + L"\\cache-" + request + L".txt", {L"PKCACHESTATUS1", L"completed", std::to_wstring(result.removedFiles), std::to_wstring(result.removedBytes), std::to_wstring(result.retained)});
    return result;
}
#ifdef PK_FIXTURE_BUILD
void SchedulePayloadCleanupFixture(const std::wstring& cache, const Manifest& manifest, Route route) { Schedule(FullPath(cache), Receipt(manifest, route), true); }
void RunPendingPayloadCleanupFixture(const std::wstring& cache, const std::wstring& activeIdentity) { Run(FullPath(cache), activeIdentity, true); }
#endif
}
