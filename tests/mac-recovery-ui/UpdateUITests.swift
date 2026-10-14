import XCTest
import AppKit
import CryptoKit

final class UpdateUITests: XCTestCase {
    var checks = [String: Bool]()
    let root = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
    var output: URL { root.appendingPathComponent("artifacts/mac-public-update-ui") }
    func hash(_ url: URL) throws -> String {
        let stream = try FileHandle(forReadingFrom: url); defer { try? stream.close() }
        var digest = SHA256()
        while let data = try stream.read(upToCount: 1048576), !data.isEmpty { digest.update(data: data) }
        return digest.finalize().map { String(format: "%02x", $0) }.joined()
    }
    func expect(_ name: String, _ value: Bool) throws {
        checks[name] = value
        let report: [String: Any] = ["Passed": checks.count == 14 && checks.values.allSatisfy { $0 }, "Checks": checks,
            "Scope": "Actual installed published 0.3.5 app menu, production signed public 0.3.6 discovery/full download, active-document guard, preserved original and system Installer handoff through XCTest. Native installed upgrade is separate; no physical/Gatekeeper/authentication claim."]
        try JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys]).write(to: output.appendingPathComponent("report.json"))
        XCTAssertTrue(value, name)
        if !value { throw NSError(domain: "UpdateUI", code: 1, userInfo: [NSLocalizedDescriptionKey: name]) }
    }
    func testRealMenuDownloadGuardAndSystemInstaller() throws {
        let fixture = try JSONSerialization.jsonObject(with: Data(contentsOf: output.appendingPathComponent("fixture.json"))) as! [String: String]
        let source = URL(fileURLWithPath: fixture["Source"]!)
        let packageRoot = URL(fileURLWithPath: fixture["DownloadsRoot"]!)
        let app = XCUIApplication(bundleIdentifier: "center.dnd.FullOffice")
        let installer = XCUIApplication(bundleIdentifier: "com.apple.installer")
        defer { if app.state != .notRunning { app.terminate() }; if installer.state != .notRunning { installer.terminate() } }
        for editing in [true, false] {
            let prefix = editing ? "ActiveDocument" : "InstallerHandoff"
            app.launchEnvironment = ["GITHUB_ACTIONS": "true"]
            app.launchArguments = ["--recovery-ui-root", fixture["Root"]!, "-AppleLanguages", "(en)"]
            if editing { app.launchArguments += ["--text-ui-file", source.path, output.path] }
            app.launch()
            if editing {
                let editor = app.textViews["markdown-source"]
                XCTAssertTrue(editor.waitForExistence(timeout: 30))
                editor.click(); editor.typeKey("a", modifierFlags: [.command]); editor.typeText("UNSAVED_UPDATE_GUARD_BODY")
            }
            let menu = app.menuBars.menuBarItems["FullOffice"]
            try expect(prefix + "ActualInstalledAppMenu", menu.waitForExistence(timeout: 20)); menu.click()
            let action = app.menuItems["检查更新…"]
            try expect(prefix + "ActualCheckUpdateMenu", action.waitForExistence(timeout: 10)); action.click()
            let offer = app.dialogs.buttons["下载更新"]
            let version = app.dialogs.staticTexts.matching(NSPredicate(format: "label CONTAINS %@", "0.3.6")).firstMatch
            try expect(prefix + "ActualSignedPublished036Offer", offer.waitForExistence(timeout: 75) && version.exists)
            let previous = Set((try? FileManager.default.subpathsOfDirectory(atPath: packageRoot.path)) ?? [])
            offer.click()
            let install = app.dialogs.buttons["打开安装程序"]
            try expect(prefix + "ActualFullDownloadCompletes", install.waitForExistence(timeout: 900))
            let now = (try? FileManager.default.subpathsOfDirectory(atPath: packageRoot.path)) ?? []
            let candidates = now.filter { !previous.contains($0) && $0.hasSuffix("FullOffice-0.3.6-Setup.pkg") }
            let package = candidates.count == 1 ? packageRoot.appendingPathComponent(candidates[0]) : nil
            let digestMatches = package != nil ? (try hash(package!)) == fixture["ExpectedSHA256"]! : false
            try expect(prefix + "ActualCompletePackageDigestMatches", digestMatches)
            try app.screenshot().pngRepresentation.write(to: output.appendingPathComponent(prefix + "-completed.png"))
            install.click()
            if editing {
                let keep = app.dialogs.buttons["继续编辑"]
                try expect(prefix + "EditingBlocksSystemInstaller", keep.waitForExistence(timeout: 20) && installer.state == .notRunning)
                keep.click(); app.activate()
                let originalIntact = try hash(source) == fixture["OriginalSHA256"]!
                let preserved = app.textViews["markdown-source"].exists && String(describing: app.textViews["markdown-source"].value ?? "").contains("UNSAVED_UPDATE_GUARD_BODY") && originalIntact
                app.typeKey("q", modifierFlags: [.command]); let discard = app.dialogs.buttons["不保存"]
                XCTAssertTrue(discard.waitForExistence(timeout: 15)); discard.click()
                let closed = app.wait(for: .notRunning, timeout: 20)
                let stillIntact = try hash(source) == fixture["OriginalSHA256"]!
                try expect(prefix + "OriginalAndUnsavedEditorPreserved", preserved && closed && stillIntact)
            } else {
                try expect(prefix + "ActualSystemInstallerOpened", installer.wait(for: .runningForeground, timeout: 30) && installer.windows.firstMatch.waitForExistence(timeout: 15))
                try expect(prefix + "MainAppClosesBeforeSystemInstall", app.wait(for: .notRunning, timeout: 20))
                installer.terminate()
                try JSONSerialization.data(withJSONObject: ["Package": package!.path, "SHA256": fixture["ExpectedSHA256"]!], options: .prettyPrinted).write(to: output.appendingPathComponent("actual-download.json"))
            }
        }
    }
}
