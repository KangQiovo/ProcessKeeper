#pragma once
#include "Platform.h"
namespace pk {
inline std::wstring HelperText(const wchar_t* english, const wchar_t* simplified, const wchar_t* traditional) {
    wchar_t preference[32]{}; const auto size = GetEnvironmentVariableW(L"PROCESSKEEPER_HELPER_LANGUAGE", preference, 32);
    if (size && size < 32) {
        if (!wcscmp(preference, L"en")) return english;
        if (!wcscmp(preference, L"zh-Hans")) return simplified;
        if (!wcscmp(preference, L"zh-Hant")) return traditional;
    }
    const auto language = GetUserDefaultUILanguage();
    if (PRIMARYLANGID(language) != LANG_CHINESE) return english;
    return SUBLANGID(language) == SUBLANG_CHINESE_TRADITIONAL || SUBLANGID(language) == SUBLANG_CHINESE_HONGKONG || SUBLANGID(language) == SUBLANG_CHINESE_MACAU ? traditional : simplified;
}
}
