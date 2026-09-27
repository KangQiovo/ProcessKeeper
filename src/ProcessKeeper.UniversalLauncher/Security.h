#pragma once
#include "Platform.h"
#include <array>
#include <vector>
#include <memory>

namespace pk {
struct Failure { std::wstring message; explicit Failure(std::wstring text) : message(std::move(text)) {} };
[[noreturn]] void Fail(const wchar_t* context, DWORD error = GetLastError());
class Handle {
    HANDLE value_ = INVALID_HANDLE_VALUE;
public:
    Handle() = default;
    explicit Handle(HANDLE value) : value_(value) {}
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
    Handle(Handle&& other) noexcept : value_(other.release()) {}
    Handle& operator=(Handle&& other) noexcept { if (this != &other) { reset(); value_ = other.release(); } return *this; }
    ~Handle() { reset(); }
    void reset() { if (valid()) CloseHandle(value_); value_ = INVALID_HANDLE_VALUE; }
    HANDLE release() { auto value = value_; value_ = INVALID_HANDLE_VALUE; return value; }
    bool valid() const { return value_ && value_ != INVALID_HANDLE_VALUE; }
    HANDLE get() const { return value_; }
};
using Digest = std::array<BYTE, 32>;
Digest HashFile(HANDLE handle);
Digest HashBytes(const void* bytes, size_t size);
std::wstring Hex(const Digest& value);
std::wstring FullPath(const std::wstring& path);
void VerifyHandlePath(HANDLE handle, const std::wstring& path, bool directory);
void VerifySecurityDescriptor(PSECURITY_DESCRIPTOR descriptor, bool directory, bool strict);
void VerifySecurity(HANDLE handle, bool directory, bool strict);
std::vector<Handle> LockParents(const std::wstring& path, bool trusted);
void EnsureProtectedDirectory(const std::wstring& path);
Handle OpenProtectedFile(const std::wstring& path);
Handle CreateProtectedFile(const std::wstring& path);
std::wstring ProtectedCacheRoot();
std::wstring UserSid();
class SourceLock {
    std::wstring path_;
    std::vector<Handle> parents_;
    Handle source_;
    Digest hash_{};
public:
    SourceLock();
    const std::wstring& path() const { return path_; }
    std::wstring sha256() const { return Hex(hash_); }
    void Verify() const;
};
}
