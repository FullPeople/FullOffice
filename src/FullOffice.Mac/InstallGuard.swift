import Foundation
import CryptoKit

func running(_ arguments: [String]) throws -> Bool {
    let process=Process();process.executableURL=URL(fileURLWithPath:"/usr/bin/pgrep");process.arguments=arguments
    process.standardOutput=FileHandle.nullDevice;process.standardError=FileHandle.nullDevice
    try process.run();process.waitUntilExit()
    return process.terminationStatus==0
}
do {
    // Installer scripts pass the destination volume as their third argument.
    guard CommandLine.arguments.count==2,CommandLine.arguments[1]=="/" else {
        throw NSError(domain:"FullOfficeInstall",code:3,userInfo:[NSLocalizedDescriptionKey:"请选择当前系统磁盘安装 FullOffice。"])
    }
    guard try !running(["-x","FullOffice"]), try !running(["-f","/FullOffice.app/Contents/Helpers/"]) else {
        throw NSError(domain:"FullOfficeInstall",code:4,userInfo:[NSLocalizedDescriptionKey:"请先保存文档并退出 FullOffice，再安装或更新。"])
    }
    let files=FileManager.default,target=URL(fileURLWithPath:"/Applications/FullOffice.app")
    if (try? files.attributesOfItem(atPath:target.path)) != nil {
        guard try files.attributesOfItem(atPath:target.path)[.type] as? FileAttributeType == .typeDirectory,
              let info=try PropertyListSerialization.propertyList(from:Data(contentsOf:target.appendingPathComponent("Contents/Info.plist")),format:nil) as? [String:Any],
              let identifier=info["CFBundleIdentifier"] as? String,["center.dnd.FullOffice","center.dnd.FullOffice.MacPreview"].contains(identifier) else {
            throw NSError(domain:"FullOfficeInstall",code:5,userInfo:[NSLocalizedDescriptionKey:"安装位置已有其他应用或链接，未覆盖该位置。"])
        }
        let before=try manifest(target)
        var bytes:Int64=0
        for path in before.keys {bytes += (try files.attributesOfItem(atPath:target.appendingPathComponent(path).path)[.size] as? NSNumber)?.int64Value ?? 0}
        let free=(try files.attributesOfFileSystem(forPath:"/Applications")[.systemFreeSize] as? NSNumber)?.int64Value ?? 0
        guard free > bytes+512*1024*1024 else {
            throw NSError(domain:"FullOfficeInstall",code:6,userInfo:[NSLocalizedDescriptionKey:"磁盘空间不足以保留上一版应用，当前版本未更改。"])
        }
        let root=URL(fileURLWithPath:"/Library/Application Support/FullOffice/InstallBackups")
        var ancestor=root
        while ancestor.path != "/" {
            if files.fileExists(atPath:ancestor.path),try files.attributesOfItem(atPath:ancestor.path)[.type] as? FileAttributeType == .typeSymbolicLink {
                throw NSError(domain:"FullOfficeInstall",code:7,userInfo:[NSLocalizedDescriptionKey:"备份位置包含链接，安装已停止。"])
            }
            ancestor=ancestor.deletingLastPathComponent()
        }
        let backup=root.appendingPathComponent(UUID().uuidString)
        try files.createDirectory(at:backup,withIntermediateDirectories:true,attributes:[.posixPermissions:0o700])
        let copy=backup.appendingPathComponent("FullOffice.app")
        let process=Process();process.executableURL=URL(fileURLWithPath:"/usr/bin/ditto");process.arguments=[target.path,copy.path]
        try process.run();process.waitUntilExit()
        guard process.terminationStatus==0,try manifest(copy)==before,try manifest(target)==before else {
            throw NSError(domain:"FullOfficeInstall",code:8,userInfo:[NSLocalizedDescriptionKey:"上一版应用的备份校验未通过，当前版本未更改。"])
        }
        try JSONSerialization.data(withJSONObject:["Target":target.path,"Backup":copy.path,"Files":before,"PreviousIdentifier":identifier],options:[.prettyPrinted,.sortedKeys])
            .write(to:backup.appendingPathComponent("receipt.json"),options:.atomic)
    }
    print("FullOffice installation preflight passed")
} catch {fputs(error.localizedDescription+"\n",stderr);exit(1)}
