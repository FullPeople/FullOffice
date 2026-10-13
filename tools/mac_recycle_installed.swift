import AppKit

guard ProcessInfo.processInfo.environment["GITHUB_ACTIONS"]=="true",CommandLine.arguments.count==2 else {exit(2)}
let target=URL(fileURLWithPath:"/Applications/FullOffice.app")
let info=try PropertyListSerialization.propertyList(from:Data(contentsOf:target.appendingPathComponent("Contents/Info.plist")),format:nil) as! [String:Any]
guard info["CFBundleIdentifier"] as? String=="center.dnd.FullOffice",NSRunningApplication.runningApplications(withBundleIdentifier:"center.dnd.FullOffice").isEmpty else {exit(3)}
var done=false,success=false
NSWorkspace.shared.recycle([target]) {urls,error in
    DispatchQueue.main.async {
    success=error==nil && !FileManager.default.fileExists(atPath:target.path)
    var record:[String:Any]=["Passed":success,"OriginalLocationRemoved":!FileManager.default.fileExists(atPath:target.path),"MovedToTrash":urls[target]?.path ?? "",
                           "EffectiveUserID":geteuid()]
    if let error = error as NSError? {record["ErrorDomain"]=error.domain;record["ErrorCode"]=error.code;record["ErrorDescription"]=error.localizedDescription}
    try? JSONSerialization.data(withJSONObject:record,options:[.prettyPrinted,.sortedKeys]).write(to:URL(fileURLWithPath:CommandLine.arguments[1]))
    done=true
    }
}
let deadline=Date().addingTimeInterval(30)
while !done && Date()<deadline {RunLoop.current.run(until:Date().addingTimeInterval(0.1))}
exit(success ? 0 : 1)
