#include "UpdateTransaction.h"
#include "DesktopShortcut.h"
#include "CacheCleanup.h"
#include "InstalledRegistration.h"
#include "SetupGuard.h"
#include <objbase.h>
#include <shlobj.h>
#include <fstream>
#include "HelperText.h"

namespace {
std::wstring DigestFile(const std::wstring& path) {
    pk::Handle file(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    if (!file.valid()) pk::Fail(L"Cannot read a fixture file."); return pk::Hex(pk::HashFile(file.get()));
}
void Bytes(const std::wstring& path, const char* text) { std::ofstream file(path, std::ios::binary); file << text; file.flush(); if (!file.good()) throw pk::Failure(L"Cannot write a fixture file."); }
std::wstring ShortcutIcon(const std::wstring& path, const wchar_t* replacement = nullptr) {
    IShellLinkW* link = nullptr; IPersistFile* persisted = nullptr;
    if (FAILED(CoCreateInstance(CLSID_ShellLink, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&link)))) throw pk::Failure(L"Cannot open a shortcut fixture.");
    try {
        if (FAILED(link->QueryInterface(IID_PPV_ARGS(&persisted))) || FAILED(persisted->Load(path.c_str(), STGM_READ))) throw pk::Failure(L"Cannot read a shortcut fixture.");
        wchar_t icon[32768]{}; int index = 0;
        if (replacement && (FAILED(link->SetIconLocation(replacement, 0)) || FAILED(persisted->Save(path.c_str(), TRUE)))) throw pk::Failure(L"Cannot set a fixture icon.");
        if (FAILED(link->GetIconLocation(icon, 32768, &index))) throw pk::Failure(L"Cannot inspect a fixture icon.");
        auto result = std::wstring(icon); persisted->Release(); link->Release(); return result;
    } catch (...) { if (persisted) persisted->Release(); link->Release(); throw; }
}
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
    auto flavored = fields; flavored.push_back(L"Windows10x64");
    check(ParseLaunchContext(flavored, session, context.directory).target == PackageTarget::Windows10x64 && context.target == PackageTarget::Universal,
        L"trusted contexts bind new package flavor while old context records remain Universal");
    flavored.back() = L"Unknown";
    check(rejects([&] { ParseLaunchContext(flavored, session, context.directory); }), L"unrecognized trusted package flavor cannot select an update");
    const auto jobId = NewContextId();
    const std::wstring currentVersion = ProductVersion;
    const auto patchOffset = currentVersion.find_last_of(L'.') + 1;
    const auto nextVersion = currentVersion.substr(0, patchOffset) +
        std::to_wstring(std::stoul(currentVersion.substr(patchOffset)) + 1) + L"-beta.1";
    check(IsNewerUpdateVersion(nextVersion, currentVersion), L"update fixture targets a version newer than the current build");
    const std::vector<std::wstring> job = {L"PKUP3", session, jobId, std::wstring(64, L'a'), nextVersion, L"KangQiovo/ProcessKeeper", L"Universal", L"Portable"};
    check(!rejects([&] { ValidateUpdateJob(job, context, jobId); }), L"update job binds the fixed official repository across managed and native boundaries");
    auto installerJob = job; installerJob[6] = L"Windows10x64"; installerJob[7] = L"Installer";
    check(!rejects([&] { ValidateUpdateJob(installerJob, context, jobId); }), L"explicit installer selection has a distinct validated transaction kind");
    auto changedJob = installerJob; changedJob[6] = L"Universal";
    check(rejects([&] { ValidateUpdateJob(changedJob, context, jobId); }), L"unsupported Universal installer cannot authorize a transaction");
    changedJob = installerJob; changedJob[7] = L"SilentInstall";
    check(rejects([&] { ValidateUpdateJob(changedJob, context, jobId); }), L"arbitrary distribution actions cannot authorize setup execution");
    auto distributionContext = fields; distributionContext.push_back(L"Windows10x64"); distributionContext.push_back(L"Installer");
    check(!rejects([&] { ParseLaunchContext(distributionContext, session, context.directory); }), L"trusted distribution metadata retains valid current session identity");
    distributionContext.back() = L"InstalledFromName";
    check(rejects([&] { ParseLaunchContext(distributionContext, session, context.directory); }), L"unrecognized distribution hints cannot impersonate trusted installed context");
    check(rejects([&] { ValidateUpdateInstaller(self.path(), ProductVersion, PackageTarget::Windows10x64); }), L"ordinary portable or fixture EXE cannot masquerade as the official installer");
    wchar_t installerFixtures[32768]{};
    const auto installerFixtureLength = GetEnvironmentVariableW(L"PROCESSKEEPER_INSTALLER_FIXTURE_DIRECTORY", installerFixtures, 32768);
    if (installerFixtureLength && installerFixtureLength < 32768) {
        const auto fixtureRoot = FullPath(installerFixtures);
        for (const auto target : {PackageTarget::Windows7Compat, PackageTarget::Windows10x64, PackageTarget::Windows10arm64}) {
            const auto name = target == PackageTarget::Windows7Compat ? L"Windows7Compat" : target == PackageTarget::Windows10x64 ? L"Windows10x64" : L"Windows10arm64";
            const auto file = fixtureRoot + L"\\" + name + L"-inert-171.exe";
            check(!rejects([&] { ValidateUpdateInstallerFixture(file, ProductVersion, target); }), L"real NSIS installer metadata validates as data without executing setup");
            check(rejects([&] { ValidateUpdateInstallerFixture(file, L"1.7.2", target); }), L"installer VERSIONINFO cannot authorize another release version");
            const auto otherTarget = target == PackageTarget::Windows10x64 ? PackageTarget::Windows7Compat : PackageTarget::Windows10x64;
            check(rejects([&] { ValidateUpdateInstallerFixture(file, ProductVersion, otherTarget); }) &&
                rejects([&] { ValidateUpdateInstallerFixture(file, ProductVersion, PackageTarget::Universal); }), L"installer resource identity rejects another target or invented Universal setup");
            check(rejects([&] { ValidateUpdateBundleFixture(file, ProductVersion); }), L"installer bytes cannot enter the portable replacement transaction");
        }
    }
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
    wchar_t fixtureTemp[MAX_PATH]{};
    const auto tempLength = GetTempPathW(MAX_PATH, fixtureTemp);
    if (!tempLength || tempLength >= MAX_PATH) Fail(L"Cannot locate the explicitly routed update fixture TEMP.");
    // Packaging nests the native build beneath a unique work directory. Keep fixtures
    // in the explicitly routed short TEMP so their Win7 paths stay below MAX_PATH.
    const auto tempRoot = FullPath(std::wstring(fixtureTemp));
    const auto base = FullPath(tempRoot + L"update-fixture-" + NewContextId());
    if (base.rfind(tempRoot, 0) != 0 || base.size() + 128 >= MAX_PATH)
        throw Failure(L"Update fixtures require an explicitly routed shorter TEMP within Win7 path limits.");
    check(CreateDirectoryW(base.c_str(), nullptr) != FALSE, L"update fixture creates only a new isolated workspace directory");
    auto path = [&](const wchar_t* name) { return base + L"\\" + name; };
    const auto packageFixture = path(L"owned-bundle.exe");
    check(CopyFileW(self.path().c_str(), packageFixture.c_str(), TRUE) != FALSE, L"installed migration fixture copies only the inert test image");
    {
        std::fstream image(packageFixture, std::ios::in | std::ios::out | std::ios::binary); IMAGE_DOS_HEADER dos{};
        image.read(reinterpret_cast<char*>(&dos), sizeof(dos));
        image.seekp(dos.e_lfanew + offsetof(IMAGE_NT_HEADERS32, OptionalHeader) + offsetof(IMAGE_OPTIONAL_HEADER32, Subsystem));
        const WORD subsystem = IMAGE_SUBSYSTEM_WINDOWS_GUI; image.write(reinterpret_cast<const char*>(&subsystem), sizeof(subsystem));
        if (!image.good()) throw Failure(L"Cannot set an inert data-only package fixture subsystem.");
    }
    const std::string archive = "inert archive fixture: never extracted or executed";
    auto archiveHash = Hex(HashBytes(archive.data(), archive.size()));
    std::string archiveHex; for (auto c : archiveHash) archiveHex += static_cast<char>(c);
    auto manifestText = std::string("PK14\t") + archiveHex + "\n";
    for (const auto* entry : {"modern/ProcessKeeper.exe", "modern/ProcessKeeper.Updater.exe", "legacy/ProcessKeeper.exe", "legacy/ProcessKeeper.Updater.exe", "modern/arm64/ProcessKeeper.exe", "modern/arm64/ProcessKeeper.Updater.exe"})
        manifestText += std::string(entry) + "\t1\t" + std::string(64, 'a') + "\n";
    auto packageResources = BeginUpdateResourceW(packageFixture.c_str(), FALSE);
    if (!packageResources || !UpdateResourceW(packageResources, RT_RCDATA, MAKEINTRESOURCEW(101), MAKELANGID(LANG_NEUTRAL, SUBLANG_NEUTRAL), &manifestText[0], static_cast<DWORD>(manifestText.size())) ||
        !UpdateResourceW(packageResources, RT_RCDATA, MAKEINTRESOURCEW(102), MAKELANGID(LANG_NEUTRAL, SUBLANG_NEUTRAL), const_cast<char*>(archive.data()), static_cast<DWORD>(archive.size())) || !EndUpdateResourceW(packageResources, FALSE))
        throw Failure(L"Cannot embed an inert installed-package identity fixture.");
    check(CheckInstalledPackage(packageFixture) == 0, L"wizard migration validates the actual existing bundle identity without requiring stale ownership target or version");
    check(GetInstalledPackageTarget(packageFixture) == 0, L"wizard confirmation uses the actual verified Universal payload target");
    check(CheckInstalledPackage(self.path()) == 2, L"a same-product inert EXE without a bundle cannot authorize installed migration");
    const auto tamperedPackage = path(L"tampered-bundle.exe");
    if (!CopyFileW(packageFixture.c_str(), tamperedPackage.c_str(), TRUE)) throw Failure(L"Cannot create an independent tampered bundle fixture.");
    auto tamperedResources = BeginUpdateResourceW(tamperedPackage.c_str(), FALSE); char badArchive[] = "changed archive";
    if (!tamperedResources || !UpdateResourceW(tamperedResources, RT_RCDATA, MAKEINTRESOURCEW(102), MAKELANGID(LANG_NEUTRAL, SUBLANG_NEUTRAL), badArchive, sizeof(badArchive)) || !EndUpdateResourceW(tamperedResources, FALSE))
        throw Failure(L"Cannot change only an inert bundle archive resource.");
    check(CheckInstalledPackage(tamperedPackage) == 2 && GetInstalledPackageTarget(tamperedPackage) == -1, L"changed archive bytes cannot authorize wizard flavor migration");
    wchar_t guardFixture[32768]{}; const auto guardFixtureLength = GetEnvironmentVariableW(L"PROCESSKEEPER_SETUP_GUARD_FIXTURE", guardFixture, 32768);
    if (guardFixtureLength && guardFixtureLength < 32768) {
        auto guardModule = LoadLibraryW(FullPath(guardFixture).c_str()); if (!guardModule) Fail(L"Cannot load the explicitly supplied locally compiled read-only guard fixture.");
        const auto targetQuery = reinterpret_cast<int(__stdcall*)(const wchar_t*)>(GetProcAddress(guardModule, "GetInstalledPackageTargetW"));
        check(targetQuery && targetQuery(packageFixture.c_str()) == 0 && targetQuery(tamperedPackage.c_str()) == -1,
            L"actual compiled setup guard export binds the wizard to verified bundle identity and rejects changed archive bytes");
        FreeLibrary(guardModule);
    }
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
    check(result.installed && launched && DigestFile(path(L"old.exe")) == newHash && result.backup.empty(), L"confirmed successful transaction removes only its byte-verified original backup");
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
    check(result.installed && binaryOld != binaryNew && result.backup.empty(), L"actual isolated accepted candidate cleans its exact original backup after startup");
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
    check(RefreshShortcutFixture(path(L"next.exe"), desktop) == L"conflict" && DigestFile(desktop + L"\\Process Keeper.lnk") == shortcutHash, L"update refresh preserves a same-name shortcut belonging to another original");
    const auto link = desktop + L"\\Process Keeper.lnk";
    ShortcutIcon(link, path(L"next.exe").c_str());
    check(RefreshShortcutFixture(path(L"old.exe"), desktop) == L"refreshed" && ShortcutIcon(link) == path(L"old.exe"), L"confirmed update rewrites its exact owned shortcut with the installed EXE icon");
    WIN32_FIND_DATAW leftover{}; auto search = FindFirstFileW((desktop + L"\\*.pk-link-*").c_str(), &leftover);
    check(search == INVALID_HANDLE_VALUE && GetLastError() == ERROR_FILE_NOT_FOUND, L"owned shortcut refresh removes only its own temporary link and backup");
    if (search != INVALID_HANDLE_VALUE) FindClose(search);
    const auto emptyDesktop = path(L"empty-desktop"); CreateDirectoryW(emptyDesktop.c_str(), nullptr);
    check(RefreshShortcutFixture(path(L"old.exe"), emptyDesktop) == L"absent" && GetFileAttributesW((emptyDesktop + L"\\Process Keeper.lnk").c_str()) == INVALID_FILE_ATTRIBUTES, L"update does not re-enable a desktop shortcut the user did not create");
    const auto installed = path(L"ProcessKeeper.exe"), installDirectory = base;
    InstallationRecord registration; registration.contract = 1; registration.markerContract = L"1"; registration.markerRepository = L"KangQiovo/ProcessKeeper"; registration.markerTarget = L"Windows10x64";
    registration.text = {{L"DisplayName", L"Process Keeper"}, {L"Publisher", L"KangQi"}, {L"ProcessKeeperRepository", L"KangQiovo/ProcessKeeper"},
        {L"ProcessKeeperPackageTarget", L"Windows10x64"}, {L"InstallLocation", installDirectory}, {L"UninstallString", L"\"" + installDirectory + L"\\Uninstall.exe\""},
        {L"DisplayIcon", L"\"" + installed + L"\",0"}, {L"URLInfoAbout", L"https://github.com/KangQiovo/ProcessKeeper"}};
    check(MatchesInstallation(registration, installed, PackageTarget::Windows10x64), L"installed version refresh requires exact fixed registry and sibling marker ownership");
    check(MatchesInstalledOwner(registration, installed, PackageTarget::Windows7Compat) && MatchesInstalledOwner(registration, installed, PackageTarget::Universal),
        L"authorized portable flavor migration retains validated installed ownership before metadata refresh");
    check(MatchesInstallation(registration, installed, PackageTarget::Windows10x64) &&
        !MatchesInstalledOwner(registration, installed, static_cast<PackageTarget>(99)), L"retained old uninstaller contract stays valid while unknown actual flavors are refused");
    auto pendingMigration = registration; pendingMigration.text[L"ProcessKeeperPackageTarget"] = L"Windows7Compat";
    check(!MatchesInstalledOwner(pendingMigration, installed, PackageTarget::Windows7Compat), L"partial registry-only migration cannot impersonate matching marker ownership");
    pendingMigration.markerTarget = L"Windows7Compat";
    check(MatchesInstalledOwner(pendingMigration, installed, PackageTarget::Windows7Compat) && MatchesInstallation(pendingMigration, installed, PackageTarget::Windows7Compat),
        L"completed setup migration keeps the replacement uninstaller and new ownership target consistent");
    pendingMigration = registration; pendingMigration.text[L"ProcessKeeperPayloadTarget"] = L"unknown-filename-hint";
    check(MatchesInstalledOwner(pendingMigration, installed, PackageTarget::Universal), L"payload metadata hints never override verified source manifest and installed ownership");
    auto mismatched = registration; mismatched.text[L"UninstallString"] += L" /other";
    check(!MatchesInstallation(mismatched, installed, PackageTarget::Windows10x64), L"another uninstaller command cannot authorize installed metadata changes");
    mismatched = registration; mismatched.markerTarget = L"Windows10arm64";
    check(!MatchesInstallation(mismatched, installed, PackageTarget::Windows10x64) && !MatchesInstallation(registration, path(L"renamed.exe"), PackageTarget::Windows10x64),
        L"different package marker or portable filename never modifies an installed registration");
    for (unsigned scenario = 0; scenario < 5; ++scenario) {
        const auto cache = path((L"cache-" + std::to_wstring(scenario)).c_str());
        if (!CreateDirectoryW(cache.c_str(), nullptr)) { const auto error = GetLastError(); Fail((L"Cannot create fixture cache directory: " + cache).c_str(), error); }
        const auto identity = std::wstring(64, L'a'), root = cache + L"\\" + identity + L"-modern", modern = root + L"\\modern";
        if (!CreateDirectoryW(root.c_str(), nullptr)) { const auto error = GetLastError(); Fail((L"Cannot create fixture payload directory: " + root).c_str(), error); }
        if (!CreateDirectoryW(modern.c_str(), nullptr)) { const auto error = GetLastError(); Fail((L"Cannot create fixture application directory: " + modern).c_str(), error); }
        const auto app = modern + L"\\ProcessKeeper.exe", helper = modern + L"\\ProcessKeeper.Updater.exe";
        Bytes(app, "owned cache app"); Bytes(helper, "owned cache helper");
        Manifest old; old.identity = identity;
        old.files.emplace(L"modern/ProcessKeeper.exe", PayloadFile{L"modern/ProcessKeeper.exe", DigestFile(app), 15});
        old.files.emplace(L"modern/ProcessKeeper.Updater.exe", PayloadFile{L"modern/ProcessKeeper.Updater.exe", DigestFile(helper), 18});
        const auto foreign = cache + L"\\unrelated-user-file.exe"; Bytes(foreign, "preserve sibling");
        SchedulePayloadCleanupFixture(cache, old, Route::ModernX64);
        const auto receipt = cache + L"\\cleanup-" + identity + L"-modern.txt";
        Handle busy;
        if (scenario == 1) busy = Handle(CreateFileW(helper.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr));
        if (scenario == 2) Bytes(helper, "changed cache leaf");
        if (scenario == 3) Bytes(modern + L"\\unowned.exe", "unowned file");
        RunPendingPayloadCleanupFixture(cache, scenario == 4 ? identity : std::wstring(64, L'b'));
        if (scenario > 0) {
            check(GetFileAttributesW(app.c_str()) != INVALID_FILE_ATTRIBUTES && GetFileAttributesW(receipt.c_str()) != INVALID_FILE_ATTRIBUTES,
                L"busy changed unowned or active cache is retained before any owned leaf is removed");
            busy.reset(); if (scenario == 2) Bytes(helper, "owned cache helper");
            if (scenario == 3) DeleteFileW((modern + L"\\unowned.exe").c_str());
            RunPendingPayloadCleanupFixture(cache, std::wstring(64, L'b'));
        }
        check(GetFileAttributesW(root.c_str()) == INVALID_FILE_ATTRIBUTES && GetFileAttributesW(receipt.c_str()) == INVALID_FILE_ATTRIBUTES &&
            GetFileAttributesW(foreign.c_str()) != INVALID_FILE_ATTRIBUTES, L"deferred cleanup deletes only one fully verified old payload tree and its owned receipt");
    }
    const auto guardCache = FullPath(tempRoot + L"setup-guard-" + NewContextId().substr(0, 8)), sidRoot = guardCache + L"\\" + UserSid(), guardSessions = sidRoot + L"\\sessions";
    const auto payloadRoot = sidRoot + L"\\" + std::wstring(64, L'a') + L"-legacy", payloadFolder = payloadRoot + L"\\legacy";
    const auto guardApp = payloadFolder + L"\\ProcessKeeper.exe", guardHelper = payloadFolder + L"\\ProcessKeeper.Updater.exe";
    check(guardCache.rfind(tempRoot, 0) == 0 && guardHelper.size() < MAX_PATH && guardSessions.size() + 80 < MAX_PATH - 2,
        L"setup guard fixtures stay in routed TEMP within Win7 path limits");
    CreateDirectoryW(guardCache.c_str(), nullptr); CreateDirectoryW(sidRoot.c_str(), nullptr); CreateDirectoryW(guardSessions.c_str(), nullptr);
    CreateDirectoryW(payloadRoot.c_str(), nullptr); CreateDirectoryW(payloadFolder.c_str(), nullptr);
    check(CopyFileW(self.path().c_str(), guardApp.c_str(), TRUE) && CopyFileW(self.path().c_str(), guardHelper.c_str(), TRUE), L"setup guard executable fixtures are isolated copies of the inert native test binary");
    // The word Updater triggers Windows' legacy installer heuristic for an unmanifested
    // x86 fixture. Explicit asInvoker keeps this inert child non-elevated.
    const std::string fixtureManifest = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><assembly xmlns=\"urn:schemas-microsoft-com:asm.v1\" manifestVersion=\"1.0\"><trustInfo xmlns=\"urn:schemas-microsoft-com:asm.v3\"><security><requestedPrivileges><requestedExecutionLevel level=\"asInvoker\" uiAccess=\"false\"/></requestedPrivileges></security></trustInfo></assembly>";
    auto resources = BeginUpdateResourceW(guardHelper.c_str(), FALSE);
    if (!resources || !UpdateResourceW(resources, RT_MANIFEST, MAKEINTRESOURCEW(1), MAKELANGID(LANG_NEUTRAL, SUBLANG_NEUTRAL), const_cast<char*>(fixtureManifest.data()), static_cast<DWORD>(fixtureManifest.size())) || !EndUpdateResourceW(resources, FALSE)) Fail(L"Cannot keep the inert helper fixture non-elevated.");
    const auto guardId = NewContextId(), guardDirectory = guardSessions + L"\\" + guardId;
    CreateDirectoryW(guardDirectory.c_str(), nullptr);
    auto spawnGuard = [&](const std::wstring& image) {
        auto command = L"\"" + image + L"\" --fixture-child 1200 0"; STARTUPINFOW startup{sizeof(startup)}; PROCESS_INFORMATION process{};
        if (!CreateProcessW(image.c_str(), &command[0], nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &startup, &process)) Fail(L"Cannot start read-only guard fixture.");
        return process;
    };
    auto guarded = spawnGuard(guardApp); Handle guardedProcess(guarded.hProcess), guardedThread(guarded.hThread);
    auto guardFields = fields; guardFields.push_back(L"Windows10x64"); guardFields[1] = guardId; guardFields[2] = EncodeContextText(installed); guardFields[4] = ProductVersion;
    guardFields[5] = EncodeContextText(guardApp); guardFields[7] = EncodeContextText(guardHelper); guardFields[9] = std::to_wstring(guarded.dwProcessId); guardFields[10] = std::to_wstring(ProcessCreated(guardedProcess.get()));
    auto saveGuard = [&] { std::ofstream output(guardDirectory + L"\\context.txt", std::ios::binary); for (const auto& line : guardFields) { for (auto character : line) output.put(static_cast<char>(character)); output.put('\n'); } };
    saveGuard();
    HMODULE oldGuardModule = nullptr; int(__stdcall* oldGuardCheck)(const wchar_t*, const wchar_t*) = nullptr;
    wchar_t oldGuardFixture[32768]{}; const auto oldGuardFixtureLength = GetEnvironmentVariableW(L"PROCESSKEEPER_OLD_GUARD_FIXTURE", oldGuardFixture, 32768);
    if (oldGuardFixtureLength && oldGuardFixtureLength < 32768) {
        oldGuardModule = LoadLibraryW(FullPath(oldGuardFixture).c_str()); if (!oldGuardModule) Fail(L"Cannot load the explicitly supplied original guard fixture.");
        oldGuardCheck = reinterpret_cast<int(__stdcall*)(const wchar_t*, const wchar_t*)>(GetProcAddress(oldGuardModule, "CheckInstalledSessionFixtureW"));
        check(oldGuardCheck && oldGuardCheck(guardCache.c_str(), installed.c_str()) == 1,
            L"actual 1.7.0 guard recognizes a current 1.7.1 canonical thirteen-field running session");
        guardFields.push_back(L"Installer"); saveGuard();
        check(oldGuardCheck(guardCache.c_str(), installed.c_str()) == 2, L"original guard control proves a fourteen-field context would break retained uninstaller compatibility");
        guardFields.pop_back(); saveGuard();
    }
    check(CheckInstalledSessionFixture(guardCache, installed) == 1, L"setup guard detects actual cached managed session even after the original wrapper would exit");
    check(CheckInstalledSessionFixture(guardCache, path(L"different.exe")) == 0, L"independently verified sessions for another original do not block installation");
    const auto actualCreated = guardFields[10]; guardFields[10] = std::to_wstring(ProcessCreated(guardedProcess.get()) + 1); saveGuard();
    check(CheckInstalledSessionFixture(guardCache, installed) == 0, L"a stale reused-PID creation identity cannot impersonate an active installed UI");
    guardFields[10] = actualCreated; saveGuard();
    check(WaitForSingleObject(guardedProcess.get(), 5000) == WAIT_OBJECT_0, L"read-only setup guard never terminates its owned inert UI fixture");
    if (oldGuardCheck) check(oldGuardCheck(guardCache.c_str(), installed.c_str()) == 0, L"retained original uninstaller guard permits the current canonical session after its actual UI exits");
    guarded = spawnGuard(guardHelper); Handle helperProcess(guarded.hProcess), helperThread(guarded.hThread);
    const auto helperJob = guardDirectory + L"\\job-" + NewContextId(); CreateDirectoryW(helperJob.c_str(), nullptr);
    { std::ofstream output(helperJob + L"\\ready.txt", std::ios::binary); output << "PKREADY1\n" << guarded.dwProcessId << "\n"; }
    check(CheckInstalledSessionFixture(guardCache, installed) == 1, L"setup guard detects the exact ready-marked background helper after its UI exits");
    if (oldGuardCheck) check(oldGuardCheck(guardCache.c_str(), installed.c_str()) == 1, L"retained original guard still protects the live current ready-marked update helper");
    check(WaitForSingleObject(helperProcess.get(), 5000) == WAIT_OBJECT_0 && CheckInstalledSessionFixture(guardCache, installed) == 0,
        L"completed helper records remain safe to inspect and do not permanently block upgrades");
    if (oldGuardCheck) check(oldGuardCheck(guardCache.c_str(), installed.c_str()) == 0, L"retained original guard permits uninstall after the current helper exits naturally");
    guardFields[2] = L"malformed"; saveGuard();
    check(CheckInstalledSessionFixture(guardCache, installed) == 2, L"malformed protected original identity fails closed rather than authorizing uninstall");
    check(CheckPortableFile(guardApp, DigestFile(guardApp)) == 0 && CheckPortableFile(guardApp, std::wstring(64, L'0')) == 2,
        L"setup staged portable validation binds exact inert PE bytes to the expected SHA256");
    const auto hardLink = path(L"guard-hardlink.exe");
    check(CreateHardLinkW(hardLink.c_str(), guardApp.c_str(), nullptr) && CheckPortableFile(guardApp, DigestFile(guardApp)) == 2,
        L"setup refuses a staged executable with another hard-link alias");
    if (oldGuardModule) FreeLibrary(oldGuardModule);
    CoUninitialize();
}
