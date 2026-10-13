import Foundation

let arguments = CommandLine.arguments
guard arguments.count == 3 else { exit(2) }
let fixtures = URL(fileURLWithPath: arguments[1], isDirectory: true)
let output = URL(fileURLWithPath: arguments[2], isDirectory: true)
let files = FileManager.default
try files.createDirectory(at: output, withIntermediateDirectories: false)
var checks = [String: Bool]()
var failure: String?
func scenario(_ name: String) throws -> (OfficeFileSession, URL) {
    let directory = output.appendingPathComponent(name, isDirectory: true)
    try files.createDirectory(at: directory, withIntermediateDirectories: false)
    let source = directory.appendingPathComponent("original.docx")
    try files.copyItem(at: fixtures.appendingPathComponent("original.docx"), to: source)
    return (try OfficeFileSession(source: source, root: directory.appendingPathComponent("sessions")), source)
}
func rejected(_ operation: () throws -> Void) -> Bool { do { try operation(); return false } catch { return true } }
do {
    let original = try Data(contentsOf: fixtures.appendingPathComponent("original.docx"))
    let edited = try Data(contentsOf: fixtures.appendingPathComponent("edited.docx"))
    let later = try Data(contentsOf: fixtures.appendingPathComponent("later.docx"))
    let (normal, normalSource) = try scenario("normal")
    try edited.write(to: normal.working)
    try normal.commit()
    checks["ActualOriginalReplacedWithValidatedEditedBytes"] = try Data(contentsOf: normalSource) == edited
    checks["ActualPreviousBytesKeptAsBackup"] = try Data(contentsOf: normal.lastBackup!) == original
    checks["ActualSnapshotAndMetadataRemainAvailable"] = files.fileExists(atPath: normal.directory.appendingPathComponent("session.json").path) && normal.lastSnapshot != nil
    let preservedSnapshot = normal.lastSnapshot!
    try Data("PK corrupt incomplete document".utf8).write(to: normal.working)
    checks["CorruptNativeSaveRejected"] = rejected { try normal.commit() }
    checks["RejectedSaveCannotDamageOriginal"] = try Data(contentsOf: normalSource) == edited
    checks["RejectedSaveKeepsPreviousValidRecoverySnapshot"] = try Data(contentsOf: preservedSnapshot) == edited

    let (conflict, conflictSource) = try scenario("external-change")
    try edited.write(to: conflict.working)
    let external = Data("external user's latest bytes".utf8)
    try external.write(to: conflictSource)
    checks["ExternalOriginalChangeRejected"] = rejected { try conflict.commit() }
    checks["ExternalUserBytesPreserved"] = try Data(contentsOf: conflictSource) == external
    checks["ConflictingEditedBytesRemainRecoverable"] = try Data(contentsOf: conflict.lastSnapshot!) == edited

    let (readOnly, readOnlySource) = try scenario("readonly")
    try edited.write(to: readOnly.working)
    try files.setAttributes([.posixPermissions: 0o444], ofItemAtPath: readOnlySource.path)
    checks["ReadOnlyOriginalRejected"] = rejected { try readOnly.commit() }
    checks["ReadOnlyOriginalAndEditsRemainIntact"] = try Data(contentsOf: readOnlySource) == original && Data(contentsOf: readOnly.lastSnapshot!) == edited

    let (missing, missingSource) = try scenario("missing-working")
    try files.removeItem(at: missing.working)
    checks["MissingWorkingFileRejected"] = rejected { try missing.commit() }
    checks["MissingWorkingFileDoesNotReplaceOriginalOrDropMetadata"] = try Data(contentsOf: missingSource) == original && files.fileExists(atPath: missing.directory.appendingPathComponent("session.json").path)

    let (alternate, alternateSource) = try scenario("save-as")
    try edited.write(to: alternate.working)
    let destination = alternateSource.deletingLastPathComponent().appendingPathComponent("new-document.docx")
    try alternate.saveAs(to: destination, expectedHash: nil)
    checks["ActualSaveAsWritesNewDestination"] = try Data(contentsOf: destination) == edited
    checks["ActualSaveAsPreservesOldOriginal"] = try Data(contentsOf: alternateSource) == original
    try later.write(to: alternate.working)
    try alternate.commit()
    checks["LaterSaveTargetsTheChosenNewDocument"] = try Data(contentsOf: destination) == later && Data(contentsOf: alternateSource) == original
    checks["LaterSaveKeepsNewDestinationPreviousBytes"] = try Data(contentsOf: alternate.lastBackup!) == edited
    checks["FormatChangingSaveAsRejected"] = rejected { try alternate.saveAs(to: destination.deletingPathExtension().appendingPathExtension("xlsx"), expectedHash: nil) }
} catch { failure = String(describing: error) }
let passed = failure == nil && checks.count == 18 && checks.values.allSatisfy { $0 }
var report: [String: Any] = ["Passed": passed, "Checks": checks,
    "Scope": "Real macOS file coordination and replacement, independent authored ZIPs, original bytes, backup, invalid-save rejection, external conflict, read-only, missing-file recovery and Save As; no editor/UI claim."]
if let failure { report["Failure"] = failure }
try JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys]).write(to: output.appendingPathComponent("report.json"))
print(String(data: try JSONSerialization.data(withJSONObject: report, options: [.sortedKeys]), encoding: .utf8)!)
exit(passed ? 0 : 1)
