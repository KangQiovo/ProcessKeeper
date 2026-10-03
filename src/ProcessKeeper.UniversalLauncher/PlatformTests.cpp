#include "Platform.h"
#include <stdexcept>
#include <functional>
#include "Payload.h"
#include "LaunchContext.h"
#include <fstream>
#include <iterator>
#include <algorithm>
void RunSecurityTests(const std::function<void(bool, const wchar_t*)>& check);
void RunPayloadTests(const std::function<void(bool, const wchar_t*)>& check);
void RunUpdateTests(const std::function<void(bool, const wchar_t*)>& check);
void RunInstanceTests(const std::function<void(bool, const wchar_t*)>& check);
bool RunInstanceFixtureCommand(int argc, wchar_t** argv, int& result);

int wmain(int argc, wchar_t** argv) {
    using namespace pk;
    int count = 0;
    auto check = [&count](bool value, const wchar_t* name) {
        if (!value) { WriteDiagnostic(std::wstring(L"FAIL ") + name + L"\n"); throw std::runtime_error("failed"); }
        ++count; WriteDiagnostic(std::wstring(L"PASS ") + name + L"\n");
    };
    try {
        int fixtureResult = 0; if (RunInstanceFixtureCommand(argc, argv, fixtureResult)) return fixtureResult;
        if (argc == 4 && wcscmp(argv[1], L"--fixture-marker") == 0) {
            const auto path = FullPath(argv[2]) + L"\\execute.txt"; Sleep(20);
            { std::ofstream file(path + L".pending", std::ios::binary); file << "PKEXECUTE1\n" << std::stoul(argv[3]) << "\n"; file.flush(); if (!file.good()) return 1; }
            return MoveFileExW((path + L".pending").c_str(), path.c_str(), MOVEFILE_WRITE_THROUGH) ? 0 : 1;
        }
        if (argc == 4 && wcscmp(argv[1], L"--fixture-child") == 0) { Sleep(static_cast<DWORD>((std::min)(5000UL, std::stoul(argv[2])))); return std::stoi(argv[3]); }
        if (argc == 4 && wcscmp(argv[1], L"--validate-update") == 0) { ValidateUpdateBundleFixture(argv[2], argv[3]); WriteDiagnostic(L"Inert package resources and product version verified; no image code executed.\n"); return 0; }
        if (argc == 6 && wcscmp(argv[1], L"--extract-verify") == 0) {
            const auto route = wcscmp(argv[5], L"modern") == 0 ? Route::ModernX64 :
                wcscmp(argv[5], L"legacy") == 0 ? Route::Legacy : wcscmp(argv[5], L"arm64") == 0 ? Route::ModernArm64 : Route::Unsupported;
            if (route == Route::Unsupported) throw Failure(L"Unknown fixture variant.");
            std::ifstream cabinet(argv[2], std::ios::binary), manifestInput(argv[3], std::ios::binary);
            std::vector<BYTE> archive((std::istreambuf_iterator<char>(cabinet)), std::istreambuf_iterator<char>());
            std::string manifestText((std::istreambuf_iterator<char>(manifestInput)), std::istreambuf_iterator<char>());
            auto manifest = ParseManifest(manifestText); const auto root = FullPath(argv[4]);
            check(CreateDirectoryW(root.c_str(), nullptr) != FALSE, L"package verification creates a new isolated workspace directory");
            ExtractCabinetFixture(archive, manifest, root, route);
            size_t verified = 0;
            for (const auto& pair : manifest.files) if (IsPayloadFile(pair.first, route)) {
                auto path = root + L"\\" + pair.first; std::replace(path.begin(), path.end(), L'/', L'\\');
                Handle file(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
                LARGE_INTEGER length{};
                if (!file.valid() || !GetFileSizeEx(file.get(), &length) || static_cast<ULONGLONG>(length.QuadPart) != pair.second.length || Hex(HashFile(file.get())) != pair.second.hash)
                    throw Failure(L"A real packaged file failed exact length/SHA-256 verification.");
                ++verified;
            }
            WriteDiagnostic(L"Actual package verified: " + std::to_wstring(verified) + L" " + PayloadDirectory(route) + L" files | no payload process launched.\n");
            return 0;
        }
        Host host{ 6, 1, 7601, 1, IMAGE_FILE_MACHINE_I386, 394806 };
        check(ChooseRoute(host) == Route::Legacy, L"Win7 SP1 x86 selects Legacy before loading a CLR");
        host.framework = 0; check(ChooseRoute(host) == Route::MissingFramework, L"missing Framework is an explicit prerequisite state");
        host.servicePack = 0; check(ChooseRoute(host) == Route::Unsupported, L"Win7 RTM is not mislabeled SP1 compatible");
        host = { 6, 2, 9200, 0, IMAGE_FILE_MACHINE_I386, 999999 };
        check(ChooseRoute(host) == Route::Unsupported, L"Win8 RTM is not given a nonexistent net462 route");
        host = { 6, 3, 9600, 0, IMAGE_FILE_MACHINE_AMD64, 394806 };
        check(ChooseRoute(host) == Route::Legacy, L"Win8.1 x64 uses Legacy");
        host = { 10, 0, 10240, 0, IMAGE_FILE_MACHINE_AMD64, 394802 };
        check(ChooseRoute(host) == Route::Legacy && FrameworkUrl(host).find(L"net462") != std::wstring::npos,
            L"early Win10 uses compatible Framework 4.6.2 instructions");
        host.build = 19041;
        check(ChooseRoute(host) == Route::ModernX64, L"qualified Win10 x64 selects the modern payload");
        check(ChooseRoute(host, true) == Route::Legacy, L"explicit compatibility choice routes modern Windows to the existing Framework UI");
        host.framework = 0;
        check(ChooseRoute(host, true) == Route::MissingFramework && ChooseRoute(host) == Route::ModernX64, L"explicit compatibility choice still checks its Framework prerequisite");
        check(CanOfferLegacy(Route::ModernX64, false, false) && !CanOfferLegacy(Route::ModernX64, false, true) && !CanOfferLegacy(Route::ModernX64, true, false) && !CanOfferLegacy(Route::Legacy, false, false), L"compatibility fallback is offered only after modern startup failed without a live child");
        check(IsApplicationWindowIdentity(L"WinUIDesktopWin32WindowClass", L"Process Keeper") && IsApplicationWindowIdentity(L"HwndWrapper[ProcessKeeper.exe;;fixture]", L"Process Keeper"), L"both actual product window identities may confirm graphical startup");
        check(!IsApplicationWindowIdentity(L"ConsoleWindowClass", L"Process Keeper") && !IsApplicationWindowIdentity(L"PseudoConsoleWindow", L"Process Keeper") && !IsApplicationWindowIdentity(L"#32770", L"Process Keeper"), L"command windows and runtime error dialogs cannot confirm graphical startup");
        check(!IsApplicationWindowIdentity(L"WinUIDesktopWin32WindowClass", L"Runtime error") && !IsApplicationWindowIdentity(L"HwndWrapper[ProcessKeeper.exe;;fixture]", L""), L"unrelated error or helper windows do not impersonate the main application");
        host.framework = 394802;
        host.machine = IMAGE_FILE_MACHINE_I386;
        check(ChooseRoute(host) == Route::Legacy, L"Win10 x86 never attempts to launch an x64 image");
        host.machine = IMAGE_FILE_MACHINE_ARM64;
        check(ChooseRoute(host) == Route::ModernArm64, L"Win10 2004 ARM64 selects its native modern payload through the x86 outer launcher");
        check(ChooseRoute(host, true) == Route::Unsupported && !CanOfferLegacy(Route::ModernArm64, false, false), L"ARM64 cannot fall back to an unverified legacy route");
        host.framework = 0; host.build = 22621;
        check(ChooseRoute(host) == Route::ModernArm64, L"Win11 ARM64 modern route is independent of x86 Framework installation");
        host.build = 18363;
        check(ChooseRoute(host) == Route::Unsupported, L"older ARM64 Windows is rejected before loading .NET");
        host.build = 22621; host.machine = IMAGE_FILE_MACHINE_ARMNT;
        check(ChooseRoute(host) == Route::Unsupported, L"ARM32 cannot be mislabeled ARM64 compatible");
        check(IsModernRoute(Route::ModernX64) && IsModernRoute(Route::ModernArm64) && !IsModernRoute(Route::Legacy), L"modern runtime help covers both native architectures");
        host = {};
        check(ChooseRoute(host) == Route::Unsupported, L"failed OS discovery cannot launch a payload");
        const auto actual = DetectHost();
        const Host modernX64{10, 0, 19041, 0, IMAGE_FILE_MACHINE_AMD64, 394802};
        check(ChoosePackageRoute(modernX64, PackageTarget::Windows10x64) == Route::ModernX64 && ChoosePackageRoute(modernX64, PackageTarget::Windows7Compat) == Route::Legacy,
            L"modern x64 and explicit compatibility packages retain their declared interface route");
        check(ChoosePackageRoute(modernX64, PackageTarget::Windows10arm64) == Route::Unsupported, L"an ARM64 package is rejected on an x64 host before loading a payload");
        Host oldX64 = modernX64; oldX64.build = 18363;
        check(ChoosePackageRoute(oldX64, PackageTarget::Windows10x64) == Route::Legacy && ChoosePackageRoute(oldX64, PackageTarget::Windows7Compat) == Route::Legacy,
            L"dual x86 x64 package selects real compatibility UI on older Intel AMD hosts");
        auto modernX86 = modernX64; modernX86.machine = IMAGE_FILE_MACHINE_I386;
        check(ChoosePackageRoute(modernX86, PackageTarget::Windows10x64) == Route::Legacy && ChoosePackageRoute(modernX64, PackageTarget::Windows10x64, true) == Route::Legacy,
            L"dual Windows package supports x86 and an explicit verified compatibility fallback");
        Host arm64 = modernX64; arm64.machine = IMAGE_FILE_MACHINE_ARM64;
        check(ChoosePackageRoute(arm64, PackageTarget::Windows10arm64) == Route::ModernArm64 && ChoosePackageRoute(arm64, PackageTarget::Windows7Compat) == Route::Unsupported,
            L"native ARM64 package never enters an unverified compatibility route");
        check(actual.major >= 6 && actual.machine != 0, L"native x86 executable reads its real host architecture and OS");
        for (const auto name : {L"DOTNET_STARTUP_HOOKS", L"dotnet_additional_deps", L"Dotnet_Shared_Store", L"COR_ENABLE_PROFILING", L"cor_profiler_path_32", L"CORECLR_PROFILER", L"COMPLUS_version", L"DEVPATH", L"__COMPAT_LAYER"})
            check(IsRuntimeOverride(name), L"runtime injection and probing overrides are filtered case-insensitively");
        check(!IsRuntimeOverride(L"LOCALAPPDATA") && !IsRuntimeOverride(L"USERPROFILE") && !IsRuntimeOverride(L"TEMP"), L"ordinary application environment is retained");
        auto environment = BuildChildEnvironment(); bool injected = false, safePath = false;
        for (auto value = environment.data(); *value; value += wcslen(value) + 1) { std::wstring entry(value); auto separator = entry.find(L'=', entry[0] == L'=' ? 1 : 0); injected |= IsRuntimeOverride(entry.substr(0, separator)); if (entry.rfind(L"PATH=", 0) == 0 || entry.rfind(L"Path=", 0) == 0) safePath = entry.find(L"System32") != std::wstring::npos || entry.find(L"system32") != std::wstring::npos; }
        check(!injected && safePath && environment[environment.size()-2] == 0, L"explicit Unicode child environment has safe system PATH and double-NUL termination");
        RunSecurityTests(check);
        RunPayloadTests(check);
        RunUpdateTests(check);
        RunInstanceTests(check);
        if (argc == 4) {
            std::ifstream cabinet(argv[1], std::ios::binary), manifestInput(argv[2], std::ios::binary);
            std::vector<BYTE> archive((std::istreambuf_iterator<char>(cabinet)), std::istreambuf_iterator<char>());
            std::string manifestText((std::istreambuf_iterator<char>(manifestInput)), std::istreambuf_iterator<char>());
            auto manifest = ParseManifest(manifestText); const auto root = FullPath(argv[3]);
            check(CreateDirectoryW(root.c_str(), nullptr) != FALSE, L"CAB fixture creates a new isolated workspace directory");
            ExtractCabinetFixture(archive, manifest, root, true);
            check(GetFileAttributesW((root + L"\\modern\\ProcessKeeper.exe").c_str()) != INVALID_FILE_ATTRIBUTES &&
                GetFileAttributesW((root + L"\\legacy").c_str()) == INVALID_FILE_ATTRIBUTES, L"real CAB extracts only the selected variant");
            for (const auto& pair : manifest.files) if (IsPayloadFile(pair.first, Route::ModernX64)) {
                auto path = root + L"\\" + pair.first; std::replace(path.begin(), path.end(), L'/', L'\\');
                Handle file(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
                check(file.valid() && Hex(HashFile(file.get())) == pair.second.hash, L"real CAB extracted file matches exact original SHA-256");
            }
            bool rejected = false;
            try { ExtractCabinetFixture(archive, manifest, root, true); } catch (const Failure&) { rejected = true; }
            check(rejected, L"CAB extraction never overwrites an existing file");
            auto missing = manifest; missing.files.erase(L"legacy/ProcessKeeper.exe"); rejected = false;
            auto second = root + L"-missing"; CreateDirectoryW(second.c_str(), nullptr);
            try { ExtractCabinetFixture(archive, missing, second, true); } catch (const Failure&) { rejected = true; }
            check(rejected, L"CAB rejects an unexpected file even in the skipped variant");
            auto wrongHash = manifest; wrongHash.files.at(L"modern/ProcessKeeper.exe").hash = std::wstring(64, L'0'); rejected = false;
            auto third = root + L"-hash"; CreateDirectoryW(third.c_str(), nullptr);
            try { ExtractCabinetFixture(archive, wrongHash, third, true); } catch (const Failure&) { rejected = true; }
            check(rejected, L"CAB rejects a decompressed file with a mismatched SHA-256");
            archive[0] ^= 1; rejected = false;
            try { ExtractCabinetFixture(archive, manifest, root, false); } catch (const Failure&) { rejected = true; }
            check(rejected, L"CAB corruption is rejected before extraction");
        }
        WriteDiagnostic(L"Native route tests: " + std::to_wstring(count) + L" passed | only isolated no-op fixture processes; no elevation or product launch.\n");
        return 0;
    } catch (const Failure& error) { WriteDiagnostic(L"ERROR " + error.message + L"\n"); return 1; }
    catch (...) { return 1; }
}
