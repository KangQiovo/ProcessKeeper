#pragma once
#include "Security.h"
namespace pk {
int CheckInstalledSessions(const std::wstring& original);
int CheckPortableFile(const std::wstring& path, const std::wstring& expectedHash);
#ifdef PK_FIXTURE_BUILD
int CheckInstalledSessionFixture(const std::wstring& cache, const std::wstring& original);
#endif
}
