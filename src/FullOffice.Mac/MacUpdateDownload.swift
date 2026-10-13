import Foundation
import CryptoKit

final class MacUpdateDownload: NSObject, URLSessionDataDelegate {
    private let manifest: MacUpdateManifest
    private let directory: URL
    private let package: URL
    private let configuration: URLSessionConfiguration
    private let progress: (Int64, Int64) -> Void
    private let completion: (Result<URL, Error>) -> Void
    private var session: URLSession?
    private var task: URLSessionDataTask?
    private var stream: FileHandle?
    private var packageDigest = SHA256()
    private var received: Int64 = 0
    private var failure: Error?
    private var lastProgress = Date.distantPast

    init(manifest: MacUpdateManifest, root: URL, configuration: URLSessionConfiguration = .ephemeral,
         progress: @escaping (Int64, Int64) -> Void, completion: @escaping (Result<URL, Error>) -> Void) {
        self.manifest = manifest
        directory = root.appendingPathComponent(UUID().uuidString, isDirectory: true)
        package = directory.appendingPathComponent("FullOffice-\(manifest.version)-Setup.pkg")
        self.configuration = configuration; self.progress = progress; self.completion = completion
    }
    func start() {
        do {
            guard manifest.url.scheme == "https" else { throw OfficeSessionError.message("更新下载地址无效。") }
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
            if let free = try FileManager.default.attributesOfFileSystem(forPath: directory.path)[.systemFreeSize] as? NSNumber,
               free.int64Value < manifest.bytes + 128 * 1024 * 1024 {
                throw OfficeSessionError.message("磁盘空间不足。请至少留出安装包大小和额外 128 MB 的空间后再下载。")
            }
            guard FileManager.default.createFile(atPath: package.path, contents: nil, attributes: [.posixPermissions: 0o600]) else {
                throw OfficeSessionError.message("无法创建更新下载文件。")
            }
            stream = try FileHandle(forWritingTo: package)
            configuration.urlCache = nil; configuration.httpCookieStorage = nil
            configuration.timeoutIntervalForRequest = 60; configuration.timeoutIntervalForResource = 3600
            let queue = OperationQueue(); queue.maxConcurrentOperationCount = 1
            let session = URLSession(configuration: configuration, delegate: self, delegateQueue: queue)
            self.session = session
            var request = URLRequest(url: manifest.url); request.cachePolicy = .reloadIgnoringLocalCacheData
            request.setValue("identity", forHTTPHeaderField: "Accept-Encoding")
            task = session.dataTask(with: request); task?.resume()
        } catch { finish(.failure(error)) }
    }
    func cancel() { task?.cancel() }
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        guard request.url?.scheme == "https", request.url?.user == nil, request.url?.password == nil else {
            failure = OfficeSessionError.message("更新下载跳转地址无效。")
            completionHandler(nil); task.cancel(); return
        }
        completionHandler(request)
    }
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        guard let response = response as? HTTPURLResponse, response.statusCode == 200, response.url?.scheme == "https",
              response.expectedContentLength < 0 || response.expectedContentLength == manifest.bytes else {
            failure = OfficeSessionError.message("更新服务器返回了错误或不完整的安装包。")
            completionHandler(.cancel); return
        }
        completionHandler(.allow)
    }
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        guard failure == nil else { return }
        do {
            guard Int64(data.count) <= manifest.bytes - received else { throw OfficeSessionError.message("更新下载超出签名清单中的大小。") }
            guard let stream else { throw OfficeSessionError.message("更新下载文件已关闭。") }
            try stream.write(contentsOf: data); packageDigest.update(data: data); received += Int64(data.count)
            if Date().timeIntervalSince(lastProgress) >= 0.1 || received == manifest.bytes {
                lastProgress = Date(); let count = received
                DispatchQueue.main.async { self.progress(count, self.manifest.bytes) }
            }
        } catch { failure = error; dataTask.cancel() }
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        do {
            if let failure { throw failure }
            if let error { throw error }
            try stream?.synchronize(); try stream?.close(); stream = nil
            guard received == manifest.bytes, packageDigest.finalize().map({String(format: "%02x", $0)}).joined() == manifest.sha256 else {
                throw OfficeSessionError.message("更新安装包校验失败，未安装。请重新检查更新。")
            }
            finish(.success(package))
        } catch { finish(.failure(error)) }
    }
    private func finish(_ result: Result<URL, Error>) {
        try? stream?.close(); stream = nil
        session?.finishTasksAndInvalidate(); session = nil; task = nil
        if case .failure = result { try? FileManager.default.removeItem(at: directory) }
        DispatchQueue.main.async { self.completion(result) }
    }
}
