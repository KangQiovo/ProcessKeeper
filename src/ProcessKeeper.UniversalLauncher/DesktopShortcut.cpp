#include "DesktopShortcut.h"
#include <shlobj.h>
#include <shobjidl.h>
#include <array>

namespace pk { namespace {
struct ShellObjects {
    IShellLinkW* link = nullptr; IPersistStream* persist = nullptr; IStream* stream = nullptr;
    ShellObjects() {
        if (FAILED(CoCreateInstance(CLSID_ShellLink, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&link))) ||
            FAILED(link->QueryInterface(IID_PPV_ARGS(&persist))) || FAILED(CreateStreamOnHGlobal(nullptr, TRUE, &stream)))
            { if (stream) stream->Release(); if (persist) persist->Release(); if (link) link->Release(); throw Failure(L"Cannot create a Windows shortcut object."); }
    }
    ~ShellObjects() { if (stream) stream->Release(); if (persist) persist->Release(); if (link) link->Release(); }
};
void RenameLink(HANDLE file, const std::wstring& path) {
    const auto bytes = static_cast<DWORD>(path.size() * sizeof(wchar_t));
    std::vector<BYTE> buffer(offsetof(FILE_RENAME_INFO, FileName) + bytes + sizeof(wchar_t), 0);
    auto* rename = reinterpret_cast<FILE_RENAME_INFO*>(buffer.data());
    rename->ReplaceIfExists = FALSE; rename->RootDirectory = nullptr; rename->FileNameLength = bytes;
    memcpy(rename->FileName, path.c_str(), bytes + sizeof(wchar_t));
    if (!SetFileInformationByHandle(file, FileRenameInfo, rename, static_cast<DWORD>(buffer.size()))) Fail(L"Cannot refresh the owned desktop shortcut safely.");
    VerifyHandlePath(file, path, false);
}
void DisposeLink(HANDLE file) {
    FILE_DISPOSITION_INFO disposal{TRUE};
    if (!SetFileInformationByHandle(file, FileDispositionInfo, &disposal, sizeof(disposal))) Fail(L"Cannot remove the owned shortcut staging file.");
}
std::wstring CreateLink(const std::wstring& original, const std::wstring& desktop, bool refresh = false) {
    const auto target = FullPath(original), path = FullPath(desktop) + L"\\Process Keeper.lnk";
    auto parents = LockParents(path, false);
    const auto status = GetFileAttributesW(path.c_str()); Handle existing;
    if (status != INVALID_FILE_ATTRIBUTES) {
        existing = Handle(CreateFileW(path.c_str(), GENERIC_READ | (refresh ? DELETE : 0), FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
        if (!existing.valid()) return L"conflict";
        try {
            VerifyHandlePath(existing.get(), path, false); LARGE_INTEGER size{};
            if (!GetFileSizeEx(existing.get(), &size) || size.QuadPart <= 0 || size.QuadPart > 1024 * 1024) return L"conflict";
            std::vector<BYTE> bytes(static_cast<size_t>(size.QuadPart)); DWORD read = 0;
            if (!ReadFile(existing.get(), bytes.data(), static_cast<DWORD>(bytes.size()), &read, nullptr) || read != bytes.size()) return L"conflict";
            ShellObjects object; ULONG written = 0;
            if (FAILED(object.stream->Write(bytes.data(), static_cast<ULONG>(bytes.size()), &written)) || written != bytes.size()) return L"conflict";
            LARGE_INTEGER zero{}; if (FAILED(object.stream->Seek(zero, STREAM_SEEK_SET, nullptr)) || FAILED(object.persist->Load(object.stream))) return L"conflict";
            std::array<wchar_t, 32768> actual{}, arguments{};
            if (FAILED(object.link->GetPath(actual.data(), static_cast<int>(actual.size()), nullptr, SLGP_RAWPATH)) || FAILED(object.link->GetArguments(arguments.data(), static_cast<int>(arguments.size())))) return L"conflict";
            if (arguments[0] != 0 || CompareStringOrdinal(actual.data(), -1, target.c_str(), -1, TRUE) != CSTR_EQUAL) return L"conflict";
            if (!refresh) return L"exists";
        } catch (...) { return L"conflict"; }
    }
    else {
        if (GetLastError() != ERROR_FILE_NOT_FOUND) Fail(L"Cannot inspect the desktop shortcut.");
        if (refresh) return L"absent"; // Updating never enables an opted-out automatic shortcut.
    }
    ShellObjects object;
    const auto working = target.substr(0, target.find_last_of(L'\\'));
    if (FAILED(object.link->SetPath(target.c_str())) || FAILED(object.link->SetArguments(L"")) ||
        FAILED(object.link->SetWorkingDirectory(working.c_str())) || FAILED(object.link->SetDescription(L"Process Keeper")) ||
        FAILED(object.link->SetIconLocation(target.c_str(), 0)) || FAILED(object.link->SetShowCmd(SW_SHOWNORMAL)) ||
        FAILED(object.persist->Save(object.stream, TRUE))) throw Failure(L"Cannot prepare the desktop shortcut.");
    STATSTG stat{}; if (FAILED(object.stream->Stat(&stat, STATFLAG_NONAME)) || stat.cbSize.QuadPart > 1024 * 1024) throw Failure(L"Invalid desktop shortcut size.");
    std::vector<BYTE> bytes(static_cast<size_t>(stat.cbSize.QuadPart)); LARGE_INTEGER zero{}; ULONG read = 0;
    if (FAILED(object.stream->Seek(zero, STREAM_SEEK_SET, nullptr)) || FAILED(object.stream->Read(bytes.data(), static_cast<ULONG>(bytes.size()), &read)) || read != bytes.size()) throw Failure(L"Cannot read the prepared shortcut.");
    const auto temporary = refresh ? path + L".pk-link-" + NewContextId() : path;
    Handle file(CreateFileW(temporary.c_str(), GENERIC_READ | GENERIC_WRITE | DELETE, FILE_SHARE_READ, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_WRITE_THROUGH, nullptr));
    if (!file.valid()) { if (GetLastError() == ERROR_FILE_EXISTS || GetLastError() == ERROR_ALREADY_EXISTS) return L"conflict"; Fail(L"Cannot create the desktop shortcut."); }
    VerifyHandlePath(file.get(), temporary, false); DWORD written = 0;
    if (!WriteFile(file.get(), bytes.data(), static_cast<DWORD>(bytes.size()), &written, nullptr) || written != bytes.size() || !FlushFileBuffers(file.get())) Fail(L"Cannot save the desktop shortcut.");
    if (!refresh) return L"created";
    const auto backup = path + L".pk-link-old-" + NewContextId(); bool backedUp = false, committed = false;
    try {
        RenameLink(existing.get(), backup); backedUp = true;
        RenameLink(file.get(), path); committed = true;
        DisposeLink(existing.get()); existing.reset(); return L"refreshed";
    } catch (...) {
        if (!committed) {
            try { DisposeLink(file.get()); file.reset(); } catch (...) {}
            if (backedUp) { try { RenameLink(existing.get(), path); } catch (...) {} }
        }
        throw;
    }
}
}
std::wstring EnsureDesktopShortcut(const LaunchContext& context, const std::wstring& request) {
    if (!ValidContextId(request)) throw Failure(L"Invalid shortcut request.");
    auto caller = OpenContextProcess(context);
    wchar_t desktop[MAX_PATH]{};
    if (FAILED(SHGetFolderPathW(nullptr, CSIDL_DESKTOPDIRECTORY, nullptr, SHGFP_TYPE_CURRENT, desktop))) throw Failure(L"Cannot locate the current account's desktop.");
    auto sourceParents = LockParents(context.original, false);
    Handle original(CreateFileW(context.original.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    if (!original.valid()) Fail(L"Cannot verify the original EXE for a shortcut."); VerifyHandlePath(original.get(), context.original, false);
    if (Hex(HashFile(original.get())) != context.originalHash) throw Failure(L"The original EXE changed; no shortcut was created.");
    auto result = CreateLink(context.original, desktop);
    WriteProtectedLines(context.directory + L"\\shortcut-" + request + L".txt", {L"PKLINK1", result, EncodeContextText(std::wstring(desktop) + L"\\Process Keeper.lnk")});
    return result;
}
std::wstring RefreshDesktopShortcut(const std::wstring& original) {
    const auto initialized = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(initialized) && initialized != RPC_E_CHANGED_MODE) throw Failure(L"Cannot initialize Windows shortcut support.");
    try {
        wchar_t desktop[MAX_PATH]{};
        if (FAILED(SHGetFolderPathW(nullptr, CSIDL_DESKTOPDIRECTORY, nullptr, SHGFP_TYPE_CURRENT, desktop))) throw Failure(L"Cannot locate the current account's desktop.");
        auto result = CreateLink(original, desktop, true);
        if (SUCCEEDED(initialized)) CoUninitialize(); return result;
    } catch (...) { if (SUCCEEDED(initialized)) CoUninitialize(); throw; }
}
#ifdef PK_FIXTURE_BUILD
std::wstring EnsureShortcutFixture(const std::wstring& original, const std::wstring& desktop) { return CreateLink(original, desktop); }
std::wstring RefreshShortcutFixture(const std::wstring& original, const std::wstring& desktop) { return CreateLink(original, desktop, true); }
#endif
}
