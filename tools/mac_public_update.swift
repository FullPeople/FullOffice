import Foundation
import CryptoKit

let root = URL(fileURLWithPath: CommandLine.arguments[1])
let output = URL(fileURLWithPath: CommandLine.arguments[2])
try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
let architecture = MacUpdateManifest.currentArchitecture
let key = try Data(contentsOf: root.appendingPathComponent("src/FullOffice.Mac/Assets/update-public-key.der"))
let url = URL(string: "https://dnd.center/downloads/fulloffice-\(architecture).manifest")!
var feedResult: Result<Data, Error>?
let feed = MacUpdateFeed { feedResult = $0 }
feed.start(url: url)
let feedDeadline = Date().addingTimeInterval(75)
while feedResult == nil && Date() < feedDeadline { RunLoop.current.run(until: Date().addingTimeInterval(0.05)) }
guard let feedResult else { feed.cancel(); throw OfficeSessionError.message("Public update feed timed out") }
let data = try feedResult.get()
try data.write(to: output.appendingPathComponent("public.manifest"), options: .atomic)
let manifest = try MacUpdateManifest.parse(data, publicKey: key)
let expectedHash = architecture == "macos-arm64" ? "e65d4e1ab4308d482dca0e5324aa36de1332250998ac26ef426302296a765a17" : "112dca90fab6188a8cf1ed32a23a9bf6a7d3e1483a768ee642f2fb14aaeb4845"
var checks = [String: Bool]()
checks["ActualPublicPublisherSignatureVerifies"] = manifest.version == "0.3.5" && manifest.sha256 == expectedHash
checks["ActualPublicPackageTargetsCurrentArchitecture"] = manifest.architecture == architecture
checks["ActualNewVersionOfferedToBaseline"] = try manifest.newer(than: "0.3.4")
checks["ActualPublishedVersionRecognizedAsLatest"] = try !manifest.newer(than: "0.3.5")
guard checks.values.allSatisfy({$0}) else { throw OfficeSessionError.message("Unexpected published update identity") }
var result: Result<URL, Error>?
var lastPrint = Date.distantPast
let download = MacUpdateDownload(manifest: manifest, root: output.appendingPathComponent("downloads"), progress: { count, total in
    if Date().timeIntervalSince(lastPrint) >= 30 { print("Public package download: \(count) / \(total) bytes"); fflush(stdout); lastPrint = Date() }
}, completion: { result = $0 })
download.start()
let deadline = Date().addingTimeInterval(1200)
while result == nil && Date() < deadline { RunLoop.current.run(until: Date().addingTimeInterval(0.05)) }
guard let result else { download.cancel(); throw OfficeSessionError.message("Public package download timed out") }
let package = try result.get()
let file = try FileHandle(forReadingFrom: package); defer { try? file.close() }
var digest = SHA256(); var bytes: Int64 = 0
while let chunk = try file.read(upToCount: 1048576), !chunk.isEmpty { digest.update(data: chunk); bytes += Int64(chunk.count) }
checks["ActualHTTPSFullDownloadRehashMatchesSignedPackage"] = bytes == manifest.bytes && digest.finalize().map { String(format: "%02x", $0) }.joined() == expectedHash
let report: [String: Any] = ["Passed": checks.values.allSatisfy({$0}), "Checks": checks, "Version": manifest.version,
    "Architecture": architecture, "Package": package.path, "Bytes": bytes, "SHA256": manifest.sha256,
    "Scope": "Production SecKey parser and URLSession feed/download classes against actual public signed feed and full released package; installer and UI are separately verified. Excludes product update-menu interaction."]
try JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys]).write(to: output.appendingPathComponent("network-report.json"))
if !checks.values.allSatisfy({$0}) { exit(1) }
