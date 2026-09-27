#pragma once
#include "LaunchContext.h"
namespace pk {
std::wstring EnsureDesktopShortcut(const LaunchContext& context, const std::wstring& request);
#ifdef PK_FIXTURE_BUILD
std::wstring EnsureShortcutFixture(const std::wstring& original, const std::wstring& desktop);
#endif
}
