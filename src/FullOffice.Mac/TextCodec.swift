import Foundation

struct TextCodec {
    enum Encoding { case utf8, utf8BOM, utf16LE, utf16BE }
    let encoding: Encoding
    let newline: String?
    let text: String
    static func decode(_ data: Data) throws -> TextCodec {
        let encoding: Encoding, raw: String?
        if data.starts(with: [0xEF, 0xBB, 0xBF]) { encoding = .utf8BOM; raw = String(data: data.dropFirst(3), encoding: .utf8) }
        else if data.starts(with: [0xFF, 0xFE]) { encoding = .utf16LE; raw = String(data: data.dropFirst(2), encoding: .utf16LittleEndian) }
        else if data.starts(with: [0xFE, 0xFF]) { encoding = .utf16BE; raw = String(data: data.dropFirst(2), encoding: .utf16BigEndian) }
        else { encoding = .utf8; raw = String(data: data, encoding: .utf8) }
        guard let raw, !raw.contains("\0") else {
            throw OfficeSessionError.message("无法读取文本编码，请使用 UTF-8 或带编码标记的 UTF-16 文件。原文件未更改。")
        }
        let withoutPairs = raw.replacingOccurrences(of: "\r\n", with: "")
        let newline: String?
        if raw.contains("\r\n") && !withoutPairs.contains("\r") && !withoutPairs.contains("\n") { newline = "\r\n" }
        else if raw.contains("\r") && !raw.contains("\n") { newline = "\r" }
        else if !raw.contains("\r") { newline = "\n" }
        else { newline = nil }
        return TextCodec(encoding: encoding, newline: newline, text: raw)
    }
    func encode(_ source: String) throws -> Data {
        var text = source
        if let newline {
            text = text.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n")
            if newline != "\n" { text = text.replacingOccurrences(of: "\n", with: newline) }
        }
        let bytes: Data?
        let prefix: Data
        switch encoding {
        case .utf8: bytes = text.data(using: .utf8); prefix = Data()
        case .utf8BOM: bytes = text.data(using: .utf8); prefix = Data([0xEF,0xBB,0xBF])
        case .utf16LE: bytes = text.data(using: .utf16LittleEndian); prefix = Data([0xFF,0xFE])
        case .utf16BE: bytes = text.data(using: .utf16BigEndian); prefix = Data([0xFE,0xFF])
        }
        guard let bytes else { throw OfficeSessionError.message("文本无法按原编码保存，当前修改仍保留。") }
        return prefix + bytes
    }
}
