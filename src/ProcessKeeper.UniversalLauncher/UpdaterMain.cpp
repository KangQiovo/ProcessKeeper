#include "UpdateTransaction.h"
#include "DesktopShortcut.h"
#include "CacheCleanup.h"
#include <shellapi.h>
#include <objbase.h>
#include "HelperText.h"

int WINAPI wWinMain(HINSTANCE, HINSTANCE, wchar_t*, int) {
    std::wstring statusPath;
    const auto title = pk::HelperText(L"Process Keeper | Update", L"Process Keeper | 更新", L"Process Keeper | 更新");
    const auto failureSummary = pk::HelperText(L"The operation did not complete. Check the details below; no unrelated file is overwritten.", L"操作未完成，请检查下方详细信息。无关文件不会被覆盖。", L"操作未完成，請檢查下方詳細資訊。無關檔案不會被覆寫。");
    try {
        if (!pk::IsAdministrator()) throw pk::Failure(L"Start the original Process Keeper EXE and approve its administrator request first.");
        int count = 0; auto arguments = CommandLineToArgvW(GetCommandLineW(), &count);
        if (!arguments || count != 4) { if (arguments) LocalFree(arguments); throw pk::Failure(L"Invalid trusted update request."); }
        const std::wstring action(arguments[1]), contextId(arguments[2]), request(arguments[3]); LocalFree(arguments);
        if ((action != L"--install" && action != L"--shortcut" && action != L"--clear-cache") || !pk::ValidContextId(contextId) || !pk::ValidContextId(request)) throw pk::Failure(L"Invalid trusted update request.");
        const auto context = pk::ReadLaunchContext(contextId); pk::SourceLock own;
        auto ownFile = pk::OpenProtectedFile(own.path());
        if (own.path() != context.helper || pk::Hex(pk::HashFile(ownFile.get())) != context.helperHash) throw pk::Failure(L"This helper does not belong to the trusted launcher context.");
        if (action == L"--shortcut") {
            const auto initialized = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
            if (FAILED(initialized)) throw pk::Failure(L"Windows shortcut services could not initialize.");
            try { pk::EnsureDesktopShortcut(context, request); } catch (...) { CoUninitialize(); throw; }
            CoUninitialize(); return 0;
        }
        if (action == L"--clear-cache") { pk::ClearPendingPayloadCache(context, request); return 0; }
        statusPath = context.directory + L"\\job-" + request + L"\\status.txt";
        const auto result = pk::InstallUpdate(context, request);
        if (!result.installed || result.message.find(L"not confirmed") != std::wstring::npos) {
            const auto summary = result.installed ? pk::HelperText(L"The new EXE was written, but its application window was not confirmed. The original backup is retained.", L"新版 EXE 已写入，但尚未确认应用窗口。原版备份已保留。", L"新版 EXE 已寫入，但尚未確認應用程式視窗。原版備份已保留。") :
                result.restored ? pk::HelperText(L"The update failed. The original EXE was restored; start it again.", L"更新失败，已恢复原始 EXE，请重新启动。", L"更新失敗，已還原原始 EXE，請重新啟動。") : failureSummary;
            MessageBoxW(nullptr, (summary + L"\r\n\r\n" + result.message).c_str(), title.c_str(), MB_OK | MB_ICONWARNING);
        }
        return result.installed ? 0 : 1;
    } catch (const pk::Failure& error) {
        if (!statusPath.empty()) { try { pk::WriteProtectedLines(statusPath, {L"PKSTATUS1", L"failed", pk::EncodeContextText(error.message), L""}); } catch (...) {} }
        MessageBoxW(nullptr, (failureSummary + L"\r\n\r\n" + error.message).c_str(), title.c_str(), MB_OK | MB_ICONERROR); return 1;
    } catch (...) {
        MessageBoxW(nullptr, pk::HelperText(L"The trusted helper could not complete. The original EXE or its backup has been retained.", L"可信助手未能完成操作，原始 EXE 或其备份已保留。", L"可信助手未能完成操作，原始 EXE 或其備份已保留。").c_str(), title.c_str(), MB_OK | MB_ICONERROR); return 1;
    }
}
