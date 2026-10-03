#include "InstanceRedirect.h"
#include <shlobj.h>
#include <sddl.h>
#include <algorithm>
#include <memory>
#include <winver.h>

namespace pk { namespace {
struct FindHandle { HANDLE value; explicit FindHandle(HANDLE v) : value(v) {} ~FindHandle() { if (value != INVALID_HANDLE_VALUE) FindClose(value); } };
struct Peer { LaunchContext context; Route route = Route::Unsupported; ULONGLONG requested = 0; Handle process; std::vector<Handle> files; };
#ifdef PK_FIXTURE_BUILD
std::wstring fixtureRoot, fixtureGate;
#endif
DWORD Session(DWORD pid) { DWORD session = 0; if (!ProcessIdToSessionId(pid, &session)) Fail(L"Cannot verify application session."); return session; }
std::wstring Cache() {
#ifdef PK_FIXTURE_BUILD
    if (!fixtureRoot.empty()) return fixtureRoot;
    throw Failure(L"Instance fixtures require an explicit isolated cache.");
#else
    wchar_t root[MAX_PATH]{}; if (FAILED(SHGetFolderPathW(nullptr, CSIDL_COMMON_APPDATA, nullptr, SHGFP_TYPE_CURRENT, root))) throw Failure(L"Cannot locate application sessions."); return std::wstring(root) + L"\\ProcessKeeper\\Universal\\" + UserSid();
#endif
}
bool Equal(const std::wstring& a, const std::wstring& b) { return CompareStringOrdinal(a.c_str(), -1, b.c_str(), -1, TRUE) == CSTR_EQUAL; }
bool Owner(HANDLE process) {
    HANDLE raw = nullptr; if (!OpenProcessToken(process, TOKEN_QUERY, &raw)) return false; Handle token(raw);
    DWORD size = 0; GetTokenInformation(token.get(), TokenUser, nullptr, 0, &size); if (size < sizeof(TOKEN_USER) || size > 65536) return false;
    std::vector<BYTE> data(size); if (!GetTokenInformation(token.get(), TokenUser, data.data(), size, &size)) return false;
    wchar_t* text = nullptr; if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(data.data())->User.Sid, &text)) return false;
    const bool result = UserSid() == text; LocalFree(text); return result;
}
Route PayloadRoute(const std::wstring& image, const std::wstring& root) {
    if (image.size() <= root.size() + 65 || !Equal(image.substr(0, root.size() + 1), root + L"\\")) return Route::Unsupported;
    const auto relative = image.substr(root.size() + 1); if (!ValidSha256(relative.substr(0, 64))) return Route::Unsupported;
    const auto suffix = relative.substr(64);
    if (Equal(suffix, L"-modern\\modern\\ProcessKeeper.exe")) return Route::ModernX64;
    if (Equal(suffix, L"-legacy\\legacy\\ProcessKeeper.exe")) return Route::Legacy;
    return Equal(suffix, L"-arm64\\modern\\arm64\\ProcessKeeper.exe") ? Route::ModernArm64 : Route::Unsupported;
}
bool Eligible(const Host& host, Route route) {
    if (route == Route::Legacy) return ChooseRoute(host, true) == route;
    return IsModernRoute(route) && ChooseRoute(host) == route;
}
bool CandidateWins(const std::wstring& candidateVersion, Route candidate, ULONGLONG requested,
    const std::wstring& existingVersion, Route existing, ULONGLONG existingRequested) {
    if (IsNewerUpdateVersion(candidateVersion, existingVersion)) return true;
    if (IsNewerUpdateVersion(existingVersion, candidateVersion)) return false;
    if (IsModernRoute(candidate) != IsModernRoute(existing)) return IsModernRoute(candidate);
    return requested > existingRequested;
}
bool Number(const std::wstring& value, ULONGLONG maximum, ULONGLONG& result) {
    if (value.empty() || value.size() > 20 || value.find_first_not_of(L"0123456789") != std::wstring::npos) return false;
    result = 0; for (auto c : value) { const auto digit = static_cast<unsigned>(c - L'0'); if (result > (maximum - digit) / 10) return false; result = result * 10 + digit; } return result > 0;
}
Handle InstanceFile(const std::wstring& path) {
#ifdef PK_FIXTURE_BUILD
    if (!fixtureRoot.empty()) {
        if (!Equal(FullPath(path).substr(0, fixtureRoot.size() + 1), fixtureRoot + L"\\")) throw Failure(L"Fixture escaped the isolated cache.");
        auto parents = LockParents(path, false); Handle file(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
        if (!file.valid()) Fail(L"Cannot read instance fixture."); VerifyHandlePath(file.get(), path, false); return file;
    }
#endif
    return OpenProtectedFile(path);
}
std::vector<std::wstring> Lines(const std::wstring& path) {
#ifdef PK_FIXTURE_BUILD
    if (!fixtureRoot.empty()) {
        auto file = InstanceFile(path); LARGE_INTEGER size{}; if (!GetFileSizeEx(file.get(), &size) || size.QuadPart <= 0 || size.QuadPart > 300000) throw Failure(L"Invalid instance fixture length.");
        std::string bytes(static_cast<size_t>(size.QuadPart), '\0'); DWORD read = 0; if (!ReadFile(file.get(), &bytes[0], static_cast<DWORD>(bytes.size()), &read, nullptr) || read != bytes.size()) Fail(L"Cannot read instance fixture.");
        std::vector<std::wstring> lines; std::wstring line;
        for (auto c : bytes) { if (c == '\n') { lines.push_back(line); line.clear(); } else if (c != '\r') { if (!c || static_cast<unsigned char>(c) > 127) throw Failure(L"Invalid instance fixture encoding."); line += c; } }
        if (!line.empty()) lines.push_back(line); return lines;
    }
#endif
    return ReadProtectedLines(path);
}
bool Metadata(const std::wstring& image, HANDLE file, Route route, const std::wstring& version) {
    LARGE_INTEGER offset{}; IMAGE_DOS_HEADER dos{}; DWORD count = 0;
    if (!SetFilePointerEx(file, offset, nullptr, FILE_BEGIN) || !ReadFile(file, &dos, sizeof(dos), &count, nullptr) || count != sizeof(dos) || dos.e_magic != IMAGE_DOS_SIGNATURE || dos.e_lfanew < sizeof(dos) || dos.e_lfanew > 1048576) return false;
    offset.QuadPart = dos.e_lfanew; DWORD signature = 0; IMAGE_FILE_HEADER header{};
    if (!SetFilePointerEx(file, offset, nullptr, FILE_BEGIN) || !ReadFile(file, &signature, sizeof(signature), &count, nullptr) || count != sizeof(signature) || signature != IMAGE_NT_SIGNATURE || !ReadFile(file, &header, sizeof(header), &count, nullptr) || count != sizeof(header) || header.Characteristics & IMAGE_FILE_DLL) return false;
    const auto machine = route == Route::Legacy ? IMAGE_FILE_MACHINE_I386 : route == Route::ModernX64 ? IMAGE_FILE_MACHINE_AMD64 : IMAGE_FILE_MACHINE_ARM64;
    if (header.Machine != machine) return false;
    DWORD ignored = 0; const auto length = GetFileVersionInfoSizeW(image.c_str(), &ignored); if (!length || length > 1048576) return false;
    std::vector<BYTE> data(length); if (!GetFileVersionInfoW(image.c_str(), 0, length, data.data())) return false;
    struct Translation { WORD language, codepage; }; Translation* translations = nullptr; UINT bytes = 0;
    if (!VerQueryValueW(data.data(), L"\\VarFileInfo\\Translation", reinterpret_cast<void**>(&translations), &bytes) || bytes < sizeof(Translation) || bytes > 256) return false;
    for (UINT index = 0; index < bytes / sizeof(Translation); ++index) {
        wchar_t key[128]{}; swprintf_s(key, L"\\StringFileInfo\\%04x%04x\\ProductName", translations[index].language, translations[index].codepage);
        wchar_t* product = nullptr; UINT characters = 0; if (!VerQueryValueW(data.data(), key, reinterpret_cast<void**>(&product), &characters) || !characters || characters > 128 || (!Equal(product, L"ProcessKeeper") && !Equal(product, L"Process Keeper"))) continue;
        swprintf_s(key, L"\\StringFileInfo\\%04x%04x\\ProductVersion", translations[index].language, translations[index].codepage);
        wchar_t* actual = nullptr; if (!VerQueryValueW(data.data(), key, reinterpret_cast<void**>(&actual), &characters) || !characters || characters > 128) continue;
        try { if (!IsNewerUpdateVersion(actual, version) && !IsNewerUpdateVersion(version, actual)) return true; } catch (...) { }
    }
    return false;
}
ULONGLONG Requested(const LaunchContext& context) {
    const auto path = context.directory + L"\\instance-request.txt"; const auto attributes = GetFileAttributesW(path.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES) { const auto error = GetLastError(); if (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND) return context.created; Fail(L"Cannot verify instance request.", error); }
    const auto marker = Lines(path); ULONGLONG pid = 0, time = 0;
    if (marker.size() != 3 || marker[0] != L"PKREQUEST1" || !Number(marker[1], MAXDWORD, pid) || pid <= 4 || !Number(marker[2], MAXLONGLONG, time) || time > context.created) throw Failure(L"Invalid instance request identity.");
    return time;
}
bool RedirectFields(const std::vector<std::wstring>& fields, const std::wstring& own) {
    return fields.size() == 3 && fields[0] == L"PKINSTANCE1" && fields[1] == own && ValidContextId(own) && ValidContextId(fields[2]) && fields[2] != own;
}
std::unique_ptr<Peer> ReadPeer(const std::wstring& root, const std::wstring& id, const Host& host) {
    if (!ValidContextId(id)) return nullptr;
    const auto directory = root + L"\\sessions\\" + id;
    if (GetFileAttributesW((directory + L"\\context.txt").c_str()) == INVALID_FILE_ATTRIBUTES && GetLastError() == ERROR_FILE_NOT_FOUND) return nullptr;
    auto parents = LockParents(directory + L"\\context.txt", false);
    auto fields = Lines(directory + L"\\context.txt");
    if ((fields.size() != 12 && fields.size() != 13) || fields[0] != L"PKLC1" || fields[1] != id || DecodeContextText(fields[11]) != UserSid() ||
        !ValidSha256(fields[3]) || !ValidSha256(fields[6]) || !ValidSha256(fields[8])) return nullptr;
    auto result = std::make_unique<Peer>(); auto& context = result->context;
    context.id = id; context.directory = directory; context.version = fields[4]; context.original = FullPath(DecodeContextText(fields[2])); context.originalHash = fields[3];
    context.payload = FullPath(DecodeContextText(fields[5])); context.payloadHash = fields[6]; context.helper = FullPath(DecodeContextText(fields[7])); context.helperHash = fields[8];
    ULONGLONG pid = 0; if (!Number(fields[9], MAXDWORD, pid) || pid <= 4 || !Number(fields[10], MAXLONGLONG, context.created)) return nullptr; context.pid = static_cast<DWORD>(pid);
    result->route = PayloadRoute(context.payload, root); if (!Eligible(host, result->route)) return nullptr;
    if (!Equal(context.helper, context.payload.substr(0, context.payload.find_last_of(L'\\')) + L"\\ProcessKeeper.Updater.exe")) return nullptr;
    if (fields.size() == 13) {
        if (fields[12] == L"Windows7Compat" && result->route != Route::Legacy || fields[12] == L"Windows10arm64" && result->route != Route::ModernArm64 ||
            fields[12] == L"Windows10x64" && result->route == Route::ModernArm64 || fields[12] != L"Universal" && fields[12] != L"Windows7Compat" && fields[12] != L"Windows10x64" && fields[12] != L"Windows10arm64") return nullptr;
    }
    result->process = Handle(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, context.pid));
    if (!result->process.valid() || WaitForSingleObject(result->process.get(), 0) != WAIT_TIMEOUT || ProcessCreated(result->process.get()) != context.created ||
        !Equal(ProcessImage(result->process.get()), context.payload) || Session(context.pid) != Session(GetCurrentProcessId()) || !Owner(result->process.get())) return nullptr;
    // Dead historical receipts never parse version metadata or lock absent old payloads.
    (void)IsNewerUpdateVersion(fields[4], ProductVersion);
    for (auto& parent : parents) result->files.push_back(std::move(parent));
    for (const auto& file : { std::make_pair(context.payload, context.payloadHash), std::make_pair(context.helper, context.helperHash) }) {
        auto handle = InstanceFile(file.first); if (!Equal(Hex(HashFile(handle.get())), file.second)) return nullptr;
        if (Equal(file.first, context.payload) && !Metadata(context.payload, handle.get(), result->route, context.version)) return nullptr;
        result->files.push_back(std::move(handle));
    }
    result->requested = Requested(context);
    return result;
}
struct WindowSearch { DWORD pid; HWND found = nullptr; };
BOOL CALLBACK FindWindow(HWND window, LPARAM parameter) {
    auto& search = *reinterpret_cast<WindowSearch*>(parameter); DWORD pid = 0; GetWindowThreadProcessId(window, &pid);
    if (pid != search.pid || (!IsWindowVisible(window) && !IsIconic(window))) return TRUE;
    wchar_t name[256]{}, title[256]{}; GetClassNameW(window, name, 256); GetWindowTextW(window, title, 256);
    if (!IsApplicationWindowIdentity(name, title)) return TRUE;
    search.found = window; return FALSE;
}
HWND PeerWindow(DWORD pid) { WindowSearch search{pid}; EnumWindows(FindWindow, reinterpret_cast<LPARAM>(&search)); return search.found; }
bool Focus(Peer& peer) {
    if (WaitForSingleObject(peer.process.get(), 0) != WAIT_TIMEOUT) return false;
    const auto window = PeerWindow(peer.context.pid); if (!window) return false;
    if (IsIconic(window)) ShowWindowAsync(window, SW_RESTORE); SetForegroundWindow(window); return true;
}
std::vector<std::unique_ptr<Peer>> Peers(const Host& host) {
    const auto root = Cache(), directory = root + L"\\sessions"; std::vector<std::unique_ptr<Peer>> result;
    if (GetFileAttributesW(directory.c_str()) == INVALID_FILE_ATTRIBUTES) {
        const auto error = GetLastError(); if (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND) return result; Fail(L"Cannot inspect application instance receipts.", error);
    }
    WIN32_FIND_DATAW item{}; const FindHandle search(FindFirstFileW((directory + L"\\*").c_str(), &item)); if (search.value == INVALID_HANDLE_VALUE) Fail(L"Cannot inspect application instances.");
    unsigned count = 0;
    do {
        if (wcscmp(item.cFileName, L".") == 0 || wcscmp(item.cFileName, L"..") == 0) continue;
        if (++count > 2048) throw Failure(L"Too many application instance receipts to verify.");
        if (!(item.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) || item.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT || !ValidContextId(item.cFileName)) throw Failure(L"Invalid application instance directory.");
        try { auto peer = ReadPeer(root, item.cFileName, host); if (peer) result.push_back(std::move(peer)); }
        catch (...) { /* A corrupt receipt carries no authority to close an application. */ }
    } while (FindNextFileW(search.value, &item));
    if (GetLastError() != ERROR_NO_MORE_FILES) Fail(L"Incomplete application instance inspection."); return result;
}
}
InstanceLaunchGate::InstanceLaunchGate(const std::atomic_bool* canceled) {
    auto name = L"Local\\ProcessKeeper.ManagedLaunch." + UserSid() + L"." + std::to_wstring(Session(GetCurrentProcessId()));
#ifdef PK_FIXTURE_BUILD
    if (fixtureGate.empty()) throw Failure(L"Instance fixtures require a private launch gate."); name = L"Local\\ProcessKeeper.FixtureLaunch." + fixtureGate;
#endif
    mutex_ = Handle(CreateMutexW(nullptr, FALSE, name.c_str())); if (!mutex_.valid()) Fail(L"Cannot coordinate application launch.");
    for (unsigned attempt = 0; attempt < 1200; ++attempt) {
        if (canceled && *canceled) throw Failure(L"Application launch canceled.");
        const auto status = WaitForSingleObject(mutex_.get(), 100);
        if (status == WAIT_OBJECT_0 || status == WAIT_ABANDONED) { owned_ = true; return; }
        if (status != WAIT_TIMEOUT) Fail(L"Cannot acquire application launch coordination.");
    }
    throw Failure(L"Another application launch has not completed.");
}
InstanceLaunchGate::~InstanceLaunchGate() { if (owned_) ReleaseMutex(mutex_.get()); }
bool ResolvePreferredInstance(Route candidate, ULONGLONG requested, const std::atomic_bool* canceled) {
    const auto host = DetectHost(); if (!Eligible(host, candidate)) return RetainVisibleCompatibleInstance(host);
    auto peers = Peers(host); if (peers.empty()) return RetainVisibleCompatibleInstance(host);
    auto best = peers.begin();
    for (auto peer = peers.begin(); peer != peers.end(); ++peer)
        if (CandidateWins((*peer)->context.version, (*peer)->route, (*peer)->requested, (*best)->context.version, (*best)->route, (*best)->requested)) best = peer;
    if (!CandidateWins(ProductVersion, candidate, requested, (*best)->context.version, (*best)->route, (*best)->requested)) { Focus(**best); return true; }
    for (auto& peer : peers) {
        const auto window = PeerWindow(peer->context.pid);
        if (!window) return true; // A verified live owner is still starting; never create a duplicate UI.
        PostMessageW(window, WM_CLOSE, 0, 0);
    }
#ifdef PK_FIXTURE_BUILD
    constexpr unsigned closeAttempts = 5;
#else
    constexpr unsigned closeAttempts = 120;
#endif
    for (unsigned attempt = 0; attempt < closeAttempts; ++attempt) {
        if (canceled && *canceled) throw Failure(L"Application launch canceled.");
        bool running = false;
        for (auto& peer : peers) if (WaitForSingleObject(peer->process.get(), 0) == WAIT_TIMEOUT) running = true;
        if (!running) return false;
        Sleep(100);
    }
    Focus(**best); return true; // Refused/unresponsive close preserves the existing compatible owner.
}
bool ConfirmInstanceRedirect(const std::wstring& own, DWORD ownPid, Route ownRoute, ULONGLONG ownCreated) {
    const auto fields = Lines(Cache() + L"\\sessions\\" + own + L"\\instance-redirect.txt");
    if (!RedirectFields(fields, own)) return false;
    const auto ownContext = ParseLaunchContext(Lines(Cache() + L"\\sessions\\" + own + L"\\context.txt"), own, Cache() + L"\\sessions\\" + own); if (ownContext.pid != ownPid || ownContext.created != ownCreated) return false;
    auto peer = ReadPeer(Cache(), fields[2], DetectHost());
    return peer && !CandidateWins(ProductVersion, ownRoute, Requested(ownContext), peer->context.version, peer->route, peer->requested) && Focus(*peer);
}
bool RetainVisibleCompatibleInstance(const Host& host) {
    struct Search { Host host; std::wstring root; bool found = false; } search{host, Cache()};
    EnumWindows([](HWND window, LPARAM argument) -> BOOL {
        auto& value = *reinterpret_cast<Search*>(argument); DWORD pid = 0; GetWindowThreadProcessId(window, &pid);
        try {
            if (!IsWindowVisible(window) && !IsIconic(window)) return TRUE;
            wchar_t name[256]{}, title[256]{}; GetClassNameW(window, name, 256); GetWindowTextW(window, title, 256);
            if (!IsApplicationWindowIdentity(name, title)) return TRUE;
            if (pid == GetCurrentProcessId() || Session(pid) != Session(GetCurrentProcessId())) return TRUE;
            Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, pid)); if (!process.valid() || !Owner(process.get())) return TRUE;
            const auto route = PayloadRoute(ProcessImage(process.get()), value.root); if (!Eligible(value.host, route) || PeerWindow(pid) != window) return TRUE;
            if (IsIconic(window)) ShowWindowAsync(window, SW_RESTORE); SetForegroundWindow(window); value.found = true; return FALSE;
        } catch (...) { return TRUE; }
    }, reinterpret_cast<LPARAM>(&search)); return search.found;
}
#ifdef PK_FIXTURE_BUILD
bool InstanceCandidateWinsFixture(const std::wstring& candidateVersion, Route candidate, ULONGLONG requested, const std::wstring& existingVersion, Route existing, ULONGLONG existingRequested) { return CandidateWins(candidateVersion, candidate, requested, existingVersion, existing, existingRequested); }
bool ValidInstanceRedirectFixture(const std::vector<std::wstring>& fields, const std::wstring& own) { return RedirectFields(fields, own); }
void ConfigureInstanceFixture(const std::wstring& root, const std::wstring& gate) {
    wchar_t temporary[MAX_PATH]{}; if (!GetTempPathW(MAX_PATH, temporary)) Fail(L"Fixture TEMP is missing.");
    const auto full = FullPath(root); auto temp = FullPath(temporary); while (temp.size() > 3 && temp.back() == L'\\') temp.pop_back();
    if (!ValidContextId(gate) || full.size() >= 180 || !Equal(full.substr(0, temp.size() + 1), temp + L"\\")) throw Failure(L"Instance fixture cache must remain in explicit short TEMP.");
    auto parents = LockParents(full + L"\\probe", false); fixtureRoot = full; fixtureGate = gate;
}
bool VerifyInstancePeerFixture(const std::wstring& id) { try { return ReadPeer(Cache(), id, DetectHost()) != nullptr; } catch (...) { return false; } }
#endif
}
