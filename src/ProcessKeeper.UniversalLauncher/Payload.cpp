#include "Payload.h"
#include "CacheCleanup.h"
#include <fdi.h>
#include <set>
#include <algorithm>

namespace pk {
namespace {
constexpr size_t MaximumFiles = 50000;
std::wstring Wide(const std::string& text) {
    if (text.find('\0') != std::string::npos || text.size() > 16 * 1024 * 1024) throw Failure(L"Invalid embedded manifest text.");
    int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), nullptr, 0);
    if (count <= 0) throw Failure(L"Invalid embedded manifest encoding.");
    std::wstring result(static_cast<size_t>(count), L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), &result[0], count); return result;
}
std::vector<std::wstring> Split(const std::wstring& text, wchar_t separator) {
    std::vector<std::wstring> parts; size_t begin = 0;
    for (;;) { auto end = text.find(separator, begin); parts.push_back(text.substr(begin, end == std::wstring::npos ? end : end - begin)); if (end == std::wstring::npos) break; begin = end + 1; }
    return parts;
}
bool ValidHash(const std::wstring& hash) {
    return hash.size() == 64 && std::all_of(hash.begin(), hash.end(), [](wchar_t value) { return value >= L'0' && value <= L'9' || value >= L'a' && value <= L'f'; });
}
ULONGLONG ReadLength(const std::wstring& value) {
    if (value.empty() || value.size() > 10) throw Failure(L"Invalid embedded file length.");
    ULONGLONG result = 0;
    for (auto digit : value) { if (digit < L'0' || digit > L'9') throw Failure(L"Invalid embedded file length."); result = result * 10 + static_cast<ULONGLONG>(digit - L'0'); }
    if (result > 1024ull * 1024 * 1024) throw Failure(L"An embedded file exceeds the extraction limit.");
    return result;
}
std::wstring ToFilePath(const std::wstring& root, const std::wstring& relative) {
    ValidateRelativePath(relative); auto name = relative; std::replace(name.begin(), name.end(), L'/', L'\\');
    auto result = FullPath(root + L"\\" + name);
    if (result.size() >= 248) throw Failure(L"The protected application path is too long for this compatibility package.");
    return result;
}
void CreateParents(const std::wstring& root, const std::wstring& relative) {
    for (auto slash = relative.find(L'/'); slash != std::wstring::npos; slash = relative.find(L'/', slash + 1))
        EnsureProtectedDirectory(ToFilePath(root, relative.substr(0, slash)));
}
struct Resource { const BYTE* bytes = nullptr; DWORD length = 0; };
Resource ReadResource(int identifier) {
    auto resource = FindResourceW(nullptr, MAKEINTRESOURCEW(identifier), RT_RCDATA);
    if (!resource) throw Failure(L"The embedded application is missing. Download the complete ProcessKeeper.exe again.");
    auto memory = LoadResource(nullptr, resource); auto bytes = static_cast<const BYTE*>(LockResource(memory)); auto length = SizeofResource(nullptr, resource);
    if (!bytes || !length) throw Failure(L"The embedded application is incomplete.");
    return { bytes, length };
}
struct Output { Handle handle; const PayloadFile* expected = nullptr; ULONGLONG written = 0; std::wstring path; };
struct CabinetContext {
    Resource archive; const Manifest* manifest = nullptr; std::wstring root, selected;
    Route route = Route::Unsupported;
    std::map<INT_PTR, LONG> inputs; INT_PTR nextInput = 0x40000000;
    std::map<INT_PTR, Output> outputs;
    std::set<std::wstring, OrdinalIgnoreCase> seen;
    std::wstring failure;
    const std::atomic_bool* canceled = nullptr;
#ifdef PK_FIXTURE_BUILD
    bool fixture = false;
#endif
};
CabinetContext* current = nullptr;
void CheckCancellation(const std::atomic_bool* canceled) { if (canceled && canceled->load()) throw Failure(L"Startup canceled."); }
Handle OutputFile(const std::wstring& path, bool create) {
#ifdef PK_FIXTURE_BUILD
    if (current && current->fixture) {
        Handle file(CreateFileW(path.c_str(), create ? GENERIC_WRITE : GENERIC_READ, create ? 0 : FILE_SHARE_READ, nullptr,
            create ? CREATE_NEW : OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
        if (!file.valid()) Fail(L"Fixture file operation failed.");
        VerifyHandlePath(file.get(), path, false); return file;
    }
#endif
    return create ? CreateProtectedFile(path) : OpenProtectedFile(path);
}
void OutputParents(const std::wstring& root, const std::wstring& relative) {
#ifdef PK_FIXTURE_BUILD
    if (current && current->fixture) {
        for (auto slash = relative.find(L'/'); slash != std::wstring::npos; slash = relative.find(L'/', slash + 1)) {
            auto path = ToFilePath(root, relative.substr(0, slash));
            if (!CreateDirectoryW(path.c_str(), nullptr) && GetLastError() != ERROR_ALREADY_EXISTS) Fail(L"Fixture directory creation failed.");
        }
        return;
    }
#endif
    CreateParents(root, relative);
}
void* DIAMONDAPI Allocate(ULONG bytes) { return HeapAlloc(GetProcessHeap(), 0, bytes); }
void DIAMONDAPI Release(void* value) { if (value) HeapFree(GetProcessHeap(), 0, value); }
INT_PTR DIAMONDAPI OpenCab(char* name, int, int) {
    if (!current) return -1;
    if (strcmp(name, "embedded.cab") != 0) { current->failure = L"An unexpected cabinet source was requested."; return -1; }
    const auto key = current->nextInput++;
    current->inputs.emplace(key, 0); return key;
}
UINT DIAMONDAPI ReadCab(INT_PTR file, void* buffer, UINT count) {
    if (!current) return static_cast<UINT>(-1);
    auto found = current->inputs.find(file);
    if (found == current->inputs.end() || found->second < 0 || static_cast<DWORD>(found->second) > current->archive.length) return static_cast<UINT>(-1);
    auto available = current->archive.length - static_cast<DWORD>(found->second); auto bytes = (std::min)(count, static_cast<UINT>(available));
    memcpy(buffer, current->archive.bytes + found->second, bytes); found->second += static_cast<LONG>(bytes); return bytes;
}
LONG DIAMONDAPI SeekCab(INT_PTR file, LONG distance, int origin) {
    if (!current) return -1;
    auto found = current->inputs.find(file); if (found == current->inputs.end()) return -1;
    LONGLONG position = distance;
    if (origin == 1) position += found->second;
    else if (origin == 2) position += current->archive.length;
    else if (origin != 0) return -1;
    if (position < 0 || position > current->archive.length || position > LONG_MAX) return -1;
    found->second = static_cast<LONG>(position); return found->second;
}
int DIAMONDAPI CloseCab(INT_PTR file) { return current && current->inputs.erase(file) ? 0 : -1; }
UINT DIAMONDAPI WriteOutput(INT_PTR file, void* data, UINT count) {
    try {
        if (!current) return static_cast<UINT>(-1);
        CheckCancellation(current->canceled);
        auto found = current->outputs.find(file); if (found == current->outputs.end()) return static_cast<UINT>(-1);
        auto& output = found->second;
        if (count > output.expected->length - output.written) throw Failure(L"An embedded file exceeds its declared length.");
        DWORD written = 0;
        if (!WriteFile(output.handle.get(), data, count, &written, nullptr) || written != count) Fail(L"Cannot extract an application file.");
        output.written += written; return written;
    } catch (const Failure& error) { current->failure = error.message; return static_cast<UINT>(-1); }
    catch (...) { if (current) current->failure = L"Application extraction failed."; return static_cast<UINT>(-1); }
}
INT_PTR DIAMONDAPI Notify(FDINOTIFICATIONTYPE kind, PFDINOTIFICATION notification) {
    try {
        if (!current) return -1;
        CheckCancellation(current->canceled);
        if (kind == fdintNEXT_CABINET || kind == fdintPARTIAL_FILE) throw Failure(L"External or split cabinets are not allowed.");
        if (kind == fdintCOPY_FILE) {
            auto name = Wide(notification->psz1); std::replace(name.begin(), name.end(), L'\\', L'/'); ValidateRelativePath(name);
            auto found = current->manifest->files.find(name);
            if (found == current->manifest->files.end() || !current->seen.insert(name).second || notification->cb < 0 ||
                static_cast<ULONGLONG>(notification->cb) != found->second.length) throw Failure(L"The cabinet does not match its embedded manifest.");
            if (!IsPayloadFile(name, current->route)) return 0;
            OutputParents(current->root, name);
            auto path = ToFilePath(current->root, name); auto file = OutputFile(path, true);
            auto key = reinterpret_cast<INT_PTR>(file.get());
            current->outputs.emplace(key, Output{ std::move(file), &found->second, 0, path }); return key;
        }
        if (kind == fdintCLOSE_FILE_INFO) {
            auto found = current->outputs.find(notification->hf);
            if (found == current->outputs.end() || found->second.written != found->second.expected->length) throw Failure(L"An extracted file is incomplete.");
            auto path = found->second.path; auto expected = found->second.expected;
            if (!FlushFileBuffers(found->second.handle.get())) Fail(L"Cannot save an application file.");
            found->second.handle.reset();
            auto verified = OutputFile(path, false);
            if (Hex(HashFile(verified.get())) != expected->hash) throw Failure(L"An extracted file failed SHA-256 verification.");
            current->outputs.erase(found); return TRUE;
        }
        return 0;
    } catch (const Failure& error) { current->failure = error.message; return -1; }
    catch (...) { if (current) current->failure = L"The application cabinet is invalid."; return -1; }
}
void Extract(Resource archive, const Manifest& manifest, const std::wstring& root, Route route, const std::atomic_bool* canceled = nullptr
#ifdef PK_FIXTURE_BUILD
    , bool fixture = false
#endif
) {
    const auto selected = PayloadDirectory(route);
    if (!manifest.files.count(selected + L"/ProcessKeeper.exe")) throw Failure(L"The package does not contain the selected processor architecture.");
    if (archive.length > LONG_MAX) throw Failure(L"The embedded cabinet is too large.");
    CabinetContext context; context.archive = archive; context.manifest = &manifest; context.root = root; context.selected = selected;
    context.route = route;
    context.canceled = canceled;
#ifdef PK_FIXTURE_BUILD
    context.fixture = fixture;
#endif
    static HMODULE library = LoadSystemLibrary(L"cabinet.dll");
    if (!library) throw Failure(L"Windows cabinet support is unavailable.");
    auto create = reinterpret_cast<decltype(&FDICreate)>(GetProcAddress(library, "FDICreate"));
    auto copy = reinterpret_cast<decltype(&FDICopy)>(GetProcAddress(library, "FDICopy"));
    auto destroy = reinterpret_cast<decltype(&FDIDestroy)>(GetProcAddress(library, "FDIDestroy"));
    if (!create || !copy || !destroy) throw Failure(L"Windows cabinet support is incomplete.");
    current = &context;
    ERF error{}; auto cabinet = create(Allocate, Release, OpenCab, ReadCab, WriteOutput, CloseCab, SeekCab, cpu80386, &error);
    if (!cabinet) { current = nullptr; throw Failure(L"Windows could not initialize cabinet extraction."); }
    char name[] = "embedded.cab", path[] = "";
    const bool success = copy(cabinet, name, path, 0, Notify, nullptr, nullptr) != FALSE;
    destroy(cabinet); current = nullptr;
    if (!success || !context.failure.empty()) throw Failure(context.failure.empty() ? L"The embedded cabinet could not be extracted. FDI " + std::to_wstring(error.erfOper) + L" | " + std::to_wstring(error.erfType) : context.failure);
    if (!context.outputs.empty() || context.seen.size() != manifest.files.size()) throw Failure(L"The embedded cabinet is incomplete.");
}
void EnumerateVerified(const Manifest& manifest, const std::wstring& root, const std::wstring& relative, std::set<std::wstring, OrdinalIgnoreCase>& seen, std::vector<Handle>& files, Route route, const std::atomic_bool* canceled = nullptr) {
    CheckCancellation(canceled);
    const auto directoryPath = ToFilePath(root, relative);
    Handle directory(CreateFileW(directoryPath.c_str(), GENERIC_READ | READ_CONTROL, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    if (!directory.valid()) Fail(L"Cannot open the application cache.");
    VerifyHandlePath(directory.get(), directoryPath, true); VerifySecurity(directory.get(), true, true);
    WIN32_FIND_DATAW item{}; auto search = FindFirstFileW((directoryPath + L"\\*").c_str(), &item);
    if (search == INVALID_HANDLE_VALUE) Fail(L"Cannot read the application cache.");
    try {
        do {
            CheckCancellation(canceled);
            if (wcscmp(item.cFileName, L".") == 0 || wcscmp(item.cFileName, L"..") == 0) continue;
            if (item.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) throw Failure(L"The application cache contains a link.");
            const auto name = relative + L"/" + item.cFileName; ValidateRelativePath(name);
            if (item.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
                if (std::count(name.begin(), name.end(), L'/') > 32) throw Failure(L"The application cache is too deeply nested.");
                const auto prefix = name + L"/";
                if (std::none_of(manifest.files.begin(), manifest.files.end(), [&](const auto& pair) { return IsPayloadFile(pair.first, route) && pair.first.rfind(prefix, 0) == 0; })) throw Failure(L"The cache contains an unexpected directory.");
                EnumerateVerified(manifest, root, name, seen, files, route, canceled);
            } else {
                auto expected = manifest.files.find(name);
                if (expected == manifest.files.end() || !IsPayloadFile(name, route) || !seen.insert(name).second) throw Failure(L"The cache contains an unexpected file.");
                auto file = OpenProtectedFile(ToFilePath(root, name)); LARGE_INTEGER length{};
                if (!GetFileSizeEx(file.get(), &length) || static_cast<ULONGLONG>(length.QuadPart) != expected->second.length || Hex(HashFile(file.get())) != expected->second.hash)
                    throw Failure(L"The application cache failed integrity verification.");
                files.push_back(std::move(file));
            }
        } while (FindNextFileW(search, &item));
        if (GetLastError() != ERROR_NO_MORE_FILES) Fail(L"Cannot completely read the application cache.");
    } catch (...) { FindClose(search); throw; }
    FindClose(search);
}
}

std::wstring PayloadDirectory(Route route) {
    if (route == Route::ModernX64) return L"modern";
    if (route == Route::ModernArm64) return L"modern/arm64";
    if (route == Route::Legacy) return L"legacy";
    throw Failure(L"No application payload matches this platform.");
}
bool IsPayloadFile(const std::wstring& path, Route route) {
    auto starts = [&](const std::wstring& prefix) {
        return path.size() >= prefix.size() && CompareStringOrdinal(path.data(), static_cast<int>(prefix.size()), prefix.data(), static_cast<int>(prefix.size()), TRUE) == CSTR_EQUAL;
    };
    if (route == Route::ModernX64) return starts(L"modern/") && !starts(L"modern/arm64/");
    if (route == Route::ModernArm64) return starts(L"modern/arm64/");
    if (route == Route::Legacy) return starts(L"legacy/");
    return false;
}
void ValidateRelativePath(const std::wstring& path) {
    if (path.empty() || path.size() > 1024) throw Failure(L"Invalid embedded file path.");
    for (auto value : path) if (value < 32 || value > 126 || value == L'\\' || value == L':' || value == L'"' || value == L'<' || value == L'>' || value == L'|' || value == L'*' || value == L'?')
        throw Failure(L"Unsafe embedded file path.");
    for (const auto& part : Split(path, L'/')) {
        if (part.empty() || part == L"." || part == L".." || part.back() == L'.' || part.back() == L' ' || part.size() > 240) throw Failure(L"Unsafe embedded file path segment.");
        auto stem = part.substr(0, part.find(L'.')); std::transform(stem.begin(), stem.end(), stem.begin(), [](wchar_t c) { return c >= L'a' && c <= L'z' ? static_cast<wchar_t>(c - 32) : c; });
        if (stem == L"CON" || stem == L"NUL" || stem == L"PRN" || stem == L"AUX" || stem == L"CLOCK$" || stem.size() == 4 &&
            (stem.rfind(L"COM", 0) == 0 || stem.rfind(L"LPT", 0) == 0) && stem[3] >= L'1' && stem[3] <= L'9') throw Failure(L"Reserved embedded file name.");
    }
}
Manifest ParseManifest(const std::string& text) {
    auto lines = Split(Wide(text), L'\n');
    if (lines.size() < 3 || lines.size() > MaximumFiles + 2) throw Failure(L"Invalid embedded manifest size.");
    for (auto& line : lines) if (!line.empty() && line.back() == L'\r') line.pop_back();
    auto header = Split(lines.front(), L'\t');
    if (!(header.size() == 2 && header[0] == L"PK14" || header.size() == 3 && header[0] == L"PK17") || !ValidHash(header[1])) throw Failure(L"Invalid embedded manifest header.");
    Manifest result; result.archiveHash = header[1]; result.identity = Hex(HashBytes(text.data(), text.size())); ULONGLONG total = 0;
    if (header.size() == 3) {
        if (header[2] == L"Windows7Compat") result.target = PackageTarget::Windows7Compat;
        else if (header[2] == L"Windows10x64") result.target = PackageTarget::Windows10x64;
        else if (header[2] == L"Windows10arm64") result.target = PackageTarget::Windows10arm64;
        else throw Failure(L"Unknown single-platform package target.");
    }
    for (size_t i = 1; i < lines.size(); ++i) {
        if (i == lines.size() - 1 && lines[i].empty()) continue;
        auto fields = Split(lines[i], L'\t');
        if (fields.size() != 3 || !ValidHash(fields[2])) throw Failure(L"Invalid embedded manifest file record.");
        ValidateRelativePath(fields[0]);
        if (fields[0].rfind(L"modern/", 0) != 0 && fields[0].rfind(L"legacy/", 0) != 0) throw Failure(L"Unknown embedded application variant.");
        PayloadFile file{ fields[0], fields[2], ReadLength(fields[1]) }; total += file.length;
        if (total > 4ull * 1024 * 1024 * 1024 || !result.files.emplace(file.path, file).second) throw Failure(L"Oversized or duplicate embedded application file.");
    }
    if (result.target == PackageTarget::Universal) {
        if (!result.files.count(L"modern/ProcessKeeper.exe") || !result.files.count(L"legacy/ProcessKeeper.exe")) throw Failure(L"An embedded application entry point is missing.");
    } else {
        const auto route = result.target == PackageTarget::Windows7Compat ? Route::Legacy : result.target == PackageTarget::Windows10x64 ? Route::ModernX64 : Route::ModernArm64;
        const auto directory = PayloadDirectory(route);
        if (!result.files.count(directory + L"/ProcessKeeper.exe") || !result.files.count(directory + L"/ProcessKeeper.Updater.exe")) throw Failure(L"The declared package entry point or updater is missing.");
        if (result.target == PackageTarget::Windows10x64 && (!result.files.count(L"legacy/ProcessKeeper.exe") || !result.files.count(L"legacy/ProcessKeeper.Updater.exe"))) throw Failure(L"The Windows x86/x64 package requires its real x86 compatibility payload and updater.");
        for (const auto& pair : result.files) if (!IsPayloadFile(pair.first, route) && !(result.target == PackageTarget::Windows10x64 && IsPayloadFile(pair.first, Route::Legacy))) throw Failure(L"The declared package contains another platform's payload.");
    }
    if (std::any_of(result.files.begin(), result.files.end(), [](const auto& pair) { return IsPayloadFile(pair.first, Route::ModernArm64); }) &&
        !result.files.count(L"modern/arm64/ProcessKeeper.exe")) throw Failure(L"The ARM64 application entry point is missing.");
    for (const auto& pair : result.files) {
        for (auto slash = pair.first.find(L'/'); slash != std::wstring::npos; slash = pair.first.find(L'/', slash + 1))
            if (result.files.count(pair.first.substr(0, slash))) throw Failure(L"Embedded file/directory conflict.");
    }
    return result;
}
PackageTarget EmbeddedPackageTarget() {
    const auto manifest = ReadResource(101);
    if (manifest.length > 16 * 1024 * 1024) throw Failure(L"The embedded manifest is too large.");
    return ParseManifest(std::string(reinterpret_cast<const char*>(manifest.bytes), manifest.length)).target;
}
PreparedPayload PreparePayload(Route route, const std::atomic_bool* canceled) {
    CheckCancellation(canceled);
    const auto manifestResource = ReadResource(101), archive = ReadResource(102);
    if (manifestResource.length > 16 * 1024 * 1024) throw Failure(L"The embedded manifest is too large.");
    const auto manifest = ParseManifest(std::string(reinterpret_cast<const char*>(manifestResource.bytes), manifestResource.length));
    if (Hex(HashBytes(archive.bytes, archive.length)) != manifest.archiveHash) throw Failure(L"The embedded cabinet failed SHA-256 verification.");
    const auto selected = PayloadDirectory(route);
    if (!manifest.files.count(selected + L"/ProcessKeeper.exe")) throw Failure(L"The package does not contain the selected processor architecture.");
    const auto cache = ProtectedCacheRoot();
    RunPendingPayloadCleanup(manifest.identity);
    const auto root = cache + L"\\" + manifest.identity + L"-" + (route == Route::ModernArm64 ? L"arm64" : selected);
    EnsureProtectedDirectory(root);
    auto parents = LockParents(root + L"\\lock", true);
    const auto mutexName = L"Local\\ProcessKeeper.Universal." + UserSid() + L"." + manifest.identity;
    Handle mutex(CreateMutexW(nullptr, FALSE, mutexName.c_str()));
    if (!mutex.valid()) Fail(L"Cannot lock the application cache.");
    DWORD waited = WAIT_TIMEOUT;
    for (int attempt = 0; attempt < 90 && waited == WAIT_TIMEOUT; ++attempt) { CheckCancellation(canceled); waited = WaitForSingleObject(mutex.get(), 1000); }
    if (waited != WAIT_OBJECT_0 && waited != WAIT_ABANDONED) throw Failure(L"Another Process Keeper startup is still preparing this version.");
    try {
        if (GetFileAttributesW(ToFilePath(root, selected).c_str()) == INVALID_FILE_ATTRIBUTES) {
            if (GetLastError() != ERROR_FILE_NOT_FOUND && GetLastError() != ERROR_PATH_NOT_FOUND) Fail(L"Cannot inspect the protected application cache.");
            const auto staging = cache + L"\\" + manifest.identity.substr(0, 24) + L"-stage-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64());
            if (GetFileAttributesW(staging.c_str()) != INVALID_FILE_ATTRIBUTES) throw Failure(L"A staging directory already exists. Please retry startup.");
            EnsureProtectedDirectory(staging);
            {
                auto stagingParents = LockParents(staging + L"\\lock", true);
                Extract(archive, manifest, staging, route, canceled);
                std::set<std::wstring, OrdinalIgnoreCase> staged; std::vector<Handle> stagedFiles;
                EnumerateVerified(manifest, staging, selected, staged, stagedFiles, route, canceled);
                size_t expected = 0; for (const auto& pair : manifest.files) if (IsPayloadFile(pair.first, route)) ++expected;
                if (staged.size() != expected) throw Failure(L"The staged application is incomplete.");
                stagedFiles.clear(); CheckCancellation(canceled);
                CreateParents(root, selected);
                if (!MoveFileExW(ToFilePath(staging, selected).c_str(), ToFilePath(root, selected).c_str(), MOVEFILE_WRITE_THROUGH)) Fail(L"Cannot commit the verified application cache.");
            }
            if (route == Route::ModernArm64) RemoveDirectoryW(ToFilePath(staging, L"modern").c_str());
            RemoveDirectoryW(staging.c_str()); // Empty owned staging only. Failed/incomplete trees are never reused or executed.
        }
        PreparedPayload result; result.directory = ToFilePath(root, selected); result.parents = std::move(parents); result.target = manifest.target;
        std::set<std::wstring, OrdinalIgnoreCase> seen;
        EnumerateVerified(manifest, root, selected, seen, result.files, route, canceled);
        size_t expected = 0;
        for (const auto& pair : manifest.files) if (IsPayloadFile(pair.first, route)) ++expected;
        if (seen.size() != expected) throw Failure(L"The application cache is incomplete. A clean copy of this version is required.");
        ReleaseMutex(mutex.get()); return result;
    } catch (...) { ReleaseMutex(mutex.get()); throw; }
}
#ifdef PK_FIXTURE_BUILD
void ExtractCabinetFixture(const std::vector<BYTE>& archive, const Manifest& manifest, const std::wstring& root, Route route) {
    if (archive.size() > MAXDWORD || Hex(HashBytes(archive.data(), archive.size())) != manifest.archiveHash) throw Failure(L"Fixture cabinet hash mismatch.");
    Extract({archive.data(), static_cast<DWORD>(archive.size())}, manifest, FullPath(root), route, nullptr, true);
}
void ExtractCabinetFixture(const std::vector<BYTE>& archive, const Manifest& manifest, const std::wstring& root, bool modern) {
    ExtractCabinetFixture(archive, manifest, root, modern ? Route::ModernX64 : Route::Legacy);
}
#endif
}
