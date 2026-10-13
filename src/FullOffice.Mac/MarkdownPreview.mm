#import <AppKit/AppKit.h>
#import <WebKit/WebKit.h>

@protocol FullOfficeMarkdownPreviewDelegate
- (void)fullOfficePreviewOpenDocument:(NSURL*)url;
@end

@class FullOfficeMarkdownPreviewController;
@interface FullOfficeMarkdownSchemeHandler : NSObject <WKURLSchemeHandler>
@property(weak) FullOfficeMarkdownPreviewController* owner;
@end
@protocol FullOfficeMarkdownPreviewAPI
@property(readonly) NSView* view;
@property(weak) id<FullOfficeMarkdownPreviewDelegate> delegate;
- (void)setSource:(NSString*)source documentURL:(NSURL*)url;
- (void)closePreview;
- (void)inspectPreview:(void (^)(NSString*))completion;
@end

@interface FullOfficeMarkdownPreviewController : NSObject <FullOfficeMarkdownPreviewAPI, WKNavigationDelegate, WKURLSchemeHandler>
@property(nonatomic,strong) WKWebView* web;
@property(weak) id<FullOfficeMarkdownPreviewDelegate> delegate;
@property(nonatomic,strong) NSURL* document;
@property(nonatomic,copy) NSString* source;
@property(nonatomic) BOOL ready;
@property(nonatomic,strong) NSHashTable* tasks;
@end

