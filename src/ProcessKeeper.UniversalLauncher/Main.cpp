#include "LaunchContext.h"
#include <shellapi.h>
#include <sddl.h>
#include <thread>
#include <atomic>
#include <algorithm>

namespace {
constexpr UINT Completed = WM_APP + 1;
constexpr UINT ReplaceIdleWindow = WM_APP + 2;
constexpr int Primary = 100, Copy = 101, Retry = 102, Exit = 103, Compatible = 104;
enum class Page { Permission, MissingFramework, Unsupported, Preparing, Failed };
struct App {
    pk::SourceLock source;
    pk::Host host;
    pk::Handle elevated, child;
    HWND window = nullptr, heading = nullptr, body = nullptr, buttons[5]{};
    HFONT font = nullptr, titleFont = nullptr;
    std::atomic_bool canceled{false};
    std::thread worker;
    Page page = Page::Preparing;
    std::wstring detail, url, result;
    bool working = false, closing = false, succeeded = false;
    std::atomic_bool childStarted{false};
    bool forceLegacy = false;
    int language = 0;
    int dpi = 96;
    int Scale(int value) const { return MulDiv(value, dpi, 96); }
    const wchar_t* Text(const wchar_t* en, const wchar_t* zh, const wchar_t* tw) const { return language == 1 ? zh : language == 2 ? tw : en; }
    ~App() { if (worker.joinable()) worker.join(); if (font) DeleteObject(font); if (titleFont) DeleteObject(titleFont); }
};
App* active = nullptr;
BOOL CALLBACK ReplaceOlderWindow(HWND window, LPARAM parameter) {
    auto& app = *reinterpret_cast<App*>(parameter); DWORD processId = 0; GetWindowThreadProcessId(window, &processId);
    if (processId == GetCurrentProcessId()) return TRUE;
    wchar_t className[128]{}; GetClassNameW(window, className, 128);
    if (wcscmp(className, L"ProcessKeeper.Universal.Startup")) return TRUE;
    pk::Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, processId));
    if (!process.valid()) return TRUE;
    wchar_t executable[32768]{}; DWORD length = 32768;
    if (!QueryFullProcessImageNameW(process.get(), 0, executable, &length) || CompareStringOrdinal(executable, -1, app.source.path().c_str(), -1, TRUE) != CSTR_EQUAL) return TRUE;
    HANDLE tokenRaw = nullptr; if (!OpenProcessToken(process.get(), TOKEN_QUERY, &tokenRaw)) return TRUE;
    pk::Handle token(tokenRaw); DWORD bytes = 0; GetTokenInformation(token.get(), TokenUser, nullptr, 0, &bytes);
    if (bytes < sizeof(TOKEN_USER) || bytes > 65536) return TRUE;
    try {
        std::vector<BYTE> info(bytes);
        if (!GetTokenInformation(token.get(), TokenUser, info.data(), bytes, &bytes)) return TRUE;
        wchar_t* sid = nullptr;
        if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(info.data())->User.Sid, &sid)) return TRUE;
        const bool sameUser = pk::UserSid() == sid; LocalFree(sid);
        if (sameUser) PostMessageW(window, ReplaceIdleWindow, 0, 0);
    } catch (...) { }
    return TRUE;
}

