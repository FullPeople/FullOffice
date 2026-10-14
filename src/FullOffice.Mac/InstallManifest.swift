import Foundation
import CryptoKit

func hash(_ url: URL) throws -> String {
    let stream = try FileHandle(forReadingFrom: url); defer { try? stream.close() }
    var digest = SHA256()
    while let bytes = try stream.read(upToCount: 65536), !bytes.isEmpty { digest.update(data: bytes) }
    return digest.finalize().map { String(format: "%02x", $0) }.joined()
}

func manifest(_ root: URL) throws -> [String: String] {
    let files = FileManager.default
    guard let iterator = files.enumerator(at: root, includingPropertiesForKeys: nil) else {
        throw NSError(domain: "FullOfficeInstall", code: 1)
    }
    var result = [String: String]()
    for case let file as URL in iterator {
        let attributes = try files.attributesOfItem(atPath: file.path)
        let relative = String(file.path.dropFirst(root.path.count + 1))
        switch attributes[.type] as? FileAttributeType {
        case .typeRegular: result[relative] = "file:" + (try hash(file))
        // Enumeration already avoids links; skipping can omit the next real directory.
        case .typeSymbolicLink: result[relative] = "link:" + (try files.destinationOfSymbolicLink(atPath: file.path))
        case .typeDirectory: break
        default: throw NSError(domain: "FullOfficeInstall", code: 2,
            userInfo: [NSLocalizedDescriptionKey: "应用内存在无法备份的特殊文件。"])
        }
    }
    return result
}
