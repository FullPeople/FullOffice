// FullOffice native Office research host. GNU AGPL version 3; see LICENSE and NOTICE.txt.
#import <AppKit/AppKit.h>
#include <filesystem>
#include <iostream>
#include <mutex>
#include "mac_application.h"
#include "mac_cefview.h"

static CAscApplicationManager* manager = nullptr;
static CCefViewEditor* editor = nullptr;
static CCefViewWrapper* wrapper = nullptr;
static NSWindow* window = nil;
static std::filesystem::path profile, document, resource, output;
static std::mutex outputMutex;
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
            if (event->m_nType == ASC_MENU_EVENT_TYPE_CEF_CHECK_KEYBOARD && manager) manager->CheckKeyboard();
            else emit("OFFICE_EVENT " + std::to_string(event->m_nType));
            if (event->m_nType == ASC_MENU_EVENT_TYPE_CEF_DESTROY && manager)
                manager->GetApplication()->ExitMessageLoop();
        }
        NSEditorApi::CAscCefMenuEventListener::OnEvent(event);
    }
};
static Listener listener;
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
    wrapper = new CCefViewWrapper(parent);
    wrapper->resizeEvent();
    emit("EDITOR_BOUNDS " + std::to_string(wrapper->cef_width) + " " + std::to_string(wrapper->cef_height));
    editor = manager->CreateCefEditor(wrapper);
    if (!editor) { emit("EDITOR_CREATION_FAILED"); std::exit(4); }
    wrapper->m_pCefView = editor;
    wrapper->UpdateSize();
    emit("EDITOR_CREATED " + std::to_string(editor->GetId()));
    editor->OpenLocalFile(officeWideString([NSString stringWithUTF8String:document.c_str()]),
        CAscApplicationManager::GetFileFormatByExtentionForSave(officeWideString([NSString stringWithUTF8String:document.c_str()])), L"lang=zh-CN");
    [window makeKeyAndOrderFront:nil]; [NSApp activateIgnoringOtherApps:YES];
    wrapper->resizeEvent();
    [NSTimer scheduledTimerWithTimeInterval:35 repeats:NO block:^(NSTimer*) {
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
    [NSApp setActivationPolicy:NSApplicationActivationPolicyRegular];
    HostDelegate* delegate = [[HostDelegate alloc] init]; [NSApp setDelegate:delegate];
    [NSApp run];
    editor = nullptr; wrapper = nullptr;
    [worker End]; manager = nullptr;
    emit("HOST_CLOSED"); return 0;
}
