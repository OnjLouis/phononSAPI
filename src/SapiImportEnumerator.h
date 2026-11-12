// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once
#include <atomic>
#include <memory>
#include <string_view>
#include <sapi.h>
#include <sapiddk.h>
#include <sperror.h>
#include "SapiImportedVoice.h"

#ifdef __MINGW32__
__CRT_UUID_DECL(SpObjectTokenEnum, 0x3918d75f, 0x0acb, 0x41f2, 0xb7, 0x33, 0x92, 0xaa, 0x15, 0xbc, 0xec, 0xf6)
__CRT_UUID_DECL(ISpObjectTokenEnumBuilder, 0x06b64f9f, 0x7fda, 0x11d2, 0xb4, 0xf2, 0x00, 0xc0, 0x4f, 0x79, 0x73, 0x96)
#endif

namespace phonon {
inline constexpr GUID ImportEnumeratorId{0x7f96a3ed,0xefb8,0x4986,{0xa9,0x74,0x6e,0x0e,0x22,0x7b,0x15,0x7e}};
inline constexpr wchar_t ImportEnumeratorPath[] = L"\\TokenEnums\\PhononSAPIImports\\";
inline constexpr wchar_t ImportEngineClass[] = L"{5261CA16-3C40-4AD8-8EC6-4D4650485341}";
inline constexpr HRESULT DataKeyEndOfItems = static_cast<HRESULT>(0x80045039); // SPERR_NO_MORE_ITEMS

struct ImportLocale { std::string_view code; const wchar_t* native; const wchar_t* lcid; };
inline constexpr ImportLocale ImportLocales[] = {
    {"en-US", L"en", L"409"}, {"en-GB", L"en", L"809"}, {"de-DE", L"de", L"407"},
    {"fr-FR", L"fr", L"40c"}, {"es-ES", L"es", L"c0a"}, {"es-MX", L"es", L"80a"},
    {"pt-BR", L"pt", L"416"},
};

struct ImportMetadata {
    std::wstring id, name;
    ImportLocale locale;
};

inline std::shared_ptr<ImportMetadata> readImportMetadata(const std::wstring& folder, const std::wstring& filename) {
    constexpr std::wstring_view extension = L".voice";
    constexpr std::string_view version = "PhononImport/1\n";
    constexpr DWORD MaxMetadataBytes = 512;
    constexpr DWORD MaxTensorBytes = 4 * 1024 * 1024;
    if (filename.size() <= extension.size() || filename.compare(filename.size() - extension.size(), extension.size(), extension)) return {};
    const auto id = filename.substr(0, filename.size() - extension.size());
    if (!importedVoiceId(utf8(id))) return {};
    const auto open = [](const std::wstring& path) {
        return CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr,
                           OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    };
    const auto fileSize = [](HANDLE file, DWORD maximum) {
        BY_HANDLE_FILE_INFORMATION info{};
        if (file == INVALID_HANDLE_VALUE || GetFileType(file) != FILE_TYPE_DISK ||
            !GetFileInformationByHandle(file, &info) ||
            (info.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) ||
            info.nFileSizeHigh || info.nFileSizeLow > maximum) return DWORD{0};
        return info.nFileSizeLow;
    };
    const auto base = folder + L"\\" + id;
    Handle tensor(open(base + L".safetensors"));
    if (!fileSize(tensor, MaxTensorBytes)) return {};
    Handle record(open(base + std::wstring(extension)));
    const DWORD bytes = fileSize(record, MaxMetadataBytes);
    if (!bytes) return {};
    std::string content(bytes, '\0');
    if (!readExact(record, content.data(), bytes)) return {};
    std::string_view text(content);
    if (text.substr(0, version.size()) != version) return {};
    text.remove_prefix(version.size());
    const auto separator = text.find('\n');
    if (separator == text.npos || separator == 0) return {};
    const auto language = text.substr(separator + 1);
    const auto locale = std::find_if(std::begin(ImportLocales), std::end(ImportLocales),
                                     [&](const auto& entry) { return entry.code == language; });
    if (locale == std::end(ImportLocales)) return {};
    const int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(separator), nullptr, 0);
    if (count <= 0 || count > 80) return {};
    std::wstring name(count, L'\0');
    if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(separator), name.data(), count) ||
        std::any_of(name.begin(), name.end(), [](wchar_t ch) { return ch < L' ' || (ch >= 0x7f && ch <= 0x9f); }) ||
        name.find_first_not_of(L' ') == name.npos) return {};
    return std::make_shared<ImportMetadata>(ImportMetadata{id, std::move(name), *locale});
}

