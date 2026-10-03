#pragma once
#include "LaunchContext.h"
#include <functional>
namespace pk {
enum class UpdateLaunchResult { Success, Failed, Unconfirmed };
struct UpdateTransactionResult { bool installed = false, restored = false, confirmed = false; std::wstring backup, message; };
void ValidateUpdateJob(const std::vector<std::wstring>& job, const LaunchContext& context, const std::wstring& id);
UpdateTransactionResult InstallUpdate(const LaunchContext& context, const std::wstring& jobId);
#ifdef PK_FIXTURE_BUILD
bool WaitUpdateMarkerFixture(const std::wstring& directory, HANDLE caller, DWORD helperPid, int attempts);
UpdateTransactionResult ReplaceUpdateFixture(const std::wstring& original, const std::wstring& originalHash,
    const std::wstring& candidate, const std::wstring& candidateHash, const std::wstring& id,
    const std::function<UpdateLaunchResult(const std::wstring&)>& launch);
#endif
}
