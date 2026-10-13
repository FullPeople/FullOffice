import AppKit

final class MacUpdateController: NSObject {
    private var feed: MacUpdateFeed?
    private var download: MacUpdateDownload?
    private var window: NSWindow?
    private var label: NSTextField?
    private var bar: NSProgressIndicator?
    private var checking = false
    private var checkID = UUID()
    var canInstall: (() -> Bool)?

    func check() {
        guard !checking, download == nil else { window?.makeKeyAndOrderFront(nil); return }
        checking = true
        let identifier = UUID(); checkID = identifier
        showProgress("正在检查更新…", maximum: 1, indeterminate: true)
        let url = URL(string: "https://dnd.center/downloads/fulloffice-\(MacUpdateManifest.currentArchitecture).manifest")!
        feed = MacUpdateFeed { [weak self] result in
                guard let self, self.checking, self.checkID == identifier else { return }
                self.checking = false; self.feed = nil; self.window?.close()
                do {
                    let data = try result.get()
                    guard let keyURL = Bundle.main.url(forResource: "update-public-key", withExtension: "der") else {
                        throw OfficeSessionError.message("暂时无法读取更新信息。请稍后再试。")
                    }
                    let manifest = try MacUpdateManifest.parse(data, publicKey: Data(contentsOf: keyURL))
                    let current = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.0.0"
                    guard try manifest.newer(than: current) else {
                        let alert = NSAlert(); alert.messageText = "当前已是最新版本"; alert.informativeText = "FullOffice \(current)"; alert.runModal(); return
                    }
                    let alert = NSAlert(); alert.messageText = "FullOffice \(manifest.version) 可供更新"
                    alert.informativeText = "安装包约 \(manifest.bytes / 1024 / 1024) MB。下载并校验完成后，将使用系统安装程序更新。"
                    alert.addButton(withTitle: "下载更新"); alert.addButton(withTitle: "稍后")
                    if alert.runModal() == .alertFirstButtonReturn { self.beginDownload(manifest) }
                } catch { NSAlert(error: error).runModal() }
        }
        feed?.start(url: url)
    }
    private func beginDownload(_ manifest: MacUpdateManifest) {
        showProgress("正在下载 FullOffice \(manifest.version)…", maximum: Double(manifest.bytes), indeterminate: false)
        let root = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("FullOffice/Updates", isDirectory: true)
        download = MacUpdateDownload(manifest: manifest, root: root, progress: { [weak self] count, total in
            self?.bar?.doubleValue = Double(count)
            self?.label?.stringValue = "正在下载：\(count / 1024 / 1024) / \(total / 1024 / 1024) MB"
        }, completion: { [weak self] result in
            guard let self else { return }; self.download = nil; self.window?.close()
            switch result {
            case .failure(let error):
                if (error as NSError).code != NSURLErrorCancelled { NSAlert(error: error).runModal() }
            case .success(let package):
                let alert = NSAlert(); alert.messageText = "更新下载与校验完成"
                alert.informativeText = "保存并关闭正在编辑的文档后，可打开系统安装程序。安装包已保留，可稍后安装。"
                alert.addButton(withTitle: "打开安装程序"); alert.addButton(withTitle: "显示安装包"); alert.addButton(withTitle: "稍后")
                let choice = alert.runModal()
                if choice == .alertSecondButtonReturn { NSWorkspace.shared.activateFileViewerSelecting([package]); return }
                if choice == .alertFirstButtonReturn {
                    guard self.canInstall?() != false else {
                        let pending = NSAlert(); pending.messageText = "请先保存并关闭正在编辑的文档"
                        pending.addButton(withTitle: "继续编辑"); pending.runModal()
                        NSWorkspace.shared.activateFileViewerSelecting([package]); return
                    }
                    guard NSWorkspace.shared.open(package) else { NSAlert(error: OfficeSessionError.message("无法打开系统安装程序。安装包已保留。请在访达中打开。")).runModal(); return }
                    NSApp.terminate(nil)
                }
            }
        })
        download?.start()
    }
    private func showProgress(_ text: String, maximum: Double, indeterminate: Bool) {
        window?.close()
        let next = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 400, height: 120), styleMask: [.titled], backing: .buffered, defer: false)
        next.title = "FullOffice 更新"; next.isReleasedWhenClosed = false; next.center()
        let label = NSTextField(labelWithString: text); label.frame = NSRect(x: 20, y: 80, width: 360, height: 20)
        let bar = NSProgressIndicator(frame: NSRect(x: 20, y: 50, width: 360, height: 16)); bar.style = .bar
        bar.isIndeterminate = indeterminate; bar.maxValue = maximum; bar.doubleValue = 0
        if indeterminate { bar.startAnimation(nil) }
        let cancel = NSButton(title: "取消", target: self, action: #selector(cancelUpdate)); cancel.frame = NSRect(x: 300, y: 12, width: 80, height: 28)
        for view in [label,bar,cancel] { next.contentView?.addSubview(view) }
        self.label = label; self.bar = bar; window = next; next.makeKeyAndOrderFront(nil)
    }
    @objc func cancelUpdate() { checking = false; feed?.cancel(); feed = nil; download?.cancel(); window?.close() }
}
