// FullOffice native Office host, GNU AGPL version 3. See LICENSE and NOTICE.txt.
#include <windows.h>
#include <shellapi.h>
#include <filesystem>
#include <iostream>
#include <string>
#include <vector>
#include <mutex>
#include <cstdint>
#include "applicationmanager.h"

CApplicationCEF* application = nullptr;
CAscApplicationManager* manager = nullptr;
CCefViewEditor* editor = nullptr;
HWND host_window = nullptr;
std::mutex output_lock;
void emit(const std::string& line) { std::lock_guard guard(output_lock);std::cout<<line<<'\n'<<std::flush; }
class Parent final : public CCefViewWidgetImpl {
public:
    void UpdateSize() override { RECT r{}; if (cef_handle && GetClientRect(cef_handle,&r)) { cef_width=r.right;cef_height=r.bottom; } }
    void OnLoaded() override { emit("VIEW_LOADED"); }
};
class Listener final : public NSEditorApi::CAscCefMenuEventListener {
public:
    void OnEvent(NSEditorApi::CAscCefMenuEvent* event) override {
        if (event && event->m_nType==ASC_MENU_EVENT_TYPE_CEF_ONKEYBOARDDOWN && event->m_pData) {
            auto* key=static_cast<NSEditorApi::CAscKeyboardDown*>(event->m_pData);
            if (key->get_IsCtrl() && !key->get_IsAlt()) {
                if (key->get_KeyCode()==VK_TAB) emit(key->get_IsShift()?"COMMAND TAB_PREVIOUS":"COMMAND TAB_NEXT");
                else if (key->get_KeyCode()=='W') emit("COMMAND CLOSE_TAB");
                else if (key->get_KeyCode()=='O') emit("COMMAND OPEN_FILES");
                else if (key->get_KeyCode()=='T') emit("COMMAND HOME");
            } else if (key->get_IsAlt() && key->get_KeyCode()==VK_F4) emit("COMMAND CLOSE_WORKSPACE");
        }
        if (event && event->m_nType==ASC_MENU_EVENT_TYPE_CEF_CHECK_KEYBOARD && manager) manager->CheckKeyboard();
        if (event && event->m_nType==ASC_MENU_EVENT_TYPE_CEF_MODIFY_CHANGED && event->m_pData) {
            auto* data=static_cast<NSEditorApi::CAscDocumentModifyChanged*>(event->m_pData);
            emit(data->get_Changed()?"DIRTY 1":"DIRTY 0");
        }
        if (event && event->m_nType==ASC_MENU_EVENT_TYPE_CEF_ONSAVE && event->m_pData) {
            auto* data=static_cast<NSEditorApi::CAscDocumentOnSaveData*>(event->m_pData);
            emit(data->get_IsCancel()?"SAVED CANCEL":"SAVED OK");
        }
        NSEditorApi::CAscCefMenuEventListener::OnEvent(event);
    }
};
LRESULT CALLBACK procedure(HWND window,UINT msg,WPARAM wp,LPARAM lp) {
    if (msg==WM_SIZE && editor) { editor->GetWidgetImpl()->UpdateSize();editor->resizeEvent();return 0; }
    if (msg==WM_SETFOCUS && editor) editor->focus();
    if (msg==WM_APP+20 && editor) { editor->Apply(editor->CreateCefEvent(ASC_MENU_EVENT_TYPE_CEF_SAVE));return 0; }
    if (msg==WM_APP+22 && editor) { editor->focus(true);return 0; }
    if (msg==WM_APP+23 && editor) { editor->focus(false);return 0; }
    if (msg==WM_CLOSE) { if (application) application->ExitMessageLoop();DestroyWindow(window);return 0; }
    if (msg==WM_DESTROY) { PostQuitMessage(0);return 0; }
    return DefWindowProcW(window,msg,wp,lp);
}
int wmain(int count,wchar_t** arguments) {
    if (count!=4 && count!=5) return 2;
    const std::filesystem::path engine=arguments[1],profile=arguments[2],document=arguments[3];
    if (!engine.is_absolute() || !profile.is_absolute() || !document.is_absolute() || !std::filesystem::is_regular_file(document)) return 2;
    std::filesystem::create_directories(profile);
    CAscApplicationManager owner;
    manager=&owner;
    auto& settings=owner.m_oSettings;
    settings.SetUserDataPath(profile.wstring());
    std::filesystem::create_directories(settings.recover_path);
    std::filesystem::create_directories(settings.user_plugins_path);
    std::filesystem::create_directories(settings.fonts_cache_info_path);
    settings.local_editors_path=(engine/L"editors/web-apps/apps/api/documents/index.html").wstring();
    settings.file_converter_path=(engine/L"converter").wstring();
    settings.spell_dictionaries_path=(engine/L"dictionaries").wstring();
    settings.system_templates_path=(engine/L"converter/templates").wstring();
    settings.connection_error_path=(engine/L"editors/webext/noconnect.html").wstring();
    settings.additional_fonts_folder.push_back((engine/L"fonts").wstring());
    settings.macroses_support=false;settings.plugins_support=false;
    settings.country="CN";
    Listener listener;owner.SetEventListener(&listener);
    CApplicationCEF cef;application=&cef;
    std::vector<std::string> storage;
    for(int index=0;index<count;++index) {
        const int bytes=WideCharToMultiByte(CP_UTF8,0,arguments[index],-1,nullptr,0,nullptr,nullptr);
        std::string text(static_cast<size_t>(bytes),0);WideCharToMultiByte(CP_UTF8,0,arguments[index],-1,text.data(),bytes,nullptr,nullptr);text.pop_back();storage.push_back(std::move(text));
    }
    std::vector<char*> argv;for(auto& text:storage) argv.push_back(text.data());
    CApplicationCEF::Prepare(static_cast<int>(argv.size()),argv.data());
    const int initialized=cef.Init_CEF(&owner,static_cast<int>(argv.size()),argv.data());
    if (cef.IsChromiumSubprocess()) return initialized;
    emit("INITIALIZED "+std::to_string(initialized));
    const HINSTANCE instance=GetModuleHandleW(nullptr);
    WNDCLASSW cls{};cls.lpfnWndProc=procedure;cls.hInstance=instance;cls.lpszClassName=L"FullOffice.NativeApiResearch";
    cls.hCursor=LoadCursorW(nullptr,IDC_ARROW);cls.hbrBackground=reinterpret_cast<HBRUSH>(COLOR_WINDOW+1);
    if (!RegisterClassW(&cls)) return 3;
    HWND embedding=count==5?reinterpret_cast<HWND>(static_cast<std::uintptr_t>(std::wcstoull(arguments[4],nullptr,10))):nullptr;
    if (embedding && !IsWindow(embedding)) return 3;
    RECT bounds{};if (embedding) GetClientRect(embedding,&bounds);
    HWND window=CreateWindowExW(0,cls.lpszClassName,L"FullOffice · ONLYOFFICE 原生编辑器",embedding?WS_CHILD|WS_VISIBLE|WS_CLIPCHILDREN:WS_OVERLAPPEDWINDOW,
        embedding?0:80,embedding?0:60,embedding?bounds.right:1200,embedding?bounds.bottom:900,embedding,nullptr,instance,nullptr);
    if (!window) return 3;
    host_window=window;emit("HWND "+std::to_string(reinterpret_cast<std::uintptr_t>(window)));
    Parent parent;parent.cef_handle=window;parent.cef_style=WS_CHILD|WS_VISIBLE;parent.UpdateSize();
    editor=owner.CreateCefEditor(&parent);
    if (!editor) return 4;
    const int format=CAscApplicationManager::GetFileFormatByExtentionForSave(document.wstring());
    emit("EDITOR_CREATED "+std::to_string(editor->GetId())+" FORMAT "+std::to_string(format));
    editor->OpenLocalFile(document.wstring(),format,L"lang=zh-CN");
    owner.StartSpellChecker();owner.StartKeyboardChecker();owner.CheckFonts();
    ShowWindow(window,SW_SHOW);
    bool ran=false;const int result=cef.RunMessageLoop(ran);
    if (!ran) { MSG message{};while(GetMessageW(&message,nullptr,0,0)>0) {TranslateMessage(&message);DispatchMessageW(&message);cef.DoMessageLoopEvent();} }
    editor=nullptr;owner.OnDestroyWindow();owner.CloseApplication();manager=nullptr;application=nullptr;
    return result;
}