@implementation FullOfficeMarkdownPreviewController
- (instancetype)init {
    if ((self = [super init])) {
        _tasks = [NSHashTable hashTableWithOptions:NSPointerFunctionsStrongMemory | NSPointerFunctionsObjectPointerPersonality];
        WKWebViewConfiguration* configuration = [WKWebViewConfiguration new];
        configuration.websiteDataStore = [WKWebsiteDataStore nonPersistentDataStore];
        FullOfficeMarkdownSchemeHandler* handler = [FullOfficeMarkdownSchemeHandler new]; handler.owner = self;
        [configuration setURLSchemeHandler:handler forURLScheme:@"fulloffice-preview"];
        _web = [[WKWebView alloc] initWithFrame:NSZeroRect configuration:configuration];
        _web.navigationDelegate = self;
        _web.accessibilityIdentifier = @"markdown-preview";
        NSBundle* bundle = [NSBundle bundleForClass:[self class]];
        NSString* path = [bundle pathForResource:@"preview" ofType:@"html"];
        NSString* html = [NSString stringWithContentsOfFile:path encoding:NSUTF8StringEncoding error:nil];
        if (html) [_web loadHTMLString:html baseURL:[NSURL URLWithString:@"fulloffice-preview://document/"]];
    }
    return self;
}
- (NSView*)view { return self.web; }
- (void)setSource:(NSString*)source documentURL:(NSURL*)url {
    self.source = source; self.document = url;
    [self render];
}
- (void)render {
    if (!self.ready || !self.source) return;
    NSString* encoded = [[self.source dataUsingEncoding:NSUTF8StringEncoding] base64EncodedStringWithOptions:0];
    [self.web evaluateJavaScript:[NSString stringWithFormat:@"window.fullOfficeRender('%@')", encoded] completionHandler:nil];
}
- (void)webView:(WKWebView*)web didFinishNavigation:(WKNavigation*)navigation { self.ready = YES; [self render]; }
- (void)webView:(WKWebView*)web decidePolicyForNavigationAction:(WKNavigationAction*)action decisionHandler:(void (^)(WKNavigationActionPolicy))decision {
    if (action.navigationType != WKNavigationTypeLinkActivated) { decision(WKNavigationActionPolicyAllow); return; }
    NSURL* url = action.request.URL;
    if ([url.scheme isEqual:@"fulloffice-preview"] && [url.path isEqual:@"/open"]) {
        NSString* path = nil;
        for (NSURLQueryItem* item in [NSURLComponents componentsWithURL:url resolvingAgainstBaseURL:NO].queryItems)
            if ([item.name isEqual:@"path"]) path = item.value;
        NSURL* target = path ? [NSURL URLWithString:path relativeToURL:self.document].absoluteURL : nil;
        if (target.isFileURL) [self.delegate fullOfficePreviewOpenDocument:target];
        decision(WKNavigationActionPolicyCancel); return;
    }
    if ([url.scheme isEqual:@"fulloffice-preview"] && url.fragment) { decision(WKNavigationActionPolicyAllow); return; }
    if ([@[@"https",@"http",@"mailto"] containsObject:url.scheme.lowercaseString]) [[NSWorkspace sharedWorkspace] openURL:url];
    decision(WKNavigationActionPolicyCancel);
}
- (void)webView:(WKWebView*)web startURLSchemeTask:(id<WKURLSchemeTask>)task {
    NSURL* url = task.request.URL;
    NSString* path = nil;
    for (NSURLQueryItem* item in [NSURLComponents componentsWithURL:url resolvingAgainstBaseURL:NO].queryItems)
        if ([item.name isEqual:@"path"]) path = item.value;
    NSURL* file = path ? [NSURL URLWithString:path relativeToURL:self.document].absoluteURL : nil;
    NSDictionary* mime = @{@"png":@"image/png",@"jpg":@"image/jpeg",@"jpeg":@"image/jpeg",@"gif":@"image/gif",@"webp":@"image/webp",@"svg":@"image/svg+xml"};
    NSString* type = mime[file.pathExtension.lowercaseString];
    if (![url.path isEqual:@"/image"] || !file.isFileURL || !type) {
        [task didFailWithError:[NSError errorWithDomain:@"FullOfficePreview" code:1 userInfo:nil]]; return;
    }
    [self.tasks addObject:task];
    dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED,0), ^{
        NSData* data = [NSData dataWithContentsOfURL:file options:NSDataReadingMappedIfSafe error:nil];
        dispatch_async(dispatch_get_main_queue(), ^{
            if (![self.tasks containsObject:task]) return;
            [self.tasks removeObject:task];
            if (!data) { [task didFailWithError:[NSError errorWithDomain:@"FullOfficePreview" code:2 userInfo:nil]]; return; }
            [task didReceiveResponse:[[NSURLResponse alloc] initWithURL:url MIMEType:type expectedContentLength:data.length textEncodingName:nil]];
            [task didReceiveData:data]; [task didFinish];
        });
    });
}
- (void)webView:(WKWebView*)web stopURLSchemeTask:(id<WKURLSchemeTask>)task { [self.tasks removeObject:task]; }
- (void)closePreview { [self.web stopLoading]; self.web.navigationDelegate = nil; [self.tasks removeAllObjects]; }
- (void)inspectPreview:(void (^)(NSString*))completion {
    if (![NSProcessInfo.processInfo.environment[@"GITHUB_ACTIONS"] isEqual:@"true"]) { completion(@"{}"); return; }
    [self.web evaluateJavaScript:@"JSON.stringify({headings:document.querySelectorAll('#content h1').length,tables:document.querySelectorAll('#content table').length,fences:document.querySelectorAll('#content pre code').length,localImageLoaded:[...document.querySelectorAll('#content img')].some(i=>i.complete&&i.naturalWidth>0),unsafeCodeExecuted:window.__fullOfficeAttack===true})" completionHandler:^(id result,NSError* error) {
        completion([result isKindOfClass:NSString.class] ? result : @"{}");
    }];
}
@end

@implementation FullOfficeMarkdownSchemeHandler
- (void)webView:(WKWebView*)web startURLSchemeTask:(id<WKURLSchemeTask>)task {
    if (self.owner) [self.owner webView:web startURLSchemeTask:task];
    else [task didFailWithError:[NSError errorWithDomain:@"FullOfficePreview" code:3 userInfo:nil]];
}
- (void)webView:(WKWebView*)web stopURLSchemeTask:(id<WKURLSchemeTask>)task { [self.owner webView:web stopURLSchemeTask:task]; }
@end
