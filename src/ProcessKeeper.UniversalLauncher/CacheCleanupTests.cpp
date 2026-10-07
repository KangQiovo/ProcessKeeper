#include "CacheCleanup.h"
#include <fstream>
#include <functional>

namespace {
void Write(const std::wstring& path, const std::string& bytes) {
    std::ofstream file(path, std::ios::binary); file << bytes; file.flush();
    if (!file.good()) throw pk::Failure(L"Cannot write cache cleanup fixture.");
}
void Lines(const std::wstring& path, const std::vector<std::wstring>& values) {
    std::string text; for (const auto& value : values) { for (auto c : value) text += static_cast<char>(c); text += '\n'; } Write(path, text);
}
bool Present(const std::wstring& path) { return GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES; }
}
void RunCacheCleanupTests(const std::function<void(bool, const wchar_t*)>& check) {
    using namespace pk;
    wchar_t temp[32768]{}; if (!GetTempPathW(32768, temp)) Fail(L"Cache fixture requires explicit task temp.");
    const auto root = FullPath(std::wstring(temp) + L"cache171-" + NewContextId().substr(0, 8));
    check(root.size() < 190 && root.rfind(FullPath(temp), 0) == 0, L"cache fixture stays within configured task temp");
    CreateDirectoryW(root.c_str(), nullptr); CreateDirectoryW((root + L"\\sessions").c_str(), nullptr);
    SourceLock self;
    const auto current = NewContextId(), stem = L"ProcessKeeper-" + NewContextId();
    for (int scenario = 0; scenario < 6; ++scenario) {
        const auto id = scenario == 0 ? current : NewContextId();
        const auto session = root + L"\\sessions\\" + id, stage = session + L"\\download-" + NewContextId();
        CreateDirectoryW(session.c_str(), nullptr); CreateDirectoryW(stage.c_str(), nullptr);
        auto fields = std::vector<std::wstring>{L"PKLC1", id, EncodeContextText(self.path()), self.sha256(), L"1.6.0",
            EncodeContextText(self.path()), self.sha256(), EncodeContextText(self.path()), self.sha256(),
            std::to_wstring(scenario == 2 ? GetCurrentProcessId() : 4294967294UL),
            std::to_wstring(ProcessCreated(GetCurrentProcess())), EncodeContextText(UserSid()), L"Universal"};
        if (scenario == 5) fields[0] = L"INVALID";
        Lines(session + L"\\context.txt", fields);
        const auto complete = stage + L"\\" + stem + L".exe", partial = stage + L"\\" + stem + L".part";
        Write(complete, "wrong edition package"); Write(partial, "interrupted download");
        Handle busy;
        if (scenario == 3) busy = Handle(CreateFileW(partial.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr));
        const auto extra = stage + L"\\unrelated.txt";
        if (scenario == 4) Write(extra, "preserve unrecognized file");
        const auto result = ClearDownloadCacheFixture(root, current);
        if (scenario == 0 || scenario == 1) {
            check(!Present(stage) && result.removedFiles >= 2 && result.removedBytes >= 41,
                L"manual cache cleanup removes completed wrong-edition and interrupted application downloads");
        } else {
            check(Present(complete) && Present(partial) && result.retained > 0,
                L"live sessions, locked files, unknown contents and invalid contexts are retained before deletion");
            busy.reset(); if (scenario == 4) DeleteFileW(extra.c_str());
            if (scenario == 5) { fields[0] = L"PKLC1"; Lines(session + L"\\context.txt", fields); }
            if (scenario == 2) { fields[9] = L"4294967294"; Lines(session + L"\\context.txt", fields); }
            ClearDownloadCacheFixture(root, current);
            check(!Present(stage), L"previously retained download can be cleaned once its precise obstacle is gone");
        }
        DeleteFileW((session + L"\\context.txt").c_str()); RemoveDirectoryW(session.c_str());
    }
    const auto session = root + L"\\sessions\\" + current;
    CreateDirectoryW(session.c_str(), nullptr);
    Lines(session + L"\\context.txt", {L"PKLC1", current, EncodeContextText(self.path()), self.sha256(), L"1.7.0",
        EncodeContextText(self.path()), self.sha256(), EncodeContextText(self.path()), self.sha256(), L"4294967294",
        std::to_wstring(ProcessCreated(GetCurrentProcess())), EncodeContextText(UserSid()), L"Universal"});
    const auto job = session + L"\\job-" + NewContextId(); CreateDirectoryW(job.c_str(), nullptr);
    Write(job + L"\\update.exe", "prepared other-edition update"); Write(job + L"\\job.txt", "PKUP3\nowned preparation\n");
    Write(job + L"\\execute.pending", "interrupted consent publication");
    const auto prepared = ClearDownloadCacheFixture(root, current);
    check(!Present(job) && prepared.removedFiles == 3, L"cache cleanup removes inactive prepared update package and interrupted handoff artifacts");
    const auto runningStage = session + L"\\download-" + NewContextId(); CreateDirectoryW(runningStage.c_str(), nullptr);
    const auto runningImage = runningStage + L"\\ProcessKeeper-" + NewContextId() + L".exe";
    check(CopyFileW(self.path().c_str(), runningImage.c_str(), TRUE) != FALSE, L"running-cache fixture uses an owned no-op native image");
    std::wstring command = L"\"" + runningImage + L"\" --fixture-child 2000 0"; std::vector<wchar_t> mutableCommand(command.begin(), command.end()); mutableCommand.push_back(0);
    STARTUPINFOW startup{sizeof(startup)}; PROCESS_INFORMATION child{};
    check(CreateProcessW(runningImage.c_str(), mutableCommand.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, root.c_str(), &startup, &child) != FALSE,
        L"cache fixture starts only its copied no-op child");
    Handle childProcess(child.hProcess), childThread(child.hThread);
    const auto active = ClearDownloadCacheFixture(root, current);
    check(Present(runningImage) && Present(runningStage) && active.retained > 0, L"running update image survives cleanup even when its original UI exited");
    check(WaitForSingleObject(childProcess.get(), 5000) == WAIT_OBJECT_0, L"cache fixture child exits naturally without termination");
    childProcess.reset(); childThread.reset();
    ClearDownloadCacheFixture(root, current);
    check(!Present(runningStage), L"formerly running cached image is cleaned only after process exit");
    DeleteFileW((session + L"\\context.txt").c_str()); RemoveDirectoryW(session.c_str());
    const auto unrelated = root + L"\\user-download.exe"; Write(unrelated, "keep explicit user file");
    ClearDownloadCacheFixture(root, current);
    check(Present(unrelated), L"cache cleanup never scans or deletes arbitrary user downloads or sibling files");
    DeleteFileW(unrelated.c_str()); RemoveDirectoryW((root + L"\\sessions").c_str()); RemoveDirectoryW(root.c_str());
}
