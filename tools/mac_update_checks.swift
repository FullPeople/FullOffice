import Foundation
import CryptoKit

let root = URL(fileURLWithPath: CommandLine.arguments[1])
let output = URL(fileURLWithPath: CommandLine.arguments[2])
try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
let key = try Data(contentsOf: root.appendingPathComponent("src/FullOffice.Mac/Assets/update-public-key.der"))
let arm = try Data(contentsOf: root.appendingPathComponent("tests/fixtures/signed-mac-arm64-test.manifest"))
let intel = try Data(contentsOf: root.appendingPathComponent("tests/fixtures/signed-mac-x64-test.manifest"))
let windows = try Data(contentsOf: root.appendingPathComponent("tests/fixtures/signed-update-test.manifest"))
var checks = [String: Bool]()
func rejects(_ operation: () throws -> Void) -> Bool { do { try operation(); return false } catch { return true } }
let body = try MacUpdateManifest.signedBody(arm, publicKey: key)
let armManifest = try MacUpdateManifest.parse(arm, publicKey: key, expectedArchitecture: "macos-arm64")
checks["ActualPublisherPSSArmSignatureVerifies"] = armManifest.architecture == "macos-arm64" && armManifest.bytes == 584
checks["ActualPublisherPSSIntelSignatureVerifies"] = try MacUpdateManifest.parse(intel, publicKey: key, expectedArchitecture: "macos-x64").architecture == "macos-x64"
checks["ExistingWindowsPublisherSignatureStillVerifies"] = !(try MacUpdateManifest.signedBody(windows, publicKey: key)).isEmpty
checks["WindowsPackageRejectedOnMac"] = rejects { _ = try MacUpdateManifest.parse(windows, publicKey: key) }
checks["OtherMacArchitectureRejected"] = rejects { _ = try MacUpdateManifest.parse(intel, publicKey: key, expectedArchitecture: "macos-arm64") }
checks["NewVersionComparedNumerically"] = try armManifest.newer(than: "0.3.4")
checks["SameVersionNotOffered"] = try !armManifest.newer(than: "0.3.5")
checks["OlderVersionNotOffered"] = try !armManifest.newer(than: "0.10.0")
checks["InvalidInstalledVersionRejected"] = rejects { _ = try armManifest.newer(than: "0.3") }
var lines = String(data: arm, encoding: .utf8)!.components(separatedBy: "\n")
let forged = String(data: body, encoding: .utf8)!.replacingOccurrences(of: "version=0.3.5", with: "version=9.9.9")
var changed = lines; changed[1] = Data(forged.utf8).base64EncodedString()
checks["ChangedVersionWithoutSignatureRejected"] = rejects { _ = try MacUpdateManifest.parse(Data(changed.joined(separator: "\n").utf8), publicKey: key) }
var signature = Data(base64Encoded: lines[2])!; signature[0] ^= 1; changed = lines; changed[2] = signature.base64EncodedString()
checks["DamagedSignatureRejected"] = rejects { _ = try MacUpdateManifest.parse(Data(changed.joined(separator: "\n").utf8), publicKey: key) }
checks["MissingSignatureRejected"] = rejects { _ = try MacUpdateManifest.parse(Data("FULLTOOLS-UPDATE-1\n\(lines[1])\n".utf8), publicKey: key) }
checks["DifferentPublicKeyRejected"] = rejects { _ = try MacUpdateManifest.parse(arm, publicKey: Data([1,2,3])) }
checks["OversizedManifestRejected"] = rejects { _ = try MacUpdateManifest.signedBody(Data(repeating: 65, count: 65537), publicKey: key) }
let original = String(data: body, encoding: .utf8)!
let malformed: [(String, String)] = [
    ("WrongProductRejected", original.replacingOccurrences(of: "product=FullOffice", with: "product=MusicBoard")),
    ("UnknownPlatformRejected", original.replacingOccurrences(of: "architecture=macos-arm64", with: "architecture=linux-x64")),
    ("HTTPURLRejected", original.replacingOccurrences(of: "https://", with: "http://")),
    ("URLCredentialsRejected", original.replacingOccurrences(of: "https://", with: "https://user:pass@")),
    ("ExecutableOnMacRejected", original.replacingOccurrences(of: ".pkg", with: ".exe")),
    ("URLFragmentRejected", original.replacingOccurrences(of: ".pkg", with: ".pkg#fragment")),
    ("MissingFieldRejected", original.components(separatedBy: "\n").filter { !$0.hasPrefix("bytes=") }.joined(separator: "\n")),
    ("DuplicateFieldRejected", original + "version=0.3.5\n"),
    ("ExtraFieldRejected", original + "command=run\n"),
    ("EmptyVersionRejected", original.replacingOccurrences(of: "version=0.3.5", with: "version=")),
    ("HugeVersionRejected", original.replacingOccurrences(of: "version=0.3.5", with: "version=65536.3.5")),
    ("ZeroLengthRejected", original.replacingOccurrences(of: "bytes=584", with: "bytes=0")),
    ("OversizedPackageRejected", original.replacingOccurrences(of: "bytes=584", with: "bytes=4294967297")),
    ("InvalidHashRejected", original.replacingOccurrences(of: armManifest.sha256, with: "not-a-hash")),
    ("UnsignedBodyTerminatorRequired", String(original.dropLast()))
]
for (name, text) in malformed {
    checks[name] = rejects { _ = try MacUpdateManifest.parseAuthenticatedBody(Data(text.utf8), expectedArchitecture: "macos-arm64") }
}

