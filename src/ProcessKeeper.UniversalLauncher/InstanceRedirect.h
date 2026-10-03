#pragma once
#include "LaunchContext.h"
namespace pk {
class InstanceLaunchGate {
    Handle mutex_; bool owned_ = false;
public:
    explicit InstanceLaunchGate(const std::atomic_bool* canceled = nullptr);
    ~InstanceLaunchGate();
    InstanceLaunchGate(const InstanceLaunchGate&) = delete;
    InstanceLaunchGate& operator=(const InstanceLaunchGate&) = delete;
};
bool ResolvePreferredInstance(Route candidate, ULONGLONG requested, const std::atomic_bool* canceled = nullptr);
bool ConfirmInstanceRedirect(const std::wstring& ownContext, DWORD ownPid, Route ownRoute, ULONGLONG ownCreated);
bool RetainVisibleCompatibleInstance(const Host& host);
#ifdef PK_FIXTURE_BUILD
bool InstanceCandidateWinsFixture(const std::wstring& candidateVersion, Route candidate,
    ULONGLONG candidateRequested, const std::wstring& existingVersion, Route existing, ULONGLONG existingRequested);
bool ValidInstanceRedirectFixture(const std::vector<std::wstring>& fields, const std::wstring& ownContext);
void ConfigureInstanceFixture(const std::wstring& root, const std::wstring& gate);
bool VerifyInstancePeerFixture(const std::wstring& id);
#endif
}
