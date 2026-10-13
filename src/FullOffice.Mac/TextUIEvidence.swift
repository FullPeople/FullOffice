import AppKit
import Darwin

// Read-only evidence in the disposable hosted fixture environment.
func startTextUIEvidence(owner: Delegate, output: URL) {
    guard ProcessInfo.processInfo.environment["GITHUB_ACTIONS"] == "true" else { return }
    try? FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
    Timer.scheduledTimer(withTimeInterval: 0.5,repeats: true) { _ in
        guard let text = owner.texts.last else { return }
        let base = Bundle.main.bundleURL
        func loaded(_ path: URL) -> Bool { let handle = dlopen(path.path,RTLD_NOLOAD | RTLD_NOW); if let handle { dlclose(handle) }; return handle != nil }
        var value: [String: Any] = ["TextReady": text.session != nil && !text.busy,
            "OfficeLoaded":loaded(base.appendingPathComponent("Contents/Helpers/FullOfficeOfficeHost.app/Contents/Frameworks/ascdocumentscore.framework/ascdocumentscore")),
            "PreviewLoaded":loaded(base.appendingPathComponent("Contents/PlugIns/FullOfficeMarkdownPreview.bundle/Contents/MacOS/FullOfficeMarkdownPreview")),
            "Dirty":text.dirty,"Busy":text.busy,"OfficeWindows":owner.offices.count]
        if let session = text.session { value["Source"] = session.source.path }
        text.inspectPreview { preview in
            if let bytes = preview.data(using:.utf8), let object = try? JSONSerialization.jsonObject(with: bytes) { value["Preview"] = object }
            if let bytes = try? JSONSerialization.data(withJSONObject: value,options:[.prettyPrinted,.sortedKeys]) { try? bytes.write(to: output.appendingPathComponent("runtime.json"),options:.atomic) }
        }
    }
}
