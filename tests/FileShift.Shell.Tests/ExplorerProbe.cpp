#include <windows.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <iostream>
#include <vector>
#include <string>
#include <wrl/client.h>
#include <io.h>
#include <fcntl.h>
#include <chrono>
#include <algorithm>
using Microsoft::WRL::ComPtr;
UINT Find(HMENU menu,const std::wstring& wanted) {
    for(int i=0;i<GetMenuItemCount(menu);i++) {
        wchar_t label[512]{}; GetMenuStringW(menu,i,label,512,MF_BYPOSITION);
        if(wanted==label) return GetMenuItemID(menu,i);
        if(auto child=GetSubMenu(menu,i)) { const auto id=Find(child,wanted); if(id!=UINT(-1)) return id; }
    }
    return UINT(-1);
}
void Print(HMENU menu, IContextMenu3* cm3, IContextMenu2* cm2, int depth=0) {
    for(int i=0;i<GetMenuItemCount(menu);i++) {
        wchar_t label[512]{};
        GetMenuStringW(menu,i,label,512,MF_BYPOSITION);
        if(label[0]) std::wcout << std::wstring(depth*2,L' ') << label << L"\n";
        auto child=GetSubMenu(menu,i);
        if(child && depth<3) {
            if(cm3) { LRESULT ignored{}; cm3->HandleMenuMsg2(WM_INITMENUPOPUP,(WPARAM)child,MAKELPARAM(i,FALSE),&ignored); }
            else if(cm2) cm2->HandleMenuMsg(WM_INITMENUPOPUP,(WPARAM)child,MAKELPARAM(i,FALSE));
            Print(child,cm3,cm2,depth+1);
        }
    }
}
int wmain(int argc,wchar_t** argv) {
    _setmode(_fileno(stdout),_O_U8TEXT); _setmode(_fileno(stderr),_O_U8TEXT);
    CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);
    const bool invoke=argc>3 && std::wstring(argv[1])==L"--invoke";
    const bool benchmark=argc>2 && std::wstring(argv[1])==L"--benchmark";
    const int first=invoke?3:benchmark?2:1;
    std::vector<PIDLIST_ABSOLUTE> absolute;
    std::vector<PCUITEMID_CHILD> children;
    ComPtr<IShellFolder> parent;
    for(int i=first;i<argc;i++) {
        PIDLIST_ABSOLUTE id{};
        auto hr=SHParseDisplayName(argv[i],nullptr,&id,0,nullptr);
        if(FAILED(hr)) { std::wcerr << L"Parse failed: " << std::hex << hr; return 1; }
        absolute.push_back(id); children.push_back(ILFindLastID(id));
    }
    if(absolute.empty()) return 1;
    if(benchmark) {
        const GUID clsid={0xb623bef3,0x493e,0x49a1,{0xaf,0x1e,0x27,0xc6,0x37,0x70,0x62,0x4b}};
        ComPtr<IShellItemArray> selection;
        auto result=SHCreateShellItemArrayFromIDLists(static_cast<UINT>(absolute.size()),const_cast<PCIDLIST_ABSOLUTE*>(absolute.data()),&selection);
        if(FAILED(result)) return 6;
        std::vector<double> samples;
        for(int run=0;run<30;run++) {
            const auto start=std::chrono::steady_clock::now();
            ComPtr<IExplorerCommand> command;
            result=CoCreateInstance(clsid,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&command));
            if(FAILED(result)) { std::wcerr << L"Activation failed: " << std::hex << result; return 7; }
            EXPCMDSTATE state{}; command->GetState(selection.Get(),FALSE,&state);
            ComPtr<IEnumExplorerCommand> commands; command->EnumSubCommands(&commands);
            for(;;) { ComPtr<IExplorerCommand> item; if(commands->Next(1,&item,nullptr)!=S_OK) break; item->GetState(selection.Get(),FALSE,&state); }
            samples.push_back(std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-start).count());
        }
        const auto cold=samples[0]; samples.erase(samples.begin()); std::sort(samples.begin(),samples.end());
        std::wcout << L"Native handler: " << absolute.size() << L" items; first activation+states=" << cold << L" ms; warm p50=" << samples[samples.size()/2] << L" ms; warm p95=" << samples[samples.size()*95/100] << L" ms\n";
        return 0;
    }
    PCUITEMID_CHILD child{};
    auto hr=SHBindToParent(absolute[0],IID_PPV_ARGS(&parent),&child);
    if(FAILED(hr)) return 2;
    ComPtr<IContextMenu> cm;
    hr=parent->GetUIObjectOf(nullptr,(UINT)children.size(),children.data(),IID_IContextMenu,nullptr,(void**)&cm);
    if(FAILED(hr)) { std::wcerr << L"GetUIObjectOf failed: " << std::hex << hr; return 3; }
    auto popup=CreatePopupMenu();
    hr=cm->QueryContextMenu(popup,0,1,0x7fff,CMF_NORMAL);
    if(FAILED(hr)) { std::wcerr << L"Query failed: " << std::hex << hr; return 4; }
    ComPtr<IContextMenu3> cm3; cm.As(&cm3);
    ComPtr<IContextMenu2> cm2; cm.As(&cm2);
    Print(popup,cm3.Get(),cm2.Get());
    if(invoke) {
        const auto id=Find(popup,argv[2]);
        if(id==UINT(-1)) { std::wcerr << L"Submenu command not found\n"; return 5; }
        CMINVOKECOMMANDINFOEX info{}; info.cbSize=sizeof(info); info.fMask=CMIC_MASK_UNICODE; info.nShow=SW_SHOWNORMAL;
        info.lpVerb=MAKEINTRESOURCEA(id-1); info.lpVerbW=MAKEINTRESOURCEW(id-1);
        hr=cm->InvokeCommand(reinterpret_cast<CMINVOKECOMMANDINFO*>(&info));
        std::wcout << L"FileShift submenu invoke result: " << std::hex << hr << L"\n";
        if(FAILED(hr)) return 8;
    }
    DestroyMenu(popup);
    for(auto id:absolute) CoTaskMemFree(id);
    return 0;
}
