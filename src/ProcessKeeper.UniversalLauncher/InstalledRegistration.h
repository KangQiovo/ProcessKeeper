#pragma once
#include "Security.h"
#include <map>
namespace pk {
struct InstallationRecord { std::map<std::wstring, std::wstring> text; DWORD contract = 0; std::wstring markerRepository, markerTarget, markerContract; };
bool MatchesInstallation(const InstallationRecord& record, const std::wstring& original, PackageTarget target);
bool MatchesInstalledOwner(const InstallationRecord& record, const std::wstring& original, PackageTarget actualTarget);
std::wstring ReadInstalledDistribution(const std::wstring& original, const std::wstring& sha256, PackageTarget target);
std::wstring RefreshInstalledRegistration(const std::wstring& original, const std::wstring& sha256, const std::wstring& version, PackageTarget target);
}
