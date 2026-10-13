import XCTest
import AppKit
import CryptoKit
import Vision

final class TextUITests: XCTestCase {
    var checks = [String: Bool]()
    let root = URL(fileURLWithPath:#filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
    var output: URL { root.appendingPathComponent("artifacts/mac-text-ui") }
    func hash(_ url: URL) throws -> String { SHA256.hash(data:try Data(contentsOf:url)).map {String(format:"%02x",$0)}.joined() }
    func runtime() -> [String: Any] {
        guard let data=try? Data(contentsOf:output.appendingPathComponent("runtime.json")),let value=try? JSONSerialization.jsonObject(with:data) as? [String:Any] else { return [:] }; return value
    }
    func wait(_ seconds: TimeInterval, _ condition: () -> Bool) -> Bool {
        let end=Date().addingTimeInterval(seconds)
        repeat { if condition() {return true}; Thread.sleep(forTimeInterval:0.2) } while Date()<end
        return false
    }
    func expect(_ name: String,_ value: Bool) throws {
        checks[name]=value
        let report:[String:Any]=["Passed":checks.count==17 && checks.values.allSatisfy{$0},"Checks":checks,
            "Scope":"Actual full Mac app: native Markdown keyboard input, undo/redo, original save, system Save As, retargeted save, lazily loaded preview with tables/code/local image and compositor body OCR, cancel/discard quit. Authored fixtures only; no performance or installation acceptance."]
        try JSONSerialization.data(withJSONObject:report,options:[.prettyPrinted,.sortedKeys]).write(to:output.appendingPathComponent("report.json"))
        XCTAssertTrue(value,name); if !value { throw NSError(domain:"TextUI",code:1,userInfo:[NSLocalizedDescriptionKey:name]) }
    }
    func testMarkdownOriginalSavePreviewAndQuit() throws {
        let fixture=try JSONSerialization.jsonObject(with:Data(contentsOf:output.appendingPathComponent("fixture.json"))) as! [String:String]
        let source=URL(fileURLWithPath:fixture["Source"]!),alternate=output.appendingPathComponent("alternate.md")
        let app=XCUIApplication(bundleIdentifier:"center.dnd.FullOffice")
        defer {if app.state != .notRunning {app.terminate()} }
        app.launchEnvironment=["GITHUB_ACTIONS":"true"]
        app.launchArguments=["--recovery-ui-root",fixture["Root"]!,"--text-ui-file",source.path,output.path,"-AppleLanguages","(en)"]
        app.launch()
        let editor=app.textViews["markdown-source"]
        try expect("ActualNativeTextEditorReady",wait(25){self.runtime()["TextReady"] as? Bool==true} && editor.waitForExistence(timeout:10))
        try expect("OfficeEngineNotLoadedForMarkdown",runtime()["OfficeLoaded"] as? Bool==false && runtime()["OfficeWindows"] as? Int==0)
        try expect("PreviewRendererDeferredUntilRequested",runtime()["PreviewLoaded"] as? Bool==false)
        editor.click(); editor.typeKey("a",modifierFlags:[.command]); editor.typeText(fixture["Edited"]!)
        try expect("ActualUnicodeAndMarkdownKeyboardInput",wait(10){String(describing:editor.value ?? "").contains("MAC_TEXT_EDIT_SAVED_20261014") && self.runtime()["Dirty"] as? Bool==true})
        editor.typeKey("a",modifierFlags:[.command]); editor.typeText("UNDO_PROBE")
        editor.typeKey("z",modifierFlags:[.command])
        try expect("NativeUndoRestoresEditedDocument",wait(10){String(describing:editor.value ?? "").contains("MAC_TEXT_EDIT_SAVED_20261014")})
        editor.typeKey("z",modifierFlags:[.command,.shift])
        try expect("NativeRedoRestoresReplacement",wait(10){String(describing:editor.value ?? "")=="UNDO_PROBE"})
        editor.typeKey("z",modifierFlags:[.command]); editor.typeKey("s",modifierFlags:[.command])
        try expect("ActualOriginalSavePreservesUTF8BOMAndCRLF",wait(20){(try? self.hash(source))==fixture["EditedSHA256"] && self.runtime()["Dirty"] as? Bool==false && self.runtime()["Busy"] as? Bool==false})
        editor.typeKey("s",modifierFlags:[.command,.shift])
        let name=app.dialogs.textFields["saveAsNameTextField"],save=app.dialogs.buttons["Save"]
        try expect("ActualSystemSaveAsPanelShown",name.waitForExistence(timeout:20) && save.waitForExistence(timeout:10))
        name.click(); name.typeKey("a",modifierFlags:[.command]); name.typeText("alternate.md"); save.click()
        try expect("ActualSaveAsWritesChosenFile",wait(20){(try? self.hash(alternate))==fixture["EditedSHA256"] && self.runtime()["Source"] as? String==alternate.path && self.runtime()["Busy"] as? Bool==false})
        try expect("SaveAsPreservesOldOriginal",try hash(source)==fixture["EditedSHA256"])
        editor.click(); editor.typeKey("a",modifierFlags:[.command]); editor.typeText(fixture["Alternate"]!); editor.typeKey("s",modifierFlags:[.command])
        try expect("LaterSaveOnlyTargetsAlternateFile",wait(20){(try? self.hash(alternate))==fixture["AlternateSHA256"] && self.runtime()["Dirty"] as? Bool==false && self.runtime()["Busy"] as? Bool==false} && (try hash(source))==fixture["EditedSHA256"])
        app.buttons["预览"].click()
        try expect("ActualPreviewRendersTablesCodeAndLocalImage",wait(30){
            let value=self.runtime(),preview=value["Preview"] as? [String:Any] ?? [:]
            return value["PreviewLoaded"] as? Bool==true && preview["headings"] as? Int==1 && preview["tables"] as? Int==1 && preview["fences"] as? Int==1 && preview["localImageLoaded"] as? Bool==true
        })
        try expect("RawMarkdownHTMLCannotExecuteScript",(runtime()["Preview"] as? [String:Any])?["unsafeCodeExecuted"] as? Bool==false)
        let web=app.webViews.firstMatch
        var rendered=false
        if web.waitForExistence(timeout:10) {
            let png=web.screenshot().pngRepresentation
            try png.write(to:output.appendingPathComponent("markdown-preview.png"))
            if let image=NSImage(data:png),let cg=image.cgImage(forProposedRect:nil,context:nil,hints:nil) {
                let request=VNRecognizeTextRequest();request.recognitionLevel = .accurate;request.usesLanguageCorrection=false
                try VNImageRequestHandler(cgImage:cg).perform([request])
                let rows=(request.results ?? []).compactMap{$0.topCandidates(1).first?.string}
                try JSONSerialization.data(withJSONObject:["RecognizedRows":rows],options:.prettyPrinted).write(to:output.appendingPathComponent("preview-ocr.json"))
                rendered=rows.joined().uppercased().filter{$0.isLetter || $0.isNumber}.contains("MARKDOWNBODY20261014")
            }
        }
        try expect("ActualPreviewPixelsShowDocumentBody",rendered)
        editor.click();editor.typeKey("a",modifierFlags:[.command]);editor.typeText("DO_NOT_SAVE")
        app.typeKey("q",modifierFlags:[.command]);let cancel=app.dialogs.buttons["取消"]
        XCTAssertTrue(cancel.waitForExistence(timeout:10));cancel.click()
        try expect("QuitCancelKeepsDirtyEditor",app.state != .notRunning && editor.exists && runtime()["Dirty"] as? Bool==true)
        app.typeKey("q",modifierFlags:[.command]);let discard=app.dialogs.buttons["不保存"]
        XCTAssertTrue(discard.waitForExistence(timeout:10));discard.click()
        try expect("ActualMarkdownAppQuitsNormally",app.wait(for:.notRunning,timeout:20))
        try expect("DiscardLeavesBothSavedFilesIntact",try hash(source)==fixture["EditedSHA256"] && hash(alternate)==fixture["AlternateSHA256"])
    }
}
