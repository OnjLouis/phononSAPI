// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdint>
#include <string>
#include <stdexcept>

namespace phonon {
constexpr uint32_t Magic = 0x31534850;
constexpr uint32_t MaxText = 16384;
constexpr uint32_t MaxFrame = 1024 * 1024;
constexpr uint32_t SampleRate = 24000;
constexpr float MaxSpeed = 5.f;
enum class Frame : uint32_t { Audio = 1, Done = 2, Error = 3 };
struct Request {
    uint32_t magic = Magic;
    uint32_t textBytes = 0;
    uint32_t voiceBytes = 0;
    uint32_t languageBytes = 0;
    float speed = 1;
    float pitch = 1;
    float volume = 1;
    uint32_t gpu = 1;
};
struct Response { uint32_t magic; Frame type; uint32_t bytes; };
static_assert(sizeof(Request) == 32);
static_assert(sizeof(Response) == 12);

inline bool valid(const Request& r) {
    return r.magic == Magic && r.textBytes && r.textBytes <= MaxText &&
        r.voiceBytes && r.voiceBytes <= 512 && r.languageBytes &&
        r.languageBytes <= 8 && r.speed >= .25f && r.speed <= MaxSpeed &&
        r.pitch >= .5f && r.pitch <= 2 && r.volume >= 0 && r.volume <= 1 && r.gpu <= 1;
}
inline bool readExact(HANDLE file, void* buffer, size_t bytes) {
    auto out = static_cast<char*>(buffer);
    while (bytes) {
        DWORD got = 0;
        if (!ReadFile(file, out, static_cast<DWORD>(bytes), &got, nullptr) || !got) return false;
        out += got; bytes -= got;
    }
    return true;
}
inline bool writeExact(HANDLE file, const void* buffer, size_t bytes) {
    auto data = static_cast<const char*>(buffer);
    while (bytes) {
        DWORD sent = 0;
        if (!WriteFile(file, data, static_cast<DWORD>(bytes), &sent, nullptr) || !sent) return false;
        data += sent; bytes -= sent;
    }
    return true;
}
inline std::string utf8(const std::wstring& value) {
    if (value.empty()) return {};
    int count = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
        static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (!count) throw std::runtime_error("Invalid Unicode text");
    std::string out(count, '\0');
    WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
        static_cast<int>(value.size()), out.data(), count, nullptr, nullptr);
    return out;
}
inline std::wstring directory(HMODULE module = nullptr) {
    wchar_t path[32768];
    DWORD count = GetModuleFileNameW(module, path, 32768);
    if (!count || count >= 32768) throw std::runtime_error("Cannot find application folder");
    std::wstring value(path, count);
    return value.substr(0, value.find_last_of(L"\\/"));
}
struct Handle {
    HANDLE value = nullptr;
    Handle() = default;
    explicit Handle(HANDLE h) : value(h) {}
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
    ~Handle() { reset(); }
    void reset(HANDLE h = nullptr) {
        if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value);
        value = h;
    }
    operator HANDLE() const { return value; }
};
}
