#pragma once
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <string>
#include <vector>

namespace pk {
enum class Route { Unsupported, MissingFramework, ModernX64, Legacy, ModernArm64 };
struct Host {
    DWORD major = 0, minor = 0, build = 0;
    WORD servicePack = 0, machine = 0;
    DWORD framework = 0;
};
Host DetectHost();
HMODULE LoadSystemLibrary(const wchar_t* name);
bool IsRuntimeOverride(const std::wstring& name);
std::vector<wchar_t> BuildChildEnvironment();
Route ChooseRoute(const Host& host, bool forceLegacy = false);
bool IsModernRoute(Route route);
bool CanOfferLegacy(Route detected, bool forceLegacy, bool childRunning);
bool IsApplicationWindowIdentity(const std::wstring& className, const std::wstring& title);
bool IsAdministrator();
std::wstring FrameworkUrl(const Host& host);
std::wstring ErrorText(DWORD error);
void WriteDiagnostic(const std::wstring& value);
}
