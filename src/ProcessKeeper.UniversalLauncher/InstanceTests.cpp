#include "InstanceRedirect.h"
#include <fstream>
#include <functional>
#include <iterator>

#define PK_WIDE_LITERAL_INNER(value) L##value
#define PK_WIDE_LITERAL(value) PK_WIDE_LITERAL_INNER(value)

namespace {
using namespace pk;
void Directory(const std::wstring& path) { if (!CreateDirectoryW(path.c_str(), nullptr) && GetLastError() != ERROR_ALREADY_EXISTS) Fail(L"Cannot create isolated instance fixture."); }
void Write(const std::wstring& path, const std::vector<std::wstring>& fields) {
    std::ofstream file(path, std::ios::binary | std::ios::trunc); for (auto& line : fields) { for (auto c : line) file.put(static_cast<char>(c)); file.put('\n'); }
    file.flush(); if (!file.good()) throw Failure(L"Cannot publish instance fixture record.");
}
std::wstring Image() { wchar_t path[32768]{}; const auto count = GetModuleFileNameW(nullptr, path, 32768); if (!count || count >= 32768) Fail(L"Cannot locate inert instance fixture."); return FullPath(path); }
struct Child { Handle process; DWORD pid = 0; };
Child Start(const std::wstring& image, const std::wstring& arguments) {
    std::wstring command = L"\"" + image + L"\" " + arguments; STARTUPINFOW startup{sizeof(startup)}; PROCESS_INFORMATION child{};
    if (!CreateProcessW(image.c_str(), &command[0], nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &startup, &child)) Fail(L"Cannot start owned inert instance fixture.");
    CloseHandle(child.hThread); return {Handle(child.hProcess), child.dwProcessId};
}
std::wstring Hash(const std::wstring& path) { Handle file(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr)); if (!file.valid()) Fail(L"Cannot read inert fixture."); return Hex(HashFile(file.get())); }
HWND Window(DWORD pid) {
    struct Search { DWORD pid; HWND value = nullptr; } search{pid};
    EnumWindows([](HWND window, LPARAM value) -> BOOL { auto& s = *reinterpret_cast<Search*>(value); DWORD id = 0; GetWindowThreadProcessId(window, &id); wchar_t title[64]{}; GetWindowTextW(window, title, 64); if (id == s.pid && wcscmp(title, L"Process Keeper") == 0) { s.value = window; return FALSE; } return TRUE; }, reinterpret_cast<LPARAM>(&search)); return search.value;
}
void WaitWindow(Child& child) { for (int i = 0; i < 100; ++i) { if (Window(child.pid)) return; if (WaitForSingleObject(child.process.get(), 20) == WAIT_OBJECT_0) break; } throw Failure(L"Owned fixture did not create a graphical window."); }
std::vector<std::wstring> Context(const std::wstring& root, const std::wstring& id, const std::wstring& image, Child& child) {
    auto directory = root + L"\\sessions\\" + id; Directory(directory);
    return { L"PKLC1", id, EncodeContextText(Image()), Hash(Image()), ProductVersion, EncodeContextText(image), Hash(image),
        EncodeContextText(image.substr(0, image.find_last_of(L'\\')) + L"\\ProcessKeeper.Updater.exe"), Hash(image),
        std::to_wstring(child.pid), std::to_wstring(ProcessCreated(child.process.get())), EncodeContextText(UserSid()), L"Windows7Compat" };
}
LRESULT CALLBACK Procedure(HWND window, UINT message, WPARAM wparam, LPARAM lparam) {
    if (message == WM_CLOSE && GetWindowLongPtrW(window, GWLP_USERDATA)) return 0;
    if (message == WM_TIMER || message == WM_CLOSE) { DestroyWindow(window); return 0; }
    if (message == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(window, message, wparam, lparam);
}
}
bool RunInstanceFixtureCommand(int argc, wchar_t** argv, int& result) {
    using namespace pk;
    if (argc == 4 && wcscmp(argv[1], L"--instance-window") == 0) {
        const auto duration = std::stoul(argv[2]); if (!duration || duration > 5000) throw Failure(L"Unbounded inert window lifetime.");
        WNDCLASSW type{}; type.lpfnWndProc = Procedure; type.hInstance = GetModuleHandleW(nullptr); type.lpszClassName = L"HwndWrapper[ProcessKeeper.exe;;owned-instance-fixture]";
        if (!RegisterClassW(&type)) Fail(L"Cannot register inert application window.");
        const auto window = CreateWindowW(type.lpszClassName, L"Process Keeper", WS_OVERLAPPEDWINDOW, 100, 100, 320, 240, nullptr, nullptr, type.hInstance, nullptr);
        if (!window) Fail(L"Cannot create inert application window."); SetWindowLongPtrW(window, GWLP_USERDATA, wcscmp(argv[3], L"refuse") == 0 ? 1 : 0); ShowWindow(window, SW_SHOWNOACTIVATE); SetTimer(window, 1, duration, nullptr);
        MSG message{}; while (GetMessageW(&message, nullptr, 0, 0) > 0) DispatchMessageW(&message); result = 0; return true;
    }
    if (argc == 5 && wcscmp(argv[1], L"--instance-gate") == 0) {
        ConfigureInstanceFixture(argv[2], argv[3]); InstanceLaunchGate gate;
        const auto marker = FullPath(argv[4]); const auto root = FullPath(argv[2]); if (marker.substr(0, root.size() + 1) != root + L"\\") throw Failure(L"Gate fixture escaped isolated cache.");
        Write(marker, {L"PKGATE1", std::to_wstring(GetCurrentProcessId())}); Sleep(300); result = 0; return true;
    }
    return false;
}
void RunInstanceTests(const std::function<void(bool, const wchar_t*)>& check) {
    using namespace pk;
    const std::wstring sourceFile = PK_WIDE_LITERAL(__FILE__);
    std::ifstream source(sourceFile.substr(0, sourceFile.find_last_of(L"\\/")) + L"\\Main.cpp", std::ios::binary);
    const std::string mainSource((std::istreambuf_iterator<char>(source)), std::istreambuf_iterator<char>());
    const auto worker = mainSource.find("app.worker = std::thread"), prepare = mainSource.find("pk::PreparePayload", worker);
    const auto election = mainSource.find("pk::ResolvePreferredInstance", worker), child = mainSource.find("CreateProcessW", worker);
    check(source.is_open() && worker != std::string::npos && prepare != std::string::npos && election != std::string::npos && child != std::string::npos &&
        prepare < election && election < child && mainSource.find("if (app.canceled)", prepare) < election,
        L"production candidate CAB payload and cancellation are verified before close authority or child creation");
    check(InstanceCandidateWinsFixture(L"1.8.0", Route::Legacy, 1, L"1.7.0", Route::ModernX64, 9), L"newer compatible release outranks old native UI");
    check(!InstanceCandidateWinsFixture(L"1.6.0", Route::ModernX64, 99, L"1.7.0", Route::Legacy, 1), L"older native UI cannot displace newer compatibility release");
    check(InstanceCandidateWinsFixture(L"v1.7.0", Route::ModernX64, 1, L"1.7.0+other", Route::Legacy, 99), L"semantic version equality still prefers eligible native UI");
    check(!InstanceCandidateWinsFixture(L"1.7.0+new", Route::Legacy, 99, L"1.7.0+old", Route::ModernX64, 1), L"build metadata does not reverse native UI preference");
    check(InstanceCandidateWinsFixture(L"1.7.0+new", Route::Legacy, 99, L"v1.7.0", Route::Legacy, 1), L"equivalent semantic versions apply newest request tie-break");
    const auto own = NewContextId(), other = NewContextId();
    check(ValidInstanceRedirectFixture({L"PKINSTANCE1", own, other}, own), L"loser redirect requires exact own and distinct opaque peer context");
    check(!ValidInstanceRedirectFixture({L"PKINSTANCE1", own, own}, own) && !ValidInstanceRedirectFixture({L"PKINSTANCE1", other, own}, own) && !ValidInstanceRedirectFixture({L"PKINSTANCE1", own, L".."}, own), L"self wrong-owner and path-like redirect receipts rejected");
    wchar_t temporary[MAX_PATH]{}; if (!GetTempPathW(MAX_PATH, temporary)) Fail(L"Instance fixtures require redirected TEMP.");
    const auto root = FullPath(std::wstring(temporary) + L"pk-instance-" + NewContextId()); Directory(root); Directory(root + L"\\sessions");
    const auto gate = NewContextId(); ConfigureInstanceFixture(root, gate);
    check(!ResolvePreferredInstance(Route::Legacy, ProcessCreated(GetCurrentProcess())), L"fresh private cache contains no competing app and never reads real ProgramData");
    const auto marker1 = root + L"\\gate-first.txt", marker2 = root + L"\\gate-second.txt";
    auto first = Start(Image(), L"--instance-gate \"" + root + L"\" " + gate + L" \"" + marker1 + L"\"");
    for (int i = 0; i < 100 && GetFileAttributesW(marker1.c_str()) == INVALID_FILE_ATTRIBUTES; ++i) Sleep(10);
    check(GetFileAttributesW(marker1.c_str()) != INVALID_FILE_ATTRIBUTES, L"first owned wrapper acquired private launch mutex");
    auto second = Start(Image(), L"--instance-gate \"" + root + L"\" " + gate + L" \"" + marker2 + L"\""); Sleep(50);
    check(GetFileAttributesW(marker2.c_str()) == INVALID_FILE_ATTRIBUTES, L"second owned wrapper cannot enter before first launch gate releases");
    check(WaitForSingleObject(first.process.get(), 3000) == WAIT_OBJECT_0 && WaitForSingleObject(second.process.get(), 3000) == WAIT_OBJECT_0 && GetFileAttributesW(marker2.c_str()) != INVALID_FILE_ATTRIBUTES, L"both private wrappers exit naturally after serialized acquisition");
    const auto base = root + L"\\" + std::wstring(64, L'a') + L"-legacy"; Directory(base); Directory(base + L"\\legacy");
    const auto image = base + L"\\legacy\\ProcessKeeper.exe", helper = base + L"\\legacy\\ProcessKeeper.Updater.exe";
    check(CopyFileW(Image().c_str(), image.c_str(), TRUE) && CopyFileW(Image().c_str(), helper.c_str(), TRUE), L"isolated peer uses exact copied inert EXE with real PE and VERSIONINFO");
    auto peer = Start(image, L"--instance-window 4000 accept"); WaitWindow(peer);
    const auto id = NewContextId(), directory = root + L"\\sessions\\" + id; auto fields = Context(root, id, image, peer); Write(directory + L"\\context.txt", fields);
    Write(directory + L"\\instance-request.txt", {L"PKREQUEST1", std::to_wstring(GetCurrentProcessId()), std::to_wstring(ProcessCreated(GetCurrentProcess()))});
    check(VerifyInstancePeerFixture(id), L"live peer matches exact path PID start SID session PE product version and hashes");
    check(ResolvePreferredInstance(Route::Unsupported, ProcessCreated(peer.process.get()) + 1) && WaitForSingleObject(peer.process.get(), 0) == WAIT_TIMEOUT, L"ineligible package can only foreground existing compatible UI and never displace it");
    fields[4] = L"1.8.0"; Write(directory + L"\\context.txt", fields); check(!VerifyInstancePeerFixture(id), L"receipt version cannot override actual executable VERSIONINFO"); fields[4] = ProductVersion;
    fields[4] = L"corrupt version"; Write(directory + L"\\context.txt", fields);
    check(ResolvePreferredInstance(Route::Legacy, ProcessCreated(peer.process.get()) + 1) && WaitForSingleObject(peer.process.get(), 0) == WAIT_TIMEOUT,
        L"malformed live receipt provides foreground-only retention and never closing authority"); fields[4] = ProductVersion;
    fields[12] = L"Windows10arm64"; Write(directory + L"\\context.txt", fields); check(!VerifyInstancePeerFixture(id), L"declared ARM flavor cannot authorize x86 legacy peer"); fields[12] = L"Windows7Compat";
    fields[9] = L"18446744073709551616"; Write(directory + L"\\context.txt", fields); check(!VerifyInstancePeerFixture(id), L"overflowing receipt PID rejected without wrapping"); fields[9] = std::to_wstring(peer.pid);
    Write(directory + L"\\context.txt", fields); Write(directory + L"\\instance-request.txt", {L"PKREQUEST1", L"4294967296", std::to_wstring(ProcessCreated(GetCurrentProcess()))});
    check(!VerifyInstancePeerFixture(id), L"overflowing request marker PID rejected"); Write(directory + L"\\instance-request.txt", {L"PKREQUEST1", std::to_wstring(GetCurrentProcessId()), std::to_wstring(ProcessCreated(GetCurrentProcess()))});
    const auto incomplete = root + L"\\sessions\\" + NewContextId(); Directory(incomplete); Write(incomplete + L"\\instance-request.txt", {L"PKREQUEST1", L"10", L"10"});
    check(ResolvePreferredInstance(Route::Legacy, 1) && WaitForSingleObject(peer.process.get(), 0) == WAIT_TIMEOUT, L"incomplete historical context does not block retaining verified higher-ranked live app");
    auto loser = Start(image, L"--instance-window 600 accept"); WaitWindow(loser);
    const auto loserId = NewContextId(), loserDir = root + L"\\sessions\\" + loserId; auto loserFields = Context(root, loserId, image, loser);
    Write(loserDir + L"\\context.txt", loserFields); Write(loserDir + L"\\instance-request.txt", {L"PKREQUEST1", std::to_wstring(GetCurrentProcessId()), std::to_wstring(ProcessCreated(GetCurrentProcess()) - 1)});
    Write(loserDir + L"\\instance-redirect.txt", {L"PKINSTANCE1", loserId, id});
    check(ConfirmInstanceRedirect(loserId, loser.pid, Route::Legacy, ProcessCreated(loser.process.get())), L"managed loser redirect uses original request ordering even when its child started later");
    check(!ConfirmInstanceRedirect(loserId, loser.pid, Route::Legacy, ProcessCreated(loser.process.get()) + 1), L"loser receipt must bind exact observed child creation time");
    Write(loserDir + L"\\instance-redirect.txt", {L"PKINSTANCE1", loserId, loserId});
    check(!ConfirmInstanceRedirect(loserId, loser.pid, Route::Legacy, ProcessCreated(loser.process.get())), L"native redirect confirmation never accepts self as retained winner");
    check(WaitForSingleObject(loser.process.get(), 2000) == WAIT_OBJECT_0, L"mixed election fixture loser exits naturally without terminating any app");
    check(!ResolvePreferredInstance(Route::Legacy, ProcessCreated(peer.process.get()) + 1) && WaitForSingleObject(peer.process.get(), 0) == WAIT_OBJECT_0, L"new request sends only owned WM_CLOSE and waits actual peer process exit before child launch");
    fields[4] = L"invalid version"; Write(directory + L"\\context.txt", fields);
    check(!VerifyInstancePeerFixture(id) && !ResolvePreferredInstance(Route::Legacy, 1), L"dead malformed historical receipt cannot prevent future launches");
    auto refused = Start(image, L"--instance-window 1800 refuse"); WaitWindow(refused);
    const auto refusedId = NewContextId(), refusedDir = root + L"\\sessions\\" + refusedId; auto refusedFields = Context(root, refusedId, image, refused); Write(refusedDir + L"\\context.txt", refusedFields);
    check(ResolvePreferredInstance(Route::Legacy, ProcessCreated(refused.process.get()) + 1) && WaitForSingleObject(refused.process.get(), 0) == WAIT_TIMEOUT, L"unresponsive owned UI is retained after bounded graceful close with no termination");
    check(WaitForSingleObject(refused.process.get(), 3000) == WAIT_OBJECT_0, L"refusing test window exits solely on its own bounded lifetime");
}
