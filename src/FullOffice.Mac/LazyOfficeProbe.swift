import AppKit
import PDFKit
import Darwin

// The app can create PDF/text windows before the native Office library is loaded.
final class FullOfficeApplication: NSApplication {
    private var handling = false
    @objc func isHandlingSendEvent() -> Bool { handling }
    @objc func setHandlingSendEvent(_ value: Bool) { handling = value }
    override func sendEvent(_ event: NSEvent) {
        let previous = handling; handling = true
        defer { handling = previous }
        super.sendEvent(event)
    }
}
typealias InitOffice = @convention(c) (UnsafePointer<CChar>, UnsafePointer<CChar>) -> Int32
typealias AttachOffice = @convention(c) (UnsafeMutableRawPointer, UnsafePointer<CChar>) -> Int32
typealias OfficeTick = @convention(c) () -> Void
typealias OfficeState = @convention(c) () -> Int32

final class LazyDelegate: NSObject, NSApplicationDelegate {
    var pdfWindow: NSWindow!, officeWindow: NSWindow!, parent: NSView!
    var handle: UnsafeMutableRawPointer?
    var pump: OfficeTick?, shutdown: OfficeState?, ready: OfficeState?, runOffice: OfficeTick?, closeOffice: OfficeTick?
    var timer: Timer?, readiness: Timer?
    var checks = [String: Bool]()
    var failure: String?
    var running = false, finishing = false
    var started = Date(), readyDate: Date?
    let args = CommandLine.arguments
    var output: URL { URL(fileURLWithPath: args[6], isDirectory: true) }
    func symbol<T>(_ name: String, _ type: T.Type) throws -> T {
        guard let handle, let pointer = dlsym(handle, name) else {
            throw NSError(domain: "FullOffice", code: 1, userInfo: [NSLocalizedDescriptionKey: "Missing Office bridge function: " + name])
        }
        return unsafeBitCast(pointer, to: type)
    }
    func application(_ sender: NSApplication, openFiles filenames: [String]) { sender.reply(toOpenOrPrint: .success) }
    func applicationDidFinishLaunching(_ notification: Notification) {
        guard args.count == 7 else { exit(2) }
        do {
            try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
            let pdf = PDFView(frame: NSRect(x: 0, y: 0, width: 700, height: 600))
            pdf.document = PDFDocument(url: URL(fileURLWithPath: args[1])); pdf.autoScales = true
            pdfWindow = NSWindow(contentRect: pdf.frame, styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
            pdfWindow.title = "FullOffice PDF"; pdfWindow.isReleasedWhenClosed = false
            pdfWindow.contentView = pdf; pdfWindow.center(); pdfWindow.makeKeyAndOrderFront(nil)
            checks["PDFOpensBeforeOfficeInitialization"] = pdf.document?.pageCount == 3 && pdfWindow.isVisible
            let unloaded = dlopen(args[2], RTLD_NOLOAD | RTLD_NOW)
            checks["OfficeLibraryNotLoadedForPDF"] = unloaded == nil
            if let unloaded { dlclose(unloaded) }
            // Never hold a main-dispatch callback across the nested CEF loop.
            // Main-queue work cannot reenter that same callback, while AppKit
            // run-loop timers continue to deliver events in the native loop.
            Timer.scheduledTimer(withTimeInterval: 1, repeats: false) { _ in
                let visible = self.capture(self.pdfWindow, "lazy-pdf-before-office.png")
                let preliminary: [String: Any] = ["Checks": self.checks, "PDFCompositorBeforeOffice": visible,
                    "Scope": "PDF startup evidence only; does not establish Office integration success"]
                do {
                    try JSONSerialization.data(withJSONObject: preliminary, options: [.prettyPrinted, .sortedKeys])
                        .write(to: self.output.appendingPathComponent("lazy-pdf-startup.json"))
                } catch { self.failure = String(describing: error); self.finish(); return }
                self.loadOffice()
            }
        } catch { failure = String(describing: error); finish() }
    }
    func loadOffice() {
        do {
            handle = dlopen(args[2], RTLD_NOW | RTLD_LOCAL)
            guard handle != nil else { throw NSError(domain: "FullOffice", code: 2, userInfo: [NSLocalizedDescriptionKey: String(cString: dlerror())]) }
            let initialize = try symbol("fo_office_initialize", InitOffice.self)
            let attach = try symbol("fo_office_attach", AttachOffice.self)
            pump = try symbol("fo_office_pump", OfficeTick.self)
            shutdown = try symbol("fo_office_shutdown", OfficeState.self)
            ready = try symbol("fo_office_ready", OfficeState.self)
            runOffice = try symbol("fo_office_run", OfficeTick.self)
            closeOffice = try symbol("fo_office_close", OfficeTick.self)
            let initialized = args[3].withCString { resources in args[4].withCString { profile in initialize(resources, profile) } }
            guard initialized == 1 else { throw NSError(domain: "FullOffice", code: 3) }
            officeWindow = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1100, height: 800), styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
            officeWindow.title = "FullOffice lazy Office"; officeWindow.isReleasedWhenClosed = false
            parent = NSView(frame: officeWindow.contentView!.bounds); parent.autoresizingMask = [.width, .height]
            officeWindow.contentView = parent; officeWindow.center(); officeWindow.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            let attached = args[5].withCString { attach(Unmanaged.passUnretained(parent).toOpaque(), $0) }
            guard attached == 1 else { throw NSError(domain: "FullOffice", code: 4) }
            checks["NativeOfficeAttachedToAppKitWindow"] = attached == 1
            timer = Timer(timeInterval: 0.016, repeats: true) { _ in self.pump?() }
            RunLoop.main.add(timer!, forMode: .common)
            readiness = Timer(timeInterval: 0.1, repeats: true) { _ in
                if self.ready?() == 1 {
                    self.readyDate = Date(); self.checks["NativeDocumentContentReady"] = true
                    self.readiness?.invalidate()
                    fputs("LAZY_CONTENT_READY_OBSERVED\n", stderr)
                    Timer.scheduledTimer(withTimeInterval: 2, repeats: false) { _ in self.finish() }
                } else if Date().timeIntervalSince(self.started) > 90 {
                    self.failure = "Native Office content-ready timeout"; self.finish()
                }
            }
            RunLoop.main.add(readiness!, forMode: .common)
            running = true
            runOffice?()
            running = false
            if !finishing { failure = "Native Office event loop ended before completion"; finish() }
            else { complete() }
        } catch { failure = String(describing: error); finish() }
    }
    func capture(_ window: NSWindow?, _ name: String) -> Bool {
        guard let window else { return false }
        let process = Process(); process.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
        process.arguments = ["-x", "-l", String(window.windowNumber), output.appendingPathComponent(name).path]
        do { try process.run(); process.waitUntilExit(); return process.terminationStatus == 0 } catch { return false }
    }
    func finish() {
        guard !finishing else { return }
        finishing = true
        fputs("LAZY_CAPTURE_BEGIN\n", stderr)
        readiness?.invalidate(); timer?.invalidate()
        checks["ActualPDFCompositorCapture"] = capture(pdfWindow, "lazy-pdf-window.png")
        checks["ActualOfficeCompositorCapture"] = capture(officeWindow, "lazy-office-window.png")
        fputs("LAZY_CLOSE_REQUEST\n", stderr)
        if running { closeOffice?(); return }
        complete()
    }
    func complete() {
        fputs("LAZY_SHUTDOWN_BEGIN\n", stderr)
        checks["ActualNativeBrowserDestroyedBeforeShutdown"] = shutdown?() == 1
        officeWindow?.close(); pdfWindow?.close()
        let report: [String: Any] = ["Passed": failure == nil && checks.count == 7 && checks.values.allSatisfy { $0 },
            "Checks": checks, "Failure": failure as Any? ?? NSNull(), "OfficeReadySeconds": readyDate?.timeIntervalSince(started) as Any? ?? NSNull(),
            "Scope": "Native PDF first, deferred Office library and real Office view in the same AppKit process; independent OCR required, not complete software acceptance"]
        do { try JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys]).write(to: output.appendingPathComponent("lazy-host-report.json")) }
        catch { exit(3) }
        NSApp.terminate(nil)
    }
}

let app = FullOfficeApplication.shared
let delegate = LazyDelegate()
app.setActivationPolicy(.regular); app.delegate = delegate
withExtendedLifetime(delegate) { app.run() }
