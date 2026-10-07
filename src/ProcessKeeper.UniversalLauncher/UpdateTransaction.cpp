#include "UpdateTransaction.h"
#include "DesktopShortcut.h"
#include "CacheCleanup.h"
#include "InstalledRegistration.h"
#include <algorithm>
#include <array>
#include <sddl.h>
#include <fstream>

namespace pk { namespace {
bool WaitMarker(const std::wstring& directory, const std::wstring& fileName, const std::wstring& header, HANDLE caller, DWORD helperPid, int attempts,
    const std::function<std::vector<std::wstring>(const std::wstring&)>& read) {
    const auto path = directory + L"\\" + fileName;
    auto present = [&]() {
        if (GetFileAttributesW(path.c_str()) == INVALID_FILE_ATTRIBUTES) return false;
        const auto record = read(path);
        if (record.size() != 2 || record[0] != header || record[1] != std::to_wstring(helperPid)) throw Failure(L"Update handoff did not match this helper.");
        return true;
    };
    for (int attempt = 0; attempt < attempts; ++attempt) {
        if (present()) return true;
        const auto state = WaitForSingleObject(caller, 50);
        // The caller publishes execute atomically immediately before exiting. Recheck
        // after wakeup so that exit cannot hide a marker published during the wait.
        if (state == WAIT_OBJECT_0) return present();
        if (state != WAIT_TIMEOUT) throw Failure(L"Cannot observe the application handoff.");
    }
    return present();
}
Handle OpenExact(const std::wstring& path, DWORD access, bool waitForRelease = false) {
    for (unsigned attempt = 0;; ++attempt) {
        Handle result(CreateFileW(path.c_str(), access, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
        if (result.valid()) { VerifyHandlePath(result.get(), path, false); return result; }
        const auto error = GetLastError();
        if (!waitForRelease || attempt >= 40 || (error != ERROR_SHARING_VIOLATION && error != ERROR_LOCK_VIOLATION)) {
            SetLastError(error); Fail(L"Cannot lock the original application file.");
        }
        // A scanner or the exiting wrapper may briefly retain a handle. Never kill it.
        Sleep(250);
    }
}
void RequireHash(HANDLE file, const std::wstring& expected) { if (Hex(HashFile(file)) != expected) throw Failure(L"The verified application file changed. No unrelated file will be replaced."); }
void RenameExact(HANDLE file, const std::wstring& path) {
    // FileRenameInfo's HANDLE/alignment differs on x86 and x64; offsetof retains both layouts.
    const auto bytes = static_cast<DWORD>(path.size() * sizeof(wchar_t));
    std::vector<BYTE> buffer(offsetof(FILE_RENAME_INFO, FileName) + bytes + sizeof(wchar_t), 0);
    auto* rename = reinterpret_cast<FILE_RENAME_INFO*>(buffer.data()); rename->ReplaceIfExists = FALSE;
    rename->RootDirectory = nullptr; rename->FileNameLength = bytes; memcpy(rename->FileName, path.c_str(), bytes + sizeof(wchar_t));
    if (!SetFileInformationByHandle(file, FileRenameInfo, rename, static_cast<DWORD>(buffer.size()))) Fail(L"Cannot commit the update without overwriting an unexpected file.");
    VerifyHandlePath(file, path, false);
}
UpdateTransactionResult Replace(const std::wstring& original, const std::wstring& oldHash, const std::wstring& candidate,
    const std::wstring& newHash, const std::wstring& id, const std::function<UpdateLaunchResult(const std::wstring&)>& launch, bool fixture) {
    if (!ValidContextId(id)) throw Failure(L"Invalid update transaction identifier.");
    auto parents = LockParents(original, false); auto newParents = LockParents(candidate, !fixture);
    auto oldFile = OpenExact(original, GENERIC_READ | DELETE, !fixture); RequireHash(oldFile.get(), oldHash);
    auto newFile = fixture ? OpenExact(candidate, GENERIC_READ) : OpenProtectedFile(candidate); RequireHash(newFile.get(), newHash);
    const auto temporary = original + L".pk-new-" + id, backup = original + L".pk-backup-" + id + L".exe";
    Handle staged;
    if (fixture) staged = Handle(CreateFileW(temporary.c_str(), GENERIC_READ | GENERIC_WRITE | DELETE, FILE_SHARE_READ, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_WRITE_THROUGH, nullptr));
    else {
        // Only Administrators/SYSTEM may write the installed EXE. Users retain read/execute
        // so the next ordinary double-click can display the launcher's UAC prompt.
        PSECURITY_DESCRIPTOR descriptor = nullptr;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(L"O:BAG:BAD:P(A;;FA;;;BA)(A;;FA;;;SY)(A;;FRFX;;;BU)", SDDL_REVISION_1, &descriptor, nullptr)) Fail(L"Cannot create update permissions.");
        SECURITY_ATTRIBUTES attributes{sizeof(attributes), descriptor, FALSE};
        staged = Handle(CreateFileW(temporary.c_str(), GENERIC_READ | GENERIC_WRITE | DELETE, FILE_SHARE_READ, &attributes, CREATE_NEW, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_WRITE_THROUGH, nullptr));
        LocalFree(descriptor);
    }
    if (!staged.valid()) Fail(L"Cannot prepare the update beside the original EXE.");
    VerifyHandlePath(staged.get(), temporary, false); LARGE_INTEGER zero{}; SetFilePointerEx(newFile.get(), zero, nullptr, FILE_BEGIN);
    std::array<BYTE, 65536> bytes{};
    for (;;) { DWORD read = 0, written = 0; if (!ReadFile(newFile.get(), bytes.data(), static_cast<DWORD>(bytes.size()), &read, nullptr)) Fail(L"Cannot copy the verified update."); if (!read) break;
        if (!WriteFile(staged.get(), bytes.data(), read, &written, nullptr) || written != read) Fail(L"Cannot stage the verified update."); }
    if (!FlushFileBuffers(staged.get())) Fail(L"Cannot flush the verified update."); RequireHash(staged.get(), newHash);
    bool backedUp = false, installed = false;
    UpdateTransactionResult result; result.backup = backup;
    try {
        RequireHash(oldFile.get(), oldHash); RenameExact(oldFile.get(), backup); backedUp = true;
        RenameExact(staged.get(), original); installed = true; RequireHash(staged.get(), newHash);
        // A DELETE-capable handle cannot coexist with the wrapper's own no-delete read lock.
        // Reopen and verify again after this transition; never execute a swapped leaf.
        staged.reset(); auto runningFile = OpenExact(original, GENERIC_READ); RequireHash(runningFile.get(), newHash);
        const auto started = launch(original);
        if (started == UpdateLaunchResult::Success) {
            result.installed = true; result.confirmed = true; result.message = L"Update installed and a real application window was confirmed.";
            try {
                // This remains the original locked handle: never sweep nearby versioned EXEs.
                VerifyHandlePath(oldFile.get(), backup, false); RequireHash(oldFile.get(), oldHash);
                FILE_DISPOSITION_INFO disposal{TRUE};
                if (!SetFileInformationByHandle(oldFile.get(), FileDispositionInfo, &disposal, sizeof(disposal))) Fail(L"Cannot remove the verified old EXE backup.");
                oldFile.reset(); result.backup.clear();
            } catch (const Failure&) { result.message += L" The verified old EXE backup was retained: " + backup; }
            return result;
        }
        if (started == UpdateLaunchResult::Unconfirmed) { result.installed = true; result.message = L"Update written, but startup was not confirmed. No process was killed. The original EXE backup is retained: " + backup; return result; }
        runningFile.reset();
        throw Failure(L"The updated application failed before opening a window.");
    } catch (const Failure& failure) {
        try {
            staged.reset();
            if (installed) {
                auto failed = OpenExact(original, GENERIC_READ | DELETE); RequireHash(failed.get(), newHash);
                RenameExact(failed.get(), temporary + L".failed");
            }
            if (backedUp) { RequireHash(oldFile.get(), oldHash); RenameExact(oldFile.get(), original); result.restored = true; }
            result.message = failure.message + (result.restored ? L" The original EXE was restored. Please start it again." : L" The original EXE was not changed.");
        } catch (const Failure& restore) { result.message = failure.message + L" Automatic restoration could not complete: " + restore.message + L" Original backup: " + backup; }
        return result;
    }
}
UpdateLaunchResult LaunchUpdated(const std::wstring& path) {
    auto command = L"\"" + path + L"\""; auto environment = BuildChildEnvironment(); STARTUPINFOW start{sizeof(start)}; PROCESS_INFORMATION process{};
    const auto parent = path.substr(0, path.find_last_of(L'\\'));
    if (!CreateProcessW(path.c_str(), &command[0], nullptr, nullptr, FALSE, CREATE_UNICODE_ENVIRONMENT, environment.data(), parent.c_str(), &start, &process)) return UpdateLaunchResult::Failed;
    Handle child(process.hProcess), thread(process.hThread);
    if (WaitForSingleObject(child.get(), 180000) != WAIT_OBJECT_0) return UpdateLaunchResult::Unconfirmed;
    DWORD code = 0; if (!GetExitCodeProcess(child.get(), &code)) return UpdateLaunchResult::Unconfirmed;
    return code == 0 ? UpdateLaunchResult::Success : code == 1 ? UpdateLaunchResult::Failed : UpdateLaunchResult::Unconfirmed;
}
}
void ValidateUpdateJob(const std::vector<std::wstring>& job, const LaunchContext& context, const std::wstring& id) {
    // Consistency with the managed official-metadata authority, not a cryptographic signature.
    if (job.size() != 8 || job[0] != L"PKUP3" || job[1] != context.id || job[2] != id || !ValidContextId(id) ||
        !ValidSha256(job[3]) || !IsNewerUpdateVersion(job[4], context.version) || job[5] != L"KangQiovo/ProcessKeeper")
        throw Failure(L"Invalid prepared update job or fixed official repository.");
    if ((job[6] != L"Universal" && job[6] != L"Windows7Compat" && job[6] != L"Windows10x64" && job[6] != L"Windows10arm64") ||
        (job[7] != L"Portable" && job[7] != L"Installer") || job[7] == L"Installer" && job[6] == L"Universal")
        throw Failure(L"Invalid update package selection.");
}
UpdateTransactionResult InstallUpdate(const LaunchContext& context, const std::wstring& id) {
    if (!ValidContextId(id)) throw Failure(L"Invalid update job identifier.");
    const auto directory = context.directory + L"\\job-" + id;
    auto directoryParents = LockParents(directory + L"\\job.txt", true);
    const auto job = ReadProtectedLines(directory + L"\\job.txt");
    ValidateUpdateJob(job, context, id);
    auto caller = OpenContextProcess(context);
    auto sourceParents = LockParents(context.original, false);
    auto original = OpenExact(context.original, GENERIC_READ); RequireHash(original.get(), context.originalHash);
    const auto candidate = directory + L"\\update.exe";
    const auto selectedTarget = job[6] == L"Windows7Compat" ? PackageTarget::Windows7Compat : job[6] == L"Windows10x64" ? PackageTarget::Windows10x64 : job[6] == L"Windows10arm64" ? PackageTarget::Windows10arm64 : PackageTarget::Universal;
    const bool installer = job[7] == L"Installer";
    auto next = OpenProtectedFile(candidate); RequireHash(next.get(), job[3]);
    if (installer) ValidateUpdateInstaller(candidate, job[4], selectedTarget); else ValidateUpdateBundle(candidate, job[4], &selectedTarget);
    const auto mutexName = L"Local\\ProcessKeeper.Update." + context.originalHash;
    Handle mutex(CreateMutexW(nullptr, TRUE, mutexName.c_str()));
    if (!mutex.valid() || GetLastError() == ERROR_ALREADY_EXISTS) throw Failure(L"Another update is already pending.");
    WriteProtectedLines(directory + L"\\ready.txt", {L"PKREADY1", std::to_wstring(GetCurrentProcessId())});
    const auto committed = WaitMarker(directory, L"commit.txt", L"PKCOMMIT1", caller.get(), GetCurrentProcessId(), 400, ReadProtectedLines);
    if (!committed) throw Failure(L"Update was not committed. The original EXE was not changed.");
    WriteProtectedLines(directory + L"\\accepted.txt", {L"PKACCEPT1", std::to_wstring(GetCurrentProcessId())});
    const auto authorized = WaitMarker(directory, L"execute.txt", L"PKEXECUTE1", caller.get(), GetCurrentProcessId(), 200, ReadProtectedLines);
    if (!authorized) throw Failure(L"Update handoff was not completed. The original EXE was not changed.");
    if (WaitForSingleObject(caller.get(), 120000) != WAIT_OBJECT_0) throw Failure(L"The app did not exit. No process was killed and the original EXE was not changed.");
    if (installer) {
        RequireHash(original.get(), context.originalHash); original.reset();
        RequireHash(next.get(), job[3]); ValidateUpdateInstaller(candidate, job[4], selectedTarget);
        // Setup owns its interactive transaction. Removing only this helper's readiness marker
        // lets the existing setup guard see that the explicitly consenting UI has exited.
        if (!DeleteFileW((directory + L"\\ready.txt").c_str())) Fail(L"Cannot complete the interactive installer handoff.");
        auto command = L"\"" + candidate + L"\"";
        STARTUPINFOW startup{sizeof(startup)}; PROCESS_INFORMATION process{};
        auto environment = BuildChildEnvironment();
        if (!CreateProcessW(candidate.c_str(), &command[0], nullptr, nullptr, FALSE, CREATE_UNICODE_ENVIRONMENT, environment.data(), directory.c_str(), &startup, &process))
            Fail(L"The verified interactive installer could not start. The current EXE remains available.");
        Handle setupProcess(process.hProcess), setupThread(process.hThread);
        UpdateTransactionResult result; result.installerStarted = true;
        result.message = L"The verified interactive installer was opened. Follow its pages to complete or cancel installation; no portable EXE replacement was performed.";
        WriteProtectedLines(directory + L"\\status.txt", {L"PKSTATUS1", L"installer-started", EncodeContextText(result.message), L""});
        ReleaseMutex(mutex.get()); return result;
    }
    PayloadCleanupPlan cleanup;
    try { cleanup = CapturePayloadCleanup(context); } catch (const Failure&) { /* Updating may proceed while unverifiable old cache is retained. */ }
    RequireHash(original.get(), context.originalHash); original.reset(); next.reset();
    auto result = Replace(context.original, context.originalHash, candidate, job[3], id, LaunchUpdated, false);
    if (result.installed && result.confirmed) {
        try {
            const auto registration = RefreshInstalledRegistration(context.original, job[3], job[4], selectedTarget);
            if (registration == L"conflict") result.message += L" An unrelated or unverified installed-app registration was preserved.";
        } catch (const Failure&) { result.message += L" The installed EXE is valid, but its installed-app version could not be refreshed."; }
        try {
            if (!cleanup.receipt.empty()) { SchedulePayloadCleanup(cleanup); result.message += L" Its exact old payload cache is scheduled for verification and cleanup on a later launch after the old helper exits."; }
            else result.message += L" The old payload cache could not be verified and was retained.";
        } catch (const Failure&) { result.message += L" The old payload cache was retained because cleanup could not be scheduled."; }
        try {
            const auto shortcut = RefreshDesktopShortcut(context.original);
            if (shortcut == L"conflict") result.message += L" An unrelated desktop shortcut was preserved.";
        } catch (const Failure&) { result.message += L" The installed EXE is valid, but its owned desktop shortcut could not be refreshed."; }
    }
    try {
        auto completed = OpenExact(candidate, GENERIC_READ | READ_CONTROL | DELETE); VerifySecurity(completed.get(), false, true); RequireHash(completed.get(), job[3]);
        FILE_DISPOSITION_INFO disposal{TRUE};
        if (!SetFileInformationByHandle(completed.get(), FileDispositionInfo, &disposal, sizeof(disposal))) Fail(L"Cannot remove the completed update staging file.");
    } catch (const Failure&) { result.message += L" The protected staging copy was retained for manual inspection."; }
    WriteProtectedLines(directory + L"\\status.txt", {L"PKSTATUS1", result.installed ? L"installed" : result.restored ? L"restored" : L"failed", EncodeContextText(result.message), EncodeContextText(result.backup)});
    ReleaseMutex(mutex.get()); return result;
}
#ifdef PK_FIXTURE_BUILD
bool WaitUpdateMarkerFixture(const std::wstring& directory, HANDLE caller, DWORD helperPid, int attempts) {
    return WaitMarker(directory, L"execute.txt", L"PKEXECUTE1", caller, helperPid, attempts, [](const std::wstring& path) {
        std::ifstream input(path, std::ios::binary); std::string line; std::vector<std::wstring> lines;
        while (std::getline(input, line)) lines.emplace_back(line.begin(), line.end()); return lines;
    });
}
UpdateTransactionResult ReplaceUpdateFixture(const std::wstring& original, const std::wstring& oldHash, const std::wstring& candidate,
    const std::wstring& newHash, const std::wstring& id, const std::function<UpdateLaunchResult(const std::wstring&)>& launch) {
    return Replace(original, oldHash, candidate, newHash, id, launch, true);
}
#endif
}