void Layout(App& app) {
    RECT area{}; GetClientRect(app.window, &area); const int margin = app.Scale(32), width = (std::max)(120L, area.right - margin * 2);
    int visible = 0; for (auto button : app.buttons) if (GetWindowLongPtrW(button, GWL_STYLE) & WS_VISIBLE) ++visible;
    const int columns = visible == 5 && width < app.Scale(600) ? 3 : (std::max)(visible, 1);
    const int rows = (visible + columns - 1) / columns;
    MoveWindow(app.heading, margin, app.Scale(40), width, app.Scale(72), TRUE);
    MoveWindow(app.body, margin, app.Scale(126), width, (std::max)(0L, area.bottom - app.Scale(218 + (rows - 1) * 46)), TRUE);
    const int buttonWidth = (std::min)(app.Scale(172), (width - (columns - 1) * app.Scale(10)) / columns);
    int index = 0;
    for (const auto button : {app.buttons[0], app.buttons[1], app.buttons[4], app.buttons[2], app.buttons[3]}) if (GetWindowLongPtrW(button, GWL_STYLE) & WS_VISIBLE) {
        const int x = margin + (index % columns) * (buttonWidth + app.Scale(10));
        const int y = area.bottom - app.Scale(66 + (rows - 1 - index / columns) * 46);
        MoveWindow(button, x, y, buttonWidth, app.Scale(36), TRUE); ++index;
    }
}
void Render(App& app) {
    std::wstring title, text;
    app.url.clear();
    for (auto button : app.buttons) ShowWindow(button, SW_HIDE);
    auto show = [&](int id, const wchar_t* label) { SetWindowTextW(app.buttons[id - Primary], label); ShowWindow(app.buttons[id - Primary], SW_SHOW); };
    if (app.page == Page::Permission) {
        title = app.Text(L"Administrator access is needed", L"需要管理员权限", L"需要系統管理員權限");
        text = app.Text(L"Process Keeper needs permission to inspect and manage applications. Choose Allow access to try again.",
            L"Process Keeper 需要授权才能查看和管理应用。点击“授予权限”可重新请求授权。", L"Process Keeper 需要授權才能檢視和管理應用程式。按一下「授予權限」可重新要求授權。");
        show(Primary, app.Text(L"Allow access", L"授予权限", L"授予權限"));
    } else if (app.page == Page::MissingFramework) {
        title = app.Text(L"A Windows component is missing", L"缺少运行组件", L"缺少執行元件");
        text = app.Text(L"The compatibility interface requires .NET Framework 4.6.2 or later. Open Microsoft's website, install a supported version, then check again. Nothing is downloaded or installed automatically.",
            L"兼容界面需要 .NET Framework 4.6.2 或更高版本。请打开微软官网，安装适用版本后重新检测。程序不会自动下载或安装。",
            L"相容介面需要 .NET Framework 4.6.2 或更新版本。請開啟微軟官網，安裝適用版本後重新偵測。程式不會自動下載或安裝。");
        app.url = pk::FrameworkUrl(app.host);
        show(Primary, app.Text(L"Microsoft", L"微软官网", L"微軟官網"));
        show(Copy, app.Text(L"Copy link", L"复制链接", L"複製連結"));
        show(Retry, app.Text(L"Check again", L"重新检测", L"重新偵測"));
    } else if (app.page == Page::Unsupported) {
        title = app.Text(L"This Windows version is unsupported", L"此系统暂不支持", L"此系統暫不支援");
        text = app.Text(L"This package supports Intel/AMD Windows 7 SP1, Windows 8.1, and Windows 10/11. Windows 8 RTM and ARM systems have no verified route in this package.",
            L"此版本适用于 Intel/AMD 架构的 Windows 7 SP1、Windows 8.1 和 Windows 10/11。Windows 8 初始版本及 ARM 系统暂没有经过验证的运行方案。",
            L"此版本適用於 Intel/AMD 架構的 Windows 7 SP1、Windows 8.1 和 Windows 10/11。Windows 8 初始版本及 ARM 系統暫無經過驗證的執行方案。");
        app.url = L"https://learn.microsoft.com/dotnet/framework/get-started/system-requirements";
        show(Primary, app.Text(L"Requirements", L"系统要求", L"系統需求"));
        show(Copy, app.Text(L"Copy link", L"复制链接", L"複製連結"));
    } else if (app.page == Page::Preparing) {
        title = app.Text(L"Starting Process Keeper", L"正在启动 Process Keeper", L"正在啟動 Process Keeper");
        text = app.closing ? (app.childStarted ? app.Text(L"Closing this status window. The started application may continue running.", L"正在关闭此状态窗口，已启动的应用可能继续运行。", L"正在關閉此狀態視窗，已啟動的應用程式可能繼續執行。") : app.Text(L"Stopping startup safely...", L"正在安全停止启动…", L"正在安全停止啟動…")) :
            app.Text(L"Checking and preparing the application. The first launch can take a little longer.", L"正在校验并准备应用。首次启动可能需要稍长时间。", L"正在校驗並準備應用程式。首次啟動可能需要稍長時間。");
    } else {
        title = app.Text(L"Process Keeper could not start", L"Process Keeper 未能启动", L"Process Keeper 未能啟動");
        text = app.Text(L"Windows reported the following problem. No application window has been confirmed.", L"Windows 返回了以下问题，尚未确认应用窗口已显示。", L"Windows 回報了以下問題，尚未確認應用程式視窗已顯示。");
        app.url = pk::ChooseRoute(app.host, app.forceLegacy) == pk::Route::ModernX64 ? L"https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps" :
            L"https://learn.microsoft.com/dotnet/framework/install/guide-for-developers";
        show(Primary, app.Text(L"Help", L"微软帮助", L"微軟說明"));
        show(Copy, app.Text(L"Copy link", L"复制链接", L"複製連結"));
        if (!app.child.valid() || WaitForSingleObject(app.child.get(), 0) == WAIT_OBJECT_0)
            show(Retry, app.Text(L"Try again", L"重试", L"重試"));
        const bool childRunning = app.child.valid() && WaitForSingleObject(app.child.get(), 0) != WAIT_OBJECT_0;
        if (pk::CanOfferLegacy(pk::ChooseRoute(app.host), app.forceLegacy, childRunning))
            show(Compatible, app.Text(L"Legacy UI", L"兼容界面", L"相容介面"));
    }
    if (!app.detail.empty()) text += L"\r\n\r\n" + app.detail;
    if (!app.url.empty()) text += L"\r\n\r\n" + app.url;
    show(Exit, app.Text(L"Exit", L"退出", L"結束"));
    SetWindowTextW(app.heading, title.c_str()); SetWindowTextW(app.body, text.c_str()); Layout(app);
    InvalidateRect(app.window, nullptr, TRUE);
}

