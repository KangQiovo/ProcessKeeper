#pragma once
#include "Payload.h"
namespace pk {
constexpr const wchar_t* ProductVersion = L"1.5.0";
bool ValidContextId(const std::wstring& value);
bool ValidSha256(const std::wstring& value);
bool IsNewerUpdateVersion(const std::wstring& candidate, const std::wstring& current);
std::wstring NewContextId();
std::wstring EncodeContextText(const std::wstring& value);
std::wstring DecodeContextText(const std::wstring& value);
std::vector<std::wstring> ReadProtectedLines(const std::wstring& path);
void WriteProtectedLines(const std::wstring& path, const std::vector<std::wstring>& lines);
ULONGLONG ProcessCreated(HANDLE process);
std::wstring ProcessImage(HANDLE process);
std::vector<wchar_t> LaunchEnvironment(const std::wstring& id);
void WriteLaunchContext(const SourceLock& source, const PreparedPayload& payload, const std::wstring& id, HANDLE process, DWORD pid);
struct LaunchContext {
    std::wstring id, directory, original, originalHash, version, payload, payloadHash, helper, helperHash;
    DWORD pid = 0; ULONGLONG created = 0;
};
LaunchContext ReadLaunchContext(const std::wstring& id);
LaunchContext ParseLaunchContext(const std::vector<std::wstring>& fields, const std::wstring& id, const std::wstring& directory);
Handle OpenContextProcess(const LaunchContext& context);
void ValidateUpdateBundle(const std::wstring& path, const std::wstring& expectedVersion);
#ifdef PK_FIXTURE_BUILD
void ValidateUpdateBundleFixture(const std::wstring& path, const std::wstring& expectedVersion);
Handle OpenContextProcessFixture(const LaunchContext& context);
#endif
}