inline HRESULT copyImportString(std::wstring_view value, wchar_t** out) {
    if (!out) return E_POINTER;
    *out = static_cast<wchar_t*>(CoTaskMemAlloc((value.size() + 1) * sizeof(wchar_t)));
    if (!*out) return E_OUTOFMEMORY;
    std::copy(value.begin(), value.end(), *out);
    (*out)[value.size()] = 0;
    return S_OK;
}

// A token's attributes are data only. Imported files cannot change the engine CLSID.
class ImportDataKey final : public ISpDataKey {
    std::atomic<ULONG> refs{1};
    std::atomic<long>& moduleObjects;
    std::shared_ptr<ImportMetadata> data;
    const bool attributes;
public:
    ImportDataKey(std::atomic<long>& objects, const std::shared_ptr<ImportMetadata>& metadata, bool attributes = false)
        : moduleObjects(objects), data(metadata), attributes(attributes) { ++moduleObjects; }
    ~ImportDataKey() { --moduleObjects; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** out) override {
        if (!out) return E_POINTER;
        *out = nullptr;
        if (id != IID_IUnknown && id != __uuidof(ISpDataKey)) return E_NOINTERFACE;
        *out = this; AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override { const auto n = --refs; if (!n) delete this; return n; }
    HRESULT STDMETHODCALLTYPE SetData(LPCWSTR, ULONG, const BYTE*) override { return E_ACCESSDENIED; }
    HRESULT STDMETHODCALLTYPE SetStringValue(LPCWSTR, LPCWSTR) override { return E_ACCESSDENIED; }
    HRESULT STDMETHODCALLTYPE SetDWORD(LPCWSTR, DWORD) override { return E_ACCESSDENIED; }
    HRESULT STDMETHODCALLTYPE CreateKey(LPCWSTR, ISpDataKey** out) override { if (out) *out = nullptr; return E_ACCESSDENIED; }
    HRESULT STDMETHODCALLTYPE DeleteKey(LPCWSTR) override { return E_ACCESSDENIED; }
    HRESULT STDMETHODCALLTYPE DeleteValue(LPCWSTR) override { return E_ACCESSDENIED; }
    HRESULT STDMETHODCALLTYPE GetData(LPCWSTR, ULONG*, BYTE*) override { return SPERR_NOT_FOUND; }
    HRESULT STDMETHODCALLTYPE GetDWORD(LPCWSTR, DWORD*) override { return SPERR_NOT_FOUND; }
    HRESULT STDMETHODCALLTYPE GetStringValue(LPCWSTR name, LPWSTR* out) override {
        if (!out) return E_POINTER;
        *out = nullptr;
        const auto is = [name](LPCWSTR field) { return name && _wcsicmp(name, field) == 0; };
        if (attributes) {
            if (is(L"Name")) return copyImportString(data->name, out);
            if (is(L"Language")) return copyImportString(data->locale.lcid, out);
            if (is(L"Vendor")) return copyImportString(L"Phonon SAPI", out);
        } else {
            if (!name || !*name) return copyImportString(data->name, out);
            if (is(L"CLSID")) return copyImportString(ImportEngineClass, out);
            if (is(L"VoiceName")) return copyImportString(data->id, out);
            if (is(L"PhononLanguage")) return copyImportString(data->locale.native, out);
        }
        return SPERR_NOT_FOUND;
    }
    HRESULT STDMETHODCALLTYPE OpenKey(LPCWSTR name, ISpDataKey** out) override {
        if (!out) return E_POINTER;
        *out = nullptr;
        if (attributes || !name || _wcsicmp(name, L"Attributes")) return SPERR_NOT_FOUND;
        *out = new (std::nothrow) ImportDataKey(moduleObjects, data, true);
        return *out ? S_OK : E_OUTOFMEMORY;
    }
    HRESULT STDMETHODCALLTYPE EnumKeys(ULONG index, LPWSTR* out) override {
        if (!out) return E_POINTER;
        *out = nullptr;
        return attributes || index != 0 ? DataKeyEndOfItems : copyImportString(L"Attributes", out);
    }
    HRESULT STDMETHODCALLTYPE EnumValues(ULONG index, LPWSTR* out) override {
        if (!out) return E_POINTER;
        *out = nullptr;
        constexpr std::wstring_view names[] = {L"Name", L"Language", L"Vendor"};
        constexpr std::wstring_view fields[] = {L"", L"CLSID", L"VoiceName", L"PhononLanguage"};
        if (attributes && index < std::size(names)) return copyImportString(names[index], out);
        if (!attributes && index < std::size(fields)) return copyImportString(fields[index], out);
        return DataKeyEndOfItems;
    }
};

template<class T> struct ImportComRelease { void operator()(T* value) const { value->Release(); } };

inline HRESULT createImportEnumerator(std::atomic<long>& objects, REFIID id, void** out) {
    if (!out) return E_POINTER;
    *out = nullptr;
    try {
        ISpObjectTokenEnumBuilder* raw = nullptr;
        HRESULT hr = CoCreateInstance(__uuidof(SpObjectTokenEnum), nullptr, CLSCTX_INPROC_SERVER,
                                      __uuidof(ISpObjectTokenEnumBuilder), reinterpret_cast<void**>(&raw));
        if (FAILED(hr)) return hr;
        std::unique_ptr<ISpObjectTokenEnumBuilder, ImportComRelease<ISpObjectTokenEnumBuilder>> builder(raw);
        if (FAILED(hr = builder->SetAttribs(nullptr, nullptr))) return hr;
        const auto folder = voiceStorage();
        if (folder.empty()) return builder->QueryInterface(id, out);
        WIN32_FIND_DATAW found{};
        struct Search { HANDLE handle; ~Search() { if (handle != INVALID_HANDLE_VALUE) FindClose(handle); } }
            search{FindFirstFileW((folder + L"\\imported-*.voice").c_str(), &found)};
        if (search.handle == INVALID_HANDLE_VALUE) return builder->QueryInterface(id, out);
        constexpr unsigned MaxRecords = 512;
        unsigned scanned = 0;
        do {
            if (found.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) continue;
            const auto data = readImportMetadata(folder, found.cFileName);
            if (!data) continue;
            ISpObjectTokenInit* rawToken = nullptr;
            hr = CoCreateInstance(__uuidof(SpObjectToken), nullptr, CLSCTX_INPROC_SERVER, __uuidof(ISpObjectTokenInit),
                                  reinterpret_cast<void**>(&rawToken));
            if (FAILED(hr)) return hr;
            std::unique_ptr<ISpObjectTokenInit, ImportComRelease<ISpObjectTokenInit>> token(rawToken);
            std::unique_ptr<ImportDataKey, ImportComRelease<ImportDataKey>> key(new ImportDataKey(objects, data));
            const auto tokenId = std::wstring(SPCAT_VOICES) + ImportEnumeratorPath + data->id;
            if (FAILED(hr = token->InitFromDataKey(SPCAT_VOICES, tokenId.c_str(), key.get()))) return hr;
            ISpObjectToken* item = token.get();
            if (FAILED(hr = builder->AddTokens(1, &item))) return hr;
        } while (++scanned < MaxRecords && FindNextFileW(search.handle, &found));
        return builder->QueryInterface(id, out);
    } catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
    catch (...) { return E_FAIL; }
}
}
