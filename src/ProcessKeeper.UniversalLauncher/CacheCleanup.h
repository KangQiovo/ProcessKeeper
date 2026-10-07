#pragma once
#include "LaunchContext.h"
namespace pk {
struct PayloadCleanupPlan { std::vector<std::wstring> receipt; };
struct PayloadCleanupResult { ULONGLONG removedFiles = 0, removedBytes = 0; unsigned retained = 0; };
PayloadCleanupPlan CapturePayloadCleanup(const LaunchContext& context);
void SchedulePayloadCleanup(const PayloadCleanupPlan& plan);
PayloadCleanupResult RunPendingPayloadCleanup(const std::wstring& activeIdentity);
PayloadCleanupResult ClearPendingPayloadCache(const LaunchContext& context, const std::wstring& request);
#ifdef PK_FIXTURE_BUILD
void SchedulePayloadCleanupFixture(const std::wstring& cache, const Manifest& manifest, Route route);
void RunPendingPayloadCleanupFixture(const std::wstring& cache, const std::wstring& activeIdentity);
PayloadCleanupResult ClearDownloadCacheFixture(const std::wstring& cache, const std::wstring& currentSession);
#endif
}
