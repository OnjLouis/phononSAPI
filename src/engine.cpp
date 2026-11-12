// SPDX-License-Identifier: GPL-3.0-or-later
#include "protocol.h"
#include <sapiddk.h>
#include <sperror.h>
#include <algorithm>
#include <atomic>
#include <cmath>
#include <mutex>
#include <memory>
#include <vector>
#include "SapiImportEnumerator.h"

using namespace phonon;
static const CLSID EngineId = {0x5261ca16,0x3c40,0x4ad8,{0x8e,0xc6,0x4d,0x46,0x50,0x48,0x53,0x41}};
static const IID TtsInterface = {0xa74d7c8e,0x4cc5,0x4f2f,{0xa6,0xeb,0x80,0x4d,0xee,0x18,0x50,0x0e}};
static const GUID PcmFormat = {0xc31adbae,0x527f,0x4ff5,{0xa2,0x30,0xf6,0x2b,0xb6,0x1f,0xf7,0x0c}};
static HMODULE module;
static std::atomic<long> objects{0};
static bool detaching = false;

class Child {
    Handle input, output, process, job;
public:
    Handle cancel;
    bool aborted = false;
    bool gpu = true;
    ~Child() { stop(); }
    bool running() const { return process.value && WaitForSingleObject(process, 0) == WAIT_TIMEOUT; }
    void stop() {
        if (cancel.value) SetEvent(cancel);
        input.reset();
        if (!detaching && process.value && WaitForSingleObject(process, 1500) == WAIT_TIMEOUT) {
            TerminateJobObject(job, 1);
            WaitForSingleObject(process, 1500);
        }
        output.reset(); cancel.reset(); process.reset(); job.reset();
    }
    bool start(const std::wstring& root) {
        stop();
        SECURITY_ATTRIBUTES security{sizeof(security),nullptr,TRUE};
        HANDLE childRead = nullptr, parentWrite = nullptr, parentRead = nullptr, childWrite = nullptr;
        if (!CreatePipe(&childRead, &parentWrite, &security, 65536)) return false;
        Handle read(childRead); input.reset(parentWrite);
        SetHandleInformation(input, HANDLE_FLAG_INHERIT, 0);
        if (!CreatePipe(&parentRead, &childWrite, &security, 65536)) return false;
        Handle write(childWrite); output.reset(parentRead);
        SetHandleInformation(output, HANDLE_FLAG_INHERIT, 0);
        cancel.reset(CreateEventW(&security, TRUE, FALSE, nullptr));
        Handle null(CreateFileW(L"NUL", GENERIC_WRITE, FILE_SHARE_WRITE | FILE_SHARE_READ,
            &security, OPEN_EXISTING, 0, nullptr));
        if (!cancel.value || null.value == INVALID_HANDLE_VALUE) return false;
        HANDLE allowed[] = {read, write, cancel, null};
        SIZE_T bytes = 0;
        InitializeProcThreadAttributeList(nullptr, 1, 0, &bytes);
        std::vector<char> storage(bytes);
        auto attrs = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(storage.data());
        if (!InitializeProcThreadAttributeList(attrs, 1, 0, &bytes)) return false;
        bool updated = UpdateProcThreadAttribute(attrs, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
            allowed, sizeof(allowed), nullptr, nullptr);
        STARTUPINFOEXW startup{};
        startup.StartupInfo.cb = sizeof(startup);
        startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
        startup.StartupInfo.hStdInput = read; startup.StartupInfo.hStdOutput = write;
        startup.StartupInfo.hStdError = null; startup.lpAttributeList = attrs;
        PROCESS_INFORMATION info{};
        std::wstring exe = root + L"\\PhononHost.exe";
        std::wstring command = L"\"" + exe + L"\" " + std::to_wstring(reinterpret_cast<uintptr_t>(cancel.value));
        bool created = updated && CreateProcessW(exe.c_str(), command.data(), nullptr, nullptr, TRUE,
            CREATE_NO_WINDOW | CREATE_SUSPENDED | EXTENDED_STARTUPINFO_PRESENT, nullptr, root.c_str(),
            &startup.StartupInfo, &info);
        DeleteProcThreadAttributeList(attrs);
        if (!created) return false;
        process.reset(info.hProcess); Handle thread(info.hThread);
        job.reset(CreateJobObjectW(nullptr,nullptr));
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!job.value || !SetInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, sizeof(limits)) ||
            !AssignProcessToJobObject(job, process)) {
            TerminateProcess(process,1); WaitForSingleObject(process,1000); stop(); return false;
        }
        if (ResumeThread(thread) == static_cast<DWORD>(-1)) { stop(); return false; }
        return true;
    }
    bool read(void* buffer, size_t bytes, ISpTTSEngineSite* site) {
        auto dest = static_cast<char*>(buffer);
        auto progress = GetTickCount64();
        ULONGLONG cancelledAt = 0;
        while (bytes) {
            DWORD actions = site->GetActions();
            if (actions & SPVES_SKIP) site->CompleteSkip(0);
            if (actions & SPVES_ABORT) {
                if (!aborted) { aborted = true; cancelledAt = GetTickCount64(); SetEvent(cancel); }
                else if (!cancelledAt) cancelledAt = GetTickCount64();
            }
            if ((cancelledAt && GetTickCount64() - cancelledAt > 2000) ||
                GetTickCount64() - progress > 60000) { stop(); return false; }
            DWORD available = 0;
            if (!PeekNamedPipe(output,nullptr,0,nullptr,&available,nullptr)) return false;
            if (!available) {
                if (!running()) return false;
                Sleep(5); continue;
            }
            DWORD count = 0;
            if (!ReadFile(output,dest,static_cast<DWORD>(std::min<size_t>(bytes,available)),&count,nullptr) || !count)
                return false;
            dest += count; bytes -= count; progress = GetTickCount64();
        }
        return true;
    }
    bool send(const Request& r, const std::string& text, const std::string& voice, const std::string& language) {
        aborted = false; ResetEvent(cancel);
        return writeExact(input,&r,sizeof(r)) && writeExact(input,text.data(),text.size()) &&
            writeExact(input,voice.data(),voice.size()) && writeExact(input,language.data(),language.size());
    }
};

