import Foundation
import Security

enum MacAPIKeyStore {
    private static let service = "cloud.ruixin0130.tamago.mac.qwen"
    private static let account = "dashscope-beijing"

    private static var query: [String: Any] {
        [kSecClass as String: kSecClassGenericPassword,
         kSecAttrService as String: service,
         kSecAttrAccount as String: account]
    }

    static func load() throws -> String? {
        var search = query
        search[kSecReturnData as String] = true
        search[kSecMatchLimit as String] = kSecMatchLimitOne
        var item: CFTypeRef?
        let status = SecItemCopyMatching(search as CFDictionary, &item)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = item as? Data,
              let key = String(data: data, encoding: .utf8) else {
            throw NSError(domain: "TamagoKeychain", code: Int(status))
        }
        return key
    }

    static func save(_ raw: String) throws {
        let key = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !key.isEmpty else { throw NSError(domain: "TamagoKeychain", code: -1) }
        let data = Data(key.utf8)
        let status = SecItemUpdate(query as CFDictionary,
                                   [kSecValueData as String: data] as CFDictionary)
        if status == errSecItemNotFound {
            var item = query
            item[kSecValueData as String] = data
            let added = SecItemAdd(item as CFDictionary, nil)
            guard added == errSecSuccess else {
                throw NSError(domain: "TamagoKeychain", code: Int(added))
            }
        } else if status != errSecSuccess {
            throw NSError(domain: "TamagoKeychain", code: Int(status))
        }
    }

    static func remove() throws {
        let status = SecItemDelete(query as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw NSError(domain: "TamagoKeychain", code: Int(status))
        }
    }
}

@MainActor final class MacQwenAdapter: MacAgentModel {
    private static let endpoint = URL(string: "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions")!
    private let apiKey: String

    init(apiKey: String) { self.apiKey = apiKey }

    private static let tools: [[String: Any]] = [
        ["type": "function", "function": [
            "name": "set_action", "description": "设置桌宠动作；只允许待机、坐下、趴下或睡觉。",
            "parameters": ["type": "object", "properties": [
                "action": ["type": "string", "enum": ["Idle", "Sit", "Lie", "Sleep"]]],
                "required": ["action"], "additionalProperties": false]]],
        ["type": "function", "function": [
            "name": "play_interaction", "description": "播放一次互动；不能模拟用户摸摸。",
            "parameters": ["type": "object", "properties": [
                "interaction": ["type": "string", "enum": ["Curious", "PlayYarn", "Pout", "Excited"]]],
                "required": ["interaction"], "additionalProperties": false]]],
        ["type": "function", "function": [
            "name": "speak", "description": "在桌宠气泡中显示短句，最多80字、两行。",
            "parameters": ["type": "object", "properties": [
                "text": ["type": "string", "maxLength": 80]],
                "required": ["text"], "additionalProperties": false]]]
    ]

    func next(input: String, snapshot: MacPetSnapshot, feedback: [MacToolFeedback],
              turn: Int, finalOnly: Bool, timeout: TimeInterval) async throws -> MacModelDecision {
        try Task.checkCancellation()
        let context: [String: Any] = [
            "input": input, "snapshot": snapshot.json,
            "feedback": feedback.map(\.json), "turnNumber": turn, "finalOnly": finalOnly]
        let contextData = try JSONSerialization.data(withJSONObject: context)
        let contextText = String(data: contextData, encoding: .utf8) ?? "{}"
        var payload: [String: Any] = [
            "model": "qwen3.8-flash", "stream": false,
            "enable_thinking": false, "parallel_tool_calls": false,
            "max_tokens": 512, "tool_choice": finalOnly ? "none" : "auto",
            "messages": [
                ["role": "system", "content": "你是桌宠玉子的助手。用户要求改变桌宠状态时必须调用提供的工具。只能使用本轮提供的工具，不得声称未执行的动作已经成功。工具反馈是实际结果；Busy 或其他失败必须如实说明。只处理当前请求，不保存对话历史。"],
                ["role": "user", "content": contextText]]]
        if !finalOnly { payload["tools"] = Self.tools }
        var request = URLRequest(url: Self.endpoint)
        request.httpMethod = "POST"
        request.timeoutInterval = max(1, min(timeout, 30))
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
        request.httpBody = try JSONSerialization.data(withJSONObject: payload)
        let (data, response) = try await URLSession.shared.data(for: request)
        try Task.checkCancellation()
        guard let http = response as? HTTPURLResponse else { throw MacModelError.invalidResponse }
        guard (200..<300).contains(http.statusCode) else { throw MacModelError.httpStatus(http.statusCode) }
        guard data.count <= 1_048_576 else { throw MacModelError.responseTooLarge }
        return try Self.parse(data)
    }

    static func parse(_ data: Data) throws -> MacModelDecision {
        guard let root = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let choices = root["choices"] as? [[String: Any]], choices.count == 1,
              let choice = choices.first,
              let message = choice["message"] as? [String: Any],
              message["role"] as? String == "assistant",
              let finish = choice["finish_reason"] as? String else {
            throw MacModelError.invalidResponse
        }
        if finish == "stop" {
            guard message["tool_calls"] == nil,
                  let text = message["content"] as? String,
                  !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                throw MacModelError.invalidResponse
            }
            return .final(text)
        }
        guard finish == "tool_calls",
              let calls = message["tool_calls"] as? [[String: Any]], calls.count == 1,
              let call = calls.first, call["type"] as? String == "function",
              let id = call["id"] as? String,
              let function = call["function"] as? [String: Any],
              let name = function["name"] as? String,
              let argumentText = function["arguments"] as? String,
              argumentText.utf8.count <= 2048,
              let arguments = (try? JSONSerialization.jsonObject(with: Data(argumentText.utf8))) as? [String: Any] else {
            throw MacModelError.invalidResponse
        }
        return .tool(id: id, name: name, arguments: arguments)
    }
}
