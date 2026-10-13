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

    // Recovery operates on retained snapshots, without overwriting originals.
    let root = conflict.directory.deletingLastPathComponent()
    try conflict.finish(clean: false, discarded: false)
    let entries = try OfficeRecovery.list(root: root)
    checks["InterruptedSessionListedForRecovery"] = entries.count == 1 && entries[0].source == conflictSource
    let entry = entries[0]
    let recovered = conflictSource.deletingLastPathComponent().appendingPathComponent("recovered.docx")
    try OfficeRecovery.export(entry, to: recovered)
    checks["RecoveryExportsExactValidatedEditedSnapshot"] = try Data(contentsOf: recovered) == edited
    checks["RecoveryPreservesExternallyChangedOriginal"] = try Data(contentsOf: conflictSource) == external
    checks["RecoveryRetainsSnapshotAndMetadata"] = files.fileExists(atPath: entry.payload.path) && files.fileExists(atPath: conflict.directory.appendingPathComponent("session.json").path)
    checks["RecoveryRefusesExistingDestination"] = rejected { try OfficeRecovery.export(entry, to: recovered) }
    checks["ExistingRecoveredBytesSurviveRejectedOverwrite"] = try Data(contentsOf: recovered) == edited
    checks["RecoveryRefusesFormatChange"] = rejected { try OfficeRecovery.export(entry, to: recovered.deletingPathExtension().appendingPathExtension("xlsx")) }
    try later.write(to: entry.payload)
    let tamperedTarget = recovered.deletingLastPathComponent().appendingPathComponent("tampered.docx")
    checks["RecoveryRejectsChangedSnapshotFingerprint"] = rejected { try OfficeRecovery.export(entry, to: tamperedTarget) }
    checks["ChangedSnapshotCannotCreateDestination"] = !files.fileExists(atPath: tamperedTarget.path)
    try edited.write(to: entry.payload)
    try conflict.noteDirty()
    checks["RecoveryRejectsChangedSessionRecord"] = rejected { try OfficeRecovery.export(entry, to: tamperedTarget) }
    try conflict.finish(clean: true, discarded: false)
    checks["CleanlyClosedSessionsExcludedFromRecovery"] = try OfficeRecovery.list(root: root).isEmpty
    try conflict.noteDirty(); try conflict.finish(clean: false, discarded: true)
    checks["ExplicitlyDiscardedSessionsExcludedFromRecovery"] = try OfficeRecovery.list(root: root).isEmpty
    let link = root.appendingPathComponent(UUID().uuidString)
    try files.createSymbolicLink(at: link, withDestinationURL: normal.directory)
    checks["ExternalSessionDirectoryLinkExcluded"] = try OfficeRecovery.list(root: root).isEmpty
    for name in ["missing-relationship", "wrong-relationship", "external-relationship"] {
        let (session, source) = try scenario(name)
        try Data(contentsOf: fixtures.appendingPathComponent(name + ".docx")).write(to: session.working)
        checks[name + "NativeSaveRejected"] = rejected { try session.commit() }
        checks[name + "OriginalBytesPreserved"] = try Data(contentsOf: source) == original
    }
    let invalidSource = fixtures.appendingPathComponent("missing-relationship.docx")
    let unusedRoot = output.appendingPathComponent("invalid-source-sessions")
    checks["InvalidSourceRejectedBeforeWorkerSessionCreation"] = rejected { _ = try OfficeFileSession(source: invalidSource, root: unusedRoot) } && !files.fileExists(atPath: unusedRoot.path)
    let unicode = "# 第一章\r\n\r\n**魔法物品** 🐉\r\n"
    let samples: [(String, Data)] = [("UTF8",Data(unicode.utf8)),("UTF8BOM",Data([0xEF,0xBB,0xBF])+Data(unicode.utf8)),
        ("UTF16LE",Data([0xFF,0xFE])+unicode.data(using:.utf16LittleEndian)!), ("UTF16BE",Data([0xFE,0xFF])+unicode.data(using:.utf16BigEndian)!)]
    for (name,data) in samples { let codec = try TextCodec.decode(data); checks[name + "ExactUnicodeAndCRLFRoundTrip"] = try codec.text == unicode && codec.encode(codec.text) == data }
    checks["InvalidUTF8TextRejected"] = rejected { _ = try TextCodec.decode(Data([0xC3,0x28])) }
    checks["TruncatedUTF16TextRejected"] = rejected { _ = try TextCodec.decode(Data([0xFF,0xFE,0x41])) }
    checks["BinaryNULTextRejected"] = rejected { _ = try TextCodec.decode(Data([65,0,66])) }
    let textRoot = output.appendingPathComponent("text-file")
    try files.createDirectory(at: textRoot, withIntermediateDirectories: false)
    let textSource = textRoot.appendingPathComponent("original.md"), textOriginal = samples[1].1
    try textOriginal.write(to: textSource)
    let text = try OfficeFileSession(source: textSource, root: textRoot.appendingPathComponent("sessions"))
    let textCodec = try TextCodec.decode(textOriginal), changed = try textCodec.encode(unicode + "已编辑\n")
    try changed.write(to: text.working); try text.commit()
    checks["MarkdownActualOriginalSavePreservesEncodingAndNewlines"] = try Data(contentsOf: textSource) == changed
    checks["MarkdownActualPreviousOriginalKeptAsBackup"] = try Data(contentsOf: text.lastBackup!) == textOriginal
    let checkpoint = try textCodec.encode(unicode + "恢复内容\n")
    try checkpoint.write(to: text.working); try text.checkpoint()
    checks["MarkdownRecoveryCheckpointDoesNotWriteOriginal"] = try Data(contentsOf: textSource) == changed
    let textEntry = try OfficeRecovery.list(root: textRoot.appendingPathComponent("sessions"))[0]
    let textRecovery = textRoot.appendingPathComponent("recovered.md")
    try OfficeRecovery.export(textEntry, to: textRecovery)
    checks["MarkdownActualRecoveryExportsEncodedCheckpoint"] = try Data(contentsOf: textRecovery) == checkpoint
    let externalText = Data("外部的新内容\n".utf8); try externalText.write(to: textSource)
    checks["MarkdownExternalOriginalChangeRejected"] = rejected { try text.commit() }
    checks["MarkdownExternalOriginalBytesPreserved"] = try Data(contentsOf: textSource) == externalText
    let textAlternate = textRoot.appendingPathComponent("new-document.txt")
    try text.saveAs(to: textAlternate, expectedHash: nil)
    checks["MarkdownSaveAsTextAliasWritesChosenDestination"] = try Data(contentsOf: textAlternate) == checkpoint && Data(contentsOf: textSource) == externalText
    try changed.write(to: text.working); try text.commit()
    checks["TextAliasLaterSaveKeepsOldOriginalUntouched"] = try Data(contentsOf: textAlternate) == changed && Data(contentsOf: textSource) == externalText
} catch { failure = String(describing: error) }
let passed = failure == nil && checks.count == 53 && checks.values.allSatisfy { $0 }
var report: [String: Any] = ["Passed": passed, "Checks": checks,
    "Scope": "Real macOS file coordination, backup, invalid-save rejection, external conflict, read-only, Save As and retained-snapshot recovery/export/lifecycle; no editor or recovery-dialog execution claim."]
if let failure { report["Failure"] = failure }
try JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys]).write(to: output.appendingPathComponent("report.json"))
print(String(data: try JSONSerialization.data(withJSONObject: report, options: [.sortedKeys]), encoding: .utf8)!)
exit(passed ? 0 : 1)
