import AppKit

@MainActor final class MacAgentUI {
    var onSend: ((String) -> Void)?
    var onCancel: (() -> Void)?
    var onSetKey: (() -> Void)?

    private let session: MacConversationSession
    private let panel: NSPanel
    private let input = NSTextView()
    private let output = NSTextView()
    private let status = NSTextField(labelWithString: "记住最近 6 轮对话；退出应用后清空。")
    private let sendButton = NSButton(title: "发送给玉子", target: nil, action: nil)
    private let cancelButton = NSButton(title: "取消请求", target: nil, action: nil)

    private let clearButton = NSButton(title: "清空对话", target: nil, action: nil)

    init(session: MacConversationSession) {
        self.session = session
        panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 480, height: 420),
                        styleMask: [.titled, .closable, .miniaturizable],
                        backing: .buffered, defer: false)
        panel.title = "和玉子说话"
        panel.isReleasedWhenClosed = false
        panel.level = .floating
        panel.center()
        guard let root = panel.contentView else { return }

        let prompt = NSTextField(labelWithString: "告诉玉子你想做什么")
        prompt.font = .boldSystemFont(ofSize: 14)
        prompt.frame = NSRect(x: 20, y: 378, width: 440, height: 22)
        root.addSubview(prompt)

        let inputScroll = NSScrollView(frame: NSRect(x: 20, y: 290, width: 440, height: 80))
        inputScroll.hasVerticalScroller = true
        inputScroll.borderType = .bezelBorder
        input.frame = inputScroll.contentView.bounds
        inputScroll.documentView = input
        input.isVerticallyResizable = true
        input.isHorizontallyResizable = false
        input.autoresizingMask = [.width]
        input.textContainer?.widthTracksTextView = true
        input.font = .systemFont(ofSize: 14)
        input.string = ""
        root.addSubview(inputScroll)

        sendButton.frame = NSRect(x: 20, y: 247, width: 116, height: 32)
        sendButton.target = self
        sendButton.action = #selector(send)
        root.addSubview(sendButton)
        cancelButton.frame = NSRect(x: 140, y: 247, width: 100, height: 32)
        cancelButton.target = self
        cancelButton.action = #selector(cancel)
        cancelButton.isEnabled = false
        root.addSubview(cancelButton)
        clearButton.frame = NSRect(x: 238, y: 247, width: 90, height: 32)
        clearButton.target = self
        clearButton.action = #selector(clearConversation)
        root.addSubview(clearButton)
        let keyButton = NSButton(title: "设置 API Key…", target: self, action: #selector(setKey))
        keyButton.frame = NSRect(x: 328, y: 247, width: 132, height: 32)
        root.addSubview(keyButton)

        status.frame = NSRect(x: 20, y: 216, width: 440, height: 22)
        status.textColor = .secondaryLabelColor
        root.addSubview(status)

        let outputScroll = NSScrollView(frame: NSRect(x: 20, y: 20, width: 440, height: 188))
        outputScroll.hasVerticalScroller = true
        outputScroll.borderType = .bezelBorder
        output.frame = outputScroll.contentView.bounds
        outputScroll.documentView = output
        output.isEditable = false
        output.isSelectable = true
        output.isVerticallyResizable = true
        output.isHorizontallyResizable = false
        output.autoresizingMask = [.width]
        output.textContainer?.widthTracksTextView = true
        output.font = .systemFont(ofSize: 13)
        output.string = "模型回复和实际动作结果会显示在这里。"
        root.addSubview(outputScroll)
        renderConversation()
    }

    func show() {
        renderConversation()
        NSApplication.shared.activate(ignoringOtherApps: true)
        panel.makeKeyAndOrderFront(nil)
        panel.makeFirstResponder(input)
    }

    func showStatus(_ message: String) {
        status.stringValue = message
        show()
    }

    func setRunning(_ running: Bool) {
        sendButton.isEnabled = !running
        input.isEditable = !running
        cancelButton.isEnabled = running
        clearButton.isEnabled = !running
        if running {
            status.stringValue = "正在等待模型…"

        }
    }

    func showResult(_ result: MacAgentResult) {
        status.stringValue = "请求状态：\(label(result.code))"
        renderConversation()
        if result.code == .completed { input.string = "" }
    }

    private func renderConversation() {
        output.string = session.turns.map { turn in
            var lines = ["你：\(turn.input)", "玉子：\(turn.reply)", "状态：\(label(turn.status))", "实际动作："]
            if turn.tools.isEmpty { lines.append("无") }
            for item in turn.tools { lines.append("\(item.name)：\(toolLabel(item.code))") }
            return lines.joined(separator: "\n")
        }.joined(separator: "\n\n────────\n\n")
        if session.turns.isEmpty { output.string = "对话只在本次运行期间保留，最近内容会随追问发送给模型。" }
        output.scrollToEndOfDocument(nil)
    }

    @objc private func clearConversation() {
        guard session.clear() else { return }
        input.string = ""
        renderConversation()
        status.stringValue = "对话已清空，下一条消息将开始新会话。"
    }

    @objc private func send() {
        guard !session.isRunning else { return }
        let request = input.string.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !request.isEmpty, request.count <= 1000 else {
            status.stringValue = "请输入 1–1000 字的请求。"
            return
        }
        onSend?(request)
    }
    @objc private func cancel() { onCancel?() }
    @objc private func setKey() { onSetKey?() }

    private func label(_ code: MacAgentCode) -> String {
        switch code {
        case .completed: "完成"
        case .busy: "桌宠忙碌"
        case .cancelled: "已取消"
        case .invalidRequest: "请求无效"
        case .modelUnavailable: "模型不可用"
        case .modelTimeout: "模型超时"
        case .protocolError: "模型回复无效"
        case .toolFailure: "动作未全部完成"
        case .limitReached: "达到调用上限"
        }
    }
    private func toolLabel(_ code: MacToolCode) -> String {
        switch code {
        case .applied: "已执行"
        case .busy: "忙碌，未执行"
        case .malformedArguments: "参数无效，未执行"
        case .unknownTool: "未知工具，未执行"
        case .executionUnknown: "执行状态未知"
        case .shuttingDown: "应用正在退出，未执行"
        }
    }
}
