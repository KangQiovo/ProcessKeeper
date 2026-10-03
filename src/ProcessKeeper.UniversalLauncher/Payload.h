#pragma once
#include "Security.h"
#include <map>
#include <atomic>

namespace pk {
struct OrdinalIgnoreCase {
    bool operator()(const std::wstring& left, const std::wstring& right) const {
        return CompareStringOrdinal(left.c_str(), static_cast<int>(left.size()), right.c_str(), static_cast<int>(right.size()), TRUE) == CSTR_LESS_THAN;
    }
};
struct PayloadFile { std::wstring path, hash; ULONGLONG length = 0; };
struct Manifest {
    std::wstring archiveHash, identity;
    PackageTarget target = PackageTarget::Universal;
    std::map<std::wstring, PayloadFile, OrdinalIgnoreCase> files;
};
void ValidateRelativePath(const std::wstring& path);
Manifest ParseManifest(const std::string& text);
PackageTarget EmbeddedPackageTarget();
std::wstring PayloadDirectory(Route route);
bool IsPayloadFile(const std::wstring& path, Route route);
struct PreparedPayload {
    std::wstring directory;
    PackageTarget target = PackageTarget::Universal;
    std::vector<Handle> files, parents;
};
PreparedPayload PreparePayload(Route route, const std::atomic_bool* canceled = nullptr);
#ifdef PK_FIXTURE_BUILD
void ExtractCabinetFixture(const std::vector<BYTE>& archive, const Manifest& manifest, const std::wstring& root, Route route);
void ExtractCabinetFixture(const std::vector<BYTE>& archive, const Manifest& manifest, const std::wstring& root, bool modern);
#endif
}
