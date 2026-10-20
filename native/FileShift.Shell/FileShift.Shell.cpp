#include <windows.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <wrl/client.h>
#include <algorithm>
#include <cstdint>
#include <cwctype>
#include <map>
#include <memory>
#include <new>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;
static constexpr GUID ClassId = {0xb623bef3,0x493e,0x49a1,{0xaf,0x1e,0x27,0xc6,0x37,0x70,0x62,0x4b}};
static LONG moduleReferences = 0;
static std::wstring Lower(std::wstring value) {
    std::transform(value.begin(), value.end(), value.begin(), [](wchar_t c) { return static_cast<wchar_t>(towlower(c)); });
    return value;
}
static std::vector<std::wstring> Split(const std::wstring& text, wchar_t delimiter) {
    std::vector<std::wstring> parts;
    size_t start = 0;
    for (;;) {
        const auto end = text.find(delimiter, start);
        parts.push_back(text.substr(start, end == std::wstring::npos ? end : end - start));
        if (end == std::wstring::npos) break;
        start = end + 1;
    }
    return parts;
}
struct Target { std::wstring name, label; };
struct Policy {
    std::wstring executable;
    std::vector<Target> targets;
    std::map<std::wstring, uint64_t> routes;
    static std::shared_ptr<Policy> Read() {
        auto policy = std::make_shared<Policy>();
        DWORD bytes = 0;
        constexpr auto key = L"Software\\FileShift\\Shell";
        if (RegGetValueW(HKEY_CURRENT_USER, key, L"MenuSnapshot", RRF_RT_REG_MULTI_SZ, nullptr, nullptr, &bytes) != ERROR_SUCCESS || bytes > 256 * 1024) return policy;
        std::vector<wchar_t> buffer(bytes / sizeof(wchar_t) + 2, 0);
        if (RegGetValueW(HKEY_CURRENT_USER, key, L"MenuSnapshot", RRF_RT_REG_MULTI_SZ, nullptr, buffer.data(), &bytes) != ERROR_SUCCESS) return policy;
        std::vector<std::wstring> entries;
        for (size_t offset = 0; offset < buffer.size() && buffer[offset];) {
            std::wstring entry(buffer.data() + offset);
            offset += entry.size() + 1;
            entries.push_back(std::move(entry));
        }
        if (entries.empty() || entries[0] != L"V|1") return policy;
        for (const auto& entry : entries) {
            const auto parts = Split(entry, L'|');
            if (parts.size() == 2 && parts[0] == L"X") policy->executable = parts[1];
            if (parts.size() == 3 && parts[0] == L"T" && policy->targets.size() < 60) policy->targets.push_back({parts[1], parts[2]});
        }
        for (const auto& entry : entries) {
            const auto parts = Split(entry, L'|');
            if (parts.size() != 3 || parts[0] != L"R") continue;
            uint64_t mask = 0;
            for (const auto& target : Split(parts[2], L',')) {
                for (size_t i = 0; i < policy->targets.size(); ++i)
                    if (policy->targets[i].name == target) mask |= uint64_t(1) << i;
            }
            policy->routes[Lower(parts[1])] = mask;
        }
        return policy;
    }
};

// One shared cache for every child command: inspect the selection once, not once per format.
// Only shell-supplied names and attributes are queried. No filesystem/engine probing occurs here.
struct SelectionCache {
    explicit SelectionCache(std::shared_ptr<Policy> value) : policy(std::move(value)) {}
    std::shared_ptr<Policy> policy;
    ComPtr<IUnknown> identity;
    uint64_t mask = 0;
    bool valid = false;
    void Update(IShellItemArray* selection) {
        if (!selection) { identity.Reset(); valid = false; mask = 0; return; }
        ComPtr<IUnknown> current;
        if (FAILED(selection->QueryInterface(IID_PPV_ARGS(&current)))) { valid = false; mask = 0; return; }
        if (identity.Get() == current.Get()) return;
        identity = std::move(current);
        valid = false; mask = 0;
        DWORD count = 0;
        if (policy->executable.empty() || FAILED(selection->GetCount(&count)) || count == 0 || count > 100) return;
        auto common = ~uint64_t(0);
        for (DWORD i = 0; i < count; ++i) {
            ComPtr<IShellItem> item;
            if (FAILED(selection->GetItemAt(i, &item))) return;
            SFGAOF attributes = 0;
            if (FAILED(item->GetAttributes(SFGAO_FILESYSTEM | SFGAO_FOLDER, &attributes)) || !(attributes & SFGAO_FILESYSTEM)) return;
            std::wstring extension;
            if (attributes & SFGAO_FOLDER) extension = L"<folder>";
            else {
                PWSTR name = nullptr;
                if (FAILED(item->GetDisplayName(SIGDN_PARENTRELATIVEPARSING, &name))) return;
                const auto suffix = PathFindExtensionW(name);
                extension = Lower(*suffix == L'.' ? suffix + 1 : suffix);
                CoTaskMemFree(name);
            }
            auto route = policy->routes.find(extension);
            if (route == policy->routes.end()) route = policy->routes.find(L"*");
            common &= route == policy->routes.end() ? 0 : route->second;
        }
        mask = common; valid = true;
    }
};

