import AppKit
import PDFKit
import Darwin

// Runs only in the disposable hosted validation environment, using authored files.
final class ProductProbe {
    weak var owner: Delegate?
    let pdfSource: URL
    let officeSource: URL
    let output: URL
    var reader: ReaderWindow?
    var timer: Timer?
    var navigations = 0
    var checks = [String: Bool]()
    var originalBefore: String?
    var failure: String?
    init(owner: Delegate, arguments: [String]) throws {
        guard ProcessInfo.processInfo.environment["GITHUB_ACTIONS"] == "true",
              let index = arguments.firstIndex(of: "--office-ui-probe"), arguments.count == index + 4 else {
            throw OfficeSessionError.message("此验证入口只用于独立的云端测试环境。")
        }
        self.owner = owner
        pdfSource = URL(fileURLWithPath: arguments[index+1])
        officeSource = URL(fileURLWithPath: arguments[index+2])
        output = URL(fileURLWithPath: arguments[index+3], isDirectory: true)
        try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
    }
    private func officeUnloaded() -> Bool {
        let core = Bundle.main.bundleURL.appendingPathComponent("Contents/Helpers/FullOfficeOfficeHost.app/Contents/Frameworks/ascdocumentscore.framework/ascdocumentscore")
        let handle = dlopen(core.path, RTLD_NOLOAD | RTLD_NOW)
        if let handle { dlclose(handle) }
        return handle == nil
    }
    func start() {
        guard let owner else { return }
        owner.openDocument(pdfSource)
        reader = owner.readers.last
        checks["ActualMainRoutesPDFBeforeOfficeStarts"] = reader?.pdfDocument?.pageCount == 3 && owner.offices.isEmpty
        checks["ActualPDFWindowVisible"] = reader?.window?.isVisible == true
        checks["OfficeNotLoadedForPDF"] = officeUnloaded()
        Timer.scheduledTimer(withTimeInterval: 1, repeats: false) { _ in
            do {
                if let window = self.reader?.window {
                    let capture = Process(); capture.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
                    capture.arguments = ["-x", "-l", String(window.windowNumber), self.output.appendingPathComponent("product-pdf-before-office.png").path]
                    try capture.run(); capture.waitUntilExit()
                    self.checks["ActualPDFCompositorCapturedBeforeOffice"] = capture.terminationStatus == 0
                }
                let runtime = try OfficeRuntime.bundled()
                self.checks["ActualBundledOfficeRuntimeSelected"] = runtime.executable.path.hasPrefix(Bundle.main.bundleURL.path + "/") && runtime.resources.path.hasPrefix(Bundle.main.bundleURL.path + "/")
                self.originalBefore = try OfficeFileSession.hash(self.officeSource)
                owner.openDocument(self.officeSource)
                self.checks["ActualMainRoutesOfficeToOwnedCoordinator"] = owner.offices.count == 1
                self.timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { _ in
                    guard let office = owner.offices.first, office.isActive, let document = self.reader?.pdfDocument else { return }
                    if let page = document.page(at: self.navigations % document.pageCount) {
                        self.reader?.pdf.go(to: page)
                        if self.reader?.pdf.currentPage === page { self.navigations += 1 }
                    }
                }
            } catch { self.fail(error.localizedDescription) }
        }
    }
    func fail(_ message: String) { failure = message }
    func completed(_ office: OfficeCoordinator, code: Int32) {
        timer?.invalidate()
        checks["ActualOfficeWorkerExitsNormally"] = code == 0
        checks["PDFNavigationContinuesDuringOfficeEditing"] = navigations >= 6
        checks["OfficeStillNotLoadedInPDFMain"] = officeUnloaded()
        checks["NoFailedSaveHidden"] = office.saveFailures == 0
        checks["ActualPDFWindowRemainsAvailableAfterOfficeClose"] = reader?.window?.isVisible == true
        do {
            if let session = office.session, let snapshot = session.lastSnapshot, let backup = session.lastBackup {
                let saved = try OfficeFileSession.hash(snapshot), current = try OfficeFileSession.hash(officeSource)
                checks["NativeEditsActuallyCommittedToOriginal"] = office.committedSaves > 0 && current != originalBefore && current == saved
                checks["PreviousOriginalBytesPreserved"] = try OfficeFileSession.hash(backup) == originalBefore
                try FileManager.default.copyItem(at: session.events.appendingPathComponent("native-office-window.png"), to: output.appendingPathComponent("native-office-window.png"))
                let log = try String(contentsOf: session.directory.appendingPathComponent("worker.log"), encoding: .utf8)
                checks["ActualNativeDestroyedAfterOriginalSaveAck"] = log.contains("ORIGINAL_SAVE_ACK OK") && log.contains("OFFICE_EVENT 8000") && log.contains("HOST_CLOSED")
            }
        } catch { failure = error.localizedDescription }
        let passed = failure == nil && checks.count == 14 && checks.values.allSatisfy { $0 }
        var report: [String: Any] = ["Passed": passed, "Checks": checks, "PDFNavigations": navigations, "WorkerExitCode": code,
            "Scope": "Actual FullOffice.app main routes authored PDF and Office files to the bundled runtime, with native edits and original save; independent ZIP/OCR and self-contained-bundle checks are also required. Not installation/update or private-workbook acceptance."]
        if let failure { report["Failure"] = failure }
        do { try JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys]).write(to: output.appendingPathComponent("product-report.json")) }
        catch { exit(3) }
        if passed { NSApp.terminate(nil) } else { exit(1) }
    }
}
