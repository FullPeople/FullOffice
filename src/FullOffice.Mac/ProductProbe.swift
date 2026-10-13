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
    let closeScenario: String
    init(owner: Delegate, arguments: [String]) throws {
        guard ProcessInfo.processInfo.environment["GITHUB_ACTIONS"] == "true",
              let index = arguments.firstIndex(of: "--office-ui-probe"), arguments.count == index + 4 else {
            throw OfficeSessionError.message("此验证入口只用于独立的云端测试环境。")
        }
        self.owner = owner
        closeScenario = ProcessInfo.processInfo.environment["FULLOFFICE_MAC_CLOSE_PROBE"] ?? ""
        guard ["", "cancel", "save", "discard"].contains(closeScenario) else {
            throw OfficeSessionError.message("未知的关闭验证场景。")
        }
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
            if let session = office.session {
                try FileManager.default.copyItem(at: session.events.appendingPathComponent("native-office-window.png"), to: output.appendingPathComponent("native-office-window.png"))
                let log = try String(contentsOf: session.directory.appendingPathComponent("worker.log"), encoding: .utf8)
                let current = try OfficeFileSession.hash(officeSource)
                if closeScenario == "discard" {
                    checks["DiscardLeavesOriginalBytesUnchanged"] = current == originalBefore && office.committedSaves == 0
                    checks["DiscardCreatesNoReplacementOrBackup"] = session.lastSnapshot == nil && session.lastBackup == nil && !log.contains("ORIGINAL_SAVE_REQUEST")
                    checks["ActualNativeDestroyedAfterDiscard"] = log.contains("DISCARD_REQUESTED") && log.contains("OFFICE_EVENT 8000") && log.contains("HOST_CLOSED")
                } else if let snapshot = session.lastSnapshot, let backup = session.lastBackup {
                    let saved = try OfficeFileSession.hash(snapshot)
                    checks["NativeEditsActuallyCommittedToOriginal"] = office.committedSaves > 0 && current != originalBefore && current == saved
                    checks["PreviousOriginalBytesPreserved"] = try OfficeFileSession.hash(backup) == originalBefore
                    checks["ActualNativeDestroyedAfterOriginalSaveAck"] = log.contains("ORIGINAL_SAVE_ACK OK") && log.contains("OFFICE_EVENT 8000") && log.contains("HOST_CLOSED")
                }
                if !closeScenario.isEmpty {
                    checks["ActualDirtyClosePromptShown"] = log.contains("DIRTY 1") && log.contains("DIRTY_CLOSE_PROMPT_SHOWN")
                    let choice = closeScenario == "discard" ? "DISCARD_REQUESTED" : "CLOSE_CHOICE " + closeScenario.uppercased()
                    checks["ActualCloseDialogChoiceUsed"] = log.contains(choice)
                    if closeScenario == "cancel" {
                        let lines = log.components(separatedBy: .newlines)
                        let kept = lines.firstIndex(of: "CANCELLED_CLOSE_KEPT_DIRTY_EDITOR")
                        let destroyed = lines.firstIndex(of: "OFFICE_EVENT 8000")
                        checks["CancelRetainsDirtyEditorUntilLaterSave"] = kept != nil && destroyed != nil && kept! < destroyed!
                    } else if closeScenario == "save" {
                        let lines = log.components(separatedBy: .newlines)
                        let ack = lines.firstIndex(of: "ORIGINAL_SAVE_ACK OK")
                        let destroyed = lines.firstIndex(of: "OFFICE_EVENT 8000")
                        checks["CloseWaitsForOriginalSaveConfirmation"] = ack != nil && destroyed != nil && ack! < destroyed!
                    } else {
                        let lines = log.components(separatedBy: .newlines)
                        let discard = lines.firstIndex(of: "DISCARD_REQUESTED")
                        let destroyed = lines.firstIndex(of: "OFFICE_EVENT 8000")
                        checks["DiscardOnlyClosesAfterExplicitChoice"] = discard != nil && destroyed != nil && discard! < destroyed!
                    }
                }
            }
        } catch { failure = error.localizedDescription }
        let passed = failure == nil && checks.count == (closeScenario.isEmpty ? 14 : 17) && checks.values.allSatisfy { $0 }
        var report: [String: Any] = ["Passed": passed, "Checks": checks, "PDFNavigations": navigations, "WorkerExitCode": code,
            "Scope": "Actual FullOffice.app main routes authored PDF and Office files to the bundled runtime, with native edits and original save; independent ZIP/OCR and self-contained-bundle checks are also required. Not installation/update or private-workbook acceptance."]
        if let failure { report["Failure"] = failure }
        report["CloseScenario"] = closeScenario
        do { try JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys]).write(to: output.appendingPathComponent("product-report.json")) }
        catch { exit(3) }
        if passed { NSApp.terminate(nil) } else { exit(1) }
    }
}