static std::wstring EscapeJson(const std::wstring& text) {
    std::wstring value = L"\"";
    for (wchar_t c : text) {
        if (c == L'"' || c == L'\\') value += L'\\';
        if (c < 0x20) {
            wchar_t escaped[7]{};
            swprintf_s(escaped, L"\\u%04x", static_cast<unsigned>(c));
            value += escaped;
        } else value += c;
    }
    return value + L"\"";
}
static HRESULT Launch(const Policy& policy, IShellItemArray* selection, const std::wstring& target) {
    if (!selection || policy.executable.empty()) return E_INVALIDARG;
    DWORD count = 0;
    if (FAILED(selection->GetCount(&count)) || count == 0 || count > 100) return E_INVALIDARG;
    std::wstring json = L"{\"Target\":" + (target.empty() ? std::wstring(L"null") : EscapeJson(target)) + L",\"Files\":[";
    for (DWORD i = 0; i < count; ++i) {
        ComPtr<IShellItem> item;
        HRESULT hr = selection->GetItemAt(i, &item);
        if (FAILED(hr)) return hr;
        PWSTR path = nullptr;
        hr = item->GetDisplayName(SIGDN_FILESYSPATH, &path);
        if (FAILED(hr)) return hr;
        if (i) json += L',';
        json += EscapeJson(path);
        CoTaskMemFree(path);
    }
    json += L"]}";
    const int length = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, json.data(), static_cast<int>(json.size()), nullptr, 0, nullptr, nullptr);
    if (length == 0 || length > 4 * 1024 * 1024) return E_INVALIDARG;
    std::string utf8(static_cast<size_t>(length), '\0');
    WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, json.data(), static_cast<int>(json.size()), utf8.data(), length, nullptr, nullptr);
    PWSTR local = nullptr;
    HRESULT hr = SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &local);
    if (FAILED(hr)) return hr;
    std::wstring directory = std::wstring(local) + L"\\FileShift";
    CoTaskMemFree(local);
    if (!CreateDirectoryW(directory.c_str(), nullptr) && GetLastError() != ERROR_ALREADY_EXISTS) return HRESULT_FROM_WIN32(GetLastError());
    directory += L"\\Requests";
    if (!CreateDirectoryW(directory.c_str(), nullptr) && GetLastError() != ERROR_ALREADY_EXISTS) return HRESULT_FROM_WIN32(GetLastError());
    GUID guid{}; hr = CoCreateGuid(&guid); if (FAILED(hr)) return hr;
    wchar_t formatted[40]{}; StringFromGUID2(guid, formatted, 40);
    std::wstring filename;
    for (const auto c : std::wstring(formatted)) if (c != L'{' && c != L'}' && c != L'-') filename += static_cast<wchar_t>(towlower(c));
    const std::wstring request = directory + L"\\" + filename + L".json";
    HANDLE file = CreateFileW(request.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return HRESULT_FROM_WIN32(GetLastError());
    DWORD written = 0;
    const BOOL success = WriteFile(file, utf8.data(), static_cast<DWORD>(utf8.size()), &written, nullptr);
    const auto error = GetLastError(); CloseHandle(file);
    if (!success || written != utf8.size()) { DeleteFileW(request.c_str()); return HRESULT_FROM_WIN32(success ? ERROR_WRITE_FAULT : error); }
    auto command = L"\"" + policy.executable + L"\" --shell-request \"" + request + L"\"";
    STARTUPINFOW start{sizeof(start)}; PROCESS_INFORMATION process{};
    if (!CreateProcessW(policy.executable.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &start, &process)) {
        const auto launchError = GetLastError(); DeleteFileW(request.c_str()); return HRESULT_FROM_WIN32(launchError);
    }
    CloseHandle(process.hThread); CloseHandle(process.hProcess);
    return S_OK;
}

