import AppKit
import Vision
guard CommandLine.arguments.count == 3, let image = NSImage(contentsOfFile: CommandLine.arguments[1]),
      let cg = image.cgImage(forProposedRect: nil, context: nil, hints: nil) else { exit(2) }
let request = VNRecognizeTextRequest()
request.recognitionLevel = .accurate; request.usesLanguageCorrection = false
try VNImageRequestHandler(cgImage: cg).perform([request])
let rows = (request.results ?? []).compactMap { $0.topCandidates(1).first?.string }
let text = rows.joined(separator: " ").uppercased().filter { $0.isLetter || $0.isNumber }
let report: [String: Any] = ["Passed": text.contains("MACENGINEBODYPROBE20261013"), "RecognizedRows": rows,
    "Scope": "Actual compositor screenshot of native ONLYOFFICE editing view; generated DOCX body marker, no user document."]
try JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys]).write(to: URL(fileURLWithPath: CommandLine.arguments[2]))
exit(report["Passed"] as? Bool == true ? 0 : 1)
