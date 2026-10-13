import XCTest
import CryptoKit
import AppKit
import Vision

final class RecoveryUITests: XCTestCase {
    var checks = [String: Bool]()
    let root = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
    var output: URL { root.appendingPathComponent("artifacts/mac-recovery-ui") }
    func hash(_ path: String) throws -> String {
        SHA256.hash(data: try Data(contentsOf: URL(fileURLWithPath: path))).map { String(format: "%02x", $0) }.joined()
    }
    func expect(_ name: String, _ value: Bool) throws {
        checks[name] = value
        let report: [String: Any] = ["Passed": checks.count == 13 && checks.values.allSatisfy { $0 }, "Checks": checks,
            "Scope": "Actual Mac application menu, recovery chooser, system Save panel, file export, native recovered body verified from compositor screenshot and normal close through XCTest; authored fixtures only, not physical-device or installer acceptance."]
        try JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys]).write(to: output.appendingPathComponent("report.json"))
        XCTAssertTrue(value, name)
        if !value { throw NSError(domain: "RecoveryUI", code: 1, userInfo: [NSLocalizedDescriptionKey: name]) }
    }
    func wait(_ seconds: TimeInterval, until condition: () -> Bool) -> Bool {
        let end = Date().addingTimeInterval(seconds)
        repeat { if condition() { return true }; Thread.sleep(forTimeInterval: 0.2) } while Date() < end
        return false
    }
    func testActualRecoveryExportAndReopen() throws {
        continueAfterFailure = true
        let fixture = try JSONSerialization.jsonObject(with: Data(contentsOf: output.appendingPathComponent("fixture.json"))) as! [String: String]
        let app = XCUIApplication(bundleIdentifier: "center.dnd.FullOffice")
        let editor = XCUIApplication(bundleIdentifier: "center.dnd.FullOffice.OfficeHost")
        defer { if editor.state != .notRunning { editor.terminate() }; if app.state != .notRunning { app.terminate() } }
        app.launchEnvironment = ["GITHUB_ACTIONS": "true"]
        app.launchArguments = ["--recovery-ui-root", fixture["Root"]!, "-AppleLanguages", "(en)"]
        app.launch()
        let menu = app.menuBars.menuBarItems["文件"]
        try expect("ActualMainFileMenuAvailable", menu.waitForExistence(timeout: 20))
        menu.click()
        let action = app.menuItems["恢复文档副本…"]
        try expect("ActualRecoveryMenuAvailable", action.waitForExistence(timeout: 10))
        action.click()
        // NSAlert duplicates its action in the Touch Bar. Select the actual
        // dialog button, rather than an ambiguous application-wide query.
        let recover = app.dialogs.buttons["恢复副本…"]
        try expect("ActualRecoveryChooserShown", recover.waitForExistence(timeout: 20))
        let selector = app.dialogs.popUpButtons.firstMatch
        try expect("ActualRecoveryEntryShowsOriginalFile", selector.exists && (selector.label.contains("original.docx") || String(describing: selector.value ?? "").contains("original.docx")))
        try app.screenshot().pngRepresentation.write(to: output.appendingPathComponent("recovery-list.png"))
        recover.click()
        let save = app.dialogs.buttons["Save"]
        try app.debugDescription.write(to: output.appendingPathComponent("save-panel-ui-tree.txt"), atomically: true, encoding: .utf8)
        try expect("ActualSystemSavePanelShown", save.waitForExistence(timeout: 20))
        try app.screenshot().pngRepresentation.write(to: output.appendingPathComponent("system-save-panel.png"))
        // Click the real system panel. Modern macOS disallows invoking ok:.
        save.click()
        let destination = fixture["Destination"]!
        try expect("ActualRecoveryFileCreated", wait(30) { FileManager.default.fileExists(atPath: destination) })
        try expect("ExactValidatedSnapshotExported", try hash(destination) == fixture["SnapshotSHA256"])
        try expect("ExternalOriginalBytesPreserved", try hash(fixture["Original"]!) == fixture["OriginalSHA256"])
        try expect("RecoveryRecordAndSnapshotRetained", FileManager.default.fileExists(atPath: fixture["Metadata"]!) && FileManager.default.fileExists(atPath: fixture["Snapshot"]!))
        func eventLog() -> String {
            let directories = (try? FileManager.default.contentsOfDirectory(atPath: fixture["Root"]!)) ?? []
            for name in directories {
                let session = URL(fileURLWithPath: fixture["Root"]!).appendingPathComponent(name)
                guard let data = try? Data(contentsOf: session.appendingPathComponent("session.json")),
                      let value = try? JSONSerialization.jsonObject(with: data) as? [String: Any], value["Source"] as? String == destination else { continue }
                return (try? String(contentsOf: session.appendingPathComponent("worker.log"), encoding: .utf8)) ?? ""
            }
            return ""
        }
        try expect("RecoveredDocumentOpensInActualNativeEditor", wait(75) { eventLog().contains("DOCUMENT_CONTENT_READY") } && editor.windows.firstMatch.waitForExistence(timeout: 10))
        try expect("RecoveredBodyActuallyRendered", wait(25) {
            do {
                let png = editor.screenshot().pngRepresentation
                try png.write(to: output.appendingPathComponent("recovered-native-editor.png"))
                guard let image = NSImage(data: png), let cg = image.cgImage(forProposedRect: nil, context: nil, hints: nil) else { return false }
                let request = VNRecognizeTextRequest()
                request.recognitionLevel = .accurate; request.usesLanguageCorrection = false
                try VNImageRequestHandler(cgImage: cg).perform([request])
                let rows = (request.results ?? []).compactMap { $0.topCandidates(1).first?.string }
                let text = rows.joined(separator: " ").uppercased().filter { $0.isLetter || $0.isNumber }
                try JSONSerialization.data(withJSONObject: ["RecognizedRows": rows], options: [.prettyPrinted]).write(to: output.appendingPathComponent("recovered-body-ocr.json"))
                return text.contains("RECOVEREDBODY20261014")
            } catch { return false }
        })
        editor.activate()
        editor.typeKey("q", modifierFlags: [.command])
        try expect("RecoveredNativeEditorClosesNormally", wait(20) { eventLog().contains("OFFICE_EVENT 8000") && eventLog().contains("HOST_CLOSED") } && editor.wait(for: .notRunning, timeout: 10))
        app.activate()
        app.typeKey("q", modifierFlags: [.command])
        try expect("ActualMainQuitsNormallyAfterRecovery", app.wait(for: .notRunning, timeout: 20))
    }
}
