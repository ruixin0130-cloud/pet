import AppKit

@MainActor private func check(_ value: @autoclosure () -> Bool, _ message: String) {
    precondition(value(), message)
}

@MainActor private func descendants(_ view: NSView) -> [NSView] {
    [view] + view.subviews.flatMap { descendants($0) }
}

// An isolated AppKit window with synthetic outcomes; never loads credentials or calls a model.
@main struct MacAgentUITests {
    @MainActor static func main() throws {
        let app = NSApplication.shared
        app.setActivationPolicy(.accessory)
        let session = MacConversationSession()
        let ui = MacAgentUI(session: session)
        ui.show()
        let panel = app.windows.first { $0.title == "和玉子说话" }!
        let root = panel.contentView!
        let views = descendants(root)
        let buttons = views.compactMap { $0 as? NSButton }
        let send = buttons.first { $0.title == "发送给玉子" }!
        let clear = buttons.first { $0.title == "清空对话" }!
        let cancel = buttons.first { $0.title == "取消请求" }!
        let textViews = views.compactMap { $0 as? NSTextView }
        let input = textViews.first { $0.isEditable }!
        let output = textViews.first { !$0.isEditable }!
        var pending: MacConversationRequest?
        var submissions = 0
        var cancelled = false
        ui.onSend = { text in
            guard let request = session.begin(text) else { return }
            pending = request
            submissions += 1
            ui.setRunning(true)
        }
        ui.onCancel = { cancelled = true }
        input.string = "今天有点累"
        send.performClick(nil)
        check(submissions == 1 && !send.isEnabled && !clear.isEnabled && cancel.isEnabled,
              "running UI disables send and clear, enables cancel")
        send.performClick(nil)
        clear.performClick(nil)
        check(submissions == 1 && session.isRunning, "disabled buttons cannot duplicate or clear a request")
        let snapshot = MacPetSnapshot(characterName: "玉子", action: "Sit", interaction: nil, currentSpeech: nil, busy: false)
        let first = MacAgentResult(code: .completed, reply: "那就先休息一会儿吧。", trace: [], snapshot: snapshot)
        session.complete(pending!, result: first)
        ui.showResult(first)
        ui.setRunning(false)
        check(output.string.contains("今天有点累") && output.string.contains("那就先休息一会儿吧。") && input.string.isEmpty,
              "transcript shows the completed exchange and clears successful input")
        let beforeClose = output.string
        panel.close()
        ui.show()
        check(output.string == beforeClose && session.turns.count == 1, "closing and reopening the panel retains memory")
        input.string = "那你陪我休息一下"
        send.performClick(nil)
        check(pending!.history.count == 1 && output.string == beforeClose, "pending follow-up keeps prior transcript visible")
        cancel.performClick(nil)
        check(cancelled, "cancel button reaches application callback")
        let second = MacAgentResult(code: .cancelled, reply: "已取消；坐下已经执行，未继续说话。",
            trace: [MacToolFeedback(name: "set_action", code: .applied, snapshot: snapshot)], snapshot: snapshot)
        session.complete(pending!, result: second)
        ui.showResult(second)
        ui.setRunning(false)
        check(output.string.contains("今天有点累") && output.string.contains("那你陪我休息一下") &&
              output.string.contains("set_action：已执行") && output.string.contains("已取消") && clear.isEnabled,
              "transcript retains both turns and truthful partial action results")
        root.layoutSubtreeIfNeeded()
        if let file = CommandLine.arguments.dropFirst().first,
           let bitmap = root.bitmapImageRepForCachingDisplay(in: root.bounds) {
            root.cacheDisplay(in: root.bounds, to: bitmap)
            try bitmap.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: file))
        }
        clear.performClick(nil)
        check(session.turns.isEmpty && !output.string.contains("今天有点累") && !output.string.contains("set_action") &&
              input.string.isEmpty, "clear removes visible transcript and model history together")
        input.string = "新会话"
        send.performClick(nil)
        check(pending!.history.isEmpty, "first request after clear cannot see old context")
        check(MacConversationSession().turns.isEmpty, "a fresh application session has no prior memory")
        panel.close()
        print("Mac Agent UI tests passed")
    }
}