bool RequestElevation(App& app) {
#ifdef PK_UI_FIXTURE
    app.detail = app.Text(L"Fixture: access was canceled. No UAC request was made.", L"测试：模拟取消授权，没有请求真实 UAC。", L"測試：模擬取消授權，沒有要求真實 UAC。");
    app.page = Page::Permission; Render(app); return false;
#else
    try {
        app.source.Verify();
        SHELLEXECUTEINFOW operation{ sizeof(operation) }; operation.fMask = SEE_MASK_NOCLOSEPROCESS | SEE_MASK_NOASYNC;
        operation.hwnd = app.window; operation.lpVerb = L"runas"; operation.lpFile = app.source.path().c_str(); operation.nShow = SW_SHOWNORMAL;
        if (!ShellExecuteExW(&operation)) {
            const auto error = GetLastError();
            app.detail = error == ERROR_CANCELLED ? L"" : pk::ErrorText(error);
            app.page = Page::Permission; Render(app); ShowWindow(app.window, SW_SHOW); return false;
        }
        if (!operation.hProcess) throw pk::Failure(L"Windows did not return the elevated process handle.");
        app.elevated = pk::Handle(operation.hProcess);
        // Keep the original EXE and all of its parent directories locked until the elevated copy exits.
        ShowWindow(app.window, SW_HIDE); SetTimer(app.window, 1, 500, nullptr); return true;
    } catch (const pk::Failure& error) { app.detail = error.message; app.page = Page::Failed; Render(app); ShowWindow(app.window, SW_SHOW); return false; }
#endif
}

