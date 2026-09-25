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
    init(_ decisions: [MacModelDecision]) { self.decisions = decisions }
    func next(input: String, snapshot: MacPetSnapshot, feedback: [MacToolFeedback],
              turn: Int, finalOnly: Bool, timeout: TimeInterval) async throws -> MacModelDecision {
        feedbackCounts.append(feedback.count)
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
        print("Mac Agent tests passed")
    }
}
