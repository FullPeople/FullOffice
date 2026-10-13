import Foundation
import Security

struct MacUpdateManifest {
    let version: String
    let architecture: String
    let url: URL
    let sha256: String
    let bytes: Int64
    static var currentArchitecture: String {
        #if arch(arm64)
        return "macos-arm64"
        #else
        return "macos-x64"
        #endif
    }
    static func versionParts(_ text: String) throws -> [Int] {
        let parts=text.split(separator:".",omittingEmptySubsequences:false)
        guard parts.count==3,parts.allSatisfy({!$0.isEmpty && $0.allSatisfy{$0.isASCII && $0.isNumber}}),
              let major=Int(parts[0]),let minor=Int(parts[1]),let patch=Int(parts[2]),[major,minor,patch].allSatisfy({$0<=65535}) else {
            throw OfficeSessionError.message("更新版本号无效。")
        }
        return [major,minor,patch]
    }
    static func signedBody(_ envelope: Data,publicKey: Data) throws -> Data {
        guard envelope.count<=65536,let text=String(data:envelope,encoding:.utf8) else {throw OfficeSessionError.message("更新清单无法读取。")}
        var lines=text.components(separatedBy:"\n")
        if lines.last=="" {lines.removeLast()}
        guard lines.count==3,lines[0]=="FULLTOOLS-UPDATE-1",let body=Data(base64Encoded:lines[1]),let signature=Data(base64Encoded:lines[2]) else {
            throw OfficeSessionError.message("更新清单缺少有效签名。")
        }
        let attributes:[String:Any]=[kSecAttrKeyType as String:kSecAttrKeyTypeRSA,kSecAttrKeyClass as String:kSecAttrKeyClassPublic,kSecAttrKeySizeInBits as String:3072]
        var error:Unmanaged<CFError>?
        guard let key=SecKeyCreateWithData(publicKey as CFData,attributes as CFDictionary,&error),
              SecKeyIsAlgorithmSupported(key,.verify,.rsaSignatureMessagePSSSHA256),
              SecKeyVerifySignature(key,.rsaSignatureMessagePSSSHA256,body as CFData,signature as CFData,&error) else {
            throw OfficeSessionError.message("更新签名校验失败，未下载或安装。")
        }
        return body
    }
    static func parse(_ envelope: Data,publicKey: Data,expectedArchitecture: String=currentArchitecture) throws -> MacUpdateManifest {
        let body=try signedBody(envelope,publicKey:publicKey)
        return try parseAuthenticatedBody(body, expectedArchitecture: expectedArchitecture)
    }
    static func parseAuthenticatedBody(_ body: Data, expectedArchitecture: String) throws -> MacUpdateManifest {
        guard let text=String(data:body,encoding:.utf8),text.hasSuffix("\n") else {throw OfficeSessionError.message("更新清单内容无效。")}
        var fields=[String:String]()
        for line in text.dropLast().components(separatedBy:"\n") {
            guard let separator=line.firstIndex(of:"=") else {throw OfficeSessionError.message("更新清单字段无效。")}
            let name=String(line[..<separator]),value=String(line[line.index(after:separator)...])
            guard fields[name]==nil else {throw OfficeSessionError.message("更新清单包含重复字段。")}
            fields[name]=value
        }
        guard Set(fields.keys)==Set(["product","version","architecture","url","sha256","bytes"]),fields["product"]=="FullOffice",
              fields["architecture"]==expectedArchitecture,["macos-arm64","macos-x64"].contains(expectedArchitecture),
              let version=fields["version"],let urlText=fields["url"],let url=URL(string:urlText),url.scheme=="https",url.host != nil,url.user==nil,url.password==nil,
              !urlText.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) || CharacterSet.whitespaces.contains($0) }),
              url.pathExtension.lowercased()=="pkg",url.fragment==nil,
              let sha=fields["sha256"],sha.count==64,sha.allSatisfy({"0123456789abcdef".contains($0)}),
              let count=fields["bytes"],count.allSatisfy({$0.isASCII && $0.isNumber}),let bytes=Int64(count),bytes>0,bytes<=4*1024*1024*1024 else {
            throw OfficeSessionError.message("更新清单与当前软件或平台不匹配。")
        }
        _ = try versionParts(version)
        return MacUpdateManifest(version:version,architecture:expectedArchitecture,url:url,sha256:sha,bytes:bytes)
    }
    func newer(than current: String) throws -> Bool {try Self.versionParts(current).lexicographicallyPrecedes(Self.versionParts(version))}
}
