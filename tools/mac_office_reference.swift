import AppKit
import CoreGraphics

guard CommandLine.arguments.count == 5, let pid = Int32(CommandLine.arguments[1]),
      let app = NSRunningApplication(processIdentifier: pid),
      app.bundleURL?.standardizedFileURL == URL(fileURLWithPath: CommandLine.arguments[2]).standardizedFileURL
else { exit(2) }
let openDocument = Process()
openDocument.executableURL = URL(fileURLWithPath: "/usr/bin/open")
openDocument.arguments = ["-a", CommandLine.arguments[2], CommandLine.arguments[3]]
try openDocument.run(); openDocument.waitUntilExit()
app.activate(options: .activateIgnoringOtherApps)
RunLoop.current.run(until: Date().addingTimeInterval(10))
let windows = (CGWindowListCopyWindowInfo(.optionAll, kCGNullWindowID) as? [[String: Any]] ?? [])
func area(_ entry: [String: Any]) -> Double {
    let bounds = entry[kCGWindowBounds as String] as? [String: Any] ?? [:]
    return (bounds["Width"] as? Double ?? 0) * (bounds["Height"] as? Double ?? 0)
}
let matches = windows.filter {
    ($0[kCGWindowOwnerPID as String] as? Int32) == pid &&
    (($0[kCGWindowBounds as String] as? [String: Any])?["Height"] as? Double ?? 0) > 100
}.sorted { area($0) > area($1) }
var captured = false
if let entry = matches.first, let number = entry[kCGWindowNumber as String] as? Int {
    let capture = Process()
    capture.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
    capture.arguments = ["-x", "-l", String(number), CommandLine.arguments[4]]
    try capture.run(); capture.waitUntilExit()
    captured = capture.terminationStatus == 0
}
let requested = app.terminate()
let deadline = Date().addingTimeInterval(15)
while !app.isTerminated && Date() < deadline {
    RunLoop.current.run(until: Date().addingTimeInterval(0.1))
}
let normal = app.isTerminated
if !normal { app.forceTerminate() }
print(String(data: try JSONSerialization.data(withJSONObject: ["OwnedPid": pid,
    "ActualCompositorScreenshot": captured, "QuitRequested": requested, "NormalQuit": normal,
    "DocumentOpenEventExitCode": openDocument.terminationStatus,
    "OwnedWindows": windows.filter { ($0[kCGWindowOwnerPID as String] as? Int32) == pid }]), encoding: .utf8)!)
exit(captured ? 0 : 1)
