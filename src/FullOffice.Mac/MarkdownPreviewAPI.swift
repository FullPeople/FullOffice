import AppKit

@objc(FullOfficeMarkdownPreviewDelegate) protocol MarkdownPreviewDelegate {
    @objc func fullOfficePreviewOpenDocument(_ url: URL)
}
@objc(FullOfficeMarkdownPreviewAPI) protocol MarkdownPreviewAPI {
    var view: NSView { get }
    var delegate: MarkdownPreviewDelegate? { get set }
    @objc(setSource:documentURL:) func setSource(_ source: String, documentURL: URL)
    @objc func closePreview()
    @objc(inspectPreview:) func inspectPreview(_ completion: @escaping (String) -> Void)
}
