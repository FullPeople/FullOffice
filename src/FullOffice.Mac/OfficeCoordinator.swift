import Foundation
import Darwin

struct OfficeRuntime {
    let executable: URL
    let resources: URL
    let editorEntry: URL
    static func bundled() throws -> OfficeRuntime {
        let bundle = Bundle.main.bundleURL
        let worker = bundle.appendingPathComponent("Contents/Helpers/FullOfficeOfficeHost.app")
        let resources = worker.appendingPathComponent("Contents/Resources")
        let executable = worker.appendingPathComponent("Contents/MacOS/FullOfficeOfficeHost")
        let entry = resources.appendingPathComponent("editors/web-apps/apps/api/documents/index.html")
        guard FileManager.default.isExecutableFile(atPath: executable.path), FileManager.default.fileExists(atPath: entry.path) else {
            throw OfficeSessionError.message("缺少办公编辑组件，请使用包含文档和表格编辑器的完整安装包。")
        }
        return OfficeRuntime(executable: executable, resources: resources, editorEntry: entry)
    }
}

final class OfficeCoordinator {
    let runtime: OfficeRuntime
    let root: URL
    var onState: ((String) -> Void)?
    var onExit: ((Int32) -> Void)?
    var onOpen: (() -> Void)?
    var onSaveAs: (() -> Void)?
    var onFailure: ((String) -> Void)?
    private(set) var session: OfficeFileSession?
    private var process: Process?
    private let activity = NSLock()
    private var opening = false
    var worker: Process? { activity.lock(); defer { activity.unlock() }; return process }
    private(set) var committedSaves = 0
    private(set) var saveFailures = 0
    private let queue = DispatchQueue(label: "center.dnd.FullOffice.office-session", qos: .userInitiated)
    private var bytes = Data()
    private var log: FileHandle?
    private var command = 0
    private var forwardResearch = false
    private var saveDestination: (URL, String?)?
    init(runtime: OfficeRuntime, root: URL) { self.runtime = runtime; self.root = root }
    var isActive: Bool { activity.lock(); defer { activity.unlock() }; return opening || process?.isRunning == true }
    private func state(_ text: String) { DispatchQueue.main.async { self.onState?(text) } }
    func open(_ source: URL, researchEdit: Bool = false) {
        activity.lock(); opening = true; activity.unlock()
        state("正在打开文档…")
        queue.async {
            do {
                self.forwardResearch = researchEdit
                let session = try OfficeFileSession(source: source, root: self.root)
                self.session = session
                let output = Pipe(), worker = Process()
                worker.executableURL = self.runtime.executable
                worker.arguments = [self.runtime.resources.path, session.profile.path, session.working.path, session.events.path, self.runtime.editorEntry.path]
                var environment = ProcessInfo.processInfo.environment
                environment["FULLOFFICE_MAC_SAVE_TO_ORIGINAL"] = "1"
                environment["FULLOFFICE_MAC_ORIGINAL_NAME"] = source.lastPathComponent
                if researchEdit { environment["FULLOFFICE_MAC_NATIVE_EDIT_PROBE"] = "1" }
                else { environment.removeValue(forKey: "FULLOFFICE_MAC_NATIVE_EDIT_PROBE") }
                worker.environment = environment
                worker.standardInput = FileHandle.nullDevice
                worker.standardOutput = output
                worker.standardError = FileHandle.nullDevice
                let logURL = session.directory.appendingPathComponent("worker.log")
                FileManager.default.createFile(atPath: logURL.path, contents: nil)
                self.log = try FileHandle(forWritingTo: logURL)
                self.activity.lock(); self.process = worker; self.activity.unlock()
                try worker.run()
                try? output.fileHandleForWriting.close()
                self.activity.lock(); self.opening = false; self.activity.unlock()
                self.state("正在打开“\(source.lastPathComponent)”…")
                DispatchQueue.global(qos: .utility).async {
                    do {
                        var buffer = [UInt8](repeating: 0, count: 65536)
                        while true {
                            let count = buffer.withUnsafeMutableBytes { Darwin.read(output.fileHandleForReading.fileDescriptor, $0.baseAddress, $0.count) }
                            if count == 0 { break }
                            if count < 0 {
                                if errno == EINTR { continue }
                                throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno))
                            }
                            let block = Data(buffer.prefix(count))
                            self.queue.sync { self.receive(block) }
                        }
                        worker.waitUntilExit()
                        self.queue.sync {
                            if !self.bytes.isEmpty { self.line(String(decoding: self.bytes, as: UTF8.self)); self.bytes.removeAll() }
                            try? self.log?.close(); self.log = nil
                            DispatchQueue.main.async { self.onExit?(worker.terminationStatus) }
                        }
                    } catch { self.state("办公窗口的状态读取未完成，编辑副本仍保留。") }
                }
            } catch {
                self.activity.lock(); self.opening = false; self.activity.unlock()
                self.state(error.localizedDescription)
                DispatchQueue.main.async { self.onFailure?(error.localizedDescription) }
                DispatchQueue.main.async { self.onExit?(-1) }
            }
        }
    }
    private func receive(_ block: Data) {
        if forwardResearch { FileHandle.standardOutput.write(block) }
        try? log?.write(contentsOf: block)
        bytes.append(block)
        while let ending = bytes.firstIndex(of: 10) {
            let data = bytes.prefix(upTo: ending)
            bytes.removeSubrange(...ending)
            line(String(decoding: data, as: UTF8.self).trimmingCharacters(in: .newlines))
        }
        if bytes.count > 65536 { bytes.removeAll(); state("办公窗口返回的状态过长，编辑副本仍保留。") }
    }
    private func line(_ text: String) {
        if text == "DIRTY 1" { state("有未保存的修改") }
        if text == "OPEN_FILE_REQUEST" { DispatchQueue.main.async { self.onOpen?() } }
        if text == "SAVE_AS_REQUEST" { DispatchQueue.main.async { self.onSaveAs?() } }
        guard text.hasPrefix("ORIGINAL_SAVE_REQUEST "), let sequence = Int(text.dropFirst("ORIGINAL_SAVE_REQUEST ".count)), let session else { return }
        state("正在保存到原文件…")
        do {
            let destination = saveDestination; saveDestination = nil
            if let destination { try session.saveAs(to: destination.0, expectedHash: destination.1) }
            else { try session.commit() }
            committedSaves += 1
            let message = destination == nil ? "已保存到原文件" : "已另存为“\(session.source.lastPathComponent)”"
            try acknowledge(sequence, success: true, message: message)
            state(message)
        } catch {
            saveFailures += 1
            let message = error.localizedDescription
            try? acknowledge(sequence, success: false, message: message)
            state(message)
            DispatchQueue.main.async { self.onFailure?(message) }
        }
    }
    private func acknowledge(_ sequence: Int, success: Bool, message: String) throws {
        guard let session else { return }
        try JSONSerialization.data(withJSONObject: ["Sequence": sequence, "Success": success, "Message": message, "DocumentName": session.source.lastPathComponent], options: [.sortedKeys])
            .write(to: session.events.appendingPathComponent("commit-state.json"), options: .atomic)
    }
    func requestSave() { send("save") }
    func requestClose() { send("close") }
    func requestSaveAs(to destination: URL) {
        queue.async {
            do {
                var fingerprint: String?
                if FileManager.default.fileExists(atPath: destination.path) { fingerprint = try OfficeFileSession.hash(destination) }
                self.saveDestination = (destination, fingerprint)
                self.send("save")
            } catch { self.state("保存位置无法读取，请重新选择。") }
        }
    }
    private func send(_ action: String) {
        queue.async {
            guard let session = self.session, self.worker?.isRunning == true else { return }
            do {
                self.command += 1
                try JSONSerialization.data(withJSONObject: ["Sequence": self.command, "Action": action], options: [.sortedKeys])
                    .write(to: session.events.appendingPathComponent("command.json"), options: .atomic)
            } catch { self.state("操作未完成，文档仍保留在当前编辑窗口。") }
        }
    }
}
