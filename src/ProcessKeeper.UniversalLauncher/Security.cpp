#include "Security.h"
#include <aclapi.h>
#include <sddl.h>
#include <shlobj.h>
#include <bcrypt.h>
#include <algorithm>

namespace pk {
[[noreturn]] void Fail(const wchar_t* context, DWORD error) { throw Failure(std::wstring(context) + L"\n" + ErrorText(error)); }
namespace {
class LocalMemory {
public:
    void* value = nullptr;
    ~LocalMemory() { if (value) LocalFree(value); }
};
class Hash {
    struct Api {
        HMODULE module = LoadSystemLibrary(L"bcrypt.dll");
        decltype(&BCryptOpenAlgorithmProvider) open;
        decltype(&BCryptGetProperty) property;
        decltype(&BCryptCreateHash) create;
        decltype(&BCryptCloseAlgorithmProvider) close;
        decltype(&BCryptDestroyHash) destroy;
        decltype(&BCryptHashData) add;
        decltype(&BCryptFinishHash) finish;
        Api() {
            if (!module) throw Failure(L"Windows SHA-256 support is unavailable.");
            open = reinterpret_cast<decltype(open)>(GetProcAddress(module, "BCryptOpenAlgorithmProvider"));
            property = reinterpret_cast<decltype(property)>(GetProcAddress(module, "BCryptGetProperty"));
            create = reinterpret_cast<decltype(create)>(GetProcAddress(module, "BCryptCreateHash"));
            close = reinterpret_cast<decltype(close)>(GetProcAddress(module, "BCryptCloseAlgorithmProvider"));
            destroy = reinterpret_cast<decltype(destroy)>(GetProcAddress(module, "BCryptDestroyHash"));
            add = reinterpret_cast<decltype(add)>(GetProcAddress(module, "BCryptHashData"));
            finish = reinterpret_cast<decltype(finish)>(GetProcAddress(module, "BCryptFinishHash"));
            if (!open || !property || !create || !close || !destroy || !add || !finish) throw Failure(L"Windows SHA-256 support is incomplete.");
        }
    };
    static Api& Functions() { static Api api; return api; }
    BCRYPT_ALG_HANDLE algorithm_ = nullptr;
    BCRYPT_HASH_HANDLE hash_ = nullptr;
    std::vector<BYTE> state_;
public:
    Hash() {
        auto& api = Functions();
        if (api.open(&algorithm_, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0) throw Failure(L"SHA-256 is unavailable.");
        DWORD size = 0, received = 0;
        if (api.property(algorithm_, BCRYPT_OBJECT_LENGTH, reinterpret_cast<BYTE*>(&size), sizeof(size), &received, 0) < 0 || size > 1024 * 1024) {
            api.close(algorithm_, 0); algorithm_ = nullptr; throw Failure(L"SHA-256 provider is invalid.");
        }
        try { state_.resize(size); } catch (...) { api.close(algorithm_, 0); algorithm_ = nullptr; throw; }
        if (api.create(algorithm_, &hash_, state_.data(), size, nullptr, 0, 0) < 0) {
            api.close(algorithm_, 0); algorithm_ = nullptr; throw Failure(L"SHA-256 initialization failed.");
        }
    }
    ~Hash() { if (hash_) Functions().destroy(hash_); if (algorithm_) Functions().close(algorithm_, 0); }
    void Add(const BYTE* data, DWORD size) { if (Functions().add(hash_, const_cast<BYTE*>(data), size, 0) < 0) throw Failure(L"SHA-256 verification failed."); }
    Digest Finish() { Digest value{}; if (Functions().finish(hash_, value.data(), static_cast<DWORD>(value.size()), 0) < 0) throw Failure(L"SHA-256 verification failed."); return value; }
};
bool TrustedSid(PSID sid, bool allowInstaller) {
    if (!sid || !IsValidSid(sid)) return false;
    if (IsWellKnownSid(sid, WinBuiltinAdministratorsSid) || IsWellKnownSid(sid, WinLocalSystemSid)) return true;
    if (!allowInstaller) return false;
    LocalMemory installer;
    if (!ConvertStringSidToSidW(L"S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", &installer.value)) return false;
    return EqualSid(sid, installer.value) != FALSE;
}
PSECURITY_DESCRIPTOR NewDescriptor(LocalMemory& memory, bool directory) {
    const auto text = directory ? L"O:BAG:BAD:P(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)" : L"O:BAG:BAD:P(A;;FA;;;BA)(A;;FA;;;SY)";
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(text, SDDL_REVISION_1, &memory.value, nullptr)) Fail(L"Cannot create protected permissions.");
    return memory.value;
}
std::wstring Parent(const std::wstring& path) {
    auto slash = path.find_last_of(L'\\');
    if (slash == std::wstring::npos || slash < 2) throw Failure(L"Invalid absolute file path.");
    return slash == 2 ? path.substr(0, 3) : path.substr(0, slash);
}
}

Digest HashFile(HANDLE file) {
    LARGE_INTEGER zero{};
    if (!SetFilePointerEx(file, zero, nullptr, FILE_BEGIN)) Fail(L"Cannot read a verified file.");
    Hash hash; std::array<BYTE, 65536> buffer{};
    for (;;) { DWORD bytes = 0; if (!ReadFile(file, buffer.data(), static_cast<DWORD>(buffer.size()), &bytes, nullptr)) Fail(L"Cannot read a verified file."); if (!bytes) break; hash.Add(buffer.data(), bytes); }
    return hash.Finish();
}
Digest HashBytes(const void* bytes, size_t size) {
    Hash hash; auto data = static_cast<const BYTE*>(bytes);
    while (size) { auto part = static_cast<DWORD>((std::min)(size, size_t(65536))); hash.Add(data, part); data += part; size -= part; }
    return hash.Finish();
}
std::wstring Hex(const Digest& value) {
    const wchar_t digits[] = L"0123456789abcdef"; std::wstring result; result.reserve(64);
    for (auto byte : value) { result.push_back(digits[byte >> 4]); result.push_back(digits[byte & 15]); }
    return result;
}
std::wstring FullPath(const std::wstring& path) {
    if (path.size() < 3 || path[1] != L':' || path[2] != L'\\' || path.size() > 32000) throw Failure(L"Only a local absolute path is allowed.");
    std::vector<wchar_t> buffer(32768);
    auto count = GetFullPathNameW(path.c_str(), static_cast<DWORD>(buffer.size()), buffer.data(), nullptr);
    if (!count || count >= buffer.size()) Fail(L"Cannot normalize the file path.");
    return std::wstring(buffer.data(), count);
}
void VerifyHandlePath(HANDLE handle, const std::wstring& path, bool directory) {
    BY_HANDLE_FILE_INFORMATION info{};
    if (!GetFileInformationByHandle(handle, &info)) Fail(L"Cannot verify a file handle.");
    if (info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT || !!(info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != directory)
        throw Failure(L"The application path contains a link or a replaced object.");
    std::vector<wchar_t> name(32768);
    auto count = GetFinalPathNameByHandleW(handle, name.data(), static_cast<DWORD>(name.size()), FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
    if (!count || count >= name.size()) Fail(L"Cannot verify the final file path.");
    std::wstring actual(name.data(), count), expected = FullPath(path);
    if (actual.rfind(L"\\\\?\\", 0) == 0) actual.erase(0, 4);
    while (!actual.empty() && actual.back() == L'\\') actual.pop_back();
    while (!expected.empty() && expected.back() == L'\\') expected.pop_back();
    if (CompareStringOrdinal(actual.c_str(), -1, expected.c_str(), -1, TRUE) != CSTR_EQUAL)
        throw Failure(L"The application path changed during verification.");
}
void VerifySecurityDescriptor(PSECURITY_DESCRIPTOR descriptor, bool directory, bool strict) {
    if (!IsValidSecurityDescriptor(descriptor)) throw Failure(L"Invalid application permissions.");
    PSID owner = nullptr; BOOL defaulted = FALSE;
    if (!GetSecurityDescriptorOwner(descriptor, &owner, &defaulted) || !TrustedSid(owner, !strict)) throw Failure(L"The application directory has an untrusted owner.");
    PACL acl = nullptr; BOOL present = FALSE;
    if (!GetSecurityDescriptorDacl(descriptor, &present, &acl, &defaulted) || !present || !acl || !IsValidAcl(acl))
        throw Failure(L"The application permissions are not restricted.");
    SECURITY_DESCRIPTOR_CONTROL control{}; DWORD revision = 0;
    if (!GetSecurityDescriptorControl(descriptor, &control, &revision)) Fail(L"Cannot verify application permissions.");
    bool admin = false, system = false;
    if (strict && (!(control & SE_DACL_PROTECTED) || acl->AceCount != 2)) throw Failure(L"The application cache must be protected for Administrators and SYSTEM only.");
    for (DWORD i = 0; i < acl->AceCount; ++i) {
        void* raw = nullptr; if (!GetAce(acl, i, &raw)) Fail(L"Cannot verify application permissions.");
        auto header = static_cast<ACE_HEADER*>(raw);
        if (!strict && (header->AceFlags & INHERIT_ONLY_ACE || header->AceType == ACCESS_DENIED_ACE_TYPE)) continue;
        if (header->AceType != ACCESS_ALLOWED_ACE_TYPE) throw Failure(L"Unsupported application permissions.");
        auto ace = static_cast<ACCESS_ALLOWED_ACE*>(raw); auto sid = static_cast<PSID>(&ace->SidStart);
        if (strict) {
            const BYTE flags = directory ? OBJECT_INHERIT_ACE | CONTAINER_INHERIT_ACE : 0;
            if (header->AceFlags != flags || ace->Mask != FILE_ALL_ACCESS || !TrustedSid(sid, false)) throw Failure(L"The application cache has untrusted write permissions.");
            if (IsWellKnownSid(sid, WinBuiltinAdministratorsSid)) { if (admin) throw Failure(L"Duplicate cache permission."); admin = true; }
            else { if (system) throw Failure(L"Duplicate cache permission."); system = true; }
        } else if (!TrustedSid(sid, true) && ace->Mask & (DELETE | FILE_DELETE_CHILD | WRITE_DAC | WRITE_OWNER | GENERIC_ALL))
            throw Failure(L"An application directory can be replaced without administrator rights.");
    }
    if (strict && (!admin || !system)) throw Failure(L"Incomplete application cache permissions.");
}
void VerifySecurity(HANDLE handle, bool directory, bool strict) {
    LocalMemory descriptor;
    auto status = GetSecurityInfo(handle, SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION, nullptr, nullptr, nullptr, nullptr, &descriptor.value);
    if (status != ERROR_SUCCESS) Fail(L"Cannot read application permissions.", status);
    VerifySecurityDescriptor(descriptor.value, directory, strict);
}
std::vector<Handle> LockParents(const std::wstring& path, bool trusted) {
    const auto parent = Parent(FullPath(path)); std::vector<Handle> handles;
    for (size_t length = 3;;) {
        const auto part = parent.substr(0, length);
        Handle handle(CreateFileW(part.c_str(), GENERIC_READ | READ_CONTROL, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
        if (!handle.valid()) Fail(L"Cannot lock the application directory.");
        VerifyHandlePath(handle.get(), part, true);
        if (trusted) VerifySecurity(handle.get(), true, false);
        handles.push_back(std::move(handle));
        if (length == parent.size()) break;
        auto slash = parent.find(L'\\', length == 3 ? 3 : length + 1);
        length = slash == std::wstring::npos ? parent.size() : slash;
    }
    return handles;
}
void EnsureProtectedDirectory(const std::wstring& path) {
    auto parents = LockParents(path, true); LocalMemory descriptor;
    SECURITY_ATTRIBUTES attributes{ sizeof(SECURITY_ATTRIBUTES), NewDescriptor(descriptor, true), FALSE };
    if (!CreateDirectoryW(path.c_str(), &attributes) && GetLastError() != ERROR_ALREADY_EXISTS) Fail(L"Cannot create the protected application directory.");
    Handle directory(CreateFileW(path.c_str(), GENERIC_READ | READ_CONTROL, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
        OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    if (!directory.valid()) Fail(L"Cannot open the protected application directory.");
    VerifyHandlePath(directory.get(), path, true); VerifySecurity(directory.get(), true, true);
}
Handle OpenProtectedFile(const std::wstring& path) {
    Handle file(CreateFileW(path.c_str(), GENERIC_READ | READ_CONTROL, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    if (!file.valid()) Fail(L"Cannot open a required application file.");
    VerifyHandlePath(file.get(), path, false); VerifySecurity(file.get(), false, true); return file;
}
Handle CreateProtectedFile(const std::wstring& path) {
    LocalMemory descriptor; SECURITY_ATTRIBUTES attributes{ sizeof(SECURITY_ATTRIBUTES), NewDescriptor(descriptor, false), FALSE };
    Handle file(CreateFileW(path.c_str(), GENERIC_WRITE | READ_CONTROL, 0, &attributes, CREATE_NEW, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_WRITE_THROUGH, nullptr));
    if (!file.valid()) Fail(L"Cannot create a protected application file.");
    VerifyHandlePath(file.get(), path, false); VerifySecurity(file.get(), false, true); return file;
}
std::wstring UserSid() {
    HANDLE raw = nullptr; if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &raw)) Fail(L"Cannot identify the current account.");
    Handle token(raw); DWORD size = 0; GetTokenInformation(token.get(), TokenUser, nullptr, 0, &size);
    if (size < sizeof(TOKEN_USER) || size > 65536) throw Failure(L"Invalid account identity.");
    std::vector<BYTE> info(size);
    if (!GetTokenInformation(token.get(), TokenUser, info.data(), size, &size)) Fail(L"Cannot identify the current account.");
    LocalMemory text;
    if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(info.data())->User.Sid, reinterpret_cast<wchar_t**>(&text.value))) Fail(L"Cannot identify the current account.");
    return static_cast<wchar_t*>(text.value);
}
std::wstring ProtectedCacheRoot() {
    wchar_t common[MAX_PATH]{};
    if (FAILED(SHGetFolderPathW(nullptr, CSIDL_COMMON_APPDATA, nullptr, SHGFP_TYPE_CURRENT, common))) throw Failure(L"Cannot locate the protected application directory.");
    std::wstring path = FullPath(common);
    for (const auto& child : { std::wstring(L"ProcessKeeper"), std::wstring(L"Universal"), UserSid() }) {
        path += L"\\" + child; EnsureProtectedDirectory(path);
    }
    return path;
}
SourceLock::SourceLock() {
    std::vector<wchar_t> name(32768); const auto length = GetModuleFileNameW(nullptr, name.data(), static_cast<DWORD>(name.size()));
    if (!length || length >= name.size()) Fail(L"Cannot verify the original Process Keeper file.");
    path_ = FullPath(std::wstring(name.data(), length)); parents_ = LockParents(path_, false);
    source_ = Handle(CreateFileW(path_.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    if (!source_.valid()) Fail(L"Cannot lock the original Process Keeper file.");
    VerifyHandlePath(source_.get(), path_, false); hash_ = HashFile(source_.get());
}
void SourceLock::Verify() const {
    Handle current(CreateFileW(path_.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    if (!current.valid()) Fail(L"The original Process Keeper file is unavailable.");
    VerifyHandlePath(current.get(), path_, false);
    if (HashFile(current.get()) != hash_) throw Failure(L"The original Process Keeper file changed. Startup stopped.");
}
}
