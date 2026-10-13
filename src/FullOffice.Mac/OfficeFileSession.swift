import Foundation
import CryptoKit

enum OfficeSessionError: LocalizedError {
    case message(String)
    var errorDescription: String? { if case let .message(text) = self { return text }; return nil }
}

private final class OfficeXMLGuard: NSObject, XMLParserDelegate {
    let expected: String
    var rootMatches = false
    var rejected = false
    let body: String?
    private var bodies = 0, kinds = 0
    private var insideBody = false
    private var depth = 0
    init(_ root: String, body: String? = nil) { expected = root; self.body = body }
    func parser(_ parser: XMLParser, didStartElement name: String, namespaceURI: String?, qualifiedName: String?, attributes: [String: String]) {
        if depth == 0 { rootMatches = name == expected }
        if let body {
            let office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0"
            if depth == 1 && name == "body" && namespaceURI == office { bodies += 1; insideBody = true }
            else if depth == 2 && insideBody { kinds += 1; if name != body || namespaceURI != office { rejected = true } }
        }
        depth += 1
    }
    func parser(_ parser: XMLParser, didEndElement name: String, namespaceURI: String?, qualifiedName: String?) {
        depth -= 1
        if depth == 1 && name == "body" { insideBody = false }
    }
    var valid: Bool { rootMatches && !rejected && (body == nil || (bodies == 1 && kinds == 1)) }
    func parser(_ parser: XMLParser, foundInternalEntityDeclarationWithName name: String, value: String?) { rejected = true; parser.abortParsing() }
    func parser(_ parser: XMLParser, foundExternalEntityDeclarationWithName name: String, publicID: String?, systemID: String?) { rejected = true; parser.abortParsing() }
}

// File I/O runs on the Office coordinator's serial queue, never the PDF UI thread.
// The native editor only sees a session copy. Original replacement occurs after
// a validated stable snapshot, an original-content check and a retained backup.
final class OfficeFileSession {
    static let formats: Set<String> = ["docx", "xlsx", "pptx", "odt", "ods", "odp", "rtf", "csv"]
    private(set) var source: URL
    let directory: URL
    let working: URL
    let profile: URL
    let events: URL
    private(set) var originalHash: String
    private(set) var lastSnapshot: URL?
    private(set) var lastBackup: URL?
    private var lastSnapshotHash: String?
    private var recordedState = ""
    private let files = FileManager.default

