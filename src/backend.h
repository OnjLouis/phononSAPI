// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once
#include "protocol.h"

namespace phonon {
constexpr uint32_t BackendMagic = 0x42534850;
enum class Backend : LONG { Starting = 1, Vulkan = 2, Cpu = 3, CpuFallback = 4, Failed = 5 };
struct BackendInfo {
    uint32_t magic;
    uint32_t version;
    uint64_t processStarted;
    LONG state;
    uint32_t reserved;
    wchar_t adapterName[128];
};
static_assert(offsetof(BackendInfo, adapterName) == 24);
class BackendPublisher {
    Handle mapping;
    BackendInfo* view = nullptr;
public:
    BackendPublisher() {
        FILETIME created{}, exited{}, kernel{}, user{};
        if (!GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernel, &user)) return;
        std::wstring name = L"Local\\PhononSAPI.Backend." + std::to_wstring(GetCurrentProcessId());
        mapping.reset(CreateFileMappingW(INVALID_HANDLE_VALUE,nullptr,PAGE_READWRITE,0,sizeof(BackendInfo),name.c_str()));
        if (!mapping.value || GetLastError() == ERROR_ALREADY_EXISTS) return;
        view = static_cast<BackendInfo*>(MapViewOfFile(mapping,FILE_MAP_WRITE,0,0,sizeof(BackendInfo)));
        if (!view) return;
        *view = {BackendMagic,2,(static_cast<uint64_t>(created.dwHighDateTime)<<32)|created.dwLowDateTime,0,0,{}};
        set(Backend::Starting);
    }
    ~BackendPublisher() { if (view) UnmapViewOfFile(view); }
    void set(Backend state, const char* adapterName = nullptr) {
        if (!view) return;
        if (adapterName && !MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, adapterName, -1,
                view->adapterName, static_cast<int>(sizeof(view->adapterName) / sizeof(wchar_t))))
            view->adapterName[0] = L'\0';
        InterlockedExchange(&view->state,static_cast<LONG>(state));
    }
};
}