final class StubTransfer: URLProtocol {
    static var payload = Data()
    static var status = 200
    static var expected: Int?
    static var disconnect = false
    static var responseScheme = "https"
    override class func canInit(with request: URLRequest) -> Bool { request.url?.host == "example.invalid" }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        var headers = [String:String]()
        if let expected = Self.expected { headers["Content-Length"] = String(expected) }
        let url = URL(string: "\(Self.responseScheme)://example.invalid/FullOffice-fixture.pkg")!
        let response = HTTPURLResponse(url: url, statusCode: Self.status, httpVersion: "HTTP/1.1", headerFields: headers)!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        for start in stride(from: 0, to: Self.payload.count, by: 73) {
            client?.urlProtocol(self, didLoad: Self.payload.subdata(in: start..<min(start+73, Self.payload.count)))
        }
        if Self.disconnect { client?.urlProtocol(self, didFailWithError: URLError(.networkConnectionLost)) }
        else { client?.urlProtocolDidFinishLoading(self) }
    }
    override func stopLoading() {}
}
let bytes = Data(String(repeating: "FullOffice signed-update test bytes; this is not an installable package.\n", count: 8).utf8)
checks["FixtureBytesMatchSignedHash"] = SHA256.hash(data: bytes).map { String(format: "%02x", $0) }.joined() == armManifest.sha256
func transfer(_ name: String, payload: Data, status: Int = 200, length: Int? = nil, disconnect: Bool = false, scheme: String = "https", success: Bool) throws {
    StubTransfer.payload = payload; StubTransfer.status = status; StubTransfer.expected = length
    StubTransfer.disconnect = disconnect; StubTransfer.responseScheme = scheme
    let configuration = URLSessionConfiguration.ephemeral; configuration.protocolClasses = [StubTransfer.self]
    let directory = output.appendingPathComponent(name)
    var result: Result<URL, Error>?
    let download = MacUpdateDownload(manifest: armManifest, root: directory, configuration: configuration, progress: { _,_ in }, completion: { result = $0 })
    download.start()
    let until = Date().addingTimeInterval(10)
    while result == nil && Date() < until { RunLoop.current.run(until: Date().addingTimeInterval(0.02)) }
    if result == nil { download.cancel(); checks[name] = false; return }
    switch result! {
    case .success(let package): checks[name] = try success && Data(contentsOf: package) == bytes
    case .failure: checks[name] = try !success && FileManager.default.contentsOfDirectory(atPath: directory.path).isEmpty
    }
}
try transfer("StreamedVerifiedPackageSaved", payload: bytes, length: bytes.count, success: true)
var damaged = bytes; damaged[0] ^= 1
try transfer("DamagedPackageRemoved", payload: damaged, success: false)
try transfer("TruncatedPackageRemoved", payload: bytes.dropLast(), success: false)
try transfer("OversizedPackageRemoved", payload: bytes + Data([1]), success: false)
try transfer("WrongContentLengthRejected", payload: bytes, length: bytes.count + 1, success: false)
try transfer("HTTPErrorBodyRejected", payload: bytes, status: 503, success: false)
try transfer("NetworkFailureRetainsNoPartialPackage", payload: bytes.prefix(73), disconnect: true, success: false)
try transfer("HTTPResponseDowngradeRejected", payload: bytes, scheme: "http", success: false)
func feedCheck(_ name: String, payload: Data, status: Int = 200, length: Int? = nil, scheme: String = "https", success: Bool) {
    StubTransfer.payload = payload; StubTransfer.status = status; StubTransfer.expected = length
    StubTransfer.disconnect = false; StubTransfer.responseScheme = scheme
    let configuration = URLSessionConfiguration.ephemeral; configuration.protocolClasses = [StubTransfer.self]
    var result: Result<Data, Error>?
    let feed = MacUpdateFeed { result = $0 }
    feed.start(url: armManifest.url, configuration: configuration)
    let until = Date().addingTimeInterval(10)
    while result == nil && Date() < until { RunLoop.current.run(until: Date().addingTimeInterval(0.02)) }
    guard let result else { feed.cancel(); checks[name] = false; return }
    switch result {
    case .success(let data): checks[name] = success && data == payload
    case .failure: checks[name] = !success
    }
}
feedCheck("BoundedFeedBytesReceived", payload: arm, length: arm.count, success: true)
feedCheck("OversizedFeedStreamingRejected", payload: Data(repeating: 65, count: 65537), success: false)
feedCheck("OversizedFeedHeaderRejected", payload: arm, length: 65537, success: false)
feedCheck("FeedServerErrorRejected", payload: arm, status: 503, success: false)
feedCheck("FeedHTTPDowngradeRejected", payload: arm, scheme: "http", success: false)
let passed = checks.count == 43 && checks.values.allSatisfy { $0 }
let report: [String:Any] = ["Passed": passed, "Checks": checks, "Scope": "Production SecKey signatures using existing publisher key, field protections and real URLSession streaming against deterministic URLProtocol fault responses. No actual install, public update feed or physical Mac acceptance."]
try JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted,.sortedKeys]).write(to: output.appendingPathComponent("report.json"))
print("Mac update checks: \(checks.count), passed: \(passed)")
if !passed { exit(1) }