    static func hash(_ url: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var digest = SHA256()
        while let bytes = try handle.read(upToCount: 65536), !bytes.isEmpty { digest.update(data: bytes) }
        return digest.finalize().map { String(format: "%02x", $0) }.joined()
    }
    static func regular(_ url: URL) throws {
        let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
        guard attributes[.type] as? FileAttributeType == .typeRegular else {
            throw OfficeSessionError.message("请选择实际的文档文件，链接或目录不能保存到原位置。")
        }
    }
    init(source selected: URL, root: URL) throws {
        guard selected.isFileURL, Self.formats.contains(selected.pathExtension.lowercased()) else {
            throw OfficeSessionError.message("当前办公编辑器不支持此文件格式。")
        }
        try Self.regular(selected)
        source = selected.standardizedFileURL.resolvingSymlinksInPath()
        originalHash = try Self.hash(source)
        try files.createDirectory(at: root, withIntermediateDirectories: true)
        directory = root.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try files.createDirectory(at: directory, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
        let documents = directory.appendingPathComponent("document", isDirectory: true)
        profile = directory.appendingPathComponent("profile", isDirectory: true)
        events = directory.appendingPathComponent("events", isDirectory: true)
        for url in [documents, profile, events] { try files.createDirectory(at: url, withIntermediateDirectories: false) }
        working = documents.appendingPathComponent(source.lastPathComponent)
        try files.copyItem(at: source, to: working)
        try files.setAttributes([.posixPermissions: 0o600], ofItemAtPath: working.path)
        guard try Self.hash(working) == originalHash, try Self.hash(source) == originalHash else {
            throw OfficeSessionError.message("文档在打开时发生变化，请重新打开。")
        }
        try record(state: "editing")
    }
    private func record(state: String) throws {
        var value: [String: Any] = ["Version": 1, "Source": source.path, "OriginalSHA256": originalHash, "State": state,
            "Working": working.lastPathComponent]
        if let snapshot = lastSnapshot, let hash = lastSnapshotHash { value["Snapshot"] = snapshot.lastPathComponent; value["SnapshotSHA256"] = hash }
        if let backup = lastBackup { value["Backup"] = backup.path }
        try JSONSerialization.data(withJSONObject: value, options: [.prettyPrinted, .sortedKeys])
            .write(to: directory.appendingPathComponent("session.json"), options: .atomic)
        recordedState = state
    }
    func noteDirty() throws {
        if recordedState != "dirty" { try record(state: "dirty") }
    }
    func finish(clean: Bool, discarded: Bool) throws {
        try record(state: discarded ? "discarded" : clean ? "closed" : "interrupted")
    }
    private static func tool(_ arguments: [String], maximum: Int = 64 * 1024 * 1024) throws -> Data {
        let process = Process(), output = Pipe()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/unzip")
        process.arguments = arguments
        process.standardInput = FileHandle.nullDevice
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        try process.run()
        DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 25) { if process.isRunning { process.terminate() } }
        var data = Data()
        while let bytes = try output.fileHandleForReading.read(upToCount: 65536), !bytes.isEmpty {
            if data.count + bytes.count > maximum { process.terminate(); process.waitUntilExit(); throw OfficeSessionError.message("文档内容过大，未替换原文件；修改仍保留。") }
            data.append(bytes)
        }
        process.waitUntilExit()
        guard process.terminationReason == .exit, process.terminationStatus == 0 else {
            throw OfficeSessionError.message("暂存文档未通过完整性检查，修改仍保留。")
        }
        return data
    }
    static func validate(_ url: URL, format kind: String) throws {
        try Self.regular(url)
        guard formats.contains(kind) else { throw OfficeSessionError.message("恢复副本的文件格式不受支持。") }
        if kind == "rtf" {
            let handle = try FileHandle(forReadingFrom: url); defer { try? handle.close() }
            guard let prefix = try handle.read(upToCount: 5), prefix == Data("{\\rtf".utf8) else { throw OfficeSessionError.message("暂存 RTF 文档无效，修改仍保留。") }
            return
        }
        if kind == "csv" {
            let handle = try FileHandle(forReadingFrom: url); defer { try? handle.close() }
            while let bytes = try handle.read(upToCount: 65536), !bytes.isEmpty {
                if bytes.contains(0) { throw OfficeSessionError.message("暂存 CSV 文档无效，修改仍保留。") }
            }
            return
        }
        _ = try tool(["-tqq", url.path], maximum: 65536)
        let namesData = try tool(["-Z1", url.path], maximum: 8 * 1024 * 1024)
        guard let listing = String(data: namesData, encoding: .utf8) else { throw OfficeSessionError.message("文档目录无法验证，修改仍保留。") }
        let names = listing.split(separator: "\n").map(String.init)
        guard names.count == Set(names).count else { throw OfficeSessionError.message("文档含重复内容，未替换原文件。") }
        let roots = ["docx": ("word/document.xml", "document"), "xlsx": ("xl/workbook.xml", "workbook"), "pptx": ("ppt/presentation.xml", "presentation")]
        let open = ["odt": "text", "ods": "spreadsheet", "odp": "presentation"]
        let entry = roots[kind]?.0 ?? "content.xml", root = roots[kind]?.1 ?? "document-content"
        let required = roots[kind] == nil ? ["mimetype", entry] : ["[Content_Types].xml", entry]
        guard required.allSatisfy({ names.contains($0) }) else { throw OfficeSessionError.message("文档缺少必要内容，未替换原文件。") }
        if roots[kind] != nil {
            let types = try tool(["-p", url.path, "\\[Content_Types\\].xml"])
            let typesGuard = OfficeXMLGuard("Types"), typesParser = XMLParser(data: types)
            typesParser.shouldProcessNamespaces = true; typesParser.shouldResolveExternalEntities = false; typesParser.delegate = typesGuard
            guard typesParser.parse(), typesGuard.valid else { throw OfficeSessionError.message("文档类型记录无效，未替换原文件。") }
        }
        if let body = open[kind] {
            let mime = try tool(["-p", url.path, "mimetype"], maximum: 512)
            guard String(data: mime, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines) == "application/vnd.oasis.opendocument." + body else {
                throw OfficeSessionError.message("文档内容与保存格式不匹配。")
            }
        }
        let xml = try tool(["-p", url.path, entry])
        let guardParser = OfficeXMLGuard(root, body: open[kind]), parser = XMLParser(data: xml)
        parser.shouldProcessNamespaces = true; parser.shouldResolveExternalEntities = false; parser.delegate = guardParser
        guard parser.parse(), guardParser.valid else { throw OfficeSessionError.message("文档正文无法验证，未替换原文件。") }
    }
    private func snapshot() throws -> URL {
        let snapshot = directory.appendingPathComponent("snapshot-" + UUID().uuidString + ".data")
        try Self.regular(working)
        try files.copyItem(at: working, to: snapshot)
        let savedHash = try Self.hash(snapshot)
        guard try Self.hash(working) == savedHash else { throw OfficeSessionError.message("文档仍在写入，请稍后再保存；原文件未更改。") }
        try Self.validate(snapshot, format: source.pathExtension.lowercased())
        lastSnapshot = snapshot
        lastSnapshotHash = savedHash
        try record(state: "saved-working-copy")
        return snapshot
    }
    private func replace(_ snapshot: URL, at destination: URL, expectedHash: String?) throws {
        let savedHash = try Self.hash(snapshot)
        var coordinationError: NSError?, failure: Error?
        let coordinator = NSFileCoordinator(filePresenter: nil)
        coordinator.coordinate(writingItemAt: destination, options: .forReplacing, error: &coordinationError) { target in
            do {
                if let expectedHash {
                    try Self.regular(target)
                    guard try Self.hash(target) == expectedHash else { throw OfficeSessionError.message("原文件已在其他程序中更改。当前修改已保留，请另存为。") }
                    let attributes = try files.attributesOfItem(atPath: target.path)
                    guard let mode = attributes[.posixPermissions] as? NSNumber, mode.intValue & 0o222 != 0 else {
                        throw OfficeSessionError.message("原文件为只读。当前修改已保留，请另存为。")
                    }
                } else if files.fileExists(atPath: target.path) {
                    throw OfficeSessionError.message("保存位置已出现其他文件，请重新选择位置。")
                }
                let replacement = target.deletingLastPathComponent().appendingPathComponent(".FullOffice-save-" + UUID().uuidString)
                try files.createDirectory(at: replacement, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
                let prepared = replacement.appendingPathComponent(target.lastPathComponent)
                try files.copyItem(at: snapshot, to: prepared)
                guard try Self.hash(prepared) == savedHash else {
                    throw OfficeSessionError.message("文件在保存时发生变化。当前修改已保留，请另存为。")
                }
                if let expectedHash {
                    guard try Self.hash(target) == expectedHash else { throw OfficeSessionError.message("原文件在保存时发生变化，当前修改仍保留。") }
                    let backupName = ".FullOffice-backup-" + UUID().uuidString + "-" + target.lastPathComponent
                    // https://developer.apple.com/documentation/foundation/filemanager/replaceitemat(_:withitemat:backupitemname:options:)
                    _ = try files.replaceItemAt(target, withItemAt: prepared, backupItemName: backupName, options: .withoutDeletingBackupItem)
                    lastBackup = target.deletingLastPathComponent().appendingPathComponent(backupName)
                    guard try Self.hash(lastBackup!) == expectedHash else { throw OfficeSessionError.message("备份核对未完成，恢复副本仍保留。") }
                } else {
                    try files.moveItem(at: prepared, to: target)
                    lastBackup = nil
                }
                guard try Self.hash(target) == savedHash else { throw OfficeSessionError.message("保存后的核对未完成；恢复副本仍保留。") }
                try? files.removeItem(at: replacement) // Own, empty staging directory after replacement.
            } catch { failure = error }
        }
        if let failure { throw failure }
        if let coordinationError { throw coordinationError }
    }
    @discardableResult func commit() throws -> URL {
        let saved = try snapshot()
        try replace(saved, at: source, expectedHash: originalHash)
        originalHash = try Self.hash(saved)
        try record(state: "committed")
        return saved
    }
    func saveAs(to destination: URL, expectedHash: String?) throws {
        guard destination.isFileURL, destination.pathExtension.lowercased() == source.pathExtension.lowercased() else {
            throw OfficeSessionError.message("另存文档请保持当前文件格式。")
        }
        let target = destination.standardizedFileURL
        let saved = try snapshot()
        try replace(saved, at: target, expectedHash: expectedHash)
        source = target
        originalHash = try Self.hash(saved)
        try record(state: "committed")
    }
}
