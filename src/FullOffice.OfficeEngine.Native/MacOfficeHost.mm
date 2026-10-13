// FullOffice native Office research host. GNU AGPL version 3; see LICENSE and NOTICE.txt.
#import <AppKit/AppKit.h>
#include <filesystem>
#include <iostream>
#include <mutex>
#include <atomic>
#include "mac_application.h"
#include "mac_cefview.h"

static CAscApplicationManager* manager = nullptr;
static CCefViewEditor* editor = nullptr;
static CCefViewWrapper* wrapper = nullptr;
static NSWindow* window = nil;
static std::filesystem::path profile, document, resource, output;
static std::mutex outputMutex;
static std::atomic<bool> keyboardPending{false};
static bool editProbe = false;
static std::atomic<bool> documentReady{false}, savedObserved{false};
static void emit(const std::string& text) { std::lock_guard<std::mutex> lock(outputMutex); std::cout << text << std::endl; }
static std::wstring officeWideString(NSString* value) {
    NSData* bytes = [value dataUsingEncoding:NSUTF32LittleEndianStringEncoding];
    return std::wstring(static_cast<const wchar_t*>([bytes bytes]), [bytes length] / sizeof(wchar_t));
}
static CAscApplicationManager* createManager() { return new CAscApplicationManager(); }
class Listener final : public NSEditorApi::CAscCefMenuEventListener {
public:
    void OnEvent(NSEditorApi::CAscCefMenuEvent* event) override {
        if (event) {
            if (event->m_nType == ASC_MENU_EVENT_TYPE_CEF_CHECK_KEYBOARD && manager) {
                if (!keyboardPending.exchange(true)) dispatch_async(dispatch_get_main_queue(), ^{
                    if (manager) manager->CheckKeyboard();
                    keyboardPending = false;
                });
            }
            else emit("OFFICE_EVENT " + std::to_string(event->m_nType));
            if (event->m_nType == ASC_MENU_EVENT_TYPE_CEF_EXECUTE_COMMAND && event->m_pData) {
                auto* data = static_cast<NSEditorApi::CAscExecCommand*>(event->m_pData);
                if (data->get_Command() == L"FullOffice.Probe.Ready" && data->get_Param() == L"1") {
                    if (!documentReady.exchange(true)) emit("DOCUMENT_CONTENT_READY");
                }
            }
            if (event->m_nType == ASC_MENU_EVENT_TYPE_CEF_MODIFY_CHANGED && event->m_pData) {
                auto* data = static_cast<NSEditorApi::CAscDocumentModifyChanged*>(event->m_pData);
                emit(data->get_Changed() ? "DIRTY 1" : "DIRTY 0");
            }
            if (event->m_nType == ASC_MENU_EVENT_TYPE_CEF_ONSAVE && event->m_pData) {
                auto* data = static_cast<NSEditorApi::CAscDocumentOnSaveData*>(event->m_pData);
                emit(data->get_IsCancel() ? "SAVED CANCEL" : "SAVED OK");
                if (!data->get_IsCancel()) savedObserved = true;
            }
            if (event->m_nType == ASC_MENU_EVENT_TYPE_CEF_DESTROY && manager)
                manager->GetApplication()->ExitMessageLoop();
        }
        NSEditorApi::CAscCefMenuEventListener::OnEvent(event);
    }
};
static Listener listener;
static void nativeKey(NSString* text, unsigned short code, NSEventModifierFlags modifiers = 0) {
    for (auto type : {NSEventTypeKeyDown, NSEventTypeKeyUp}) {
        NSEvent* event = [NSEvent keyEventWithType:type location:NSZeroPoint modifierFlags:modifiers
            timestamp:[[NSProcessInfo processInfo] systemUptime] windowNumber:[window windowNumber] context:nil
            characters:text charactersIgnoringModifiers:text isARepeat:NO keyCode:code];
        [NSApp sendEvent:event];
    }
}
static void inputProbeCharacter(NSString* text, NSUInteger index) {
    if (index == [text length]) {
        emit("NATIVE_INPUT_COMPLETE");
        dispatch_after(dispatch_time(DISPATCH_TIME_NOW, NSEC_PER_SEC), dispatch_get_main_queue(), ^{
            editor->Apply(editor->CreateCefEvent(ASC_MENU_EVENT_TYPE_CEF_SAVE));
            emit("NATIVE_SAVE_REQUESTED");
        });
        return;
    }
    nativeKey([text substringWithRange:NSMakeRange(index, 1)], 0);
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, 60 * NSEC_PER_MSEC), dispatch_get_main_queue(), ^{
        inputProbeCharacter(text, index + 1);
    });
}
@interface OfficeParent : NSView
@end
@implementation OfficeParent
- (void)setFrameSize:(NSSize)size {
    [super setFrameSize:size];
    if (wrapper) wrapper->resizeEvent();
}
@end
@interface HostDelegate : NSObject <NSApplicationDelegate>
@end
@implementation HostDelegate
- (void)application:(NSApplication*)sender openFiles:(NSArray<NSString*>*)files {
    [sender replyToOpenOrPrint:NSApplicationDelegateReplySuccess];
}
- (void)applicationDidFinishLaunching:(NSNotification*)notification {
    emit("APPLICATION_LAUNCHED");
    window = [[NSWindow alloc] initWithContentRect:NSMakeRect(0, 0, 1100, 800)
        styleMask:NSWindowStyleMaskTitled | NSWindowStyleMaskClosable | NSWindowStyleMaskResizable
        backing:NSBackingStoreBuffered defer:NO];
    [window setTitle:@"FullOffice · ONLYOFFICE 原生编辑研究"];
    [window center]; [window setReleasedWhenClosed:NO];
    OfficeParent* parent = [[OfficeParent alloc] initWithFrame:[[window contentView] bounds]];
    [parent setAutoresizingMask:NSViewWidthSizable | NSViewHeightSizable];
    [window setContentView:parent];
    [window makeKeyAndOrderFront:nil]; [NSApp activateIgnoringOtherApps:YES];
    wrapper = new CCefViewWrapper(parent);
    wrapper->resizeEvent();
    __block bool inputStarted = false;
    __block int readinessTicks = 0;
    if (editProbe) [NSTimer scheduledTimerWithTimeInterval:1 repeats:YES block:^(NSTimer* timer) {
        if (++readinessTicks > 75) { emit("DOCUMENT_READY_TIMEOUT"); [timer invalidate]; return; }
        if (!documentReady) {
            // Observe real editor state; never change document content through JavaScript.
            editor->ExecuteInAllFrames("(function(){var e=(window.Asc&&window.Asc.editor)||window.editor;"
                "if(e&&e.isDocumentLoadComplete===true&&window.AscDesktopEditor)"
                "window.AscDesktopEditor.execCommand('FullOffice.Probe.Ready','1');})();", true);
            return;
        }
        if (inputStarted) return;
        inputStarted = true; [timer invalidate];
        [window makeKeyAndOrderFront:nil]; [NSApp activateIgnoringOtherApps:YES]; editor->focus(true);
        emit(std::string("INPUT_RESPONDER ") + [NSStringFromClass([[window firstResponder] class]) UTF8String]);
        if (document.extension() == ".xlsx") {
            nativeKey(@"\uF729", 115, NSEventModifierFlagCommand);
            nativeKey(@"1", 18); nativeKey(@"0", 29); nativeKey(@"\r", 36);
            dispatch_after(dispatch_time(DISPATCH_TIME_NOW, NSEC_PER_SEC), dispatch_get_main_queue(), ^{
                editor->Apply(editor->CreateCefEvent(ASC_MENU_EVENT_TYPE_CEF_SAVE));
                emit("NATIVE_SAVE_REQUESTED");
            });
            return;
        }
        NSPoint location = NSMakePoint(400, std::max(180.0, parent.frame.size.height - 330.0));
        for (auto type : {NSEventTypeLeftMouseDown, NSEventTypeLeftMouseUp})
            [NSApp sendEvent:[NSEvent mouseEventWithType:type location:location modifierFlags:0
                timestamp:[[NSProcessInfo processInfo] systemUptime] windowNumber:[window windowNumber] context:nil
                eventNumber:0 clickCount:1 pressure:1]];
        nativeKey(@"\uF701", 125, NSEventModifierFlagCommand);
        nativeKey(@"\r", 36);
        inputProbeCharacter(@"MAC_NATIVE_EDIT_SAVED_20261013", 0);
    }];
    emit("EDITOR_BOUNDS " + std::to_string(wrapper->cef_width) + " " + std::to_string(wrapper->cef_height));
    editor = manager->CreateCefEditor(wrapper);
    if (!editor) { emit("EDITOR_CREATION_FAILED"); std::exit(4); }
    wrapper->m_pCefView = editor;
    wrapper->UpdateSize();
    emit("EDITOR_CREATED " + std::to_string(editor->GetId()));
    editor->OpenLocalFile(officeWideString([NSString stringWithUTF8String:document.c_str()]),
        CAscApplicationManager::GetFileFormatByExtentionForSave(officeWideString([NSString stringWithUTF8String:document.c_str()])), L"lang=zh-CN");
    emit("CHILD_VIEWS " + std::to_string([[parent subviews] count]));
    for (NSView* child in [parent subviews])
        emit(std::string("CHILD_VIEW ") + [NSStringFromClass([child class]) UTF8String]);
    wrapper->resizeEvent();
    __block int captureTicks = 0;
    [NSTimer scheduledTimerWithTimeInterval:1 repeats:YES block:^(NSTimer* timer) {
        ++captureTicks;
        if (editProbe && !savedObserved && captureTicks < 90) return;
        if (!editProbe && captureTicks < 35) return;
        [timer invalidate];
        NSString* target = [NSString stringWithUTF8String:(output / "native-office-window.png").c_str()];
        NSTask* capture = [[NSTask alloc] init];
        [capture setLaunchPath:@"/usr/sbin/screencapture"];
        [capture setArguments:@[@"-x", @"-l", [NSString stringWithFormat:@"%ld", [window windowNumber]], target]];
        [capture launch]; [capture waitUntilExit];
        emit("CAPTURE_EXIT " + std::to_string([capture terminationStatus]));
        [NSApp terminate:nil];
    }];
}
@end
int main(int argc, const char* argv[]) {
    if (argc != 6) return 2;
    editProbe = std::getenv("FULLOFFICE_MAC_NATIVE_EDIT_PROBE") != nullptr;
    resource = argv[1]; profile = argv[2]; document = argv[3]; output = argv[4];
    const std::filesystem::path editorEntry = argv[5];
    if (!resource.is_absolute() || !profile.is_absolute() || !document.is_absolute() || !output.is_absolute()
        || !std::filesystem::is_regular_file(document) || !std::filesystem::is_regular_file(editorEntry)) return 2;
    std::filesystem::create_directories(profile); std::filesystem::create_directories(output);
    NSAscApplicationWorker* worker = [[NSAscApplicationWorker alloc] initWithCreator:&createManager];
    manager = [NSAscApplicationWorker getAppManager];
    auto& settings = manager->m_oSettings;
    // Worker initialization selects a default system path; override it before CEF or font setup.
    settings.SetUserDataPath(officeWideString([NSString stringWithUTF8String:profile.c_str()]));
    for (const auto& path : {settings.recover_path, settings.user_plugins_path, settings.fonts_cache_info_path})
        std::filesystem::create_directories(std::filesystem::path(path));
    settings.local_editors_path = officeWideString([NSString stringWithUTF8String:editorEntry.c_str()]);
    settings.file_converter_path = officeWideString([NSString stringWithUTF8String:(resource / "converter").c_str()]);
    settings.spell_dictionaries_path = officeWideString([NSString stringWithUTF8String:(resource / "dictionaries").c_str()]);
    settings.system_templates_path = officeWideString([NSString stringWithUTF8String:(resource / "converter/templates").c_str()]);
    settings.macroses_support = false; settings.plugins_support = false; settings.country = "CN";
    manager->SetEventListener(&listener);
    [worker Start:argc argv:argv];
    emit("CEF_INITIALIZED");
    emit(std::string("APPLICATION_CLASS ") + [NSStringFromClass([NSApp class]) UTF8String]);
    [NSApp setActivationPolicy:NSApplicationActivationPolicyRegular];
    HostDelegate* delegate = [[HostDelegate alloc] init]; [NSApp setDelegate:delegate];
    [NSApp run];
    editor = nullptr; wrapper = nullptr;
    [worker End]; manager = nullptr;
    emit("HOST_CLOSED"); return 0;
}
