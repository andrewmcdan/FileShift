#include "../../native/FileShift.Shell/FileShift.Shell.cpp"
#include <chrono>
#include <iostream>
#include <stdexcept>

static size_t nameQueries = 0;
class Item final : public IShellItem {
    LONG references = 1;
    std::wstring name;
    bool folder;
public:
    Item(std::wstring value, bool directory) : name(std::move(value)), folder(directory) {}
    IFACEMETHODIMP QueryInterface(REFIID iid, void** value) override { if (!value) return E_POINTER; *value = nullptr; if (iid == IID_IUnknown || iid == __uuidof(IShellItem)) { *value = static_cast<IShellItem*>(this); AddRef(); return S_OK; } return E_NOINTERFACE; }
    IFACEMETHODIMP_(ULONG) AddRef() override { return static_cast<ULONG>(InterlockedIncrement(&references)); }
    IFACEMETHODIMP_(ULONG) Release() override { auto count = InterlockedDecrement(&references); if (!count) delete this; return static_cast<ULONG>(count); }
    IFACEMETHODIMP BindToHandler(IBindCtx*, REFGUID, REFIID, void**) override { return E_NOTIMPL; }
    IFACEMETHODIMP GetParent(IShellItem**) override { return E_NOTIMPL; }
    IFACEMETHODIMP GetDisplayName(SIGDN, LPWSTR* value) override { ++nameQueries; return SHStrDupW(name.c_str(), value); }
    IFACEMETHODIMP GetAttributes(SFGAOF requested, SFGAOF* value) override { *value = (SFGAO_FILESYSTEM | (folder ? SFGAO_FOLDER : 0)) & requested; return S_OK; }
    IFACEMETHODIMP Compare(IShellItem*, SICHINTF, int*) override { return E_NOTIMPL; }
};
class Items final : public IShellItemArray {
    LONG references = 1;
    std::vector<ComPtr<IShellItem>> items;
public:
    explicit Items(const std::vector<std::pair<std::wstring, bool>>& names) {
        for (const auto& [name, folder] : names) { ComPtr<IShellItem> item; item.Attach(new Item(name, folder)); items.push_back(std::move(item)); }
    }
    IFACEMETHODIMP QueryInterface(REFIID iid, void** value) override { if (!value) return E_POINTER; *value = nullptr; if (iid == IID_IUnknown || iid == __uuidof(IShellItemArray)) { *value = static_cast<IShellItemArray*>(this); AddRef(); return S_OK; } return E_NOINTERFACE; }
    IFACEMETHODIMP_(ULONG) AddRef() override { return static_cast<ULONG>(InterlockedIncrement(&references)); }
    IFACEMETHODIMP_(ULONG) Release() override { auto count = InterlockedDecrement(&references); if (!count) delete this; return static_cast<ULONG>(count); }
    IFACEMETHODIMP BindToHandler(IBindCtx*, REFGUID, REFIID, void**) override { return E_NOTIMPL; }
    IFACEMETHODIMP GetPropertyStore(GETPROPERTYSTOREFLAGS, REFIID, void**) override { return E_NOTIMPL; }
    IFACEMETHODIMP GetPropertyDescriptionList(REFPROPERTYKEY, REFIID, void**) override { return E_NOTIMPL; }
    IFACEMETHODIMP GetAttributes(SIATTRIBFLAGS, SFGAOF, SFGAOF*) override { return E_NOTIMPL; }
    IFACEMETHODIMP GetCount(DWORD* count) override { *count = static_cast<DWORD>(items.size()); return S_OK; }
    IFACEMETHODIMP GetItemAt(DWORD index, IShellItem** item) override { if (index >= items.size()) return E_INVALIDARG; *item = items[index].Get(); (*item)->AddRef(); return S_OK; }
    IFACEMETHODIMP EnumItems(IEnumShellItems**) override { return E_NOTIMPL; }
};
static void Require(bool condition, const char* message) { if (!condition) throw std::runtime_error(message); }
static std::vector<std::wstring> Visible(const std::shared_ptr<SelectionCache>& cache, IShellItemArray* selection) {
    ComPtr<IExplorerCommand> root; root.Attach(new Command(cache));
    EXPCMDSTATE state{}; Require(SUCCEEDED(root->GetState(selection, FALSE, &state)), "Root state failed");
    if (state != ECS_ENABLED) return {};
    ComPtr<IEnumExplorerCommand> commands; Require(SUCCEEDED(root->EnumSubCommands(&commands)), "Enumeration failed");
    std::vector<std::wstring> result;
    for (;;) {
        ComPtr<IExplorerCommand> child;
        if (commands->Next(1, &child, nullptr) != S_OK) break;
        Require(SUCCEEDED(child->GetState(selection, FALSE, &state)), "Child state failed");
        if (state == ECS_ENABLED) { PWSTR title = nullptr; child->GetTitle(selection, &title); result.push_back(title); CoTaskMemFree(title); }
    }
    return result;
}
int main() {
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    try {
        auto policy = std::make_shared<Policy>(); policy->executable = L"C:\\FileShift.exe";
        policy->targets = {{L"png",L"PNG"},{L"wav",L"WAV"},{L"mp3",L"MP3"},{L"zip",L"ZIP"}};
        policy->routes = {{L"jpg",9},{L"jpeg",9},{L"mp4",14},{L"wav",12},{L"*",8},{L"<folder>",8}};
        auto cache = std::make_shared<SelectionCache>(policy);
        auto selection = [](std::vector<std::pair<std::wstring,bool>> names) { ComPtr<IShellItemArray> result; result.Attach(new Items(names)); return result; };
        const std::wstring open = L"Open FileShift...";
        auto images = selection({{L"a.JPG",false},{L"b.jpeg",false}});
        nameQueries = 0;
        Require(Visible(cache,images.Get()) == std::vector<std::wstring>{L"PNG",L"ZIP",open}, "Image selection offered wrong formats");
        Require(nameQueries == 2, "Selection was inspected repeatedly per target");
        Require(Visible(cache,images.Get()).size() == 3 && nameQueries == 2, "Selection cache was not reused");
        auto mixed = selection({{L"a.jpg",false},{L"b.mp4",false}});
        Require(Visible(cache,mixed.Get()) == std::vector<std::wstring>{L"ZIP",open}, "Mixed incompatible types must only offer ZIP");
        auto media = selection({{L"a.mp4",false},{L"b.wav",false}});
        Require(Visible(cache,media.Get()) == std::vector<std::wstring>{L"MP3",L"ZIP",open}, "Mixed media did not intersect formats / excluded no-op target incorrectly");
        auto folders = selection({{L"a.jpg",true},{L"b.jpg",false}});
        Require(Visible(cache,folders.Get()) == std::vector<std::wstring>{L"ZIP",open}, "A dotted folder was treated as an image");
        auto unknown = selection({{L"file.unknown",false},{L"README",false}});
        Require(Visible(cache,unknown.Get()) == std::vector<std::wstring>{L"ZIP",open}, "Unknown files must retain ZIP");
        Require(Visible(cache,nullptr).empty(), "Missing selection was enabled");
        Require(Visible(cache,images.Get()).size() == 3, "A null shell callback corrupted the next selection state");
        std::vector<std::pair<std::wstring,bool>> many(100,{L"image.jpg",false});
        auto batch = selection(many);
        Require(Visible(cache,batch.Get()).size() == 3, "100-file batch was rejected");
        many.push_back({L"image.jpg",false}); auto oversized = selection(many);
        Require(Visible(cache,oversized.Get()).empty(), "Over-limit batch was enabled");
        Require(EscapeJson(L"C:\\a\"\n.jpg") == L"\"C:\\\\a\\\"\\u000a.jpg\"", "Request JSON escaping is incorrect");
        {
            ComPtr<IExplorerCommand> root; root.Attach(new Command(cache));
            ComPtr<IEnumExplorerCommand> enumerator; root->EnumSubCommands(&enumerator);
            Require(enumerator->Skip(2) == S_OK,"Skip failed");
            ComPtr<IEnumExplorerCommand> clone; enumerator->Clone(&clone);
            ComPtr<IExplorerCommand> next; Require(clone->Next(1,&next,nullptr)==S_OK,"Clone lost position");
            PWSTR name=nullptr; next->GetTitle(nullptr,&name); Require(std::wstring(name)==L"MP3","Clone returned wrong target"); CoTaskMemFree(name);
            enumerator->Reset(); Require(enumerator->Skip(100)==S_FALSE,"Skip should report end");
        }
        Require(moduleReferences == 0,"COM objects leaked module references");
        std::cout << "PASS native selection intersection, no-op filtering, folders, unknown types, 100-file batches, JSON escaping, COM enumeration and lifetime\n";
        return 0;
    } catch (const std::exception& error) { std::cerr << "FAIL " << error.what() << "\n"; return 1; }
}
