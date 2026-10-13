// FullOffice native Office research host. GNU AGPL version 3; see LICENSE and NOTICE.txt.
#import <AppKit/AppKit.h>
#include <filesystem>
#include <iostream>
#include <mutex>
#include <atomic>
#include <fstream>
#include <vector>
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
static bool saveToOriginal = false;
static std::string closeProbe;
static bool closePromptProbed = false;
static std::atomic<bool> documentReady{false}, savedObserved{false};
static std::atomic<bool> nativeDirty{false}, originalPending{false}, originalFailed{false}, originalUncommitted{false};
static std::atomic<unsigned long> editRevision{0}, savedRevision{0}, saveSequence{0};
static std::atomic<bool> closingAfterSave{false};
static NSTextField* commitStatus = nil;
static unsigned long commandSequence = 0;
static std::wstring lastProbeState;
static void emit(const std::string& text) {
    std::lock_guard<std::mutex> lock(outputMutex);
    std::cout << text << std::endl;
    if (editProbe && saveToOriginal) {
        std::ofstream diagnostic(output / "native-events.log", std::ios::app);
        diagnostic << text << std::endl;
    }
}
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
                if (data->get_Command() == L"FullOffice.Probe.State" && data->get_Param() != lastProbeState) {
                    lastProbeState = data->get_Param();
                    NSString* state = [[NSString alloc] initWithBytes:lastProbeState.data() length:lastProbeState.size()*sizeof(wchar_t) encoding:NSUTF32LittleEndianStringEncoding];
                    emit(std::string("READINESS_STATE ") + ([state UTF8String] ?: "decode-error"));
                }
            }
            if (event->m_nType == ASC_MENU_EVENT_TYPE_CEF_MODIFY_CHANGED && event->m_pData) {
                auto* data = static_cast<NSEditorApi::CAscDocumentModifyChanged*>(event->m_pData);
                nativeDirty = data->get_Changed();
                if (nativeDirty) { ++editRevision; originalUncommitted = true; }
                dispatch_async(dispatch_get_main_queue(), ^{ [window setDocumentEdited:nativeDirty || originalPending || originalUncommitted]; });
                emit(data->get_Changed() ? "DIRTY 1" : "DIRTY 0");
            }
            if (event->m_nType == ASC_MENU_EVENT_TYPE_CEF_ONSAVE && event->m_pData) {
                auto* data = static_cast<NSEditorApi::CAscDocumentOnSaveData*>(event->m_pData);
                emit(data->get_IsCancel() ? "SAVED CANCEL" : "SAVED OK");
                if (!data->get_IsCancel()) {
                    savedObserved = true;
                    if (saveToOriginal) {
                        savedRevision = editRevision.load(); originalPending = true; originalFailed = false;
                        emit("ORIGINAL_SAVE_REQUEST " + std::to_string(++saveSequence));
                        dispatch_async(dispatch_get_main_queue(), ^{ [commitStatus setStringValue:@"正在保存到原文件…"]; });
                    }
                } else if (closingAfterSave) dispatch_async(dispatch_get_main_queue(), ^{
                    closingAfterSave = false;
                });
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
static void captureOfficeWindow() {
    NSString* target = [NSString stringWithUTF8String:(output / "native-office-window.png").c_str()];
    NSTask* capture = [[NSTask alloc] init];
    [capture setLaunchPath:@"/usr/sbin/screencapture"];
    [capture setArguments:@[@"-x", @"-l", [NSString stringWithFormat:@"%ld", [window windowNumber]], target]];
    [capture launch]; [capture waitUntilExit];
    emit("CAPTURE_EXIT " + std::to_string([capture terminationStatus]));
}
static void probeFinishedInput() {
    emit("NATIVE_INPUT_COMPLETE");
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, NSEC_PER_SEC), dispatch_get_main_queue(), ^{
        if (!closeProbe.empty()) {
            if (!nativeDirty || !originalUncommitted) { emit("DIRTY_CLOSE_PROBE_NOT_DIRTY"); return; }
            captureOfficeWindow();
            [NSApp terminate:nil];
            if (closeProbe == "cancel") dispatch_after(dispatch_time(DISPATCH_TIME_NOW, NSEC_PER_SEC), dispatch_get_main_queue(), ^{
                if (!nativeDirty || !originalUncommitted || !editor || ![window isVisible]) return;
                emit("CANCELLED_CLOSE_KEPT_DIRTY_EDITOR");
                editor->Apply(editor->CreateCefEvent(ASC_MENU_EVENT_TYPE_CEF_SAVE));
                emit("NATIVE_SAVE_REQUESTED");
            });
            return;
        }
        editor->Apply(editor->CreateCefEvent(ASC_MENU_EVENT_TYPE_CEF_SAVE));
        emit("NATIVE_SAVE_REQUESTED");
    });
}
static void inputProbeCharacter(NSString* text, NSUInteger index) {
    if (index == [text length]) {
        probeFinishedInput();
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
@interface HostDelegate : NSObject <NSApplicationDelegate, NSWindowDelegate, NSMenuItemValidation>
@end
@implementation HostDelegate
- (void)saveDocument:(id)sender {
    if (editor && documentReady && !originalPending) editor->Apply(editor->CreateCefEvent(ASC_MENU_EVENT_TYPE_CEF_SAVE));
}
- (BOOL)validateMenuItem:(NSMenuItem*)item {
    if ([item action] == @selector(saveDocument:)) return editor && documentReady && !originalPending;
    return YES;
}
- (void)openOtherDocument:(id)sender { emit("OPEN_FILE_REQUEST"); }
- (void)saveDocumentAs:(id)sender { emit("SAVE_AS_REQUEST"); }
- (BOOL)windowShouldClose:(NSWindow*)sender { [NSApp terminate:nil]; return NO; }
- (NSApplicationTerminateReply)applicationShouldTerminate:(NSApplication*)sender {
    // The pinned NSAscApplication only honors NSTerminateCancel; NSTerminateLater
    // would still destroy its views. Cancel this request and retry after our ACK.
    if (originalPending) { closingAfterSave = true; return NSTerminateCancel; }
    if (!nativeDirty && (!saveToOriginal || !originalUncommitted)) return NSTerminateNow;
    NSAlert* alert = [[NSAlert alloc] init];
    [alert setMessageText:@"保存当前文档的修改？"];
    [alert setInformativeText:@"保存完成后再关闭文档。"];
    [alert addButtonWithTitle:@"保存"]; [alert addButtonWithTitle:@"取消"]; [alert addButtonWithTitle:@"不保存"];
    if (!closeProbe.empty() && !closePromptProbed) {
        closePromptProbed = true;
        emit("DIRTY_CLOSE_PROMPT_SHOWN");
        const NSUInteger index = closeProbe == "cancel" ? 1 : closeProbe == "discard" ? 2 : 0;
        NSTimer* click = [NSTimer timerWithTimeInterval:0.5 repeats:NO block:^(NSTimer* timer) {
            [[alert buttons][index] performClick:nil];
        }];
        [[NSRunLoop mainRunLoop] addTimer:click forMode:NSModalPanelRunLoopMode];
    }
    const NSModalResponse choice = [alert runModal];
    if (choice == NSAlertSecondButtonReturn) { emit("CLOSE_CHOICE CANCEL"); return NSTerminateCancel; }
    if (choice == NSAlertThirdButtonReturn) { emit("DISCARD_REQUESTED"); return NSTerminateNow; }
    emit("CLOSE_CHOICE SAVE");
    closingAfterSave = true; [self saveDocument:nil]; return NSTerminateCancel;
}
- (void)application:(NSApplication*)sender openFiles:(NSArray<NSString*>*)files {
    [sender replyToOpenOrPrint:NSApplicationDelegateReplySuccess];
}
- (void)applicationDidFinishLaunching:(NSNotification*)notification {
    emit("APPLICATION_LAUNCHED");
    window = [[NSWindow alloc] initWithContentRect:NSMakeRect(0, 0, 1100, 800)
        styleMask:NSWindowStyleMaskTitled | NSWindowStyleMaskClosable | NSWindowStyleMaskResizable | NSWindowStyleMaskMiniaturizable
        backing:NSBackingStoreBuffered defer:NO];
    NSString* title = [[NSProcessInfo processInfo] environment][@"FULLOFFICE_MAC_ORIGINAL_NAME"];
    [window setTitle:title ? [title stringByAppendingString:@" — FullOffice"] : @"FullOffice · ONLYOFFICE 原生编辑研究"];
    [window center]; [window setReleasedWhenClosed:NO];
    [window setDelegate:self];
    OfficeParent* parent = [[OfficeParent alloc] initWithFrame:[[window contentView] bounds]];
    [parent setAutoresizingMask:NSViewWidthSizable | NSViewHeightSizable];
    [window setContentView:parent];
    if (saveToOriginal) {
        NSView* surface = [[NSView alloc] initWithFrame:[parent frame]];
        [window setContentView:surface];
        [parent setFrame:NSMakeRect(0, 28, [surface bounds].size.width, [surface bounds].size.height-28)];
        [surface addSubview:parent];
        commitStatus = [NSTextField labelWithString:@"正在打开文档…"];
        [commitStatus setFrame:NSMakeRect(12, 3, [surface bounds].size.width-24, 22)];
        [commitStatus setAutoresizingMask:NSViewWidthSizable]; [surface addSubview:commitStatus];
    }
    NSMenu* menu = [[NSMenu alloc] init]; NSMenuItem* appItem = [[NSMenuItem alloc] init];
    NSMenu* appMenu = [[NSMenu alloc] initWithTitle:@"FullOffice"];
    [appItem setSubmenu:appMenu]; [menu addItem:appItem];
    [appMenu addItemWithTitle:@"关闭文档" action:@selector(terminate:) keyEquivalent:@"q"];
    NSMenuItem* fileItem = [[NSMenuItem alloc] initWithTitle:@"文件" action:nil keyEquivalent:@""];
    NSMenu* fileMenu = [[NSMenu alloc] initWithTitle:@"文件"]; [fileItem setSubmenu:fileMenu]; [menu addItem:fileItem];
    NSMenuItem* openItem = [fileMenu addItemWithTitle:@"打开其他文档…" action:@selector(openOtherDocument:) keyEquivalent:@"o"]; [openItem setTarget:self];
    NSMenuItem* saveItem = [fileMenu addItemWithTitle:@"保存" action:@selector(saveDocument:) keyEquivalent:@"s"]; [saveItem setTarget:self];
    NSMenuItem* saveAsItem = [fileMenu addItemWithTitle:@"另存为…" action:@selector(saveDocumentAs:) keyEquivalent:@"s"];
    [saveAsItem setKeyEquivalentModifierMask:NSEventModifierFlagCommand | NSEventModifierFlagShift]; [saveAsItem setTarget:self];
    [NSApp setMainMenu:menu];
    [window makeKeyAndOrderFront:nil]; [NSApp activateIgnoringOtherApps:YES];
    wrapper = new CCefViewWrapper(parent);
    wrapper->resizeEvent();
    __block bool inputStarted = false;
    __block int readinessTicks = 0;
    [NSTimer scheduledTimerWithTimeInterval:1 repeats:YES block:^(NSTimer* timer) {
        if (++readinessTicks > 75 && editProbe) { emit("DOCUMENT_READY_TIMEOUT"); [timer invalidate]; return; }
        if (!documentReady) {
            // Observe real editor state; never change document content through JavaScript.
            editor->ExecuteInAllFrames("(function(){var a=window.Asc&&window.Asc.editor,b=window.editor,c=window.AscCommon&&window.AscCommon.editor;"
                "if(!window.AscDesktopEditor)return;var e=a||b||c;"
                "window.AscDesktopEditor.execCommand('FullOffice.Probe.State',JSON.stringify({asc:typeof a,editor:typeof b,common:typeof c,callback:e&&typeof e.asc_registerCallback}));"
                "if(e&&typeof e.asc_registerCallback==='function'&&window.__fullOfficeReadyApi!==e){"
                "window.__fullOfficeReadyApi=e;e.asc_registerCallback('asc_onDocumentContentReady',function(){"
                "window.AscDesktopEditor.execCommand('FullOffice.Probe.Ready','1');},false);"
                "window.AscDesktopEditor.execCommand('FullOffice.Probe.State','public-ready-callback-registered');}})();", true);
            return;
        }
        if (inputStarted) return;
        inputStarted = true; [timer invalidate];
        if (!editProbe) { [commitStatus setStringValue:@"文档已打开"]; return; }
        [window makeKeyAndOrderFront:nil]; [NSApp activateIgnoringOtherApps:YES]; editor->focus(true);
        emit(std::string("INPUT_RESPONDER ") + [NSStringFromClass([[window firstResponder] class]) UTF8String]);
        if (document.extension() == ".xlsx") {
            nativeKey(@"\uF729", 115, NSEventModifierFlagCommand);
            nativeKey(@"1", 18); nativeKey(@"0", 29); nativeKey(@"\r", 36);
            probeFinishedInput();
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
    if (saveToOriginal) [NSTimer scheduledTimerWithTimeInterval:0.2 repeats:YES block:^(NSTimer* timer) {
        const auto readState = [](const char* name) -> NSDictionary* {
            NSString* path = [NSString stringWithUTF8String:(output / name).c_str()];
            NSData* bytes = [NSData dataWithContentsOfFile:path];
            if (!bytes || [bytes length] > 65536) return nil;
            id value = [NSJSONSerialization JSONObjectWithData:bytes options:0 error:nullptr];
            return [value isKindOfClass:[NSDictionary class]] ? value : nil;
        };
        NSDictionary* command = readState("command.json");
        if ([command[@"Sequence"] unsignedLongValue] > commandSequence) {
            commandSequence = [command[@"Sequence"] unsignedLongValue];
            if ([command[@"Action"] isEqual:@"save"]) [self saveDocument:nil];
            else if ([command[@"Action"] isEqual:@"close"]) [NSApp terminate:nil];
        }
        if (!originalPending) return;
        NSDictionary* state = readState("commit-state.json");
        if ([state[@"Sequence"] unsignedLongValue] != saveSequence.load()) return;
        const bool ok = [state[@"Success"] boolValue];
        originalPending = false; originalFailed = !ok;
        if (ok && editRevision == savedRevision && !nativeDirty) originalUncommitted = false;
        NSString* message = state[@"Message"];
        if ([message isKindOfClass:[NSString class]]) [commitStatus setStringValue:message];
        NSString* name = state[@"DocumentName"];
        if (ok && [name isKindOfClass:[NSString class]]) [window setTitle:[name stringByAppendingString:@" — FullOffice"]];
        [window setDocumentEdited:nativeDirty || originalUncommitted];
        emit(ok ? "ORIGINAL_SAVE_ACK OK" : "ORIGINAL_SAVE_ACK FAILED");
        if (closingAfterSave) {
            closingAfterSave = false;
            if (ok && !nativeDirty && !originalUncommitted) [NSApp terminate:nil];
        }
    }];
    __block int captureTicks = 0;
    if ((editProbe && closeProbe != "save" && closeProbe != "discard") || !saveToOriginal) [NSTimer scheduledTimerWithTimeInterval:1 repeats:YES block:^(NSTimer* timer) {
        ++captureTicks;
        if (editProbe && !savedObserved && captureTicks < 90) return;
        if (!editProbe && captureTicks < 35) return;
        if (saveToOriginal && (originalPending || originalFailed)) return;
        [timer invalidate];
        captureOfficeWindow();
        [NSApp terminate:nil];
    }];
}
@end
int main(int argc, const char* argv[]) {
    if (argc != 6) return 2;
    editProbe = std::getenv("FULLOFFICE_MAC_NATIVE_EDIT_PROBE") != nullptr;
    saveToOriginal = std::getenv("FULLOFFICE_MAC_SAVE_TO_ORIGINAL") != nullptr;
    if (editProbe && saveToOriginal && std::getenv("GITHUB_ACTIONS") && std::string(std::getenv("GITHUB_ACTIONS")) == "true") {
        if (const char* scenario = std::getenv("FULLOFFICE_MAC_CLOSE_PROBE")) {
            closeProbe = scenario;
            if (closeProbe != "cancel" && closeProbe != "save" && closeProbe != "discard") return 2;
        }
    }
    resource = argv[1]; profile = argv[2]; document = argv[3]; output = argv[4];
    const std::filesystem::path editorEntry = argv[5];
    if (!resource.is_absolute() || !profile.is_absolute() || !document.is_absolute() || !output.is_absolute()
        || !std::filesystem::is_regular_file(document) || !std::filesystem::is_regular_file(editorEntry)) return 2;
    std::filesystem::create_directories(profile); std::filesystem::create_directories(output);
    emit("HOST_BEFORE_WORKER_CREATION");
    if (editProbe) {
        emit(std::string("HOST_MAIN_BUNDLE ") + [[[NSBundle mainBundle] bundlePath] UTF8String]);
        emit(std::string("HOST_MAIN_FRAMEWORKS ") + [[[NSBundle mainBundle] privateFrameworksPath] UTF8String]);
    }
    NSAscApplicationWorker* worker = [[NSAscApplicationWorker alloc] initWithCreator:&createManager];
    emit("HOST_WORKER_CREATED");
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
    // CEF derives its default bundle from the outermost .app in the executable
    // path. Our editor is nested inside the PDF app, so give it its own bundle
    // and framework explicitly. These are CEF's supported Mac path switches.
    // https://github.com/chromiumembedded/cef/blob/6533/libcef/common/util_mac.mm
    const std::string mainBundle = [[[NSBundle mainBundle] bundlePath] UTF8String];
    const auto cefFramework = std::filesystem::path(mainBundle) / "Contents/Frameworks/Chromium Embedded Framework.framework";
    if (!std::filesystem::is_directory(cefFramework)) return 2;
    std::vector<std::string> cefArguments;
    for (int index = 0; index < argc; ++index) cefArguments.emplace_back(argv[index]);
    cefArguments.emplace_back("--main-bundle-path=" + mainBundle);
    cefArguments.emplace_back("--framework-dir-path=" + cefFramework.string());
    std::vector<const char*> cefArgv;
    for (const auto& argument : cefArguments) cefArgv.push_back(argument.c_str());
    emit("HOST_BEFORE_CEF_START");
    [worker Start:static_cast<int>(cefArgv.size()) argv:cefArgv.data()];
    emit("CEF_INITIALIZED");
    emit(std::string("APPLICATION_CLASS ") + [NSStringFromClass([NSApp class]) UTF8String]);
    [NSApp setActivationPolicy:NSApplicationActivationPolicyRegular];
    HostDelegate* delegate = [[HostDelegate alloc] init]; [NSApp setDelegate:delegate];
    [NSApp run];
    editor = nullptr; wrapper = nullptr;
    [worker End]; manager = nullptr;
    emit("HOST_CLOSED"); return 0;
}
