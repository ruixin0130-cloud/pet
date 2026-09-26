import Foundation

private final class OversizeResponseProtocol: URLProtocol {
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        let response = HTTPURLResponse(url: request.url!, statusCode: 200,
                                       httpVersion: nil, headerFields: nil)!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: Data(repeating: 65, count: 1_048_577))
        client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() {}
}

@MainActor private final class FakePet: MacPetPort {
    var action = "Idle"
    var speech: String?
    var busy = false
    var actionCalls = 0
    func snapshot() -> MacPetSnapshot {
        MacPetSnapshot(characterName: "玉子", action: action, interaction: nil,
                       currentSpeech: speech, busy: busy)
    }
    func setAction(_ value: String) -> MacToolCode {
        if busy { return .busy }
        actionCalls += 1
        action = value
        return .applied
    }
    func playInteraction(_ name: String) -> MacToolCode { busy ? .busy : .applied }
    func speak(_ text: String) -> MacToolCode {
        if busy { return .busy }
        speech = text
        return .applied
    }
}

@MainActor private final class FakeModel: MacAgentModel {
    var decisions: [MacModelDecision]
    var feedbackCounts: [Int] = []
    var histories: [[MacConversationTurn]] = []
    var snapshots: [MacPetSnapshot] = []
    var onNext: ((Int) throws -> Void)?
    init(_ decisions: [MacModelDecision]) { self.decisions = decisions }
    func next(input: String, history: [MacConversationTurn], snapshot: MacPetSnapshot, feedback: [MacToolFeedback],
              turn: Int, finalOnly: Bool, timeout: TimeInterval) async throws -> MacModelDecision {
        feedbackCounts.append(feedback.count)
        histories.append(history)
        snapshots.append(snapshot)
        try onNext?(feedbackCounts.count)
        if Task.isCancelled { throw CancellationError() }
        return decisions.removeFirst()
    }
}

@MainActor private func check(_ condition: @autoclosure () -> Bool, _ message: String) {
    precondition(condition(), message)
}

@main struct MacAgentTests {
    @MainActor static func main() async throws {
        do {
            let pet = FakePet()
            let model = FakeModel([.tool(id: "1", name: "set_action", arguments: ["action": "Sit"]),
                                   .final("玉子坐好了。")])
            let result = await MacAgentRuntime(port: pet, model: model).run("坐下")
            check(result.code == .completed && result.trace.count == 1, "success result")
            check(result.trace[0].code == .applied && pet.action == "Sit", "actual action")
            check(model.feedbackCounts == [0, 1], "feedback loop")
        }
        do {
            let pet = FakePet()
            pet.busy = true
            let model = FakeModel([.tool(id: "1", name: "set_action", arguments: ["action": "Sleep"])])
            let result = await MacAgentRuntime(port: pet, model: model).run("睡觉")
            check(result.code == .busy && result.trace[0].code == .busy, "busy result")
            check(pet.actionCalls == 0, "busy must not execute")
        }
        do {
            let pet = FakePet()
            let model = FakeModel([.tool(id: "1", name: "play_interaction", arguments: ["interaction": "Petted"])])
            let result = await MacAgentRuntime(port: pet, model: model).run("摸摸")
            check(result.code == .toolFailure && result.trace[0].code == .malformedArguments,
                  "Petted must be rejected")
        }
        do {
            let pet = FakePet()
            let model = FakeModel([.final("你好")])
            let task = Task { await MacAgentRuntime(port: pet, model: model).run("你好") }
            task.cancel()
            let result = await task.value
            check(result.code == .cancelled, "cancellation")
            check(result.trace.isEmpty, "cancelled call has no actions")
        }
        do {
            let response = Data("""
                {"choices":[{"finish_reason":"tool_calls","message":{"role":"assistant",\
                "tool_calls":[{"id":"call-1","type":"function","function":{\
                "name":"speak","arguments":"{\\\"text\\\":\\\"你好\\\"}"}}]}}]}
                """.utf8)
            let decision = try MacQwenAdapter.parse(response)
            if case .tool(let id, let name, let args) = decision {
                check(id == "call-1" && name == "speak" && args["text"] as? String == "你好",
                      "Qwen tool parse")
            } else { preconditionFailure("expected tool") }
        }
        do {
            let configuration = URLSessionConfiguration.ephemeral
            configuration.protocolClasses = [OversizeResponseProtocol.self]
            let session = URLSession(configuration: configuration)
            defer { session.invalidateAndCancel() }
            let adapter = MacQwenAdapter(apiKey: "test-only", session: session)
            let snapshot = FakePet().snapshot()
            do {
                _ = try await adapter.next(input: "test", snapshot: snapshot, feedback: [],
                                           turn: 1, finalOnly: false, timeout: 10)
                preconditionFailure("oversized response was accepted")
            } catch MacModelError.responseTooLarge {}
        }
        try await testConversation()
        try await testConversationPayload()
        print("Mac Agent tests passed")
    }
}

