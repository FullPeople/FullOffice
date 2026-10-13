import Foundation

guard CommandLine.arguments.count == 4, ProcessInfo.processInfo.environment["GITHUB_ACTIONS"] == "true" else { exit(2) }
let fixtures = URL(fileURLWithPath: CommandLine.arguments[1])
let output = URL(fileURLWithPath: CommandLine.arguments[2])
let product = URL(fileURLWithPath: CommandLine.arguments[3])
let files = FileManager.default
try files.createDirectory(at: output, withIntermediateDirectories: false)
let source = output.appendingPathComponent("original.docx")
try files.copyItem(at: fixtures.appendingPathComponent("original.docx"), to: source)
let root = output.appendingPathComponent("sessions")
let session = try OfficeFileSession(source: source, root: root)
try Data(contentsOf: fixtures.appendingPathComponent("edited.docx")).write(to: session.working)
try Data("external authored current bytes".utf8).write(to: source)
let externalHash = try OfficeFileSession.hash(source)
var conflictRejected = false
do { try session.commit() }
catch { conflictRejected = error.localizedDescription.contains("原文件已在其他程序中更改") }
guard conflictRejected, session.lastSnapshot != nil, try OfficeFileSession.hash(source) == externalHash else { exit(3) }
try session.finish(clean: false, discarded: false)
guard try OfficeRecovery.list(root: root).count == 1 else { exit(3) }
let value: [String: Any] = ["Root": root.path, "Original": source.path, "OriginalSHA256": try OfficeFileSession.hash(source),
    "Snapshot": session.lastSnapshot!.path, "SnapshotSHA256": try OfficeFileSession.hash(session.lastSnapshot!),
    "Metadata": session.directory.appendingPathComponent("session.json").path,
    "Destination": output.appendingPathComponent("original（恢复）.docx").path, "Product": product.path]
try JSONSerialization.data(withJSONObject: value, options: [.prettyPrinted, .sortedKeys]).write(to: output.appendingPathComponent("fixture.json"))
