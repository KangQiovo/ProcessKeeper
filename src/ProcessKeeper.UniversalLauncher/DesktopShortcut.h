#pragma once
#include "LaunchContext.h"
namespace pk {
std::wstring EnsureDesktopShortcut(const LaunchContext& context, const std::wstring& request);
std::wstring RefreshDesktopShortcut(const std::wstring& original);
#ifdef PK_FIXTURE_BUILD
std::wstring EnsureShortcutFixture(const std::wstring& original, const std::wstring& desktop);
std::wstring RefreshShortcutFixture(const std::wstring& original, const std::wstring& desktop);
#endif
}