struct WindowCheck { DWORD process; bool found = false; };
BOOL CALLBACK FindApplicationWindow(HWND window, LPARAM parameter) {
    auto& check = *reinterpret_cast<WindowCheck*>(parameter); DWORD process = 0; GetWindowThreadProcessId(window, &process);
    if (process != check.process || !IsWindowVisible(window) || IsIconic(window)) return TRUE;
    wchar_t className[256]{}; GetClassNameW(window, className, 256);
    wchar_t title[256]{}; GetWindowTextW(window, title, 256);
    if (!pk::IsApplicationWindowIdentity(className, title)) return TRUE;
    RECT bounds{}; if (!GetWindowRect(window, &bounds) || bounds.right - bounds.left < 100 || bounds.bottom - bounds.top < 80) return TRUE;
    if (!MonitorFromRect(&bounds, MONITOR_DEFAULTTONULL)) return TRUE;
    check.found = true; return FALSE;
}
void StartApplication(App& app) {
    if (app.working) return;
    app.host = pk::DetectHost(); const auto route = pk::ChooseRoute(app.host, app.forceLegacy);
    app.detail.clear();
    if (route == pk::Route::Unsupported || route == pk::Route::MissingFramework) {
        app.page = route == pk::Route::Unsupported ? Page::Unsupported : Page::MissingFramework; Render(app); ShowWindow(app.window, SW_SHOW); return;
    }
#ifdef PK_UI_FIXTURE
    app.page = Page::Preparing; Render(app); return;
#else
    if (!pk::IsAdministrator()) { RequestElevation(app); return; }
    app.page = Page::Preparing; app.working = true; app.canceled = false; app.succeeded = false; app.childStarted = false; app.result.clear();
    Render(app); ShowWindow(app.window, SW_SHOW);
    app.worker = std::thread([&app, route] {
        try {
            auto payload = pk::PreparePayload(route == pk::Route::ModernX64, &app.canceled);
            if (app.canceled) throw pk::Failure(L"Startup canceled.");
            const auto executable = payload.directory + L"\\ProcessKeeper.exe";
            auto command = L"\"" + executable + L"\"";
            STARTUPINFOW startup{ sizeof(startup) }; PROCESS_INFORMATION process{};
            const auto contextId = pk::NewContextId();
            auto environment = pk::LaunchEnvironment(contextId);
            if (!CreateProcessW(executable.c_str(), &command[0], nullptr, nullptr, FALSE, CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED,
                environment.data(), payload.directory.c_str(), &startup, &process)) pk::Fail(L"Windows could not start the application.");
            app.childStarted = true;
            app.child = pk::Handle(process.hProcess); pk::Handle thread(process.hThread);
            try {
                pk::WriteLaunchContext(app.source, payload, contextId, app.child.get(), process.dwProcessId);
                if (ResumeThread(thread.get()) == static_cast<DWORD>(-1)) pk::Fail(L"Cannot resume the verified application.");
            } catch (...) { TerminateProcess(app.child.get(), 1); throw; } // Only this newly created, still-suspended owned child.
            // A console, a process handle, and input-idle alone are never reported as a displayed application.
            for (int attempt = 0; attempt < 240; ++attempt) {
                if (app.canceled) throw pk::Failure(L"Startup canceled.");
                if (WaitForSingleObject(app.child.get(), 0) == WAIT_OBJECT_0) {
                    DWORD code = 0; if (!GetExitCodeProcess(app.child.get(), &code)) pk::Fail(L"Cannot read the application exit status.");
                    throw pk::Failure(L"The application exited before a visible window was confirmed.\r\n" + pk::ErrorText(code));
                }
                WindowCheck check{ process.dwProcessId }; EnumWindows(FindApplicationWindow, reinterpret_cast<LPARAM>(&check));
                if (check.found) { app.succeeded = true; break; }
                Sleep(250);
            }
            if (!app.succeeded) throw pk::Failure(L"The process is running, but no visible application window was confirmed within 60 seconds.");
        } catch (const pk::Failure& error) { app.result = error.message; }
        catch (...) { app.result = L"An unexpected startup error occurred."; }
        PostMessageW(app.window, Completed, 0, 0);
    });
#endif
}
void OpenWebsite(App& app) {
    if (app.url.empty()) return;
#ifdef PK_UI_FIXTURE
    return;
#else
    const auto result = reinterpret_cast<INT_PTR>(ShellExecuteW(app.window, L"open", app.url.c_str(), nullptr, nullptr, SW_SHOWNORMAL));
    if (result <= 32) { app.detail = app.Text(L"The default browser could not be opened. Use Copy link instead.", L"无法打开默认浏览器，请复制链接后打开。", L"無法開啟預設瀏覽器，請複製連結後開啟。"); Render(app); }
#endif
}
void CopyLink(App& app) {
    if (app.url.empty() || !OpenClipboard(app.window)) return;
    const auto bytes = (app.url.size() + 1) * sizeof(wchar_t); auto memory = GlobalAlloc(GMEM_MOVEABLE, bytes);
    if (memory) {
        auto value = GlobalLock(memory);
        if (value) { memcpy(value, app.url.c_str(), bytes); GlobalUnlock(memory); EmptyClipboard(); if (SetClipboardData(CF_UNICODETEXT, memory)) memory = nullptr; }
        if (memory) GlobalFree(memory);
    }
    CloseClipboard();
}
LRESULT CALLBACK Procedure(HWND window, UINT message, WPARAM wparam, LPARAM lparam) {
    if (message == WM_NCCREATE) { active->window = window; return TRUE; }
    auto& app = *active;
    switch (message) {
    case WM_CREATE: {
        app.font = CreateFontW(-app.Scale(17), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
        app.titleFont = CreateFontW(-app.Scale(28), 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
        app.heading = CreateWindowExW(0, L"STATIC", L"", WS_CHILD | WS_VISIBLE | SS_NOPREFIX, 0, 0, 0, 0, window, nullptr, nullptr, nullptr);
        app.body = CreateWindowExW(0, L"EDIT", L"", WS_CHILD | WS_VISIBLE | ES_MULTILINE | ES_READONLY | ES_AUTOVSCROLL | WS_VSCROLL, 0, 0, 0, 0, window, nullptr, nullptr, nullptr);
        SendMessageW(app.heading, WM_SETFONT, reinterpret_cast<WPARAM>(app.titleFont), FALSE);
        SendMessageW(app.body, WM_SETFONT, reinterpret_cast<WPARAM>(app.font), FALSE);
        for (int i = 0; i < 5; ++i) {
            app.buttons[i] = CreateWindowExW(0, L"BUTTON", L"", WS_CHILD | WS_TABSTOP | BS_PUSHBUTTON, 0, 0, 0, 0, window, reinterpret_cast<HMENU>(static_cast<INT_PTR>(Primary + i)), nullptr, nullptr);
            SendMessageW(app.buttons[i], WM_SETFONT, reinterpret_cast<WPARAM>(app.font), FALSE);
        }
        return 0;
    }
    case WM_GETMINMAXINFO: {
        MONITORINFO monitor{ sizeof(monitor) };
        int width = app.Scale(580), height = app.Scale(440);
        if (GetMonitorInfoW(MonitorFromWindow(window, MONITOR_DEFAULTTOPRIMARY), &monitor)) {
            width = (std::min)(width, static_cast<int>(monitor.rcWork.right - monitor.rcWork.left));
            height = (std::min)(height, static_cast<int>(monitor.rcWork.bottom - monitor.rcWork.top));
        }
        reinterpret_cast<MINMAXINFO*>(lparam)->ptMinTrackSize = { width, height }; return 0;
    }
    case WM_SIZE: Layout(app); return 0;
    case WM_CTLCOLORSTATIC:
    case WM_CTLCOLOREDIT: SetBkColor(reinterpret_cast<HDC>(wparam), GetSysColor(COLOR_WINDOW)); SetTextColor(reinterpret_cast<HDC>(wparam), GetSysColor(COLOR_WINDOWTEXT)); return reinterpret_cast<LRESULT>(GetSysColorBrush(COLOR_WINDOW));
    case WM_TIMER:
        if (wparam == 1 && app.elevated.valid() && WaitForSingleObject(app.elevated.get(), 0) == WAIT_OBJECT_0) DestroyWindow(window);
#ifdef PK_UI_FIXTURE
        if (wparam == 2) DestroyWindow(window);
#endif
        return 0;
    case WM_COMMAND:
        if (HIWORD(wparam) != BN_CLICKED) break;
        switch (LOWORD(wparam)) {
        case Primary: if (app.page == Page::Permission) RequestElevation(app); else OpenWebsite(app); return 0;
        case Copy: CopyLink(app); return 0;
        case Retry: StartApplication(app); return 0;
        case Compatible:
            if (!app.working && app.page == Page::Failed && pk::CanOfferLegacy(pk::ChooseRoute(app.host), app.forceLegacy, app.child.valid() && WaitForSingleObject(app.child.get(), 0) != WAIT_OBJECT_0)) { app.forceLegacy = true; StartApplication(app); }
            return 0;
        case Exit: SendMessageW(window, WM_CLOSE, 0, 0); return 0;
        }
        break;
    case Completed:
        if (app.worker.joinable()) app.worker.join(); app.working = false;
        if (app.closing || app.succeeded) DestroyWindow(window);
        else { app.page = Page::Failed; app.detail = app.result; Render(app); }
        return 0;
    case ReplaceIdleWindow:
        if (!app.working && !app.elevated.valid() && (app.page == Page::Permission || app.page == Page::Failed || app.page == Page::MissingFramework || app.page == Page::Unsupported)) DestroyWindow(window);
        return 0;
    case WM_CLOSE:
        if (app.working) { app.closing = true; app.canceled = true; Render(app); } else DestroyWindow(window);
        return 0;
    case WM_DESTROY: PostQuitMessage(0); return 0;
    }
    return DefWindowProcW(window, message, wparam, lparam);
}
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, wchar_t* arguments, int) {
    // Remove the current working directory from subsequent dependency searches. Optional libraries use absolute System32 paths.
    SetDllDirectoryW(L"");
    try {
        App app; active = &app; app.host = pk::DetectHost();
        auto display = GetDC(nullptr); if (display) { app.dpi = GetDeviceCaps(display, LOGPIXELSY); ReleaseDC(nullptr, display); }
        const auto language = GetUserDefaultUILanguage();
        if (PRIMARYLANGID(language) == LANG_CHINESE) app.language = SUBLANGID(language) == SUBLANG_CHINESE_TRADITIONAL || SUBLANGID(language) == SUBLANG_CHINESE_HONGKONG || SUBLANGID(language) == SUBLANG_CHINESE_MACAU ? 2 : 1;
        WNDCLASSEXW type{ sizeof(type) }; type.lpfnWndProc = Procedure; type.hInstance = instance;
        type.hCursor = LoadCursorW(nullptr, IDC_ARROW); type.hIcon = static_cast<HICON>(LoadImageW(instance, MAKEINTRESOURCEW(201), IMAGE_ICON, 0, 0, LR_DEFAULTSIZE));
        type.hIconSm = type.hIcon; type.hbrBackground = GetSysColorBrush(COLOR_WINDOW); type.lpszClassName = L"ProcessKeeper.Universal.Startup";
        if (!RegisterClassExW(&type)) pk::Fail(L"Cannot register the startup window.");
        RECT work{}; int width = app.Scale(740), height = app.Scale(510), x = CW_USEDEFAULT, y = CW_USEDEFAULT;
        if (SystemParametersInfoW(SPI_GETWORKAREA, 0, &work, 0)) {
            width = (std::min)(width, static_cast<int>(work.right - work.left)); height = (std::min)(height, static_cast<int>(work.bottom - work.top));
            x = work.left + (work.right - work.left - width) / 2; y = work.top + (work.bottom - work.top - height) / 2;
        }
        auto window = CreateWindowExW(0, type.lpszClassName, L"Process Keeper", WS_OVERLAPPEDWINDOW, x, y, width, height, nullptr, nullptr, instance, nullptr);
        if (!window) pk::Fail(L"Cannot create the startup window.");
        EnumWindows(ReplaceOlderWindow, reinterpret_cast<LPARAM>(&app));
#ifdef PK_UI_FIXTURE
        const std::wstring options(arguments);
        if (options.find(L"zh-CN") != std::wstring::npos) app.language = 1;
        else if (options.find(L"zh-TW") != std::wstring::npos) app.language = 2;
        else app.language = 0;
        app.page = options.find(L"framework") != std::wstring::npos ? Page::MissingFramework : options.find(L"unsupported") != std::wstring::npos ? Page::Unsupported : options.find(L"failed") != std::wstring::npos ? Page::Failed : Page::Permission;
        if (app.page == Page::Failed) app.detail = L"Fixture diagnostic | 0xC0000135";
        if (options.find(L"exit-success") != std::wstring::npos) app.succeeded = true;
        Render(app); ShowWindow(window, SW_SHOW); SetTimer(window, 2, options.find(L"exit-") != std::wstring::npos ? 100 : 15000, nullptr);
#else
        if (*arguments) { app.page = Page::Failed; app.detail = L"This launcher does not accept command-line parameters."; Render(app); ShowWindow(window, SW_SHOW); }
        else if (pk::ChooseRoute(app.host) == pk::Route::Unsupported) StartApplication(app);
        else if (!pk::IsAdministrator()) RequestElevation(app);
        else StartApplication(app);
#endif
        MSG message{}; while (GetMessageW(&message, nullptr, 0, 0) > 0) { if (!IsDialogMessageW(window, &message)) { TranslateMessage(&message); DispatchMessageW(&message); } }
        if (app.succeeded) return 0;
        if (app.elevated.valid()) { DWORD code = 2; if (GetExitCodeProcess(app.elevated.get(), &code) && code != STILL_ACTIVE) return code == 0 ? 0 : code == 1 ? 1 : 2; return 2; }
#ifdef PK_UI_FIXTURE
        if (options.find(L"exit-unconfirmed") != std::wstring::npos) return 2;
#endif
        return app.child.valid() && WaitForSingleObject(app.child.get(), 0) == WAIT_TIMEOUT ? 2 : 1;
    } catch (const pk::Failure& error) { MessageBoxW(nullptr, error.message.c_str(), L"Process Keeper", MB_ICONERROR | MB_OK); return 1; }
    catch (...) { MessageBoxW(nullptr, L"Process Keeper could not initialize its startup components.", L"Process Keeper", MB_ICONERROR | MB_OK); return 1; }
}