// Only idle engines are reused. Active SAPI clients never share a synthesis pipe.
static std::mutex idleMutex;
static std::unique_ptr<Child> idleChild;
static std::unique_ptr<Child> acquireChild() {
    std::lock_guard<std::mutex> lock(idleMutex);
    if (idleChild) return std::move(idleChild);
    return std::make_unique<Child>();
}
static void releaseChild(std::unique_ptr<Child> child) {
    if (!child || !child->running()) return;
    std::lock_guard<std::mutex> lock(idleMutex);
    if (!idleChild) idleChild = std::move(child);
}

class Engine final : public ISpTTSEngine, public ISpObjectWithToken {
    std::atomic<ULONG> refs{1};
    ISpObjectToken* token = nullptr;
    std::unique_ptr<Child> child;
    std::mutex mutex;
    std::wstring voice = L"Maeve", language = L"en";
    HRESULT write(ISpTTSEngineSite* site, const void* data, ULONG bytes, ULONGLONG& offset) {
        USHORT volume = 100; site->GetVolume(&volume);
        std::vector<short> scaled(bytes / sizeof(short));
        const auto samples = static_cast<const short*>(data);
        for (size_t i = 0; i < scaled.size(); ++i)
            scaled[i] = static_cast<short>(samples[i] * std::min<USHORT>(volume,100) / 100);
        auto pcm = reinterpret_cast<const char*>(scaled.data());
        while (bytes) {
            if (site->GetActions() & SPVES_ABORT) { if (child) SetEvent(child->cancel); return S_FALSE; }
            ULONG sent = 0;
            HRESULT hr = site->Write(pcm, bytes, &sent);
            if (FAILED(hr)) return hr;
            if (!sent || sent > bytes) return E_FAIL;
            pcm += sent; bytes -= sent; offset += sent;
        }
        return S_OK;
    }
    HRESULT request(const std::wstring& text, const SPVSTATE& state, ISpTTSEngineSite* site, ULONGLONG& offset) {
        DWORD gpu = 1, size = sizeof(gpu);
        RegGetValueW(HKEY_CURRENT_USER,L"Software\\Onj\\PhononSAPI",L"UseGpu",RRF_RT_REG_DWORD,nullptr,&gpu,&size);
        if (!child) child = acquireChild();
        if (child->running() && child->gpu != (gpu != 0)) child->stop();
        child->gpu = gpu != 0;
        if (!child->running() && !child->start(directory(module))) return HRESULT_FROM_WIN32(ERROR_PROCESS_ABORTED);
        Request request;
        auto input = utf8(text), speaker = utf8(voice), locale = utf8(language);
        long rate = 0;
        site->GetRate(&rate);
        request.textBytes = static_cast<uint32_t>(input.size());
        request.voiceBytes = static_cast<uint32_t>(speaker.size());
        request.languageBytes = static_cast<uint32_t>(locale.size());
        request.speed = std::clamp(std::pow(2.f, (rate + state.RateAdj) / 5.f), .25f, 4.f);
        request.pitch = std::clamp(std::pow(2.f, state.PitchAdj.MiddleAdj / 12.f), .5f, 2.f);
        request.volume = std::clamp(state.Volume / 100.f,0.f,1.f);
        request.gpu = gpu != 0;
        if (!valid(request) || !child->send(request,input,speaker,locale)) { child->stop(); return E_FAIL; }
        bool failed = false;
        while (true) {
            Response frame;
            if (!child->read(&frame,sizeof(frame),site) || frame.magic != Magic || frame.bytes > MaxFrame) {
                child->stop(); return E_FAIL;
            }
            std::vector<char> data(frame.bytes);
            if (frame.bytes && !child->read(data.data(),data.size(),site)) { child->stop(); return E_FAIL; }
            if (frame.type == Frame::Done) {
                if (frame.bytes) { child->stop(); return E_FAIL; }
                if (failed) { child->stop(); return E_FAIL; }
                return child->aborted ? S_FALSE : S_OK;
            }
            if (frame.type == Frame::Error) { failed = true; continue; }
            if (frame.type != Frame::Audio || frame.bytes % 2) { child->stop(); return E_FAIL; }
            if (!child->aborted && !failed) {
                HRESULT hr = write(site,data.data(),frame.bytes,offset);
                if (FAILED(hr)) { child->stop(); return hr; }
            }
        }
    }
public:
    Engine() { ++objects; }
    ~Engine() { releaseChild(std::move(child)); if (token) token->Release(); --objects; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** out) override {
        if (!out) return E_POINTER;
        *out = nullptr;
        if (id == IID_IUnknown || id == TtsInterface) *out = static_cast<ISpTTSEngine*>(this);
        else if (id == __uuidof(ISpObjectWithToken)) *out = static_cast<ISpObjectWithToken*>(this);
        else return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override { auto n = --refs; if (!n) delete this; return n; }
    HRESULT STDMETHODCALLTYPE SetObjectToken(ISpObjectToken* value) override {
        if (!value) return E_POINTER;
        std::lock_guard<std::mutex> lock(mutex);
        if (token) return HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED);
        wchar_t* name = nullptr; wchar_t* code = nullptr;
        HRESULT hr = value->GetStringValue(L"VoiceName",&name);
        if (FAILED(hr)) return hr;
        hr = value->GetStringValue(L"PhononLanguage",&code);
        if (FAILED(hr)) { CoTaskMemFree(name); return hr; }
        voice = name; language = code; CoTaskMemFree(name); CoTaskMemFree(code);
        token = value; token->AddRef(); return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetObjectToken(ISpObjectToken** out) override {
        if (!out) return E_POINTER;
        *out = token; if (token) token->AddRef(); return token ? S_OK : CO_E_NOTINITIALIZED;
    }
    HRESULT STDMETHODCALLTYPE GetOutputFormat(const GUID*, const WAVEFORMATEX*, GUID* id, WAVEFORMATEX** out) override {
        if (!id || !out) return E_POINTER;
        *out = static_cast<WAVEFORMATEX*>(CoTaskMemAlloc(sizeof(WAVEFORMATEX)));
        if (!*out) return E_OUTOFMEMORY;
        **out = {WAVE_FORMAT_PCM,1,SampleRate,SampleRate*2,2,16,0};
        *id = PcmFormat; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Speak(DWORD, REFGUID format, const WAVEFORMATEX* wave,
        const SPVTEXTFRAG* fragment, ISpTTSEngineSite* site) override {
        if (!wave || !site) return E_POINTER;
        if (format != PcmFormat || wave->nSamplesPerSec != SampleRate ||
            wave->nChannels != 1 || wave->wBitsPerSample != 16 || wave->wFormatTag != WAVE_FORMAT_PCM)
            return E_INVALIDARG;
        std::lock_guard<std::mutex> lock(mutex);
        ULONGLONG offset = 0;
        try {
            for (; fragment; fragment = fragment->pNext) {
                if (site->GetActions() & SPVES_ABORT) return S_OK;
                if (site->GetActions() & SPVES_SKIP) site->CompleteSkip(0);
                if (fragment->State.eAction == SPVA_Bookmark) {
                    if (!fragment->pTextStart || fragment->ulTextLen > MaxText) return E_INVALIDARG;
                    std::wstring bookmark(fragment->pTextStart,fragment->ulTextLen);
                    SPEVENT event{}; event.eEventId = SPEI_TTS_BOOKMARK;
                    event.elParamType = SPET_LPARAM_IS_STRING; event.ullAudioStreamOffset = offset;
                    event.lParam = reinterpret_cast<LPARAM>(bookmark.c_str());
                    event.wParam = wcstoul(bookmark.c_str(),nullptr,10);
                    HRESULT hr = site->AddEvents(&event,1); if (FAILED(hr)) return hr;
                    continue;
                }
                if (fragment->State.eAction == SPVA_Silence) {
                    uint64_t bytes = static_cast<uint64_t>(fragment->State.SilenceMSecs) * SampleRate / 1000 * 2;
                    short silence[2400]{};
                    while (bytes) {
                        ULONG count = static_cast<ULONG>(std::min<uint64_t>(bytes,sizeof(silence)));
                        HRESULT hr = write(site,silence,count,offset); if (hr != S_OK) return FAILED(hr) ? hr : S_OK;
                        bytes -= count;
                    }
                    continue;
                }
                if (!fragment->pTextStart || !fragment->ulTextLen || fragment->State.eAction == SPVA_ParseUnknownTag) continue;
                std::wstring text(fragment->pTextStart,fragment->ulTextLen);
                if (fragment->State.eAction == SPVA_SpellOut) {
                    for (size_t position = 0; position < text.size();) {
                        size_t length = text[position] >= 0xd800 && text[position] <= 0xdbff && position + 1 < text.size() ? 2 : 1;
                        HRESULT hr = request(text.substr(position,length),fragment->State,site,offset);
                        if (hr != S_OK) return FAILED(hr) ? hr : S_OK;
                        position += length;
                    }
                    continue;
                }
                for (size_t start = 0; start < text.size();) {
                    size_t end = std::min(start + 3000,text.size());
                    if (end < text.size()) {
                        size_t split = text.find_last_of(L".!?;\n ",end - 1);
                        if (split != text.npos && split > start) end = split + 1;
                        if (end && text[end-1] >= 0xd800 && text[end-1] <= 0xdbff) --end;
                    }
                    HRESULT hr = request(text.substr(start,end-start),fragment->State,site,offset);
                    if (hr != S_OK) return FAILED(hr) ? hr : S_OK;
                    start = end;
                }
            }
            return S_OK;
        } catch (...) { if (child) child->stop(); return E_FAIL; }
    }
};

class Factory final : public IClassFactory {
    std::atomic<ULONG> refs{1};
    const bool enumerator;
public:
    explicit Factory(bool enumerate) : enumerator(enumerate) { ++objects; }
    ~Factory() { --objects; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** out) override {
        if (!out) return E_POINTER;
        *out = nullptr;
        if (id != IID_IUnknown && id != IID_IClassFactory) return E_NOINTERFACE;
        *out = this; AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override { auto n = --refs; if (!n) delete this; return n; }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID id, void** out) override {
        if (!out) return E_POINTER;
        *out = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        if (enumerator) return createImportEnumerator(objects,id,out);
        try { auto engine = new Engine; HRESULT hr = engine->QueryInterface(id,out); engine->Release(); return hr; }
        catch (...) { return E_OUTOFMEMORY; }
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override { if (lock) ++objects; else --objects; return S_OK; }
};
extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID id, void** out) {
    if (!out) return E_POINTER;
    *out = nullptr;
    if (clsid != EngineId && clsid != ImportEnumeratorId) return CLASS_E_CLASSNOTAVAILABLE;
    auto factory = new (std::nothrow) Factory(clsid == ImportEnumeratorId);
    if (!factory) return E_OUTOFMEMORY;
    HRESULT hr = factory->QueryInterface(id,out); factory->Release(); return hr;
}
extern "C" HRESULT __stdcall DllCanUnloadNow() {
    if (objects != 0) return S_FALSE;
    std::unique_ptr<Child> idle;
    {
        std::lock_guard<std::mutex> lock(idleMutex);
        idle = std::move(idleChild);
    }
    idle.reset();
    return objects == 0 ? S_OK : S_FALSE;
}
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) { module = instance; DisableThreadLibraryCalls(instance); }
    if (reason == DLL_PROCESS_DETACH) detaching = true;
    return TRUE;
}
