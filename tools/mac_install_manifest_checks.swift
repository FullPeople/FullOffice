import Foundation
import CryptoKit

let output = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
let files = FileManager.default
let app = output.appendingPathComponent("Authored.app", isDirectory: true)
var expected = [String: String]()
for name in ["First", "Second"] {
    let framework = "Contents/Frameworks/\(name).framework/"
    for relative in ["Versions/A/\(name)", "Versions/A/Resources/Info.plist", "Versions/A/_CodeSignature/CodeResources"] {
        let path = app.appendingPathComponent(framework + relative)
        try files.createDirectory(at: path.deletingLastPathComponent(), withIntermediateDirectories: true)
        let data = Data((name + ":" + relative).utf8)
        try data.write(to: path)
        expected[framework + relative] = "file:" + SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }
    for (relative, target) in [("Versions/Current", "A"), ("Resources", "Versions/Current/Resources"), ("_CodeSignature", "Versions/Current/_CodeSignature"), (name, "Versions/Current/" + name)] {
        try files.createSymbolicLink(atPath: app.appendingPathComponent(framework + relative).path, withDestinationPath: target)
        expected[framework + relative] = "link:" + target
    }
}
let actual = try manifest(app)
var checks = ["AllVersionedFrameworkFilesAndLinksRecorded": actual == expected,
    "FrameworkAliasesDoNotDuplicateOrDropRecords": actual.count == 14,
    "ActualVersionedCoreDigestRecorded": actual["Contents/Frameworks/First.framework/Versions/A/First"] == expected["Contents/Frameworks/First.framework/Versions/A/First"]]
let core = app.appendingPathComponent("Contents/Frameworks/First.framework/Versions/A/First")
try Data("A damaged core must change the backup evidence".utf8).write(to: core)
checks["ActualFrameworkCoreDamageChangesBackupManifest"] = try manifest(app) != actual
let record: [String: Any] = ["Passed": checks.values.allSatisfy({$0}), "Checks": checks,
    "ExpectedEntries": expected.count, "ActualEntries": actual.count,
    "Scope": "Production InstallGuard manifest on two authored versioned frameworks and their real aliases, including actual core damage; complete installed-bundle and package upgrade checks remain separate."]
try JSONSerialization.data(withJSONObject: record, options: [.prettyPrinted, .sortedKeys]).write(to: output.appendingPathComponent("report.json"))
if !checks.values.allSatisfy({$0}) { exit(1) }
