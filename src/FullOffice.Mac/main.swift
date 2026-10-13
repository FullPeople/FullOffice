import AppKit
import PDFKit
import UniformTypeIdentifiers

func probeStage(_ phase: String) {
    if CommandLine.arguments.contains("--ui-probe") { fputs("MAC_PROBE \(phase)\n", stderr) }
}

final class ReaderWindow: NSWindowController, NSOutlineViewDataSource, NSOutlineViewDelegate {
    var openDocument: ((URL) -> Void)?
    let pdf = PDFView()
    let outline = NSOutlineView()
    let sidebar = NSScrollView()
    let status = NSTextField(labelWithString: "请选择 PDF")
    private(set) var pdfDocument: PDFDocument?
    private(set) var url: URL?

    init() {
        probeStage("window-construction")
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1100, height: 780),
                              styleMask: [.titled, .closable, .miniaturizable, .resizable],
                              backing: .buffered, defer: false)
        super.init(window: window)
        window.title = "FullOffice · macOS 阅读预览"
        window.center()
        window.isReleasedWhenClosed = false
        probeStage("window-created")
        let host = NSView(); window.contentView = host
        let open = NSButton(title: "打开文件", target: self, action: #selector(openFile))
        let fit = NSButton(title: "适应整页", target: self, action: #selector(fitPage))
        let width = NSButton(title: "适应宽度", target: self, action: #selector(fitWidth))
        let continuous = NSButton(checkboxWithTitle: "连续滚动", target: self, action: #selector(togglePdfDisplay(_:)))
        continuous.state = .on
        let fold = NSButton(title: "折叠书签", target: self, action: #selector(collapseBookmarks))
        let controls = NSStackView(views: [open, fit, width, continuous, fold, status])
        controls.spacing = 8; controls.orientation = .horizontal
        let split = NSSplitView(); split.isVertical = true; split.dividerStyle = .thin
        let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("bookmark"))
        column.title = "书签"; column.width = 230
        outline.addTableColumn(column); outline.outlineTableColumn = column
        outline.headerView = nil; outline.rowHeight = 22; outline.indentationPerLevel = 14
        outline.dataSource = self; outline.delegate = self
        outline.target = self; outline.action = #selector(jumpBookmark)
        sidebar.documentView = outline; sidebar.hasVerticalScroller = true
        pdf.displayMode = .singlePageContinuous; pdf.displayDirection = .vertical
        pdf.autoScales = true; pdf.backgroundColor = .windowBackgroundColor
        split.addArrangedSubview(sidebar); split.addArrangedSubview(pdf)
        for view in [controls, split] {
            view.translatesAutoresizingMaskIntoConstraints = false; host.addSubview(view)
        }
        NSLayoutConstraint.activate([
            controls.topAnchor.constraint(equalTo: host.topAnchor, constant: 8),
            controls.leadingAnchor.constraint(equalTo: host.leadingAnchor, constant: 10),
            controls.trailingAnchor.constraint(lessThanOrEqualTo: host.trailingAnchor, constant: -10),
            split.topAnchor.constraint(equalTo: controls.bottomAnchor, constant: 8),
            split.leadingAnchor.constraint(equalTo: host.leadingAnchor),
            split.trailingAnchor.constraint(equalTo: host.trailingAnchor),
            split.bottomAnchor.constraint(equalTo: host.bottomAnchor)
        ])
        sidebar.widthAnchor.constraint(greaterThanOrEqualToConstant: 180).isActive = true
        sidebar.widthAnchor.constraint(lessThanOrEqualToConstant: 350).isActive = true
        split.setPosition(240, ofDividerAt: 0)
        NotificationCenter.default.addObserver(self, selector: #selector(pageChanged),
                                               name: .PDFViewPageChanged, object: pdf)
        probeStage("reader-created")
    }
    required init?(coder: NSCoder) { fatalError("Programmatic window") }
    deinit { NotificationCenter.default.removeObserver(self) }

    func load(_ source: URL) throws {
        guard let next = PDFDocument(url: source), next.pageCount > 0, !next.isLocked else {
            throw NSError(domain: "FullOffice", code: 1, userInfo: [NSLocalizedDescriptionKey: "无法打开 PDF，文件可能损坏或需要密码。"])
        }
        pdfDocument = next; url = source; pdf.document = next
        window?.title = source.lastPathComponent + " — FullOffice 阅读预览"
        window?.representedURL = source
        outline.reloadData(); outline.expandItem(nil, expandChildren: false)
        pageChanged()
    }
    @objc func openFile() {
        let panel = NSOpenPanel(); panel.allowedContentTypes = [.pdf] + OfficeFileSession.formats.compactMap { UTType(filenameExtension: $0) }; panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let selected = panel.url else { return }
        if selected.pathExtension.lowercased() != "pdf", let openDocument { openDocument(selected); return }
        do { try load(selected) } catch { NSAlert(error: error).runModal() }
    }
    @objc func fitPage() { pdf.autoScales = true; pdf.scaleFactor = pdf.scaleFactorForSizeToFit }
    @objc func fitWidth() {
        guard let page = pdf.currentPage else { return }
        let bounds = page.bounds(for: pdf.displayBox)
        guard bounds.width > 0 else { return }
        pdf.autoScales = false; pdf.scaleFactor = max(0.05, (pdf.bounds.width - 24) / bounds.width)
    }
    @objc func togglePdfDisplay(_ sender: NSButton) {
        pdf.displayMode = sender.state == .on ? .singlePageContinuous : .singlePage
    }
    @objc func collapseBookmarks() { outline.collapseItem(nil, collapseChildren: true) }
    @objc func pageChanged() {
        if let doc = pdfDocument, let page = pdf.currentPage { status.stringValue = "\(doc.index(for: page) + 1) / \(doc.pageCount)" }
        else { status.stringValue = "请选择 PDF" }
    }
    func outlineView(_ view: NSOutlineView, numberOfChildrenOfItem item: Any?) -> Int {
        ((item as? PDFOutline) ?? pdfDocument?.outlineRoot)?.numberOfChildren ?? 0
    }
    func outlineView(_ view: NSOutlineView, child index: Int, ofItem item: Any?) -> Any {
        ((item as? PDFOutline) ?? pdfDocument?.outlineRoot)!.child(at: index)!
    }
    func outlineView(_ view: NSOutlineView, isItemExpandable item: Any) -> Bool { (item as? PDFOutline)?.numberOfChildren ?? 0 > 0 }
    func outlineView(_ view: NSOutlineView, objectValueFor column: NSTableColumn?, byItem item: Any?) -> Any? { (item as? PDFOutline)?.label }
    @objc func jumpBookmark() {
        guard let selected = outline.item(atRow: outline.selectedRow) as? PDFOutline else { return }
        if let destination = selected.destination { pdf.go(to: destination) }
        else if let action = selected.action as? PDFActionGoTo { pdf.go(to: action.destination) }
    }
}

final class Delegate: NSObject, NSApplicationDelegate {
    var readers: [ReaderWindow] = []
    var offices: [OfficeCoordinator] = []
    var officeMessages = [ObjectIdentifier: String]()
    var officeRoot: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("FullOffice/EditingSessions", isDirectory: true)
    }
    func openDocument(_ selected: URL) {
        if OfficeFileSession.formats.contains(selected.pathExtension.lowercased()) {
            do {
                let office = OfficeCoordinator(runtime: try OfficeRuntime.bundled(), root: officeRoot)
                offices.append(office)
                office.onState = { [weak self, weak office] message in
                    guard let self, let office else { return }
                    self.officeMessages[ObjectIdentifier(office)] = message
                }
                office.onFailure = { message in NSAlert(error: OfficeSessionError.message(message)).runModal() }
                office.onOpen = { [weak self] in self?.openFile() }
                office.onSaveAs = { [weak self, weak office] in
                    guard let self, let office, let session = office.session else { return }
                    let panel = NSSavePanel(); panel.nameFieldStringValue = session.source.lastPathComponent
                    panel.allowedContentTypes = UTType(filenameExtension: session.source.pathExtension).map { [$0] } ?? [.data]
                    panel.allowsOtherFileTypes = false
                    if panel.runModal() == .OK, let target = panel.url { office.requestSaveAs(to: target) }
                }
                office.open(selected)
            } catch { NSAlert(error: error).runModal() }
            return
        }
        let reader = ReaderWindow()
        reader.openDocument = { [weak self] in self?.openDocument($0) }
        do { try reader.load(selected); readers.append(reader); reader.showWindow(nil) }
        catch { NSAlert(error: error).runModal() }
    }
    @objc func openFile() {
        let panel = NSOpenPanel(); panel.allowedContentTypes = [.pdf] + OfficeFileSession.formats.compactMap { UTType(filenameExtension: $0) }
        panel.allowsMultipleSelection = true
        if panel.runModal() == .OK { for url in panel.urls { openDocument(url) } }
    }
    func applicationDidFinishLaunching(_ notification: Notification) {
        probeStage("did-finish-launching")
        let menu = NSMenu(), appMenu = NSMenu(), item = NSMenuItem()
        item.submenu = appMenu; menu.addItem(item)
        appMenu.addItem(withTitle: "退出 FullOffice", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        let fileMenu = NSMenu(title: "文件"), fileItem = NSMenuItem(title: "文件", action: nil, keyEquivalent: "")
        fileItem.submenu = fileMenu; menu.addItem(fileItem)
        fileMenu.addItem(withTitle: "打开文件…", action: #selector(openFile), keyEquivalent: "o").target = self
        let edit = NSMenu(title: "编辑"), editItem = NSMenuItem(title: "编辑", action: nil, keyEquivalent: "")
        editItem.submenu = edit; menu.addItem(editItem)
        edit.addItem(withTitle: "复制", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: "全选", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        NSApp.mainMenu = menu
        if CommandLine.arguments.contains("--ui-probe") { runProbe(); return }
        if readers.isEmpty {
            let reader = ReaderWindow(); reader.openDocument = { [weak self] in self?.openDocument($0) }; readers.append(reader); reader.showWindow(nil)
        }
        NSApp.activate(ignoringOtherApps: true)
    }
    func application(_ sender: NSApplication, openFiles filenames: [String]) {
        if CommandLine.arguments.contains("--ui-probe") {
            probeStage("probe-launch-arguments-handled")
            sender.reply(toOpenOrPrint: .success)
            return
        }
        for filename in filenames { openDocument(URL(fileURLWithPath: filename)) }
        sender.reply(toOpenOrPrint: .success)
    }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { !offices.contains { $0.isActive } }
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        if offices.contains(where: { $0.isActive }) {
            let alert = NSAlert(); alert.messageText = "还有正在打开或编辑的文档"
            alert.informativeText = "请等待文档打开，在文档窗口中保存或关闭后再退出 FullOffice。"
            alert.addButton(withTitle: "继续编辑"); alert.runModal(); return .terminateCancel
        }
        return .terminateNow
    }

    func runProbe() {
        probeStage("probe-enter")
        let args = CommandLine.arguments
        guard let index = args.firstIndex(of: "--ui-probe"), args.count > index + 2 else { exit(2) }
        let source = URL(fileURLWithPath: args[index + 1]), output = URL(fileURLWithPath: args[index + 2], isDirectory: true)
        var checks = [String: Bool](), failure: String?
        let reader = ReaderWindow(); readers.append(reader)
        do {
            try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
            probeStage("load-fixture")
            try reader.load(source); reader.showWindow(nil)
            reader.window?.contentView?.layoutSubtreeIfNeeded()
            probeStage("fixture-visible")
            checks["ActualPdfKitDocumentLoaded"] = reader.pdfDocument?.pageCount == 3
            checks["FullOutlineImported"] = reader.pdfDocument?.outlineRoot?.numberOfChildren == 2
            checks["TextSelectionAvailable"] = reader.pdfDocument?.page(at: 0)?.string?.contains("FullOffice fixture") == true
            reader.fitPage(); checks["FitPageHasValidScale"] = reader.pdf.scaleFactor.isFinite && reader.pdf.scaleFactor > 0
            reader.fitWidth(); checks["FitWidthHasValidScale"] = reader.pdf.scaleFactor.isFinite && reader.pdf.scaleFactor > 0
            if let last = reader.pdfDocument?.page(at: 2) { reader.pdf.go(to: PDFDestination(page: last, at: CGPoint(x: 0, y: last.bounds(for: .mediaBox).maxY))) }
            checks["PageNavigationWorks"] = reader.pdf.currentPage.map { reader.pdfDocument?.index(for: $0) == 2 } ?? false
            checks["OwnedNativeWindowVisible"] = reader.window?.isVisible == true
        } catch { failure = String(describing: error) }
        DispatchQueue.main.asyncAfter(deadline: .now() + 2) {
            probeStage("capture-and-report")
            do {
                if let window = reader.window {
                    let capture = Process()
                    capture.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
                    capture.arguments = ["-x", "-l", String(window.windowNumber), output.appendingPathComponent("window.png").path]
                    try capture.run(); capture.waitUntilExit()
                    if capture.terminationStatus != 0 { failure = "Native window capture failed: \(capture.terminationStatus)" }
                }
                checks["RenderedWindowCapture"] = FileManager.default.fileExists(atPath: output.appendingPathComponent("window.png").path)
                let passed = failure == nil && checks.count == 8 && checks.values.allSatisfy { $0 }
                var report: [String: Any] = ["Passed": passed, "Checks": checks, "Architecture": ProcessInfo.processInfo.machineArchitecture,
                    "WindowWidth": reader.window?.frame.width ?? 0, "SidebarRight": reader.sidebar.frame.maxX,
                    "Scope": "Eight native PDFKit/window checks and compositor capture; independent PNG pixel verification is required. No Office editing, installation, updates or real-user-document acceptance."]
                if let failure { report["Failure"] = failure }
                try JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys]).write(to: output.appendingPathComponent("report.json"))
                reader.close(); NSApp.terminate(nil)
            } catch { fputs("Probe report write failed\n", stderr); exit(3) }
        }
    }
}
extension ProcessInfo {
    var machineArchitecture: String {
        #if arch(arm64)
        return "arm64"
        #else
        return "x86_64"
        #endif
    }
}
let app = NSApplication.shared
let delegate = Delegate()
probeStage("application-created")
app.setActivationPolicy(.regular); app.delegate = delegate
withExtendedLifetime(delegate) { app.run() }
