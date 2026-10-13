import AppKit
import PDFKit
import Darwin

// Research probe: a PDF-only parent starts the independently validated native
// Office worker on demand. All documents are authored disposable fixtures.
final class ProcessProbe: NSObject, NSApplicationDelegate {
    let arguments = CommandLine.arguments
    var window: NSWindow!
    let pdf = PDFView()
    var child: Process?
    var timer: Timer?
    var checks = [String: Bool]()
    var failure: String?
    var started = Date()
    var navigations = 0
    var returnedNormally = false
    var childExit: Int32?
    var output: URL { URL(fileURLWithPath: arguments[6], isDirectory: true) }
    var core: String {
        URL(fileURLWithPath: arguments[2]).deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("Frameworks/ascdocumentscore.framework/ascdocumentscore").path
    }
    func officeUnloaded() -> Bool {
        let handle = dlopen(core, RTLD_NOLOAD | RTLD_NOW)
        if let handle { dlclose(handle) }
        return handle == nil
    }
    func application(_ sender: NSApplication, openFiles filenames: [String]) {
        sender.reply(toOpenOrPrint: .success)
    }
    func applicationDidFinishLaunching(_ notification: Notification) {
        guard arguments.count == 8 else { exit(2) }
        do {
            try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
            pdf.frame = NSRect(x: 0, y: 0, width: 720, height: 640)
            pdf.document = PDFDocument(url: URL(fileURLWithPath: arguments[1]))
            pdf.autoScales = true
            window = NSWindow(contentRect: pdf.frame, styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
            window.title = "FullOffice PDF · 进程集成验证"
            window.isReleasedWhenClosed = false
            window.contentView = pdf
            window.center()
            window.makeKeyAndOrderFront(nil)
            checks["PDFOpensBeforeOfficeWorkerStarts"] = pdf.document?.pageCount == 3 && window.isVisible
            checks["OfficeLibraryNotLoadedForPDF"] = officeUnloaded()
            Timer.scheduledTimer(withTimeInterval: 1, repeats: false) { _ in self.startOffice() }
        } catch { failure = String(describing: error); finish() }
    }
    func startOffice() {
        do {
            let capture = Process()
            capture.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
            capture.arguments = ["-x", "-l", String(window.windowNumber), output.appendingPathComponent("process-pdf-before-office.png").path]
            try capture.run()
            capture.waitUntilExit()
            checks["PDFCompositorCapturedBeforeOfficeStarts"] = capture.terminationStatus == 0
            let worker = Process()
            worker.executableURL = URL(fileURLWithPath: arguments[2])
            worker.arguments = [arguments[3], arguments[4], arguments[5], arguments[6], arguments[7]]
            worker.environment = ProcessInfo.processInfo.environment.merging(["FULLOFFICE_MAC_NATIVE_EDIT_PROBE": "1"]) { _, value in value }
            worker.terminationHandler = { process in
                DispatchQueue.main.async {
                    self.childExit = process.terminationStatus
                    self.checks["OwnedNativeOfficeWorkerExitsNormally"] = process.terminationReason == .exit && process.terminationStatus == 0
                    self.checks["PDFNavigationContinuesDuringOfficeEditing"] = self.navigations >= 6
                    self.checks["OfficeLibraryStillNotLoadedInPDFParent"] = self.officeUnloaded()
                    self.finish()
                }
            }
            child = worker
            try worker.run()
            started = Date()
            timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { _ in
                guard worker.isRunning else { return }
                if let document = self.pdf.document, let page = document.page(at: self.navigations % document.pageCount) {
                    self.pdf.go(to: page)
                    if self.pdf.currentPage === page { self.navigations += 1 }
                }
                if Date().timeIntervalSince(self.started) > 100 {
                    self.failure = "Owned Office worker reached its deadline"
                    worker.terminate()
                }
            }
        } catch { failure = String(describing: error); finish() }
    }
    func finish() {
        timer?.invalidate()
        returnedNormally = true
        NSApp.stop(nil)
        if let wake = NSEvent.otherEvent(with: .applicationDefined, location: .zero, modifierFlags: [],
            timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: 0, context: nil, subtype: 0, data1: 0, data2: 0) {
            NSApp.postEvent(wake, atStart: false)
        }
    }
    func report() -> Bool {
        checks["PDFApplicationLoopReturnsNormallyAfterOfficeWorker"] = returnedNormally
        let passed = failure == nil && checks.count == 7 && checks.values.allSatisfy { $0 }
        var report: [String: Any] = ["Passed": passed, "Checks": checks, "PDFNavigations": navigations,
            "OwnedWorkerPID": child?.processIdentifier ?? 0,
            "Scope": "Authored PDF parent remains responsive while a deferred, separately owned native Office worker edits and saves. Independent document and native teardown verification are also required. No final app, installer or physical-device claim."]
        if let childExit { report["WorkerExitCode"] = childExit }
        if let failure { report["Failure"] = failure }
        do {
            try JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys])
                .write(to: output.appendingPathComponent("process-parent-report.json"))
        } catch { return false }
        return passed
    }
}

let app = NSApplication.shared
let probe = ProcessProbe()
app.setActivationPolicy(.regular)
app.delegate = probe
withExtendedLifetime(probe) { app.run() }
exit(probe.report() ? 0 : 1)
