// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once
#include "protocol.h"
#include <array>

namespace phonon {
constexpr wchar_t SettingsKey[] = L"Software\\Onj\\PhononSAPI";
using AdapterId = std::array<uint8_t, 16>;

inline DWORD setting(const wchar_t* name, DWORD initial = 0) {
    DWORD value = 0, size = sizeof(value);
    return RegGetValueW(HKEY_CURRENT_USER, SettingsKey, name, RRF_RT_REG_DWORD,
        nullptr, &value, &size) == ERROR_SUCCESS ? value : initial;
}

inline std::wstring selectedAdapter() {
    wchar_t value[AdapterId{}.size() * 2 + 1]{};
    DWORD size = sizeof(value);
    if (RegGetValueW(HKEY_CURRENT_USER, SettingsKey, L"GraphicsAdapter", RRF_RT_REG_SZ,
            nullptr, value, &size) != ERROR_SUCCESS || size != sizeof(value))
        return {};
    std::wstring id(value);
    if (id.size() != AdapterId{}.size() * 2 ||
        id.find_first_not_of(L"0123456789abcdef") != id.npos) return {};
    return id;
}

inline AdapterId decodeAdapter(const std::wstring& id) {
    AdapterId bytes{};
    auto nibble = [](wchar_t ch) { return ch <= L'9' ? ch - L'0' : ch - L'a' + 10; };
    for (size_t i = 0; i < bytes.size(); ++i)
        bytes[i] = static_cast<uint8_t>((nibble(id[2*i]) << 4) | nibble(id[2*i+1]));
    return bytes;
}

inline float voiceTemperature() {
    switch (setting(L"VoiceVariation")) {
        case 1: return .1f; // Less
        case 2: return .7f; // More
        default: return .3f;
    }
}
}
