// SPDX-License-Identifier: GPL-3.0-or-later
#include "protocol.h"
#include "backend.h"
#include "sonic.h"
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <memory>
#include <vector>
#include "SapiImportedVoice.h"
#include "pause_length.h"

using namespace phonon;
static void log(const std::string& message) noexcept {
    try {
    wchar_t profile[32768];
    DWORD count = GetEnvironmentVariableW(L"LOCALAPPDATA",profile,32768);
    if (!count || count >= 32768) return;
    std::wstring root = std::wstring(profile,count) + L"\\PhononSAPI";
    CreateDirectoryW(root.c_str(),nullptr);
    std::wstring path = root + L"\\host.log";
    WIN32_FILE_ATTRIBUTE_DATA data{};
    if (GetFileAttributesExW(path.c_str(),GetFileExInfoStandard,&data) &&
        (data.nFileSizeHigh || data.nFileSizeLow > 128 * 1024))
        MoveFileExW(path.c_str(),(path + L".old").c_str(),MOVEFILE_REPLACE_EXISTING);
    Handle file(CreateFileW(path.c_str(),FILE_APPEND_DATA,FILE_SHARE_READ|FILE_SHARE_WRITE,
        nullptr,OPEN_ALWAYS,FILE_ATTRIBUTE_NORMAL,nullptr));
    if (file.value == INVALID_HANDLE_VALUE) return;
    SYSTEMTIME now; GetSystemTime(&now);
    char stamp[80];
    std::snprintf(stamp,sizeof(stamp),"%04u-%02u-%02uT%02u:%02u:%02uZ PID %lu ",
        now.wYear,now.wMonth,now.wDay,now.wHour,now.wMinute,now.wSecond,GetCurrentProcessId());
    std::string entry = stamp + message.substr(0,512) + "\r\n";
    writeExact(file,entry.data(),entry.size());
    } catch (...) { }
}
struct Result { uint32_t frames; size_t samples; double firstMs; double totalMs; };
using Callback = bool (*)(const float*, size_t, void*);
struct Native {
    BackendPublisher* status = nullptr;
    HMODULE library = nullptr;
    void* engine = nullptr;
    void* (*create)(const char*, uint32_t, const char*);
    bool (*speak)(void*, const char*, Callback, void*, Result*);
    bool (*voice)(void*, const char*);
    bool (*language)(void*, const char*);
    void (*free)(void*);
    bool (*importVoice)(void*, const uint8_t*, size_t) = nullptr;
    std::string activeImport;
    const char* (*error)(void*);
    ~Native() { if (engine) free(engine); if (library) FreeLibrary(library); }
    template<class T> void symbol(T& fn, const char* name) {
        fn = reinterpret_cast<T>(GetProcAddress(library, name));
        if (!fn) throw std::runtime_error("Missing native runtime entry point");
    }
    void load() {
        auto folder = directory() + L"\\runtime";
        SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32 | LOAD_LIBRARY_SEARCH_USER_DIRS);
        AddDllDirectory(folder.c_str());
        library = LoadLibraryExW((folder + L"\\ptts_ffi.dll").c_str(), nullptr,
            LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        if (!library) throw std::runtime_error("Cannot load Phonon runtime");
        symbol(create,"ptts_new"); symbol(speak,"ptts_speak"); symbol(voice,"ptts_set_voice");
        symbol(importVoice,"ptts_set_imported_voice");
        symbol(language,"ptts_set_language"); symbol(free,"ptts_free"); symbol(error,"ptts_last_error");
    }
    bool selectVoice(const std::string& id) {
        if (id.rfind("imported-", 0) == 0) {
            if (selectImportedVoice(engine, id, activeImport, importVoice)) return true;
            activeImport.clear();
            return voice(engine, "Freya");
        }
        activeImport.clear();
        return voice(engine, id.c_str());
    }
    void open(bool gpu) {
        if (!IsProcessorFeaturePresent(40)) throw std::runtime_error("An AVX2-capable processor is required");
        std::string model = utf8(directory() + L"\\model");
        if (gpu) {
            HMODULE vulkan = LoadLibraryExW(L"vulkan-1.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
            if (vulkan) {
                engine = create(model.c_str(), 3, "en");
                Result result{};
                size_t samples = 0;
                auto discard = [](const float*, size_t n, void* user) -> bool {
                    *static_cast<size_t*>(user) += n; return true;
                };
                if (engine && (!speak(engine, "A", discard, &samples, &result) || !samples)) {
                    free(engine); engine = nullptr;
                }
                FreeLibrary(vulkan);
            }
        }
        bool accelerated = engine != nullptr;
        if (!engine) engine = create(model.c_str(), 0, "en");
        if (!engine) throw std::runtime_error("Cannot initialize the bundled model");
        if (status) status->set(accelerated ? Backend::Vulkan : (gpu ? Backend::CpuFallback : Backend::Cpu));
        log(accelerated ? "Initialized Vulkan synthesis" : (gpu ? "GPU unavailable; initialized CPU fallback" : "Initialized CPU synthesis"));
    }
};
struct Sink {
    HANDLE output;
    HANDLE cancel;
    sonicStream stream;
    bool failed = false;
    PauseLength pauses{};
    std::vector<short> adjusted;
    static bool appendAdjusted(void* user, const float* audio, size_t count) {
        auto& sink = *static_cast<Sink*>(user);
        if (sink.cancelled()) return false;
        for (size_t i = 0; i < count; ++i) sink.adjusted.push_back(static_cast<short>(audio[i] * 32768.f));
        return true;
    }
    bool cancelled() const { return WaitForSingleObject(cancel, 0) == WAIT_OBJECT_0; }
    bool send(const short* pcm, size_t count) {
        if (cancelled() || failed) return false;
        Response frame{Magic, Frame::Audio, static_cast<uint32_t>(count * sizeof(short))};
        failed = !writeExact(output, &frame, sizeof(frame)) || !writeExact(output, pcm, frame.bytes);
        return !failed && !cancelled();
    }
    bool drain() {
        short pcm[4096];
        int count;
        while ((count = sonicReadShortFromStream(stream, pcm, 4096)) > 0) {
            if (pauses.mode == PAUSE_DEFAULT) {
                if (!send(pcm, count)) return false;
                continue;
            }
            float samples[4096];
            for (int i = 0; i < count; ++i) samples[i] = pcm[i] / 32768.f;
            adjusted.clear();
            if (!pause_push(&pauses, samples, count, appendAdjusted, this) ||
                (!adjusted.empty() && !send(adjusted.data(), adjusted.size()))) return false;
        }
        return !cancelled();
    }
    bool finish() {
        if (!sonicFlushStream(stream) || !drain()) return false;
        if (pauses.mode == PAUSE_DEFAULT) return true;
        adjusted.clear();
        return pause_finish(&pauses, appendAdjusted, this) &&
            (adjusted.empty() || send(adjusted.data(), adjusted.size()));
    }
    static bool callback(const float* pcm, size_t count, void* user) {
        auto& self = *static_cast<Sink*>(user);
        if (self.cancelled() || count > MaxFrame / sizeof(float)) return false;
        if (!sonicWriteFloatToStream(self.stream, const_cast<float*>(pcm), static_cast<int>(count)))
            return false;
        return self.drain();
    }
};

int wmain(int argc, wchar_t** argv) {
    if (argc != 2) return 2;
    wchar_t* end = nullptr;
    auto value = wcstoull(argv[1], &end, 10);
    if (!value || !end || *end) return 2;
    HANDLE cancel = reinterpret_cast<HANDLE>(static_cast<uintptr_t>(value));
    HANDLE input = GetStdHandle(STD_INPUT_HANDLE), output = GetStdHandle(STD_OUTPUT_HANDLE);
    BackendPublisher status;
    Native native;
    native.status = &status;
    try {
        native.load();
        Request request;
        while (readExact(input, &request, sizeof(request))) {
            if (!valid(request)) return 3;
            std::string text(request.textBytes, '\0'), voice(request.voiceBytes, '\0'),
                language(request.languageBytes, '\0');
            if (!readExact(input, text.data(), text.size()) ||
                !readExact(input, voice.data(), voice.size()) ||
                !readExact(input, language.data(), language.size())) return 3;
            if (text.find('\0') != text.npos || voice.find('\0') != voice.npos ||
                language.find('\0') != language.npos) return 3;
            try {
                if (!native.engine) native.open(request.gpu != 0);
                if (!native.selectVoice(voice) || !native.language(native.engine, language.c_str()))
                    throw std::runtime_error("The requested voice or language is unavailable");
                auto stream = sonicCreateStream(SampleRate, 1);
                if (!stream) throw std::bad_alloc();
                std::unique_ptr<sonicStreamStruct, decltype(&sonicDestroyStream)> owner(stream, sonicDestroyStream);
                sonicSetSpeed(stream, request.speed); sonicSetPitch(stream, request.pitch);
                sonicSetVolume(stream, request.volume);
                Sink sink{output, cancel, stream};
                DWORD pause = 0, size = sizeof(pause);
                if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\Onj\\PhononSAPI", L"PauseLength",
                        RRF_RT_REG_DWORD, nullptr, &pause, &size) == ERROR_SUCCESS &&
                    (pause == PAUSE_SHORTER || pause == PAUSE_LONGER) && request.volume != 0) {
                    sink.pauses.mode = static_cast<PauseMode>(pause);
                }
                Result result{};
                bool success = !sink.cancelled() && native.speak(native.engine, text.c_str(), Sink::callback, &sink, &result);
                if (!sink.cancelled()) {
                    if (!success || sink.failed) throw std::runtime_error("Speech generation failed");
                    if (!sink.finish()) throw std::runtime_error("Audio processing failed");
                }
            } catch (const std::exception& error) {
                log(error.what());
                std::string message = error.what();
                if (message.size() > 512) message.resize(512);
                Response frame{Magic, Frame::Error, static_cast<uint32_t>(message.size())};
                if (!writeExact(output, &frame, sizeof(frame)) || !writeExact(output, message.data(), message.size())) return 4;
            }
            Response done{Magic, Frame::Done, 0};
            if (!writeExact(output, &done, sizeof(done))) return 0;
        }
    } catch (const std::exception& error) {
        status.set(Backend::Failed);
        log(error.what());
        std::string message = error.what();
        Response frame{Magic, Frame::Error, static_cast<uint32_t>(message.size())};
        writeExact(output, &frame, sizeof(frame)); writeExact(output, message.data(), message.size());
        return 5;
    }
    return 0;
}
