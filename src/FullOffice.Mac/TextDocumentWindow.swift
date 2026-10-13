import AppKit
import UniformTypeIdentifiers

final class TextDocumentWindow: NSWindowController, NSTextViewDelegate, NSWindowDelegate, MarkdownPreviewDelegate {
    let editor = NSTextView()
    let status = NSTextField(labelWithString: "正在打开文本…")
    let split = NSSplitView()
    let scroll = NSScrollView()
    let previewButton = NSButton(title: "预览", target: nil, action: nil)
    var openDocument: ((URL) -> Void)?
    var onSaved: ((Bool) -> Void)?
    var onClosed: (() -> Void)?
    private(set) var session: OfficeFileSession?
    private(set) var dirty = false
    private(set) var busy = true
    private var codec: TextCodec?
    private var preview: MarkdownPreviewAPI?
    private var previewTimer: Timer?
    private var recoveryTimer: Timer?
    private var revision = 0, checkpointRevision = -1
    private var checkpointPrevious: URL?
    private var closingAfterSave = false
    private var untitled = false
    private var root: URL
    private let queue = DispatchQueue(label: "center.dnd.FullOffice.text-file", qos: .userInitiated)
    init(source: URL?, root: URL) {
        self.root = root
        let window = NSWindow(contentRect: NSRect(x: 0,y: 0,width: 1100,height: 780), styleMask: [.titled,.closable,.resizable,.miniaturizable], backing: .buffered, defer: false)
        super.init(window: window)
        window.title = source.map { $0.lastPathComponent + " — FullOffice" } ?? "未命名.md — FullOffice"
        window.isReleasedWhenClosed = false; window.center(); window.delegate = self
        let host = NSView(); window.contentView = host
        let save = NSButton(title: "保存", target: self, action: #selector(saveDocument(_:)))
        let saveAs = NSButton(title: "另存为…", target: self, action: #selector(saveDocumentAs(_:)))
        previewButton.target = self; previewButton.action = #selector(togglePreview)
        let controls = NSStackView(views: [save,saveAs,previewButton,status]); controls.spacing = 8
        split.isVertical = true; split.dividerStyle = .thin
        editor.isRichText = false; editor.importsGraphics = false; editor.allowsUndo = true
        editor.isAutomaticQuoteSubstitutionEnabled = false; editor.isAutomaticDashSubstitutionEnabled = false
        editor.isAutomaticTextReplacementEnabled = false; editor.isAutomaticSpellingCorrectionEnabled = false
        editor.font = .monospacedSystemFont(ofSize: 14, weight: .regular)
        editor.textContainerInset = NSSize(width: 18,height: 18)
        editor.isVerticallyResizable = true; editor.isHorizontallyResizable = false
        editor.autoresizingMask = .width; editor.textContainer?.widthTracksTextView = true
        editor.usesFindBar = true; editor.isIncrementalSearchingEnabled = true
        editor.setAccessibilityIdentifier("markdown-source")
        editor.delegate = self; editor.isEditable = false
        scroll.documentView = editor; scroll.hasVerticalScroller = true; scroll.hasHorizontalScroller = false
        split.addArrangedSubview(scroll)
        for view in [controls,split] { view.translatesAutoresizingMaskIntoConstraints = false; host.addSubview(view) }
        NSLayoutConstraint.activate([
            controls.topAnchor.constraint(equalTo: host.topAnchor,constant: 8),controls.leadingAnchor.constraint(equalTo: host.leadingAnchor,constant: 10),controls.trailingAnchor.constraint(lessThanOrEqualTo: host.trailingAnchor,constant: -10),
            split.topAnchor.constraint(equalTo: controls.bottomAnchor,constant: 8),split.leadingAnchor.constraint(equalTo: host.leadingAnchor),split.trailingAnchor.constraint(equalTo: host.trailingAnchor),split.bottomAnchor.constraint(equalTo: host.bottomAnchor)])
        scroll.widthAnchor.constraint(greaterThanOrEqualToConstant: 300).isActive = true
        queue.async {
            do {
                let selected: URL
                if let source { selected = source }
                else {
                    let draft = root.appendingPathComponent("Drafts/" + UUID().uuidString)
                    try FileManager.default.createDirectory(at: draft, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
                    selected = draft.appendingPathComponent("未命名.md")
                    try Data("# 新文档\n\n".utf8).write(to: selected, options: .atomic)
                }
                let session = try OfficeFileSession(source: selected, root: root)
                let codec = try TextCodec.decode(Data(contentsOf: session.working))
                DispatchQueue.main.async {
                    self.session = session; self.codec = codec; self.editor.string = codec.text
                    self.busy = false; self.untitled = source == nil; self.dirty = self.untitled
                    self.editor.isEditable = true; self.editor.undoManager?.removeAllActions()
                    self.window?.representedURL = source; self.window?.isDocumentEdited = self.dirty
                    self.status.stringValue = "文本已打开"; self.previewButton.isEnabled = selected.pathExtension.lowercased() != "txt"
                    self.recoveryTimer = Timer.scheduledTimer(withTimeInterval: 20, repeats: true) { [weak self] _ in self?.checkpoint() }
                    self.window?.makeFirstResponder(self.editor)
                }
            } catch {
                DispatchQueue.main.async { self.busy = false; self.status.stringValue = "打开失败"; NSAlert(error: error).runModal() }
            }
        }
    }
    required init?(coder: NSCoder) { fatalError("Programmatic text window") }
    func textDidChange(_ notification: Notification) {
        guard session != nil, editor.isEditable else { return }
        dirty = true; revision += 1; window?.isDocumentEdited = true; status.stringValue = "有未保存的修改"
        queue.async { try? self.session?.noteDirty() }
        previewTimer?.invalidate()
        if preview != nil { previewTimer = Timer.scheduledTimer(withTimeInterval: 0.4,repeats: false) { [weak self] _ in self?.refreshPreview() } }
    }
    @objc func togglePreview() {
        if let preview {
            preview.closePreview(); preview.view.removeFromSuperview(); self.preview = nil; previewButton.title = "预览"; return
        }
        let url = Bundle.main.bundleURL.appendingPathComponent("Contents/PlugIns/FullOfficeMarkdownPreview.bundle")
        guard let bundle = Bundle(url: url), bundle.load(), let type = NSClassFromString("FullOfficeMarkdownPreviewController") as? NSObject.Type,
              let controller = type.init() as? MarkdownPreviewAPI else {
            status.stringValue = "缺少 Markdown 预览组件，请使用完整安装包。"; return
        }
        preview = controller; controller.delegate = self
        split.addArrangedSubview(controller.view); split.setPosition(split.bounds.width / 2, ofDividerAt: 0)
        previewButton.title = "收起预览"; refreshPreview()
    }
    private func refreshPreview() {
        guard let preview, let session else { return }
        preview.setSource(editor.string, documentURL: session.source)
    }
    func inspectPreview(_ completion: @escaping (String) -> Void) {
        if let preview { preview.inspectPreview(completion) } else { completion("{}") }
    }
    func fullOfficePreviewOpenDocument(_ url: URL) {
        if OfficeFileSession.formats.contains(url.pathExtension.lowercased()) || url.pathExtension.lowercased() == "pdf" { openDocument?(url) }
        else { status.stringValue = "该链接的文件格式暂不支持。" }
    }
    @objc func saveDocument(_ sender: Any?) { if untitled { saveDocumentAs(sender) } else { save(to: nil, expectedHash: nil) } }
    @objc func saveDocumentAs(_ sender: Any?) {
        guard !busy, let session else { return }
        let panel = NSSavePanel(); panel.nameFieldStringValue = session.source.lastPathComponent
        panel.directoryURL = session.source.deletingLastPathComponent()
        panel.allowedContentTypes = OfficeFileSession.textFormats.compactMap { UTType(filenameExtension: $0) }
        panel.allowsOtherFileTypes = false
        guard panel.runModal() == .OK, let target = panel.url else { closingAfterSave = false; onSaved?(false); return }
        do { let hash = FileManager.default.fileExists(atPath: target.path) ? try OfficeFileSession.hash(target) : nil; save(to: target, expectedHash: hash) }
        catch { closingAfterSave = false; onSaved?(false); NSAlert(error: error).runModal() }
    }
    private func save(to target: URL?, expectedHash: String?) {
        guard !busy, let session, let codec else { return }
        let text = editor.string, savedRevision = revision
        busy = true; status.stringValue = "正在保存…"
        queue.async {
            do {
                try codec.encode(text).write(to: session.working, options: .atomic)
                if let target { try session.saveAs(to: target, expectedHash: expectedHash) } else { try session.commit() }
                DispatchQueue.main.async {
                    self.busy = false; self.untitled = false; self.dirty = self.revision != savedRevision
                    self.window?.title = session.source.lastPathComponent + " — FullOffice"; self.window?.representedURL = session.source
                    self.window?.isDocumentEdited = self.dirty; self.status.stringValue = self.dirty ? "有未保存的修改" : "已保存到原文件"
                    self.refreshPreview(); self.onSaved?(true)
                    let close = self.closingAfterSave; self.closingAfterSave = false
                    if close && !self.dirty { self.finish(discarded: false) }
                }
            } catch {
                DispatchQueue.main.async { self.busy = false; self.closingAfterSave = false; self.status.stringValue = "保存失败，修改仍保留"; self.onSaved?(false); NSAlert(error: error).runModal() }
            }
        }
    }
    private func checkpoint() {
        guard dirty, !busy, revision != checkpointRevision, let session, let codec else { return }
        let text = editor.string, revision = self.revision
        queue.async {
            do {
                try codec.encode(text).write(to: session.working, options: .atomic)
                let previous = session.lastSnapshot
                _ = try session.checkpoint()
                let retired = self.checkpointPrevious; self.checkpointPrevious = previous
                if let retired, retired.deletingLastPathComponent() == session.directory, retired.lastPathComponent.hasPrefix("snapshot-"), retired.pathExtension == "data" {
                    try OfficeFileSession.regular(retired); try? FileManager.default.removeItem(at: retired)
                }
                DispatchQueue.main.async { self.checkpointRevision = revision }
            } catch { DispatchQueue.main.async { self.status.stringValue = "恢复副本未写入，当前编辑窗口仍保留。" } }
        }
    }
    func windowShouldClose(_ sender: NSWindow) -> Bool {
        if busy { status.stringValue = "请等待当前打开或保存操作完成。"; return false }
        if !dirty { finish(discarded: false); return false }
        let alert = NSAlert(); alert.messageText = "保存文本的修改？"
        alert.addButton(withTitle: "保存"); alert.addButton(withTitle: "取消"); alert.addButton(withTitle: "不保存")
        let choice = alert.runModal()
        if choice == .alertSecondButtonReturn { return false }
        if choice == .alertThirdButtonReturn { finish(discarded: true); return false }
        closingAfterSave = true; saveDocument(nil); return false
    }
    func finish(discarded: Bool) {
        guard !busy else { return }
        busy = true; recoveryTimer?.invalidate(); previewTimer?.invalidate(); preview?.closePreview(); preview?.view.removeFromSuperview(); preview = nil
        let clean = !dirty
        queue.async {
            try? self.session?.finish(clean: clean, discarded: discarded)
            DispatchQueue.main.async { self.busy = false; self.dirty = false; self.window?.delegate = nil; self.close(); self.onClosed?() }
        }
    }
}
