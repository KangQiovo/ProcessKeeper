#include "UpdateTransaction.h"
#include "DesktopShortcut.h"
#include <objbase.h>
#include <fstream>
#include "HelperText.h"

namespace {
std::wstring DigestFile(const std::wstring& path) {
    pk::Handle file(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    if (!file.valid()) pk::Fail(L"Cannot read a fixture file."); return pk::Hex(pk::HashFile(file.get()));
}
void Bytes(const std::wstring& path, const char* text) { std::ofstream file(path, std::ios::binary); file << text; file.flush(); if (!file.good()) throw pk::Failure(L"Cannot write a fixture file."); }
}
void RunUpdateTests(const std::function<void(bool, const wchar_t*)>& check) {
    using namespace pk;
    auto rejects = [](const std::function<void()>& action) { try { action(); return false; } catch (...) { return true; } };
    wchar_t priorLanguage[32]{}; const auto priorLength = GetEnvironmentVariableW(L"PROCESSKEEPER_HELPER_LANGUAGE", priorLanguage, 32);
    for (const auto* language : {L"en", L"zh-Hans", L"zh-Hant"}) {
        SetEnvironmentVariableW(L"PROCESSKEEPER_HELPER_LANGUAGE", language);
        check(HelperText(L"en", L"zh-Hans", L"zh-Hant") == language, L"native helper primary messages follow each explicitly selected UI language");
    }
    SetEnvironmentVariableW(L"PROCESSKEEPER_HELPER_LANGUAGE", priorLength && priorLength < 32 ? priorLanguage : nullptr);
    check(ValidContextId(NewContextId()) && !ValidContextId(L"..\\other") && !ValidContextId(std::wstring(32, L'G')), L"launch context accepts only an opaque exact identifier");
    check(ValidSha256(std::wstring(64, L'a')) && !ValidSha256(std::wstring(64, L'z')), L"update digest must be exact lowercase SHA-256");
    check(DecodeContextText(EncodeContextText(L"E:\\软件\\Process Keeper.exe")) == L"E:\\软件\\Process Keeper.exe" && DecodeContextText(L"").empty(), L"launch context preserves non-ASCII original paths and empty status fields");
    check(rejects([] { DecodeContextText(L"0000"); }) && rejects([] { DecodeContextText(L"xyz1"); }), L"launch context rejects NUL and malformed encoding");
    check(IsNewerUpdateVersion(L"v1.5.1-beta.2", L"1.5.0") && IsNewerUpdateVersion(L"1.5.1-beta.10", L"1.5.1-beta.2"), L"native update comparison accepts genuine newer prereleases numerically");
    check(IsNewerUpdateVersion(L"1.5.1", L"1.5.1-rc.1") && !IsNewerUpdateVersion(L"1.5.1-rc.2", L"1.5.1"), L"stable update outranks its prereleases");
    check(!IsNewerUpdateVersion(L"1.5.0+new", L"1.5.0+old") && !IsNewerUpdateVersion(L"1.4.9", L"1.5.0"), L"build metadata and older releases cannot bypass downgrade prevention");
    for (const auto* invalid : {L"1.5", L"1.5.1.2", L"01.5.1", L"1.5.1-beta.01", L"65536.1.1", L"1.5.1+", L"1.5.1-"})
        check(rejects([&] { IsNewerUpdateVersion(invalid, L"1.5.0"); }), L"native version parser rejects malformed or unrepresentable PE versions");
    auto environment = LaunchEnvironment(std::wstring(32, L'a')); size_t contexts = 0;
    for (const auto* value = environment.data(); *value; value += wcslen(value) + 1) if (std::wstring(value).rfind(L"PROCESSKEEPER_LAUNCH_CONTEXT=", 0) == 0) ++contexts;
    check(contexts == 1 && environment[environment.size() - 2] == 0, L"child receives one opaque context and a terminated sanitized environment");
    SourceLock self; self.Verify();
    const auto session = NewContextId();
    std::vector<std::wstring> fields = {L"PKLC1", session, EncodeContextText(self.path()), self.sha256(), ProductVersion,
        EncodeContextText(self.path()), self.sha256(), EncodeContextText(self.path()), self.sha256(), std::to_wstring(GetCurrentProcessId()),
        std::to_wstring(ProcessCreated(GetCurrentProcess())), EncodeContextText(UserSid())};
    const auto context = ParseLaunchContext(fields, session, self.path().substr(0, self.path().find_last_of(L'\\')));
    const auto jobId = NewContextId();
    const std::wstring currentVersion = ProductVersion;
    const auto patchOffset = currentVersion.find_last_of(L'.') + 1;
    const auto nextVersion = currentVersion.substr(0, patchOffset) +
        std::to_wstring(std::stoul(currentVersion.substr(patchOffset)) + 1) + L"-beta.1";
    check(IsNewerUpdateVersion(nextVersion, currentVersion), L"update fixture targets a version newer than the current build");
    const std::vector<std::wstring> job = {L"PKUP2", session, jobId, std::wstring(64, L'a'), nextVersion, L"KangQiovo/ProcessKeeper"};
    check(!rejects([&] { ValidateUpdateJob(job, context, jobId); }), L"update job binds the fixed official repository across managed and native boundaries");
    auto wrongJob = job; wrongJob[5] = L"fork/ProcessKeeper";
    check(rejects([&] { ValidateUpdateJob(wrongJob, context, jobId); }), L"native job from another repository is refused");
    wrongJob = job; wrongJob[0] = L"PKUP1"; wrongJob.pop_back();
    check(rejects([&] { ValidateUpdateJob(wrongJob, context, jobId); }), L"older unbound update job format is refused");
    wrongJob = job; wrongJob[4] = ProductVersion;
    check(rejects([&] { ValidateUpdateJob(wrongJob, context, jobId); }), L"repository binding never permits same-version replacement");
    auto caller = OpenContextProcessFixture(context);
    check(caller.valid() && context.original == self.path() && context.originalHash == self.sha256(), L"real source-locked wrapper identity round-trips through the production context parser");
    auto stale = context; ++stale.created;
    check(rejects([&] { OpenContextProcessFixture(stale); }), L"reused PID with a different creation time cannot authorize updates");
    stale = context; stale.payloadHash = std::wstring(64, L'0');
    check(rejects([&] { OpenContextProcessFixture(stale); }), L"changed managed payload identity cannot authorize updates");
    auto malformed = fields; malformed[11] = EncodeContextText(L"S-1-5-18");
    check(rejects([&] { ParseLaunchContext(malformed, session, L"unused"); }), L"launch record for a different account is rejected");
    malformed = fields; malformed[9] += L"suffix";
    check(rejects([&] { ParseLaunchContext(malformed, session, L"unused"); }), L"partial numeric process identifiers are rejected");
    const auto base = self.path().substr(0, self.path().find_last_of(L'\\')) + L"\\update-fixture-" + NewContextId();
    check(CreateDirectoryW(base.c_str(), nullptr) != FALSE, L"update fixture creates only a new isolated workspace directory");
    auto path = [&](const wchar_t* name) { return base + L"\\" + name; };
    for (int run = 0; run < 5; ++run) {
        const auto directory = base + L"\\handoff-" + std::to_wstring(run); CreateDirectoryW(directory.c_str(), nullptr);
        auto command = L"\"" + self.path() + L"\" --fixture-marker \"" + directory + L"\" " + std::to_wstring(GetCurrentProcessId());
        STARTUPINFOW startup{sizeof(startup)}; PROCESS_INFORMATION child{};
        if (!CreateProcessW(self.path().c_str(), &command[0], nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &startup, &child)) Fail(L"Cannot start the isolated marker fixture.");
        Handle childProcess(child.hProcess), childThread(child.hThread);
        check(WaitUpdateMarkerFixture(directory, childProcess.get(), GetCurrentProcessId(), 100), L"atomic execute published during caller-exit wait is observed after exit");
        check(WaitForSingleObject(childProcess.get(), 5000) == WAIT_OBJECT_0, L"owned handoff fixture exits normally without termination");
    }
    const auto missing = path(L"missing-marker"); CreateDirectoryW(missing.c_str(), nullptr);
    check(!WaitUpdateMarkerFixture(missing, GetCurrentProcess(), GetCurrentProcessId(), 1), L"missing final execute marker times out while caller stays alive");
    Bytes(missing + L"\\execute.txt", "PKEXECUTE1\n0\n");
    check(rejects([&] { WaitUpdateMarkerFixture(missing, GetCurrentProcess(), GetCurrentProcessId(), 1); }), L"a final execute marker for another helper cannot authorize installation");
    Bytes(path(L"old.exe"), "old original"); Bytes(path(L"next.exe"), "new candidate");
    const auto oldHash = DigestFile(path(L"old.exe")), newHash = DigestFile(path(L"next.exe"));
    bool launched = false;
    auto result = ReplaceUpdateFixture(path(L"old.exe"), oldHash, path(L"next.exe"), newHash, NewContextId(), [&](const std::wstring& file) {
        launched = DigestFile(file) == newHash;
        check(!MoveFileW(base.c_str(), (base + L"-moved").c_str()), L"transaction locks every destination parent against rename");
        Handle writer(CreateFileW(file.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr));
        check(!writer.valid(), L"new original is read locked across verified launch");
        return UpdateLaunchResult::Success;
    });
    check(result.installed && launched && DigestFile(path(L"old.exe")) == newHash && DigestFile(result.backup) == oldHash, L"successful transaction replaces exact original and retains byte-identical backup");
    Bytes(path(L"rollback.exe"), "old original");
    result = ReplaceUpdateFixture(path(L"rollback.exe"), oldHash, path(L"next.exe"), newHash, NewContextId(), [](const std::wstring&) { return UpdateLaunchResult::Failed; });
    check(!result.installed && result.restored && DigestFile(path(L"rollback.exe")) == oldHash, L"startup failure restores the original byte-for-byte");
    Bytes(path(L"unconfirmed.exe"), "old original");
    result = ReplaceUpdateFixture(path(L"unconfirmed.exe"), oldHash, path(L"next.exe"), newHash, NewContextId(), [](const std::wstring&) { return UpdateLaunchResult::Unconfirmed; });
    check(result.installed && !result.restored && DigestFile(result.backup) == oldHash && result.message.find(L"not confirmed") != std::wstring::npos, L"unconfirmed live startup preserves backup and never claims a confirmed window");
    Bytes(path(L"changed.exe"), "changed user file"); launched = false;
    check(rejects([&] { ReplaceUpdateFixture(path(L"changed.exe"), oldHash, path(L"next.exe"), newHash, NewContextId(), [&](const std::wstring&) { launched = true; return UpdateLaunchResult::Success; }); }) && !launched,
        L"changed original hash rejects replacement before any launch");
    Bytes(path(L"corrupt.exe"), "old original");
    check(rejects([&] { ReplaceUpdateFixture(path(L"corrupt.exe"), oldHash, path(L"next.exe"), std::wstring(64, L'0'), NewContextId(), [](const std::wstring&) { return UpdateLaunchResult::Success; }); }) && DigestFile(path(L"corrupt.exe")) == oldHash,
        L"changed downloaded hash cannot alter original");
    Bytes(path(L"conflict.exe"), "old original"); const auto conflictId = NewContextId();
    Bytes(path(L"conflict.exe") + L".pk-backup-" + conflictId + L".exe", "unrelated backup");
    result = ReplaceUpdateFixture(path(L"conflict.exe"), oldHash, path(L"next.exe"), newHash, conflictId, [](const std::wstring&) { return UpdateLaunchResult::Success; });
    check(!result.installed && DigestFile(path(L"conflict.exe")) == oldHash, L"existing unrelated backup is never overwritten");
    check(CopyFileW(self.path().c_str(), path(L"binary-old.exe").c_str(), TRUE) && CopyFileW(self.path().c_str(), path(L"binary-next.exe").c_str(), TRUE), L"transaction executable fixtures are private copies of this inert test binary");
    { std::ofstream next(path(L"binary-next.exe"), std::ios::binary | std::ios::app); next << "independent candidate fixture"; }
    const auto binaryOld = DigestFile(path(L"binary-old.exe")), binaryNew = DigestFile(path(L"binary-next.exe"));
    auto launch = [](const std::wstring& file, int exitCode) {
        auto command = L"\"" + file + L"\" --fixture-child 5 " + std::to_wstring(exitCode);
        STARTUPINFOW startup{sizeof(startup)}; PROCESS_INFORMATION child{};
        if (!CreateProcessW(file.c_str(), &command[0], nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &startup, &child)) return UpdateLaunchResult::Failed;
        Handle process(child.hProcess), thread(child.hThread); if (WaitForSingleObject(process.get(), 5000) != WAIT_OBJECT_0) return UpdateLaunchResult::Unconfirmed;
        DWORD code = 100; if (!GetExitCodeProcess(process.get(), &code)) return UpdateLaunchResult::Unconfirmed;
        return code == 0 ? UpdateLaunchResult::Success : UpdateLaunchResult::Failed;
    };
    result = ReplaceUpdateFixture(path(L"binary-old.exe"), binaryOld, path(L"binary-next.exe"), binaryNew, NewContextId(), [&](const std::wstring& file) { return launch(file, 0); });
    check(result.installed && binaryOld != binaryNew && DigestFile(result.backup) == binaryOld, L"actual isolated no-op candidate process launches after exact binary replacement");
    check(CopyFileW(self.path().c_str(), path(L"binary-rollback.exe").c_str(), TRUE) != FALSE, L"failed-start fixture creates an independent original binary");
    result = ReplaceUpdateFixture(path(L"binary-rollback.exe"), binaryOld, path(L"binary-next.exe"), binaryNew, NewContextId(), [&](const std::wstring& file) { return launch(file, 1); });
    check(result.restored && !result.installed && DigestFile(path(L"binary-rollback.exe")) == binaryOld, L"actual failed no-op candidate exit triggers byte-exact original binary rollback");
    const auto apartment = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(apartment)) throw Failure(L"Fixture COM initialization failed.");
    const auto desktop = path(L"fixture-desktop"); CreateDirectoryW(desktop.c_str(), nullptr);
    check(EnsureShortcutFixture(path(L"old.exe"), desktop) == L"created", L"shortcut fixture creates only an isolated desktop link");
    const auto shortcutHash = DigestFile(desktop + L"\\Process Keeper.lnk");
    check(EnsureShortcutFixture(path(L"old.exe"), desktop) == L"exists" && DigestFile(desktop + L"\\Process Keeper.lnk") == shortcutHash, L"identical original-target shortcut is retained unchanged");
    check(EnsureShortcutFixture(path(L"next.exe"), desktop) == L"conflict" && DigestFile(desktop + L"\\Process Keeper.lnk") == shortcutHash, L"unrelated same-name shortcut is never overwritten");
    CoUninitialize();
}
