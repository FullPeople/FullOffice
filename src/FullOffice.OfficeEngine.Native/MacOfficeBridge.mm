// FullOffice native Office bridge. GNU AGPL version 3; see LICENSE and NOTICE.txt.
#import <AppKit/AppKit.h>
#include <atomic>
#include <filesystem>
#include <iostream>
#include "mac_application.h"
#include "mac_cefview.h"

namespace {
NSAscApplicationWorker* worker = nil;
CAscApplicationManager* manager = nullptr;
CCefViewEditor* editor = nullptr;
CCefViewWrapper* wrapper = nullptr;
std::atomic<bool> ready{false}, dirty{false}, saved{false}, keyboardPending{false}, destroyed{false};
std::wstring wide(const char* text) {
    NSString* value = [NSString stringWithUTF8String:text];
    NSData* data = [value dataUsingEncoding:NSUTF32LittleEndianStringEncoding];
    return std::wstring(static_cast<const wchar_t*>([data bytes]), [data length]/sizeof(wchar_t));
}
CAscApplicationManager* createManager() { return new CAscApplicationManager(); }
class Listener final : public NSEditorApi::CAscCefMenuEventListener {
    void OnEvent(NSEditorApi::CAscCefMenuEvent* event) override {
        if (event) {
            if (event->m_nType == ASC_MENU_EVENT_TYPE_CEF_CHECK_KEYBOARD && manager && !keyboardPending.exchange(true))
                dispatch_async(dispatch_get_main_queue(), ^{ if (manager) manager->CheckKeyboard(); keyboardPending=false; });
            if (event->m_nType == ASC_MENU_EVENT_TYPE_CEF_EXECUTE_COMMAND && event->m_pData) {
                auto* data=static_cast<NSEditorApi::CAscExecCommand*>(event->m_pData);
                if (data->get_Command()==L"FullOffice.Bridge.Ready") { ready=true; std::cout<<"BRIDGE_DOCUMENT_READY"<<std::endl; }
            }
            if (event->m_nType == ASC_MENU_EVENT_TYPE_CEF_MODIFY_CHANGED && event->m_pData)
                dirty=static_cast<NSEditorApi::CAscDocumentModifyChanged*>(event->m_pData)->get_Changed();
            if (event->m_nType == ASC_MENU_EVENT_TYPE_CEF_ONSAVE && event->m_pData) {
                saved=!static_cast<NSEditorApi::CAscDocumentOnSaveData*>(event->m_pData)->get_IsCancel();
                std::cout<<(saved ? "BRIDGE_SAVED_OK" : "BRIDGE_SAVE_CANCEL")<<std::endl;
            }
            if (event->m_nType == ASC_MENU_EVENT_TYPE_CEF_DESTROY) destroyed=true;
        }
        NSEditorApi::CAscCefMenuEventListener::OnEvent(event);
    }
} listener;
}

extern "C" int fo_office_initialize(const char* resources, const char* profile) {
    if (manager) return 1;
    try {
        std::filesystem::path root(resources), user(profile);
        auto entry=root/"editors/web-apps/apps/api/documents/index.html";
        if (!root.is_absolute() || !user.is_absolute() || !std::filesystem::is_regular_file(entry)) return 0;
        std::filesystem::create_directories(user);
        worker=[[NSAscApplicationWorker alloc] initWithCreator:&createManager];
        manager=[NSAscApplicationWorker getAppManager];
        auto& s=manager->m_oSettings;
        s.SetUserDataPath(wide(profile));
        for (const auto& p : {s.recover_path,s.user_plugins_path,s.fonts_cache_info_path}) std::filesystem::create_directories(p);
        s.local_editors_path=wide(entry.c_str()); s.file_converter_path=wide((root/"converter").c_str());
        s.spell_dictionaries_path=wide((root/"dictionaries").c_str());
        s.system_templates_path=wide((root/"converter/templates").c_str());
        s.plugins_support=false; s.macroses_support=false; s.country="CN";
        manager->SetEventListener(&listener);
        const char* executable=[[[NSProcessInfo processInfo] arguments][0] UTF8String];
        [worker Start:1 argv:&executable];
        std::cout<<"BRIDGE_INITIALIZED app="<<[NSStringFromClass([NSApp class]) UTF8String]<<std::endl;
        return 1;
    } catch (const std::exception& error) { std::cerr<<"BRIDGE_INITIALIZATION_FAILED "<<error.what()<<std::endl; return 0; }
}
extern "C" int fo_office_attach(void* parent, const char* document) {
    if (!manager || editor || !std::filesystem::is_regular_file(document)) return 0;
    wrapper=new CCefViewWrapper((__bridge NSView*)parent); wrapper->resizeEvent();
    editor=manager->CreateCefEditor(wrapper); if (!editor) return 0;
    wrapper->m_pCefView=editor; ready=false; dirty=false; saved=false; destroyed=false;
    auto path=wide(document);
    editor->OpenLocalFile(path,CAscApplicationManager::GetFileFormatByExtentionForSave(path),L"lang=zh-CN");
    wrapper->UpdateSize(); wrapper->resizeEvent(); editor->focus(true);
    return 1;
}
extern "C" void fo_office_pump() {
    if (!manager) return;
    if (wrapper && !destroyed) {
        NSSize size=wrapper->m_pParent.frame.size;
        if (wrapper->cef_width!=(unsigned int)size.width || wrapper->cef_height!=(unsigned int)size.height) wrapper->resizeEvent();
    }
    manager->GetApplication()->DoMessageLoopEvent();
}
extern "C" int fo_office_ready() {
    if (editor && !ready && !destroyed)
        editor->ExecuteInAllFrames("(function(){var e=(window.Asc&&window.Asc.editor)||window.editor;"
            "if(e&&typeof e.asc_registerCallback==='function'&&window.__foBridgeReadyApi!==e){window.__foBridgeReadyApi=e;"
            "e.asc_registerCallback('asc_onDocumentContentReady',function(){window.AscDesktopEditor.execCommand('FullOffice.Bridge.Ready','1');},false);}})();",true);
    return ready ? 1 : 0;
}
extern "C" int fo_office_dirty() { return dirty ? 1 : 0; }
extern "C" int fo_office_save() {
    if (!editor || !ready || destroyed) return 0;
    saved=false; editor->Apply(editor->CreateCefEvent(ASC_MENU_EVENT_TYPE_CEF_SAVE)); return 1;
}
extern "C" int fo_office_saved() { return saved ? 1 : 0; }
extern "C" void fo_office_shutdown() {
    if (!manager) return;
    manager->DestroyCefView(-1);
    NSDate* deadline=[NSDate dateWithTimeIntervalSinceNow:5];
    while (!destroyed && [deadline timeIntervalSinceNow]>0) {
        manager->GetApplication()->DoMessageLoopEvent();
        [[NSRunLoop currentRunLoop] runMode:NSDefaultRunLoopMode beforeDate:[NSDate dateWithTimeIntervalSinceNow:.01]];
    }
    [worker End]; manager=nullptr; editor=nullptr; wrapper=nullptr; worker=nil;
    std::cout<<"BRIDGE_CLOSED"<<std::endl;
}
