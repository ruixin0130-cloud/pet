import Foundation

enum MacAgentCode: String {
    case completed, busy, cancelled, invalidRequest, modelUnavailable, modelTimeout
    case protocolError, toolFailure, limitReached
}

enum MacToolCode: String {
    case applied, busy, malformedArguments, unknownTool, executionUnknown, shuttingDown
}

struct MacPetSnapshot {
    let characterName: String
    let action: String
    let interaction: String?
    let currentSpeech: String?
    let busy: Bool

    var json: [String: Any] {
        ["characterName": characterName, "action": action,
         "interaction": interaction.map { $0 as Any } ?? NSNull(),
         "currentSpeech": currentSpeech.map { $0 as Any } ?? NSNull(),
         "busy": busy]
    }
}

struct MacToolFeedback {
    let name: String
    let code: MacToolCode
    let snapshot: MacPetSnapshot

    var json: [String: Any] {
        ["name": name, "code": code.rawValue, "snapshot": snapshot.json]
    }
}

struct MacAgentResult {
    let code: MacAgentCode
    let reply: String
    let trace: [MacToolFeedback]
    let snapshot: MacPetSnapshot
}

enum MacModelDecision {
    case final(String)
    case tool(id: String, name: String, arguments: [String: Any])
}

enum MacModelError: Error {
    case invalidResponse, responseTooLarge, httpStatus(Int)
}

@MainActor protocol MacPetPort: AnyObject {
    func snapshot() -> MacPetSnapshot
    func setAction(_ action: String) -> MacToolCode
    func playInteraction(_ name: String) -> MacToolCode
    func speak(_ text: String) -> MacToolCode
}

@MainActor protocol MacAgentModel {
    func next(input: String, snapshot: MacPetSnapshot, feedback: [MacToolFeedback],
              turn: Int, finalOnly: Bool, timeout: TimeInterval) async throws -> MacModelDecision
}

@MainActor final class MacAgentRuntime {
    static let maxTurns = 4
    static let maxTools = 3
    private let port: MacPetPort
    private let model: MacAgentModel
    private var active = false

    init(port: MacPetPort, model: MacAgentModel) {
        self.port = port
        self.model = model
    }

    private func result(_ code: MacAgentCode, _ reply: String,
                        _ trace: [MacToolFeedback]) -> MacAgentResult {
        MacAgentResult(code: code, reply: reply, trace: trace, snapshot: port.snapshot())
    }

    private func execute(name: String, arguments: [String: Any]) -> MacToolCode {
        guard arguments.count == 1 else { return .malformedArguments }
        switch name {
        case "set_action":
            guard let value = arguments["action"] as? String,
                  ["Idle", "Sit", "Lie", "Sleep"].contains(value) else { return .malformedArguments }
            return port.setAction(value)
        case "play_interaction":
            guard let value = arguments["interaction"] as? String,
                  ["Curious", "PlayYarn", "Pout", "Excited"].contains(value) else {
                return .malformedArguments
            }
            return port.playInteraction(value)
        case "speak":
            guard let value = arguments["text"] as? String,
                  Self.validSpeech(value) else { return .malformedArguments }
            return port.speak(value.trimmingCharacters(in: .whitespacesAndNewlines))
        default:
            return .unknownTool
        }
    }

    private static func validSpeech(_ text: String) -> Bool {
        let normalized = text.replacingOccurrences(of: "\r\n", with: "\n")
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard !normalized.isEmpty, normalized.count <= 80,
              !normalized.contains("\n\n"), normalized.split(separator: "\n", omittingEmptySubsequences: false).count <= 2 else {
            return false
        }
        return !normalized.unicodeScalars.contains { CharacterSet.controlCharacters.contains($0) && $0 != "\n" }
    }

    func run(_ request: String) async -> MacAgentResult {
        let input = request.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !input.isEmpty, input.count <= 1000 else {
            return result(.invalidRequest, "请输入 1–1000 字的请求。", [])
        }
        guard !active else { return result(.busy, "已有请求正在处理。", []) }
        active = true
        defer { active = false }
        var trace: [MacToolFeedback] = []
        var ids: Set<String> = []
        let deadline = ProcessInfo.processInfo.systemUptime + 30

        for turn in 1...Self.maxTurns {
            if Task.isCancelled { return result(.cancelled, "请求已取消；已执行的操作见记录。", trace) }
            let remaining = deadline - ProcessInfo.processInfo.systemUptime
            if remaining <= 0 { return result(.modelTimeout, "模型响应超时；已执行的操作见记录。", trace) }
            let finalOnly = trace.count >= Self.maxTools
            let decision: MacModelDecision
            do {
                decision = try await model.next(input: input, snapshot: port.snapshot(), feedback: trace,
                                                turn: turn, finalOnly: finalOnly, timeout: remaining)
            } catch is CancellationError {
                return result(.cancelled, "请求已取消；已执行的操作见记录。", trace)
            } catch let error as URLError where error.code == .timedOut {
                return result(.modelTimeout, "模型响应超时；已执行的操作见记录。", trace)
            } catch MacModelError.invalidResponse {
                return result(.protocolError, "模型返回了无效内容。", trace)
            } catch MacModelError.responseTooLarge {
                return result(.protocolError, "模型返回内容过大。", trace)
            } catch let MacModelError.httpStatus(status) {
                let detail = status == 401 ? "请检查 API Key 与地域。" : "请稍后重试。"
                return result(.modelUnavailable, "模型服务返回 HTTP \(status)；\(detail)", trace)
            } catch {
                return result(Task.isCancelled ? .cancelled : .modelUnavailable,
                              Task.isCancelled ? "请求已取消；已执行的操作见记录。" : "模型暂不可用；已执行的操作见记录。", trace)
            }
            if Task.isCancelled { return result(.cancelled, "请求已取消；已执行的操作见记录。", trace) }
            switch decision {
            case .final(let text):
                let reply = text.trimmingCharacters(in: .whitespacesAndNewlines)
                guard !reply.isEmpty, reply.count <= 2000 else {
                    return result(.protocolError, "模型返回了无效回复。", trace)
                }
                return result(.completed, reply, trace)
            case .tool(let id, let name, let arguments):
                if finalOnly { return result(.limitReached, "本次工具调用已达到上限。", trace) }
                guard !id.isEmpty, id.count <= 128, !id.unicodeScalars.contains(where: CharacterSet.controlCharacters.contains),
                      ids.insert(id).inserted else {
                    return result(.protocolError, "模型返回了重复或无效的工具调用。", trace)
                }
                let code = execute(name: name, arguments: arguments)
                trace.append(MacToolFeedback(name: name, code: code, snapshot: port.snapshot()))
                if code != .applied {
                    let reply = code == .busy ? "桌宠正忙，动作没有执行；请稍后再试。" :
                        "动作没有执行；请查看执行记录。"
                    return result(code == .busy ? .busy : .toolFailure, reply, trace)
                }
            }
        }
        return result(.limitReached, "本次请求达到模型决策上限；已执行的操作见记录。", trace)
    }
}