@MainActor private func testConversation() async throws {
    let session = MacConversationSession()
    let pet = FakePet()
    let result = MacAgentResult(code: .completed, reply: "休息一会儿吧。", trace: [], snapshot: pet.snapshot())
    check(session.begin("  ") == nil && session.begin(String(repeating: "a", count: 1001)) == nil,
          "invalid input must not enter history")
    let first = session.begin("今天有点累")!
    check(first.history.isEmpty && session.begin("duplicate") == nil && !session.clear(),
          "new session is empty, duplicates and clear blocked during request")
    check(session.complete(first, result: result) && !session.complete(first, result: result),
          "record completion exactly once")
    let second = session.begin("那你陪我休息一下")!
    check(second.history.count == 1 && second.history[0].input == first.input, "follow-up retains prior exchange")
    let model = FakeModel([.tool(id: "sit", name: "set_action", arguments: ["action": "Sit"]), .final("坐好陪你了。")])
    let actual = await MacAgentRuntime(port: pet, model: model).run(second.input, history: second.history)
    session.complete(second, result: actual)
    check(model.histories.map(\.count) == [1, 1] && second.history.count == 1,
          "all model turns see the same immutable historical snapshot")
    check(model.snapshots[0].action == "Idle" && model.snapshots[1].action == "Sit",
          "live pet snapshots are refreshed independently of history")
    check(session.turns[1].tools[0].code == .applied, "record actual tool outcome")
    pet.action = "Sleep" // A manual action between requests must supersede historical state.
    let third = session.begin("你现在在做什么")!
    let followup = FakeModel([.final("现在睡着了。")])
    let next = await MacAgentRuntime(port: pet, model: followup).run(third.input, history: third.history)
    session.complete(third, result: next)
    check(followup.snapshots[0].action == "Sleep" && pet.actionCalls == 1,
          "history does not replay tools or replace current state")
    let failureRequest = session.begin("继续")!
    let failing = FakeModel([.tool(id: "lie", name: "set_action", arguments: ["action": "Lie"])])
    failing.onNext = { if $0 == 2 { throw URLError(.notConnectedToInternet) } }
    let failed = await MacAgentRuntime(port: pet, model: failing).run(failureRequest.input, history: failureRequest.history)
    session.complete(failureRequest, result: failed)
    check(session.turns.last!.status == .modelUnavailable && session.turns.last!.tools[0].code == .applied,
          "partial execution remains recorded after model failure")
    let cancelRequest = session.begin("先坐下再说话")!
    let cancelling = FakeModel([.tool(id: "cancel", name: "set_action", arguments: ["action": "Sit"]), .final("unused")])
    var task: Task<MacAgentResult, Never>?
    cancelling.onNext = { if $0 == 2 { task?.cancel() } }
    task = Task { await MacAgentRuntime(port: pet, model: cancelling).run(cancelRequest.input, history: cancelRequest.history) }
    let cancelled = await task!.value
    session.complete(cancelRequest, result: cancelled)
    check(session.turns.last!.status == .cancelled && session.turns.last!.tools[0].code == .applied,
          "cancellation records an already executed action")
    let oldCount = session.turns.count
    let rejected = session.begin("already busy")!
    session.complete(rejected, result: MacAgentResult(code: .busy, reply: "已有请求", trace: [], snapshot: pet.snapshot()))
    check(session.turns.count == oldCount, "concurrent runtime rejection is not a new exchange")
    let busyRequest = session.begin("坐下")!
    pet.busy = true
    let busyModel = FakeModel([.tool(id: "busy", name: "set_action", arguments: ["action": "Sit"])])
    let busy = await MacAgentRuntime(port: pet, model: busyModel).run(busyRequest.input, history: busyRequest.history)
    session.complete(busyRequest, result: busy)
    check(session.turns.last!.status == .busy && session.turns.last!.tools[0].code == .busy,
          "pet busy is retained as an actual failed tool outcome")
    session.clear()
    for i in 0..<8 {
        let request = session.begin("request-\(i)")!
        session.complete(request, result: result)
    }
    check(session.turns.count == 6 && session.turns.first!.input == "request-2" && session.turns.last!.input == "request-7",
          "oldest whole exchanges are evicted after six")
    session.clear()
    for _ in 0..<4 {
        let request = session.begin(String(repeating: "猫", count: 1000))!
        session.complete(request, result: MacAgentResult(code: .completed, reply: String(repeating: "猫", count: 2000),
                                                        trace: [], snapshot: pet.snapshot()))
    }
    let encoded = try JSONSerialization.data(withJSONObject: session.turns.map(\.json))
    check(session.turns.count == 2 && encoded.count <= MacConversationSession.maxBytes,
          "24 KiB limit accounts for multibyte Unicode")
    let oversized = MacConversationTurn(input: "escaped", result: MacAgentResult(code: .completed,
        reply: String(repeating: "\u{0001}", count: 5000), trace: [], snapshot: pet.snapshot()))
    check(MacConversationSession.bounded([oversized]).isEmpty, "JSON escaping is counted in size limit")
    let frozen = session.turns
    check(session.clear() && session.turns.isEmpty && frozen.count == 2 && MacConversationSession().turns.isEmpty,
          "clear and a new application session have no memory; earlier snapshots do not mutate")
    check(session.begin("new conversation")!.history.isEmpty, "next request after clear has empty history")
}

