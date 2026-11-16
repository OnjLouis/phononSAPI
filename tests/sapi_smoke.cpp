// SPDX-License-Identifier: GPL-3.0-or-later
#include "../src/protocol.h"
#include <sapi.h>
#include <algorithm>
#include <cassert>
#include <cstdio>
#include <vector>
#include <tlhelp32.h>

using namespace phonon;
static const CLSID EngineId = {0x5261ca16,0x3c40,0x4ad8,{0x8e,0xc6,0x4d,0x46,0x50,0x48,0x53,0x41}};
static const GUID PcmFormat = {0xc31adbae,0x527f,0x4ff5,{0xa2,0x30,0xf6,0x2b,0xb6,0x1f,0xf7,0x0c}};
void check(HRESULT hr, const char* operation) {
    if (FAILED(hr)) { std::fprintf(stderr,"%s failed: 0x%08lx\n",operation,static_cast<unsigned long>(hr)); std::exit(1); }
}
std::vector<DWORD> hostPids() {
    Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS,0));
    PROCESSENTRY32W entry{};entry.dwSize=sizeof(entry);
    std::vector<DWORD> ids;
    if(Process32FirstW(snapshot,&entry))do {
        if(entry.th32ParentProcessID==GetCurrentProcessId() && _wcsicmp(entry.szExeFile,L"PhononHost.exe")==0)
            ids.push_back(entry.th32ProcessID);
    }while(Process32NextW(snapshot,&entry));
    return ids;
}
int wmain(int argc, wchar_t** argv) {
    if (argc != 4) return 2;
    check(CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED),"COM");
    HMODULE dll = LoadLibraryW(argv[1]);
    if (!dll) { std::fprintf(stderr,"Load adapter failed: %lu\n",GetLastError()); return 3; }
    using FactoryFn = HRESULT (__stdcall*)(REFCLSID,REFIID,void**);
    auto getFactory = reinterpret_cast<FactoryFn>(GetProcAddress(dll,"DllGetClassObject"));
    IClassFactory* factory = nullptr;
    check(getFactory(EngineId,IID_IClassFactory,reinterpret_cast<void**>(&factory)),"Factory");
    DWORD registration = 0;
    check(CoRegisterClassObject(EngineId,factory,CLSCTX_INPROC_SERVER,REGCLS_MULTIPLEUSE,&registration),"Class registration");
    std::wstring key = L"Software\\Onj\\PhononSAPI\\Tests\\" + std::to_wstring(GetCurrentProcessId());
    std::wstring tokenId = L"HKEY_CURRENT_USER\\" + key;
    ISpObjectToken* token = nullptr;
    check(CoCreateInstance(__uuidof(SpObjectToken),nullptr,CLSCTX_INPROC_SERVER,__uuidof(ISpObjectToken),reinterpret_cast<void**>(&token)),"Token creation");
    check(token->SetId(nullptr,tokenId.c_str(),TRUE),"Token id");
    check(token->SetStringValue(L"CLSID",L"{5261CA16-3C40-4AD8-8EC6-4D4650485341}"),"Token class");
    check(token->SetStringValue(L"VoiceName",argv[2]),"Voice name");
    check(token->SetStringValue(L"PhononLanguage",L"en"),"Language");
    check(token->SetStringValue(nullptr,L"Phonon isolated SAPI test"),"Description");
    ISpDataKey* attributes = nullptr;
    check(token->CreateKey(L"Attributes",&attributes),"Attributes");
    check(attributes->SetStringValue(L"Language",L"809"),"Locale");
    check(attributes->SetStringValue(L"Name",L"Phonon isolated SAPI test"),"Name");
    check(attributes->SetStringValue(L"Gender",L"Female"),"Gender");
    check(attributes->SetStringValue(L"Vendor",L"Phonon SAPI"),"Vendor");
    attributes->Release();
    ISpVoice* voice = nullptr;
    check(CoCreateInstance(__uuidof(SpVoice),nullptr,CLSCTX_INPROC_SERVER,__uuidof(ISpVoice),reinterpret_cast<void**>(&voice)),"SpVoice");
    check(voice->SetVoice(token),"Select voice");
    IStream* memory = nullptr;
    check(CreateStreamOnHGlobal(nullptr,TRUE,&memory),"Memory stream");
    ISpStream* audio = nullptr;
    check(CoCreateInstance(__uuidof(SpStream),nullptr,CLSCTX_INPROC_SERVER,__uuidof(ISpStream),reinterpret_cast<void**>(&audio)),"Speech stream");
    WAVEFORMATEX wave{WAVE_FORMAT_PCM,1,SampleRate,SampleRate*2,2,16,0};
    check(audio->SetBaseStream(memory,PcmFormat,&wave),"Stream format");
    check(voice->SetOutput(audio,TRUE),"Output");
    ULONGLONG start = GetTickCount64();
    check(voice->Speak(L"This is Phonon speaking through the Microsoft speech interface.",SPF_IS_NOT_XML,nullptr),"Speak");
    STATSTG stat{}; check(memory->Stat(&stat,STATFLAG_NONAME),"Output size");
    if (stat.cbSize.QuadPart < SampleRate) { std::fprintf(stderr,"No usable PCM: %llu\n",stat.cbSize.QuadPart); return 4; }
    LARGE_INTEGER zero{}; memory->Seek(zero,STREAM_SEEK_SET,nullptr);
    std::vector<char> pcm(static_cast<size_t>(stat.cbSize.QuadPart)); ULONG got = 0;
    check(memory->Read(pcm.data(),static_cast<ULONG>(pcm.size()),&got),"Read PCM");
    Handle file(CreateFileW(argv[3],GENERIC_WRITE,0,nullptr,CREATE_ALWAYS,0,nullptr));
    if (!writeExact(file,pcm.data(),got)) return 5;
    auto millis = GetTickCount64() - start;
    auto reset = [&]() {
        ULARGE_INTEGER empty{}; check(memory->SetSize(empty),"Clear output");
        check(memory->Seek(zero,STREAM_SEEK_SET,nullptr),"Rewind output");
    };
    auto size = [&]() {STATSTG value{};check(memory->Stat(&value,STATFLAG_NONAME),"Measure output");return value.cbSize.QuadPart;};
    reset(); check(voice->SetRate(5),"Set rate");
    check(voice->Speak(L"This is Phonon speaking through the Microsoft speech interface.",SPF_IS_NOT_XML,nullptr),"Fast speech");
    auto fast = size(); if(fast >= got * .8 || fast < 1000) return 6;
    reset(); check(voice->SetRate(10),"Set maximum rate");
    check(voice->Speak(L"This is Phonon speaking through the Microsoft speech interface.",SPF_IS_NOT_XML,nullptr),"Maximum rate speech");
    auto fastest = size();
    std::printf("Rate PCM bytes: normal=%llu fast=%llu maximum=%llu\n", got + 0ULL, fast, fastest);
    bool fastEnough = fastest < fast && fastest >= 1000;
    reset(); check(voice->SetRate(0),"Reset rate"); check(voice->SetVolume(0),"Mute");
    check(voice->Speak(L"This speech should be silent.",SPF_IS_NOT_XML,nullptr),"Muted speech");
    std::vector<short> muted(static_cast<size_t>(size())/2); memory->Seek(zero,STREAM_SEEK_SET,nullptr);
    ULONG muteBytes=0;check(memory->Read(muted.data(),static_cast<ULONG>(muted.size()*2),&muteBytes),"Read muted PCM");
    if(muted.empty() || std::any_of(muted.begin(),muted.end(),[](short s){return s!=0;}))return 7;
    check(voice->SetVolume(100),"Unmute"); reset();
    check(voice->Speak(L"<pitch absmiddle='5'>Pitch control is working.</pitch>",SPF_IS_XML,nullptr),"XML pitch");
    if(size()<1000)return 8;
    for(int i=0;i<12;i++) {
        reset();check(voice->Speak(i%2?L"One.":L"Two.",SPF_IS_NOT_XML,nullptr),"Repeated speech");
        if(size()<200)return 9;
    }
    std::wstring longText;for(int i=0;i<60;i++)longText+=L"This is a long passage which must stop immediately when interrupted. ";
    reset();check(voice->Speak(longText.c_str(),SPF_ASYNC|SPF_IS_NOT_XML,nullptr),"Async long speech");
    Sleep(40);auto cancelled=GetTickCount64();
    check(voice->Speak(L"",SPF_ASYNC|SPF_PURGEBEFORESPEAK,nullptr),"Purge speech");
    check(voice->WaitUntilDone(5000),"Wait for purge");
    auto cancelMs=GetTickCount64()-cancelled;if(cancelMs>1500)return 10;
    reset();check(voice->Speak(L"Fresh speech after cancellation.",SPF_IS_NOT_XML,nullptr),"Recover after purge");
    if(size()<1000)return 11;
    reset();
    check(voice->SetInterest(SPFEI(SPEI_TTS_BOOKMARK),SPFEI(SPEI_TTS_BOOKMARK)),"Bookmark interest");
    check(voice->Speak(L"Before the bookmark.<bookmark mark='23'/> After the bookmark.",SPF_IS_XML,nullptr),"Bookmark speech");
    SPEVENT event{}; ULONG fetched=0;
    check(voice->GetEvents(1,&event,&fetched),"Bookmark event");
    if(fetched!=1 || event.eEventId!=SPEI_TTS_BOOKMARK || event.wParam!=23 ||
        event.ullAudioStreamOffset==0 || event.ullAudioStreamOffset>=size())return 12;
    if(event.elParamType==SPET_LPARAM_IS_STRING)CoTaskMemFree(reinterpret_cast<void*>(event.lParam));
    reset();check(voice->Speak(L"<spell>ABC</spell>",SPF_IS_XML,nullptr),"Spelling");
    if(size()<1000)return 13;
    auto originalPids=hostPids();
    ISpObjectToken* alternate=nullptr;
    check(CoCreateInstance(__uuidof(SpObjectToken),nullptr,CLSCTX_INPROC_SERVER,__uuidof(ISpObjectToken),reinterpret_cast<void**>(&alternate)),"Alternate token");
    check(alternate->SetId(nullptr,(tokenId+L"\\alternate").c_str(),TRUE),"Alternate token id");
    check(alternate->SetStringValue(L"CLSID",L"{5261CA16-3C40-4AD8-8EC6-4D4650485341}"),"Alternate class");
    check(alternate->SetStringValue(L"VoiceName",L"Freya"),"Alternate voice");
    check(alternate->SetStringValue(L"PhononLanguage",L"en"),"Alternate language");
    check(alternate->SetStringValue(nullptr,L"Phonon isolated alternate"),"Alternate description");
    check(alternate->CreateKey(L"Attributes",&attributes),"Alternate attributes");
    check(attributes->SetStringValue(L"Language",L"809"),"Alternate locale");
    check(attributes->SetStringValue(L"Name",L"Phonon isolated alternate"),"Alternate name");attributes->Release();
    auto switchStart=GetTickCount64();
    check(voice->SetVoice(alternate),"Switch voice");reset();
    check(voice->Speak(L"This is the alternate voice.",SPF_IS_NOT_XML,nullptr),"Warm switched speech");
    auto switchedPids=hostPids();auto switchMs=GetTickCount64()-switchStart;
    bool reused=originalPids.size()==1 && switchedPids==originalPids;
    check(voice->SetVoice(token),"Restore voice");reset();
    check(voice->Speak(L"This is the original voice.",SPF_IS_NOT_XML,nullptr),"Restored speech");
    reused=reused && hostPids()==originalPids;
    DWORD handlesBefore=0,handlesAfter=0;
    GetProcessHandleCount(GetCurrentProcess(),&handlesBefore);
    for(int i=0;i<24;i++) {
        check(voice->SetVoice(i%2?token:alternate),"Churn voice");reset();
        check(voice->Speak(L"Hello.",SPF_IS_NOT_XML,nullptr),"Churn speech");
        reused=reused && hostPids()==originalPids && size()>1000;
    }
    GetProcessHandleCount(GetCurrentProcess(),&handlesAfter);
    bool bounded=handlesAfter<=handlesBefore+4;
    ISpVoice* independent=nullptr;ISpStream* audio2=nullptr;IStream* memory2=nullptr;
    check(CoCreateInstance(__uuidof(SpVoice),nullptr,CLSCTX_INPROC_SERVER,__uuidof(ISpVoice),reinterpret_cast<void**>(&independent)),"Independent SpVoice");
    check(independent->SetVoice(alternate),"Independent voice");
    check(CreateStreamOnHGlobal(nullptr,TRUE,&memory2),"Independent memory");
    check(CoCreateInstance(__uuidof(SpStream),nullptr,CLSCTX_INPROC_SERVER,__uuidof(ISpStream),reinterpret_cast<void**>(&audio2)),"Independent stream");
    check(audio2->SetBaseStream(memory2,PcmFormat,&wave),"Independent format");
    check(independent->SetOutput(audio2,TRUE),"Independent output");
    check(independent->Speak(L"This speech belongs to an independent client.",SPF_IS_NOT_XML,nullptr),"Independent speech");
    bool isolated=hostPids().size()==2;
    reset();
    check(voice->Speak(longText.c_str(),SPF_ASYNC|SPF_IS_NOT_XML,nullptr),"Concurrent first client");
    check(independent->Speak(L"The second client is still independent.",SPF_IS_NOT_XML,nullptr),"Concurrent second client");
    check(voice->Speak(L"",SPF_ASYNC|SPF_PURGEBEFORESPEAK,nullptr),"Cancel only first client");
    check(voice->WaitUntilDone(5000),"First client cancellation complete");
    check(independent->Speak(L"Fresh independent speech.",SPF_IS_NOT_XML,nullptr),"Second client after first cancellation");
    isolated=isolated && hostPids().size()==2;
    independent->Release();audio2->Release();memory2->Release();alternate->Release();
    std::printf("{\"bits\":%zu,\"pcm_bytes\":%lu,\"elapsed_ms\":%llu,\"fast_pcm_bytes\":%llu,\"cancel_ms\":%llu,\"repeated_requests\":12,\"real_SpVoice\":true,\"rate_volume_pitch_purge_recovery\":true,\"voice_switch_reuses_host\":%s,\"independent_clients_isolated\":%s,\"warm_switch_ms\":%llu}\n",sizeof(void*)*8,got,millis,fast,cancelMs,reused?"true":"false",isolated?"true":"false",switchMs);
    voice->Release(); audio->Release(); memory->Release(); token->Release();
    CoRevokeClassObject(registration); factory->Release();
    using UnloadFn=HRESULT (__stdcall*)();
    auto canUnload=reinterpret_cast<UnloadFn>(GetProcAddress(dll,"DllCanUnloadNow"));
    check(canUnload(),"Unload cleanup");
    bool cleaned=hostPids().empty();
    RegDeleteTreeW(HKEY_CURRENT_USER,key.c_str());
    CoUninitialize(); FreeLibrary(dll);
    std::printf("{\"voice_changes\":24,\"handles_before\":%lu,\"handles_after\":%lu,\"bounded_handles\":%s,\"unload_cleaned_hosts\":%s}\n",handlesBefore,handlesAfter,bounded?"true":"false",cleaned?"true":"false");
    return reused && isolated && cleaned && bounded && fastEnough ? 0 : 14;
}