class Enumeration;
class Command final : public IExplorerCommand {
    LONG references = 1;
    std::shared_ptr<SelectionCache> cache;
    int index; // -1 parent; -2 open app; otherwise a target index.
public:
    explicit Command(std::shared_ptr<SelectionCache> value, int target = -1) : cache(std::move(value)), index(target) { InterlockedIncrement(&moduleReferences); }
    ~Command() { InterlockedDecrement(&moduleReferences); }
    IFACEMETHODIMP QueryInterface(REFIID iid, void** value) override {
        if (!value) return E_POINTER; *value = nullptr;
        if (iid == IID_IUnknown || iid == __uuidof(IExplorerCommand)) { *value = static_cast<IExplorerCommand*>(this); AddRef(); return S_OK; }
        return E_NOINTERFACE;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return static_cast<ULONG>(InterlockedIncrement(&references)); }
    IFACEMETHODIMP_(ULONG) Release() override { auto count = InterlockedDecrement(&references); if (!count) delete this; return static_cast<ULONG>(count); }
    IFACEMETHODIMP GetTitle(IShellItemArray*, LPWSTR* title) override {
        if (!title) return E_POINTER;
        return SHStrDupW(index == -1 ? L"Convert to" : index == -2 ? L"Open FileShift..." : cache->policy->targets[static_cast<size_t>(index)].label.c_str(), title);
    }
    IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* icon) override { if (!icon) return E_POINTER; *icon = nullptr; return E_NOTIMPL; }
    IFACEMETHODIMP GetToolTip(IShellItemArray*, LPWSTR* tip) override { return tip ? SHStrDupW(L"Convert the selected items with FileShift. Outputs are saved beside the originals.", tip) : E_POINTER; }
    IFACEMETHODIMP GetCanonicalName(GUID* name) override {
        if (!name) return E_POINTER; *name = ClassId;
        if (index != -1) { uint32_t hash = 2166136261u; for (auto c : index == -2 ? std::wstring(L"open") : cache->policy->targets[static_cast<size_t>(index)].name) hash = (hash ^ static_cast<uint32_t>(c)) * 16777619u; name->Data1 ^= hash; }
        return S_OK;
    }
    IFACEMETHODIMP GetState(IShellItemArray* selection, BOOL, EXPCMDSTATE* state) override {
        if (!state) return E_POINTER;
        *state = ECS_HIDDEN;
        try {
            cache->Update(selection);
            if (cache->valid && (index < 0 || (cache->mask & (uint64_t(1) << index)))) *state = ECS_ENABLED;
            return S_OK;
        } catch (...) { return E_FAIL; }
    }
    IFACEMETHODIMP Invoke(IShellItemArray* selection, IBindCtx*) override {
        try {
            cache->Update(selection);
            if (!cache->valid || (index >= 0 && !(cache->mask & (uint64_t(1) << index)))) return E_INVALIDARG;
            return Launch(*cache->policy, selection, index < 0 ? L"" : cache->policy->targets[static_cast<size_t>(index)].name);
        } catch (...) { return E_FAIL; }
    }
    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override { if (!flags) return E_POINTER; *flags = index == -1 ? ECF_HASSUBCOMMANDS : index == -2 ? ECF_SEPARATORBEFORE : ECF_DEFAULT; return S_OK; }
    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override;
};

