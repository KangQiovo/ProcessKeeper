#include "LaunchContext.h"
#include <objbase.h>
#include <sstream>
#include <winver.h>
#include <algorithm>

namespace pk {
bool ValidContextId(const std::wstring& value) {
    if (value.size() != 32) return false;
    for (auto c : value) if (!(c >= L'0' && c <= L'9') && !(c >= L'a' && c <= L'f')) return false;
    return true;
}
bool ValidSha256(const std::wstring& value) {
    return value.size() == 64 && ValidContextId(value.substr(0, 32)) && ValidContextId(value.substr(32));
}
namespace {
struct SemanticVersion { std::wstring core, full; std::vector<unsigned> numbers; std::vector<std::wstring> prerelease; };
SemanticVersion ParseVersion(std::wstring value) {
    if (!value.empty() && (value[0] == L'v' || value[0] == L'V')) value.erase(0, 1);
    if (value.empty() || value.size() > 128) throw Failure(L"Invalid update version.");
    SemanticVersion result; result.full = value;
    auto metadata = value.find(L'+');
    auto validIdentifiers = [](const std::wstring& part, bool prerelease) {
        std::vector<std::wstring> items; size_t begin = 0;
        do { auto end = part.find(L'.', begin); auto item = part.substr(begin, end == std::wstring::npos ? end : end - begin);
            if (item.empty()) throw Failure(L"Invalid update version identifier.");
            bool numeric = true;
            for (auto c : item) { if (!(c >= L'0' && c <= L'9')) numeric = false;
                if (!(c >= L'0' && c <= L'9') && !(c >= L'a' && c <= L'z') && !(c >= L'A' && c <= L'Z') && c != L'-') throw Failure(L"Invalid update version identifier."); }
            if (prerelease && numeric && item.size() > 1 && item[0] == L'0') throw Failure(L"Invalid numeric prerelease identifier.");
            items.push_back(item); if (end == std::wstring::npos) break; begin = end + 1;
        } while (true); return items;
    };
    if (metadata != std::wstring::npos) { validIdentifiers(value.substr(metadata + 1), false); value.resize(metadata); }
    auto dash = value.find(L'-');
    if (dash != std::wstring::npos) { result.prerelease = validIdentifiers(value.substr(dash + 1), true); value.resize(dash); }
    result.core = value; size_t begin = 0;
    for (int index = 0; index < 3; ++index) {
        auto end = value.find(L'.', begin); auto number = value.substr(begin, end == std::wstring::npos ? end : end - begin);
        if (number.empty() || number.size() > 5 || (number.size() > 1 && number[0] == L'0')) throw Failure(L"Invalid update numeric version.");
        unsigned total = 0; for (auto c : number) { if (c < L'0' || c > L'9') throw Failure(L"Invalid update numeric version."); total = total * 10 + c - L'0'; }
        if (total > 65535 || (index < 2) == (end == std::wstring::npos)) throw Failure(L"Update version cannot be represented by a native PE.");
        result.numbers.push_back(total); begin = end == std::wstring::npos ? value.size() : end + 1;
    }
    return result;
}
}
bool IsNewerUpdateVersion(const std::wstring& candidate, const std::wstring& current) {
    const auto next = ParseVersion(candidate), prior = ParseVersion(current);
    for (size_t i = 0; i < 3; ++i) if (next.numbers[i] != prior.numbers[i]) return next.numbers[i] > prior.numbers[i];
    if (next.prerelease.empty() || prior.prerelease.empty()) return next.prerelease.empty() && !prior.prerelease.empty();
    for (size_t i = 0; i < (std::min)(next.prerelease.size(), prior.prerelease.size()); ++i) {
        const auto& left = next.prerelease[i]; const auto& right = prior.prerelease[i]; if (left == right) continue;
        auto numeric = [](const std::wstring& item) { return item.find_first_not_of(L"0123456789") == std::wstring::npos; };
        const bool ln = numeric(left), rn = numeric(right); if (ln != rn) return !ln;
        if (ln && left.size() != right.size()) return left.size() > right.size(); return left > right;
    }
    return next.prerelease.size() > prior.prerelease.size();
}
std::wstring NewContextId() {
    GUID id{}; if (FAILED(CoCreateGuid(&id))) throw Failure(L"Cannot create an update session.");
    const auto* bytes = reinterpret_cast<const BYTE*>(&id); const wchar_t* hex = L"0123456789abcdef"; std::wstring text;
    for (size_t i = 0; i < sizeof(id); ++i) { text += hex[bytes[i] >> 4]; text += hex[bytes[i] & 15]; } return text;
}
std::wstring EncodeContextText(const std::wstring& value) {
    const auto* bytes = reinterpret_cast<const BYTE*>(value.data()); const wchar_t* hex = L"0123456789abcdef"; std::wstring text;
    for (size_t i = 0; i < value.size() * sizeof(wchar_t); ++i) { text += hex[bytes[i] >> 4]; text += hex[bytes[i] & 15]; } return text;
}
std::wstring DecodeContextText(const std::wstring& text) {
    if (text.size() > 131068 || text.size() % 4) throw Failure(L"Invalid update context text.");
    if (text.empty()) return {};
    std::wstring result(text.size() / 4, L'\0'); auto* bytes = reinterpret_cast<BYTE*>(&result[0]);
    auto nibble = [](wchar_t c) -> BYTE { if (c >= L'0' && c <= L'9') return static_cast<BYTE>(c - L'0'); if (c >= L'a' && c <= L'f') return static_cast<BYTE>(c - L'a' + 10); throw Failure(L"Invalid update context encoding."); };
    for (size_t i = 0; i < text.size() / 2; ++i) bytes[i] = static_cast<BYTE>(nibble(text[i * 2]) * 16 + nibble(text[i * 2 + 1]));
    if (result.find(L'\0') != std::wstring::npos) throw Failure(L"Invalid update context text."); return result;
}
std::vector<std::wstring> ReadProtectedLines(const std::wstring& path) {
    auto parents = LockParents(path, true); auto file = OpenProtectedFile(path); LARGE_INTEGER size{};
    if (!GetFileSizeEx(file.get(), &size) || size.QuadPart <= 0 || size.QuadPart > 300000) throw Failure(L"Invalid update record size.");
    std::string bytes(static_cast<size_t>(size.QuadPart), '\0'); DWORD read = 0;
    if (!ReadFile(file.get(), &bytes[0], static_cast<DWORD>(bytes.size()), &read, nullptr) || read != bytes.size()) Fail(L"Cannot read the update record.");
    std::vector<std::wstring> lines; std::wstring line;
    for (auto ch : bytes) { if (ch == '\n') { lines.push_back(line); line.clear(); } else if (ch != '\r') { if (static_cast<unsigned char>(ch) > 127 || ch == '\0') throw Failure(L"Invalid update record."); line += static_cast<wchar_t>(ch); } }
    if (!line.empty()) lines.push_back(line); return lines;
}
void WriteProtectedLines(const std::wstring& path, const std::vector<std::wstring>& lines) {
    auto parents = LockParents(path, true); const auto pending = path + L".pending-" + NewContextId(); auto file = CreateProtectedFile(pending); std::string bytes;
    for (const auto& line : lines) { for (auto c : line) { if (c > 127 || c == L'\r' || c == L'\n' || c == 0) throw Failure(L"Invalid update record."); bytes += static_cast<char>(c); } bytes += '\n'; }
    DWORD written = 0; if (!WriteFile(file.get(), bytes.data(), static_cast<DWORD>(bytes.size()), &written, nullptr) || written != bytes.size() || !FlushFileBuffers(file.get())) Fail(L"Cannot save the update record.");
    file.reset();
    if (!MoveFileExW(pending.c_str(), path.c_str(), MOVEFILE_WRITE_THROUGH)) Fail(L"Cannot publish the completed update record.");
}
ULONGLONG ProcessCreated(HANDLE process) {
    FILETIME created{}, exited{}, kernel{}, user{}; if (!GetProcessTimes(process, &created, &exited, &kernel, &user)) Fail(L"Cannot verify the calling process.");
    return (static_cast<ULONGLONG>(created.dwHighDateTime) << 32) | created.dwLowDateTime;
}
std::wstring ProcessImage(HANDLE process) {
    std::vector<wchar_t> image(32768); DWORD count = static_cast<DWORD>(image.size());
    if (!QueryFullProcessImageNameW(process, 0, image.data(), &count)) Fail(L"Cannot verify the calling process image."); return FullPath(std::wstring(image.data(), count));
}
std::vector<wchar_t> LaunchEnvironment(const std::wstring& id) {
    if (!ValidContextId(id)) throw Failure(L"Invalid launch context identifier.");
    auto environment = BuildChildEnvironment(); std::vector<wchar_t> result; std::vector<std::wstring> entries;
    for (size_t offset = 0; offset < environment.size() && environment[offset];) {
        std::wstring entry(&environment[offset]); offset += entry.size() + 1;
        const auto name = entry.substr(0, entry.find(L'='));
        if (CompareStringOrdinal(name.c_str(), -1, L"PROCESSKEEPER_LAUNCH_CONTEXT", -1, TRUE) == CSTR_EQUAL) continue;
        entries.push_back(entry);
    }
    entries.push_back(L"PROCESSKEEPER_LAUNCH_CONTEXT=" + id); std::sort(entries.begin(), entries.end(), OrdinalIgnoreCase{});
    for (const auto& entry : entries) { result.insert(result.end(), entry.begin(), entry.end()); result.push_back(0); }
    result.push_back(0); return result;
}
void WriteLaunchContext(const SourceLock& source, const PreparedPayload& payload, const std::wstring& id, HANDLE process, DWORD pid) {
    if (!ValidContextId(id)) throw Failure(L"Invalid launch session.");
    const auto sessions = ProtectedCacheRoot() + L"\\sessions"; EnsureProtectedDirectory(sessions);
    const auto directory = sessions + L"\\" + id; EnsureProtectedDirectory(directory);
    const auto executable = payload.directory + L"\\ProcessKeeper.exe", helper = payload.directory + L"\\ProcessKeeper.Updater.exe";
    auto image = OpenProtectedFile(executable), updater = OpenProtectedFile(helper);
    source.Verify();
    WriteProtectedLines(directory + L"\\context.txt", {L"PKLC1", id, EncodeContextText(source.path()), source.sha256(), ProductVersion,
        EncodeContextText(executable), Hex(HashFile(image.get())), EncodeContextText(helper), Hex(HashFile(updater.get())),
        std::to_wstring(pid), std::to_wstring(ProcessCreated(process)), EncodeContextText(UserSid()),
        payload.target == PackageTarget::Windows7Compat ? L"Windows7Compat" : payload.target == PackageTarget::Windows10x64 ? L"Windows10x64" : payload.target == PackageTarget::Windows10arm64 ? L"Windows10arm64" : L"Universal"});
}
LaunchContext ReadLaunchContext(const std::wstring& id) {
    if (!ValidContextId(id)) throw Failure(L"Invalid launch context identifier.");
    const auto directory = ProtectedCacheRoot() + L"\\sessions\\" + id;
    return ParseLaunchContext(ReadProtectedLines(directory + L"\\context.txt"), id, directory);
}
LaunchContext ParseLaunchContext(const std::vector<std::wstring>& fields, const std::wstring& id, const std::wstring& directory) {
    if (!ValidContextId(id)) throw Failure(L"Invalid launch context identifier.");
    LaunchContext result; result.id = id; result.directory = directory;
    if ((fields.size() != 12 && fields.size() != 13) || fields[0] != L"PKLC1" || fields[1] != id || DecodeContextText(fields[11]) != UserSid()) throw Failure(L"Invalid launch context.");
    if (fields.size() == 13) {
        if (fields[12] == L"Windows7Compat") result.target = PackageTarget::Windows7Compat;
        else if (fields[12] == L"Windows10x64") result.target = PackageTarget::Windows10x64;
        else if (fields[12] == L"Windows10arm64") result.target = PackageTarget::Windows10arm64;
        else if (fields[12] != L"Universal") throw Failure(L"Unknown trusted package flavor.");
    }
    result.original = FullPath(DecodeContextText(fields[2])); result.originalHash = fields[3]; result.version = fields[4];
    result.payload = FullPath(DecodeContextText(fields[5])); result.payloadHash = fields[6]; result.helper = FullPath(DecodeContextText(fields[7])); result.helperHash = fields[8];
    if (fields[9].empty() || fields[10].empty() || fields[9].find_first_not_of(L"0123456789") != std::wstring::npos || fields[10].find_first_not_of(L"0123456789") != std::wstring::npos)
        throw Failure(L"Invalid launch process identity.");
    result.pid = static_cast<DWORD>(std::stoul(fields[9])); result.created = std::stoull(fields[10]);
    if (result.pid <= 4 || !ValidSha256(result.originalHash) || !ValidSha256(result.payloadHash) || !ValidSha256(result.helperHash) || result.version != ProductVersion) throw Failure(L"Invalid launch identity.");
    return result;
}
namespace {
Handle ContextProcess(const LaunchContext& context, bool fixture) {
    Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, context.pid));
    if (!process.valid() || ProcessCreated(process.get()) != context.created || ProcessImage(process.get()) != context.payload || WaitForSingleObject(process.get(), 0) != WAIT_TIMEOUT)
        throw Failure(L"The original application instance is no longer available.");
    auto image = fixture ? Handle(CreateFileW(context.payload.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr)) : OpenProtectedFile(context.payload);
    if (!image.valid()) Fail(L"Cannot verify application identity."); VerifyHandlePath(image.get(), context.payload, false);
    if (Hex(HashFile(image.get())) != context.payloadHash) throw Failure(L"The application identity changed.");
    return process;
}
void ValidateBundle(const std::wstring& path, const std::wstring& expectedVersion, bool fixture, const PackageTarget* expectedTarget = nullptr) {
    const auto semanticVersion = ParseVersion(expectedVersion);
    auto locked = fixture ? Handle(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr)) : OpenProtectedFile(path);
    if (!locked.valid()) Fail(L"Cannot verify the update package."); VerifyHandlePath(locked.get(), path, false);
    IMAGE_DOS_HEADER dos{}; DWORD read = 0;
    if (!ReadFile(locked.get(), &dos, sizeof(dos), &read, nullptr) || read != sizeof(dos) || dos.e_magic != IMAGE_DOS_SIGNATURE || dos.e_lfanew < sizeof(dos) || dos.e_lfanew > 1024 * 1024)
        throw Failure(L"The update is not a valid native executable.");
    LARGE_INTEGER offset{}; offset.QuadPart = dos.e_lfanew; IMAGE_NT_HEADERS32 pe{};
    if (!SetFilePointerEx(locked.get(), offset, nullptr, FILE_BEGIN) || !ReadFile(locked.get(), &pe, sizeof(pe), &read, nullptr) || read != sizeof(pe) ||
        pe.Signature != IMAGE_NT_SIGNATURE || pe.FileHeader.Machine != IMAGE_FILE_MACHINE_I386 || pe.OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR32_MAGIC ||
        pe.FileHeader.SizeOfOptionalHeader != sizeof(IMAGE_OPTIONAL_HEADER32) || pe.OptionalHeader.Subsystem != IMAGE_SUBSYSTEM_WINDOWS_GUI ||
        pe.OptionalHeader.NumberOfRvaAndSizes <= IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR || pe.OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR].VirtualAddress ||
        (pe.FileHeader.Characteristics & IMAGE_FILE_DLL)) throw Failure(L"The update must be a native x86 universal launcher.");
    // Resource-only mapping never runs DllMain, CLR or any code from the downloaded image.
    HMODULE module = LoadLibraryExW(path.c_str(), nullptr, LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
    if (!module) Fail(L"The update is not a readable native Process Keeper package.");
    try {
        auto resource = [&](int id) { const auto item = FindResourceW(module, MAKEINTRESOURCEW(id), RT_RCDATA); if (!item) throw Failure(L"The update does not contain a universal Process Keeper payload."); const auto size = SizeofResource(module, item); const auto data = LockResource(LoadResource(module, item)); if (!size || !data) throw Failure(L"Invalid update payload."); return std::make_pair(data, size); };
        const auto manifestResource = resource(101), archive = resource(102);
        if (manifestResource.second > 16 * 1024 * 1024) throw Failure(L"Invalid update manifest size.");
        const auto manifest = ParseManifest(std::string(static_cast<const char*>(manifestResource.first), manifestResource.second));
        if (expectedTarget && manifest.target != *expectedTarget) throw Failure(L"The update package flavor differs from the trusted current application.");
        if (Hex(HashBytes(archive.first, archive.second)) != manifest.archiveHash || manifest.target == PackageTarget::Universal &&
            (!manifest.files.count(L"modern/ProcessKeeper.Updater.exe") || !manifest.files.count(L"legacy/ProcessKeeper.Updater.exe") ||
            !manifest.files.count(L"modern/arm64/ProcessKeeper.exe") || !manifest.files.count(L"modern/arm64/ProcessKeeper.Updater.exe")))
            throw Failure(L"The update payload integrity check failed.");
        if (!fixture) {
            const auto route = ChoosePackageRoute(DetectHost(), manifest.target);
            if (route == Route::Unsupported || route == Route::MissingFramework) throw Failure(L"The update package does not support this operating system or processor architecture.");
        }
        const auto versionResource = FindResourceW(module, MAKEINTRESOURCEW(1), RT_VERSION);
        const auto raw = versionResource ? LockResource(LoadResource(module, versionResource)) : nullptr;
        if (!raw) throw Failure(L"The update product version is missing.");
        auto versionModule = LoadSystemLibrary(L"version.dll");
        const auto query = reinterpret_cast<BOOL(WINAPI*)(LPCVOID, LPCWSTR, LPVOID*, PUINT)>(GetProcAddress(versionModule, "VerQueryValueW"));
        VS_FIXEDFILEINFO* version = nullptr; UINT size = 0;
        if (!query || !query(raw, L"\\", reinterpret_cast<void**>(&version), &size) || size < sizeof(*version) || version->dwSignature != 0xFEEF04BD) throw Failure(L"Invalid update product version.");
        const auto text = std::to_wstring(HIWORD(version->dwFileVersionMS)) + L"." + std::to_wstring(LOWORD(version->dwFileVersionMS)) + L"." + std::to_wstring(HIWORD(version->dwFileVersionLS));
        if (text != semanticVersion.core || LOWORD(version->dwFileVersionLS) != 0) throw Failure(L"The downloaded EXE version does not match its verified release.");
        wchar_t* product = nullptr; wchar_t* productVersion = nullptr;
        if (!query(raw, L"\\StringFileInfo\\040904b0\\ProductName", reinterpret_cast<void**>(&product), &size) || !product || std::wstring(product) != L"Process Keeper" ||
            !query(raw, L"\\StringFileInfo\\040904b0\\ProductVersion", reinterpret_cast<void**>(&productVersion), &size) || !productVersion || std::wstring(productVersion) != semanticVersion.full)
            throw Failure(L"The update product identity does not match its verified release.");
    } catch (...) { FreeLibrary(module); throw; }
    FreeLibrary(module);
}
}
Handle OpenContextProcess(const LaunchContext& context) { return ContextProcess(context, false); }
void ValidateUpdateBundle(const std::wstring& path, const std::wstring& version, const PackageTarget* expectedTarget) { ValidateBundle(path, version, false, expectedTarget); }
#ifdef PK_FIXTURE_BUILD
Handle OpenContextProcessFixture(const LaunchContext& context) { return ContextProcess(context, true); }
void ValidateUpdateBundleFixture(const std::wstring& path, const std::wstring& version) { ValidateBundle(path, version, true); }
#endif
}
