import AppKit
import PDFKit
import CoreText
let target = URL(fileURLWithPath: CommandLine.arguments[1])
var box = CGRect(x: 0, y: 0, width: 612, height: 792)
guard let context = CGContext(target as CFURL, mediaBox: &box, nil) else { exit(2) }
for index in 0..<3 {
    context.beginPDFPage(nil)
    context.textPosition = CGPoint(x: 60, y: 700)
    let text = NSAttributedString(string: "FullOffice fixture page \(index + 1)", attributes: [.font: NSFont.systemFont(ofSize: 24)])
    CTLineDraw(CTLineCreateWithAttributedString(text), context)
    context.endPDFPage()
}
context.closePDF()
guard let doc = PDFDocument(url: target), let first = doc.page(at: 0), let last = doc.page(at: 2) else { exit(3) }
let outline = PDFOutline()
for (label, page) in [("First", first), ("Last", last)] {
    let item = PDFOutline(); item.label = label; item.destination = PDFDestination(page: page, at: CGPoint(x: 0, y: 792))
    outline.insertChild(item, at: outline.numberOfChildren)
}
doc.outlineRoot = outline
guard doc.write(to: target) else { exit(4) }