class Enumeration final : public IEnumExplorerCommand {
    LONG references = 1;
    std::vector<ComPtr<IExplorerCommand>> commands;
    size_t offset = 0;
public:
    explicit Enumeration(std::shared_ptr<SelectionCache> cache) {
        for (size_t i = 0; i < cache->policy->targets.size(); ++i) { ComPtr<IExplorerCommand> command; command.Attach(new Command(cache, static_cast<int>(i))); commands.push_back(std::move(command)); }
        ComPtr<IExplorerCommand> open; open.Attach(new Command(cache, -2)); commands.push_back(std::move(open));
        InterlockedIncrement(&moduleReferences);
    }
    Enumeration(const Enumeration& other) : commands(other.commands), offset(other.offset) { InterlockedIncrement(&moduleReferences); }
    ~Enumeration() { InterlockedDecrement(&moduleReferences); }
    IFACEMETHODIMP QueryInterface(REFIID iid, void** value) override { if (!value) return E_POINTER; *value = nullptr; if (iid == IID_IUnknown || iid == __uuidof(IEnumExplorerCommand)) { *value = static_cast<IEnumExplorerCommand*>(this); AddRef(); return S_OK; } return E_NOINTERFACE; }
    IFACEMETHODIMP_(ULONG) AddRef() override { return static_cast<ULONG>(InterlockedIncrement(&references)); }
    IFACEMETHODIMP_(ULONG) Release() override { auto count = InterlockedDecrement(&references); if (!count) delete this; return static_cast<ULONG>(count); }
    IFACEMETHODIMP Next(ULONG count, IExplorerCommand** result, ULONG* fetched) override {
        if (!result || (!fetched && count != 1)) return E_POINTER;
        ULONG done = 0;
        while (done < count && offset < commands.size()) { result[done] = commands[offset].Get(); result[done]->AddRef(); ++done; ++offset; }
        if (fetched) *fetched = done;
        return done == count ? S_OK : S_FALSE;
    }
    IFACEMETHODIMP Skip(ULONG count) override { const auto remaining = commands.size() - offset; offset += std::min(static_cast<size_t>(count), remaining); return count <= remaining ? S_OK : S_FALSE; }
    IFACEMETHODIMP Reset() override { offset = 0; return S_OK; }
    IFACEMETHODIMP Clone(IEnumExplorerCommand** result) override { if (!result) return E_POINTER; *result = nullptr; try { *result = new Enumeration(*this); return S_OK; } catch (...) { return E_OUTOFMEMORY; } }
};
HRESULT Command::EnumSubCommands(IEnumExplorerCommand** result) {
    if (!result) return E_POINTER; *result = nullptr;
    if (index != -1) return E_NOTIMPL;
    try { *result = new Enumeration(cache); return S_OK; } catch (...) { return E_OUTOFMEMORY; }
}
class Factory final : public IClassFactory {
    LONG references = 1;
public:
    Factory() { InterlockedIncrement(&moduleReferences); }
    ~Factory() { InterlockedDecrement(&moduleReferences); }
    IFACEMETHODIMP QueryInterface(REFIID iid, void** value) override { if (!value) return E_POINTER; *value = nullptr; if (iid == IID_IUnknown || iid == IID_IClassFactory) { *value = static_cast<IClassFactory*>(this); AddRef(); return S_OK; } return E_NOINTERFACE; }
    IFACEMETHODIMP_(ULONG) AddRef() override { return static_cast<ULONG>(InterlockedIncrement(&references)); }
    IFACEMETHODIMP_(ULONG) Release() override { auto count = InterlockedDecrement(&references); if (!count) delete this; return static_cast<ULONG>(count); }
    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID iid, void** value) override {
        if (!value) return E_POINTER; *value = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        try { auto command = new Command(std::make_shared<SelectionCache>(Policy::Read())); const auto hr = command->QueryInterface(iid, value); command->Release(); return hr; } catch (...) { return E_OUTOFMEMORY; }
    }
    IFACEMETHODIMP LockServer(BOOL lock) override { if (lock) InterlockedIncrement(&moduleReferences); else InterlockedDecrement(&moduleReferences); return S_OK; }
};
extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID iid, void** value) {
    if (!value) return E_POINTER; *value = nullptr;
    if (clsid != ClassId) return CLASS_E_CLASSNOTAVAILABLE;
    auto factory = new (std::nothrow) Factory(); if (!factory) return E_OUTOFMEMORY;
    const auto hr = factory->QueryInterface(iid, value); factory->Release(); return hr;
}
extern "C" HRESULT __stdcall DllCanUnloadNow() { return InterlockedCompareExchange(&moduleReferences, 0, 0) == 0 ? S_OK : S_FALSE; }
