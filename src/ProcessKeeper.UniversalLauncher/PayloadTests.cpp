#include "Payload.h"
#include <functional>

void RunPayloadTests(const std::function<void(bool, const wchar_t*)>& check) {
    using namespace pk;
    const std::string hash(64, 'a');
    const std::string valid = "PK14\t" + hash + "\nmodern/ProcessKeeper.exe\t1\t" + hash + "\nlegacy/ProcessKeeper.exe\t1\t" + hash + "\n";
    const auto manifest = ParseManifest(valid);
    check(manifest.files.size() == 2 && manifest.identity.size() == 64, L"bounded manifest binds both fixed application entry points");
    const auto triple = ParseManifest(valid + "modern/arm64/ProcessKeeper.exe\t1\t" + hash + "\n");
    check(triple.files.size() == 3 && triple.files.count(L"modern/arm64/ProcessKeeper.exe"), L"ARM64 uses an old-PK14-compatible nested modern prefix");
    check(IsPayloadFile(L"modern/ProcessKeeper.exe", Route::ModernX64) && !IsPayloadFile(L"modern/arm64/ProcessKeeper.exe", Route::ModernX64), L"x64 extraction excludes the ARM64 subtree");
    check(IsPayloadFile(L"modern/arm64/ProcessKeeper.exe", Route::ModernArm64) && !IsPayloadFile(L"modern/ProcessKeeper.exe", Route::ModernArm64) && !IsPayloadFile(L"legacy/ProcessKeeper.exe", Route::ModernArm64), L"ARM64 extraction selects only its own subtree");
    check(!IsPayloadFile(L"modern/ARM64/ProcessKeeper.exe", Route::ModernX64) && !IsPayloadFile(L"modern/arm64-escape/ProcessKeeper.exe", Route::ModernArm64), L"ARM64 isolation respects Windows case rules and whole directory boundaries");
    check(PayloadDirectory(Route::ModernArm64) == L"modern/arm64" && PayloadDirectory(Route::Legacy) == L"legacy", L"payload paths are fixed rather than user-selectable");
    auto rejects = [](const std::wstring& path) { try { ValidateRelativePath(path); return false; } catch (const Failure&) { return true; } };
    for (const auto* path : { L"../escape.exe", L"modern/../../escape.exe", L"/absolute.exe", L"modern/C:/escape.exe", L"modern\\escape.exe",
        L"modern/stream.exe:data", L"modern//empty.exe", L"modern/trailing.", L"modern/trailing ", L"modern/CON.exe", L"modern/COM1.txt", L"modern/name?.exe" })
        check(rejects(path), L"unsafe or ambiguous embedded path is rejected");
    auto bad = [&](const std::string& text) { try { ParseManifest(text); return false; } catch (const Failure&) { return true; } };
    for (const auto& fixture : std::vector<std::pair<std::string, Route>>{{"Windows7Compat", Route::Legacy}, {"Windows10x64", Route::ModernX64}, {"Windows10arm64", Route::ModernArm64}}) {
        const auto directory = PayloadDirectory(fixture.second); std::string prefix;
        for (const auto character : directory) prefix.push_back(static_cast<char>(character));
        auto split = "PK17\t" + hash + "\t" + fixture.first + "\n" + prefix + "/ProcessKeeper.exe\t1\t" + hash + "\n" + prefix + "/ProcessKeeper.Updater.exe\t1\t" + hash + "\n";
        if (fixture.first == "Windows10x64") split += "legacy/ProcessKeeper.exe\t1\t" + hash + "\nlegacy/ProcessKeeper.Updater.exe\t1\t" + hash + "\n";
        check(ParseManifest(split).files.size() == (fixture.first == "Windows10x64" ? 4u : 2u), L"each declared package requires its real matching app and trusted helper payloads");
        check(bad(split + (fixture.second == Route::Legacy ? "modern" : fixture.second == Route::ModernArm64 ? "legacy" : "modern/arm64") + "/foreign.dll\t1\t" + hash + "\n"), L"declared packages reject every unrelated platform subtree");
        check(bad("PK17\t" + hash + "\t" + fixture.first + "\n" + prefix + "/ProcessKeeper.exe\t1\t" + hash + "\n"), L"single-platform updater cannot be omitted");
    }
    check(bad("PK17\t" + hash + "\tWindows10x64\nmodern/ProcessKeeper.exe\t1\t" + hash + "\nmodern/ProcessKeeper.Updater.exe\t1\t" + hash + "\n"), L"dual x86/x64 package cannot omit its compatibility payload");
    check(bad("PK17\t" + hash + "\tUniversal\nmodern/ProcessKeeper.exe\t1\t" + hash + "\n"), L"single-platform format cannot disguise an unsupported target as Universal");
    check(bad(valid + "modern/arm64/helper.dll\t1\t" + hash + "\n"), L"partial ARM64 subtree without its entry point is rejected");
    check(bad(valid + "MODERN/processkeeper.EXE\t1\t" + hash + "\n"), L"case-insensitive duplicate payload paths are rejected");
    check(bad(valid + "modern/helper\t1\t" + hash + "\nmodern/helper/file.dll\t1\t" + hash + "\n"), L"file/directory conflicts are rejected before extraction");
    check(bad(valid + "other/ProcessKeeper.exe\t1\t" + hash + "\n"), L"unrecognized payload variants cannot select an executable");
    check(bad(valid + "modern/too-big.dll\t9999999999\t" + hash + "\n"), L"oversized files cannot expand into the cache");
    check(bad(valid + "modern/bad.dll\t-1\t" + hash + "\n"), L"negative file lengths are rejected");
    check(bad("PK14\t" + hash + "\nmodern/ProcessKeeper.exe\t1\t" + hash + "\n"), L"missing legacy entry point is rejected");
    check(bad("PK13\t" + hash + "\n"), L"unknown manifest format is rejected");
}
