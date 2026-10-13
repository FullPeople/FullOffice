import AppKit
import CoreGraphics

guard CommandLine.arguments.count == 4, let pid = Int32(CommandLine.arguments[1]),
      let app = NSRunningApplication(processIdentifier: pid),
      app.bundleURL?.standardizedFileURL == URL(fileURLWithPath: CommandLine.arguments[2]).standardizedFileURL
else { exit(2) }
app.activate(options: .activateIgnoringOtherApps)
RunLoop.current.run(until: Date().addingTimeInterval(3))
let windows = (CGWindowListCopyWindowInfo(.optionAll, kCGNullWindowID) as? [[String: Any]] ?? [])
let matches = windows.filter {
    ($0[kCGWindowOwnerPID as String] as? Int32) == pid &&
    ($0[kCGWindowLayer as String] as? Int) == 0 &&
    (($0[kCGWindowBounds as String] as? [String: Any])?["Width"] as? Double ?? 0) > 300
}
var captured = false
if let entry = matches.first, let number = entry[kCGWindowNumber as String] as? Int {
    let capture = Process()
    capture.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
    capture.arguments = ["-x", "-l", String(number), CommandLine.arguments[3]]
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
    "OwnedWindows": windows.filter { ($0[kCGWindowOwnerPID as String] as? Int32) == pid }]), encoding: .utf8)!)
exit(captured ? 0 : 1)
