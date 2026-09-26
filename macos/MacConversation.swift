import Foundation

struct MacConversationTool {
    let name: String
    let code: MacToolCode
    var json: [String: Any] { ["name": name, "code": code.rawValue] }
}

// Deliberately contains no state snapshot, arguments, credential or model call ID.
struct MacConversationTurn {
    let input: String
    let reply: String
    let status: MacAgentCode
    let tools: [MacConversationTool]

    init(input: String, result: MacAgentResult) {
        self.input = input
        reply = result.reply
        status = result.code
        tools = result.trace.map { MacConversationTool(name: String($0.name.prefix(128)), code: $0.code) }
    }

    var json: [String: Any] {
        ["input": input, "reply": reply, "status": status.rawValue, "tools": tools.map(\.json)]
    }
}

struct MacConversationRequest {
    let id: UUID
    let input: String
    let history: [MacConversationTurn]
}

@MainActor final class MacConversationSession {
    static let maxTurns = 6
    static let maxBytes = 24 * 1024
    private(set) var turns: [MacConversationTurn] = []
    private var pending: MacConversationRequest?
    var isRunning: Bool { pending != nil }

    static func bounded(_ history: [MacConversationTurn]) -> [MacConversationTurn] {
        var result = Array(history.suffix(maxTurns))
        while !result.isEmpty {
            if let data = try? JSONSerialization.data(withJSONObject: result.map(\.json)),
               data.count <= maxBytes { break }
            result.removeFirst()
        }
        return result
    }

    func begin(_ text: String) -> MacConversationRequest? {
        let input = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard pending == nil, !input.isEmpty, input.count <= 1000 else { return nil }
        let request = MacConversationRequest(id: UUID(), input: input, history: turns)
        pending = request
        return request
    }

    @discardableResult func complete(_ request: MacConversationRequest, result: MacAgentResult) -> Bool {
        guard pending?.id == request.id else { return false }
        pending = nil
        guard result.code != .invalidRequest,
              !(result.code == .busy && result.trace.isEmpty) else { return false }
        turns = Self.bounded(turns + [MacConversationTurn(input: request.input, result: result)])
        return true
    }

    @discardableResult func clear() -> Bool {
        guard pending == nil else { return false }
        turns.removeAll()
        return true
    }
}