private final class ConversationResponseProtocol: URLProtocol {
    private static let lock = NSLock()
    private static var captured: Data?
    static var body: Data? {
        lock.lock(); defer { lock.unlock() }; return captured
    }
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        var data = request.httpBody ?? Data()
        if let stream = request.httpBodyStream {
            stream.open(); defer { stream.close() }
            var buffer = [UInt8](repeating: 0, count: 4096)
            while true {
                let count = stream.read(&buffer, maxLength: buffer.count)
                if count <= 0 { break }
                data.append(contentsOf: buffer.prefix(count))
            }
        }
        Self.lock.lock(); Self.captured = data; Self.lock.unlock()
        let response = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: Data("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"测试回复\"}}]}".utf8))
        client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() {}
}

@MainActor private func testConversationPayload() async throws {
    let configuration = URLSessionConfiguration.ephemeral
    configuration.protocolClasses = [ConversationResponseProtocol.self]
    let http = URLSession(configuration: configuration)
    defer { http.invalidateAndCancel() }
    let pet = FakePet()
    let history = [MacConversationTurn(input: "坐下", result: MacAgentResult(code: .cancelled,
        reply: "已取消，但坐下已执行。", trace: [MacToolFeedback(name: "set_action", code: .applied, snapshot: pet.snapshot())],
        snapshot: pet.snapshot()))]
    pet.action = "Sleep"
    let adapter = MacQwenAdapter(apiKey: "test-only", session: http)
    _ = try await adapter.next(input: "然后呢", history: history, snapshot: pet.snapshot(), feedback: [],
                               turn: 1, finalOnly: false, timeout: 10)
    let body = ConversationResponseProtocol.body!
    let payload = try JSONSerialization.jsonObject(with: body) as! [String: Any]
    let messages = payload["messages"] as! [[String: Any]]
    let context = try JSONSerialization.jsonObject(with: Data((messages[1]["content"] as! String).utf8)) as! [String: Any]
    let sentHistory = context["history"] as! [[String: Any]]
    check(sentHistory.count == 1 && sentHistory[0]["status"] as? String == "cancelled" &&
          sentHistory[0]["snapshot"] == nil && (context["snapshot"] as! [String: Any])["action"] as? String == "Sleep",
          "HTTP payload carries past outcomes separately from the current snapshot")
    check(!String(data: body, encoding: .utf8)!.contains("test-only") && messages.count == 2,
          "history never becomes a system message and credentials stay out of message contents")
    _ = try await adapter.next(input: "new", snapshot: pet.snapshot(), feedback: [], turn: 1, finalOnly: true, timeout: 10)
    let emptyPayload = try JSONSerialization.jsonObject(with: ConversationResponseProtocol.body!) as! [String: Any]
    let emptyMessages = emptyPayload["messages"] as! [[String: Any]]
    let emptyContext = try JSONSerialization.jsonObject(with: Data((emptyMessages[1]["content"] as! String).utf8)) as! [String: Any]
    check((emptyContext["history"] as! [Any]).isEmpty && emptyPayload["tools"] == nil,
          "adapter has no persistent history and final-only requests still suppress tools")
}
