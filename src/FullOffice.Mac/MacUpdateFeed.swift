import Foundation

final class MacUpdateFeed: NSObject, URLSessionDataDelegate {
    private let completion: (Result<Data, Error>) -> Void
    private var session: URLSession?
    private var task: URLSessionDataTask?
    private var content = Data()
    private var failure: Error?
    init(completion: @escaping (Result<Data, Error>) -> Void) { self.completion = completion }
    func start(url: URL, configuration: URLSessionConfiguration = .ephemeral) {
        guard url.scheme == "https", url.user == nil, url.password == nil else {
            finish(.failure(OfficeSessionError.message("更新清单地址无效。"))); return
        }
        configuration.urlCache = nil; configuration.httpCookieStorage = nil
        configuration.timeoutIntervalForRequest = 30; configuration.timeoutIntervalForResource = 60
        let queue = OperationQueue(); queue.maxConcurrentOperationCount = 1
        let session = URLSession(configuration: configuration, delegate: self, delegateQueue: queue); self.session = session
        var request = URLRequest(url: url); request.cachePolicy = .reloadIgnoringLocalCacheData
        task = session.dataTask(with: request); task?.resume()
    }
    func cancel() { task?.cancel() }
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        guard request.url?.scheme == "https", request.url?.user == nil, request.url?.password == nil else {
            failure = OfficeSessionError.message("更新清单跳转地址无效。")
            completionHandler(nil); task.cancel(); return
        }
        completionHandler(request)
    }
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        guard let response = response as? HTTPURLResponse, response.statusCode == 200,
              response.url?.scheme == "https", response.expectedContentLength <= 65536 else {
            failure = OfficeSessionError.message("暂时无法读取更新信息。请稍后再试。")
            completionHandler(.cancel); return
        }
        completionHandler(.allow)
    }
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        guard failure == nil else { return }
        guard data.count <= 65536 - content.count else {
            failure = OfficeSessionError.message("更新清单超出允许大小。")
            dataTask.cancel(); return
        }
        content.append(data)
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        if let error = failure ?? error { finish(.failure(error)) }
        else { finish(.success(content)) }
    }
    private func finish(_ result: Result<Data, Error>) {
        session?.finishTasksAndInvalidate(); session = nil; task = nil
        DispatchQueue.main.async { self.completion(result) }
    }
}
