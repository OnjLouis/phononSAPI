// SPDX-License-Identifier: GPL-3.0-or-later
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <msi.h>
#include <msiquery.h>
#include <RestartManager.h>
#include <array>
#include <string>
#include <vector>

namespace {
constexpr DWORD Avx2Feature = 40;
constexpr DWORD Net48Release = 528040;
constexpr wchar_t ClassKey[] = L"SOFTWARE\\Classes\\CLSID\\{5261CA16-3C40-4AD8-8EC6-4D4650485341}\\InprocServer32";
std::wstring property(MSIHANDLE install, const wchar_t* name) {
    DWORD size = 0;
    wchar_t empty = L'\0';
    MsiGetPropertyW(install, name, &empty, &size);
    std::wstring value(size + 1, L'\0');
    ++size;
    if (MsiGetPropertyW(install, name, value.data(), &size) != ERROR_SUCCESS) return {};
    value.resize(size); return value;
}
UINT fail(MSIHANDLE install, const std::wstring& text) {
    MSIHANDLE record = MsiCreateRecord(1);
    MsiRecordSetStringW(record, 0, L"[1]");
    MsiRecordSetStringW(record, 1, text.c_str());
    MsiProcessMessage(install, INSTALLMESSAGE_ERROR, record);
    MsiCloseHandle(record); return ERROR_INSTALL_FAILURE;
}
bool ownedRegistration(MSIHANDLE install, const std::wstring& folder) {
    HKEY root = property(install,L"PhononRegistryRoot") == L"HKCU" ? HKEY_CURRENT_USER : HKEY_LOCAL_MACHINE;
    for (bool x64 : {true,false}) {
        std::wstring key = property(install,x64?L"PhononRegistryPrefix64":L"PhononRegistryPrefix32") + ClassKey;
        wchar_t existing[32768]{}; DWORD bytes = sizeof(existing);
        LSTATUS result = RegGetValueW(root,key.c_str(),nullptr,
            RRF_RT_REG_SZ | (x64?RRF_SUBKEY_WOW6464KEY:RRF_SUBKEY_WOW6432KEY),nullptr,existing,&bytes);
        if (result == ERROR_FILE_NOT_FOUND || result == ERROR_PATH_NOT_FOUND) continue;
        std::wstring expected = folder + (x64?L"PhononSAPI64.dll":L"PhononSAPI32.dll");
        if (result != ERROR_SUCCESS || _wcsicmp(existing,expected.c_str())) return false;
    }
    return true;
}
}
extern "C" __declspec(dllexport) UINT __stdcall CheckPhononUsage(MSIHANDLE install) {
    try {
        if (!IsProcessorFeaturePresent(Avx2Feature)) return fail(install,L"Phonon SAPI requires an AVX2-capable processor.");
        DWORD release = 0, bytes = sizeof(release);
        if (RegGetValueW(HKEY_LOCAL_MACHINE,L"SOFTWARE\\Microsoft\\NET Framework Setup\\NDP\\v4\\Full",L"Release",
            RRF_RT_REG_DWORD | RRF_SUBKEY_WOW6464KEY,nullptr,&release,&bytes) != ERROR_SUCCESS || release < Net48Release)
            return fail(install,L"Install Microsoft .NET Framework 4.8 before installing Phonon SAPI.");
        std::wstring folder = property(install,L"INSTALLFOLDER");
        if (folder.empty()) return fail(install,L"The installation folder could not be resolved.");
        if (folder.back()!=L'\\') folder+=L'\\';
        if (!ownedRegistration(install,folder)) return fail(install,L"Another installation owns the Phonon speech identifiers. No changes were made.");
        DWORD session = 0; wchar_t key[CCH_RM_SESSION_KEY + 1]{};
        if (RmStartSession(&session,0,key)!=ERROR_SUCCESS) return fail(install,L"Windows could not check for applications using Phonon. No changes were made.");
        struct EndSession {DWORD id;~EndSession(){RmEndSession(id);}} end{session};
        std::vector<std::wstring> paths;
        for (auto name : {L"PhononSAPI.exe",L"PhononHost.exe",L"PhononSAPI32.dll",L"PhononSAPI64.dll",L"runtime\\ptts_ffi.dll",L"runtime\\libunwind.dll"})
            if (GetFileAttributesW((folder+name).c_str())!=INVALID_FILE_ATTRIBUTES) paths.push_back(folder+name);
        std::vector<LPCWSTR> names; for (const auto& path:paths) names.push_back(path.c_str());
        if (names.empty()) return ERROR_SUCCESS;
        if (RmRegisterResources(session,static_cast<UINT>(names.size()),names.data(),0,nullptr,0,nullptr)!=ERROR_SUCCESS)
            return fail(install,L"Windows could not check Phonon's files. No changes were made.");
        UINT needed=0,count=0; DWORD reasons=0;
        DWORD result=RmGetList(session,&needed,&count,nullptr,&reasons);
        if (result==ERROR_MORE_DATA || needed)
            return fail(install,L"Close Phonon SAPI and applications using its voices, then retry. If a screen reader is using Phonon, select another voice first. No applications will be closed automatically.");
        if (result!=ERROR_SUCCESS) return fail(install,L"Windows could not determine whether Phonon is in use. No changes were made.");
        return ERROR_SUCCESS;
    } catch (...) {return fail(install,L"The installation safety check failed. No changes were made.");}
}
#ifdef PHONON_INSTALLER_TEST
extern "C" __declspec(dllexport) UINT __stdcall FailIsolatedUpgrade(MSIHANDLE) {return ERROR_INSTALL_FAILURE;}
#endif
extern "C" __declspec(dllexport) UINT __stdcall CleanDownloadedInstaller(MSIHANDLE install) {
    try {
#ifdef PHONON_INSTALLER_TEST
        std::wstring root=property(install,L"PhononTestCacheRoot");
#else
        std::wstring root=property(install,L"LocalAppDataFolder")+L"PhononSAPI\\updates";
#endif
        if(root.empty())return ERROR_SUCCESS;
        if(root.back()!=L'\\')root+=L'\\';
        std::wstring source=property(install,L"OriginalDatabase");
        if(source.size()<=root.size() || _wcsnicmp(source.c_str(),root.c_str(),root.size()))return ERROR_SUCCESS;
        std::wstring relative=source.substr(root.size());
        size_t split=relative.find(L'\\');
        if(split!=32 || relative.substr(split+1)!=L"Phonon-SAPI-"+property(install,L"ProductVersion")+L".msi")return ERROR_SUCCESS;
        for(size_t i=0;i<32;i++)if(!((relative[i]>=L'0' && relative[i]<=L'9') || (relative[i]>=L'a' && relative[i]<=L'f')))return ERROR_SUCCESS;
        // Never follow a redirected cache path or delete anything outside this download.
        for(size_t i=3;i<source.size();i++)if(source[i]==L'\\') {
            DWORD attributes=GetFileAttributesW(source.substr(0,i).c_str());
            if(attributes==INVALID_FILE_ATTRIBUTES || (attributes&FILE_ATTRIBUTE_REPARSE_POINT))return ERROR_SUCCESS;
        }
        DWORD attributes=GetFileAttributesW(source.c_str());
        if(attributes==INVALID_FILE_ATTRIBUTES || (attributes&FILE_ATTRIBUTE_REPARSE_POINT))return ERROR_SUCCESS;
        if(DeleteFileW(source.c_str()))RemoveDirectoryW((root+relative.substr(0,32)).c_str());
    }catch(...){}
    return ERROR_SUCCESS;
}
