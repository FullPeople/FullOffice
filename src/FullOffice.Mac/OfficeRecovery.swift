import Foundation

struct OfficeRecoveryEntry {
    let directory: URL
    let source: URL
    let payload: URL
    let expectedHash: String?
    let metadataHash: String
    let state: String
    let modified: Date
    var format: String { source.pathExtension.lowercased() }
}

enum OfficeRecovery {
    // Called only when the user requests recovery. Opening PDFs never scans
    // sessions, profiles, or the user's document directories.
    static func list(root: URL) throws -> [OfficeRecoveryEntry] {
        let files = FileManager.default
        guard files.fileExists(atPath: root.path) else { return [] }
        let root = root.standardizedFileURL.resolvingSymlinksInPath()
        let children = try files.contentsOfDirectory(at: root, includingPropertiesForKeys: nil, options: [.skipsHiddenFiles])
        var results = [OfficeRecoveryEntry]()
        for directory in children {
            guard UUID(uuidString: directory.lastPathComponent) != nil,
                  (try? files.attributesOfItem(atPath: directory.path)[.type] as? FileAttributeType) == .typeDirectory,
                  directory.resolvingSymlinksInPath().deletingLastPathComponent() == root else { continue }
            let metadata = directory.appendingPathComponent("session.json")
            guard (try? OfficeFileSession.regular(metadata)) != nil,
                  let size = try? files.attributesOfItem(atPath: metadata.path)[.size] as? NSNumber,
                  size.intValue <= 65536,
                  let bytes = try? Data(contentsOf: metadata),
                  let value = try? JSONSerialization.jsonObject(with: bytes) as? [String: Any],
                  value["Version"] as? Int == 1,
                  let state = value["State"] as? String, ["editing", "dirty", "saved-working-copy", "interrupted"].contains(state),
                  let original = value["Source"] as? String, original.hasPrefix("/"), original.count <= 8192,
                  let working = value["Working"] as? String, leaf(working) else { continue }
            let source = URL(fileURLWithPath: original)
            guard OfficeFileSession.formats.contains(source.pathExtension.lowercased()) else { continue }
            let payload: URL
            let hash: String?
            if let snapshot = value["Snapshot"] as? String, leaf(snapshot),
               let fingerprint = value["SnapshotSHA256"] as? String,
               fingerprint.count == 64 && fingerprint.allSatisfy({ $0.isHexDigit }) {
                payload = directory.appendingPathComponent(snapshot); hash = fingerprint
            } else {
                payload = directory.appendingPathComponent("document").appendingPathComponent(working); hash = nil
            }
            guard payload.resolvingSymlinksInPath().path.hasPrefix(directory.path + "/"),
                  (try? OfficeFileSession.regular(payload)) != nil,
                  let metadataHash = try? OfficeFileSession.hash(metadata) else { continue }
            let changed = (try? files.attributesOfItem(atPath: metadata.path)[.modificationDate] as? Date) ?? .distantPast
            results.append(OfficeRecoveryEntry(directory: directory, source: source, payload: payload, expectedHash: hash,
                metadataHash: metadataHash, state: state, modified: changed))
        }
        return results.sorted { $0.modified > $1.modified }
    }
    private static func leaf(_ value: String) -> Bool {
        !value.isEmpty && value != "." && value != ".." && !value.contains("/") && !value.contains("\\")
    }
    static func export(_ entry: OfficeRecoveryEntry, to destination: URL) throws {
        let files = FileManager.default
        let target = destination.standardizedFileURL
        guard target.isFileURL, target.pathExtension.lowercased() == entry.format else {
            throw OfficeSessionError.message("请保持恢复文档的原文件格式。")
        }
        guard try OfficeFileSession.hash(entry.directory.appendingPathComponent("session.json")) == entry.metadataHash,
              entry.payload.resolvingSymlinksInPath().path.hasPrefix(entry.directory.path + "/") else {
            throw OfficeSessionError.message("恢复记录已变化，请重新打开恢复列表。")
        }
        try OfficeFileSession.regular(entry.payload)
        let fingerprint = try OfficeFileSession.hash(entry.payload)
        if let expected = entry.expectedHash, fingerprint != expected {
            throw OfficeSessionError.message("恢复副本与记录不一致，未写入所选位置。")
        }
        var coordinationError: NSError?, failure: Error?
        NSFileCoordinator(filePresenter: nil).coordinate(writingItemAt: target, options: .forReplacing, error: &coordinationError) { coordinated in
            do {
                guard !files.fileExists(atPath: coordinated.path) else {
                    throw OfficeSessionError.message("该位置已有文件，请为恢复副本选择一个新名称。")
                }
                let staging = coordinated.deletingLastPathComponent().appendingPathComponent(".FullOffice-recover-" + UUID().uuidString)
                try files.createDirectory(at: staging, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
                let prepared = staging.appendingPathComponent(coordinated.lastPathComponent)
                try files.copyItem(at: entry.payload, to: prepared)
                try OfficeFileSession.validate(prepared, format: entry.format)
                guard try OfficeFileSession.hash(prepared) == fingerprint,
                      try OfficeFileSession.hash(entry.payload) == fingerprint,
                      try OfficeFileSession.hash(entry.directory.appendingPathComponent("session.json")) == entry.metadataHash else {
                    throw OfficeSessionError.message("恢复副本在读取时发生变化，原文件未更改。")
                }
                try files.moveItem(at: prepared, to: coordinated)
                try? files.removeItem(at: staging) // Our now-empty staging directory.
            } catch { failure = error }
        }
        if let failure { throw failure }
        if let coordinationError { throw coordinationError }
    }
}
