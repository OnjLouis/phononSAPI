// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once
#include <shlobj.h>

namespace phonon {
inline std::wstring voiceStorage() {
#ifdef PHONON_IMPORT_TEST
    wchar_t local[32768];
    DWORD size = GetEnvironmentVariableW(L"LOCALAPPDATA", local, 32768);
    if (!size || size >= 32768) return {};
    return std::wstring(local, size) + L"\\PhononSAPI\\ImportedVoices";
#else
    PWSTR path = nullptr;
    if (FAILED(SHGetKnownFolderPath(FOLDERID_LocalAppData, KF_FLAG_DEFAULT, nullptr, &path))) return {};
    std::unique_ptr<wchar_t, decltype(&CoTaskMemFree)> owner(path, CoTaskMemFree);
    return std::wstring(path) + L"\\PhononSAPI\\ImportedVoices";
#endif
}

inline bool importedVoiceId(const std::string& id) {
    const std::string prefix = "imported-";
    return id.size() == prefix.size() + 32 && id.compare(0, prefix.size(), prefix) == 0 &&
        std::all_of(id.begin() + prefix.size(), id.end(), [](char ch) {
            return (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f');
        });
}

inline bool selectImportedVoice(void* engine, const std::string& id, std::string& active,
                                bool (*select)(void*, const uint8_t*, size_t)) {
    constexpr DWORD MaxVoiceBytes = 4 * 1024 * 1024;
    const auto folder = voiceStorage();
    if (!select || folder.empty() || !importedVoiceId(id)) return false;
    const auto path = folder + L"\\" + std::wstring(id.begin(), id.end()) + L".safetensors";
    Handle file(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr,
                           OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    BY_HANDLE_FILE_INFORMATION info{};
    if (file.value == INVALID_HANDLE_VALUE || GetFileType(file) != FILE_TYPE_DISK ||
        !GetFileInformationByHandle(file, &info) ||
        (info.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) ||
        info.nFileSizeHigh || !info.nFileSizeLow || info.nFileSizeLow > MaxVoiceBytes) return false;
    if (active == id) return true;
    std::vector<uint8_t> bytes(info.nFileSizeLow);
    if (!readExact(file, bytes.data(), bytes.size()) || !select(engine, bytes.data(), bytes.size())) return false;
    active = id;
    return true;
}
}
