import AppKit
import ImageIO

private struct PetManifest: Decodable {
    struct Frame: Decodable {
        let image: String
        let rect: [Int]
        let time: Double
    }
    struct Clip: Decodable {
        let frames: [Frame]
        let loop: Bool
    }
    let images: [String: String]
    let actions: [String: Clip]
    let interactions: [String: Clip]
}

private struct PetProfile: Decodable {
    struct Interaction: Decodable { let line: String }
    let characterName: String
    let welcome: String
    let interactions: [String: Interaction]
}

private struct SpriteFrame {
    let image: NSImage
    let duration: TimeInterval
}

private final class SpriteLibrary {
    let manifest: PetManifest
    let profile: PetProfile
    private let atlases: [String: CGImage]
    private var cache: [String: [SpriteFrame]] = [:]

    private static func boundedData(at url: URL) throws -> Data {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        let data = try handle.read(upToCount: 1_048_577) ?? Data()
        guard data.count <= 1_048_576 else { throw CocoaError(.fileReadTooLarge) }
        return data
    }

    private static func pngDimensions(at url: URL) throws -> (Int, Int) {
        let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
        guard let size = attributes[.size] as? NSNumber,
              size.int64Value <= 32 * 1024 * 1024 else { throw CocoaError(.fileReadTooLarge) }
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        let header = try handle.read(upToCount: 24) ?? Data()
        let signature: [UInt8] = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82]
        guard header.count == 24, Array(header.prefix(16)) == signature else {
            throw CocoaError(.fileReadCorruptFile)
        }
        let width = header[16..<20].reduce(0) { $0 * 256 + Int($1) }
        let height = header[20..<24].reduce(0) { $0 * 256 + Int($1) }
        guard (1...8192).contains(width), (1...8192).contains(height) else {
            throw CocoaError(.fileReadCorruptFile)
        }
        return (width, height)
    }

    init(resources: URL) throws {
        let pet = resources.appendingPathComponent("tamago", isDirectory: true)
        manifest = try JSONDecoder().decode(PetManifest.self,
            from: Self.boundedData(at: pet.appendingPathComponent("manifest.json")))
        profile = try JSONDecoder().decode(PetProfile.self,
            from: Self.boundedData(at: pet.appendingPathComponent("profile.json")))
        guard (1...16).contains(manifest.images.count) else { throw CocoaError(.fileReadCorruptFile) }
        var loaded: [String: CGImage] = [:]
        var totalPixels = 0
        for (key, filename) in manifest.images {
            guard filename.lowercased().hasSuffix(".png"), !filename.contains("/"),
                  !filename.contains("\\"), !filename.contains("..") else {
                throw CocoaError(.fileReadInvalidFileName)
            }
            let url = pet.appendingPathComponent(filename)
            let (width, height) = try Self.pngDimensions(at: url)
            totalPixels += width * height
            guard totalPixels <= 32_000_000 else { throw CocoaError(.fileReadTooLarge) }
            guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
                  let image = CGImageSourceCreateImageAtIndex(source, 0, nil),
                  image.width == width, image.height == height else {
                throw CocoaError(.fileReadCorruptFile)
            }
            loaded[key] = image
        }
        atlases = loaded
    }

    func frames(named name: String, interaction: Bool = false) -> [SpriteFrame]? {
        let key = (interaction ? "interaction:" : "action:") + name
        if let cached = cache[key] { return cached }
        guard let clip = (interaction ? manifest.interactions : manifest.actions)[name],
              (1...128).contains(clip.frames.count) else { return nil }
        var result: [SpriteFrame] = []
        for frame in clip.frames {
            guard frame.rect.count == 4, frame.time.isFinite,
                  frame.time > 0, frame.time <= 60,
                  let atlas = atlases[frame.image] else { return nil }
            let values = frame.rect
            guard values[0] >= 0, values[1] >= 0, values[2] > 0, values[3] > 0,
                  values[2] <= atlas.width, values[3] <= atlas.height,
                  values[0] <= atlas.width - values[2],
                  values[1] <= atlas.height - values[3] else { return nil }
            let area = CGRect(x: values[0], y: values[1], width: values[2], height: values[3])
            guard let cropped = atlas.cropping(to: area) else { return nil }
            result.append(SpriteFrame(image: NSImage(cgImage: cropped,
                size: NSSize(width: values[2], height: values[3])), duration: frame.time))
        }
        guard !result.isEmpty else { return nil }
        cache[key] = result
        return result
    }
}

private final class PetView: NSView {
    var sprite: NSImage? { didSet { needsDisplay = true } }
    var onPet: (() -> Void)?
    var onDraggingChanged: ((Bool) -> Void)?
    private var mouseStart = NSPoint.zero
    private var windowStart = NSPoint.zero
    private var dragged = false

    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)
        sprite?.draw(in: bounds.insetBy(dx: 2, dy: 2), from: .zero,
                     operation: .sourceOver, fraction: 1)
    }
    override func mouseDown(with event: NSEvent) {
        mouseStart = NSEvent.mouseLocation
        windowStart = window?.frame.origin ?? .zero
        dragged = false
    }
    override func mouseDragged(with event: NSEvent) {
        let current = NSEvent.mouseLocation
        let dx = current.x - mouseStart.x
        let dy = current.y - mouseStart.y
        if abs(dx) + abs(dy) > 4, !dragged {
            dragged = true
            onDraggingChanged?(true)
        }
        window?.setFrameOrigin(NSPoint(x: windowStart.x + dx, y: windowStart.y + dy))
    }
    override func mouseUp(with event: NSEvent) {
        if dragged { onDraggingChanged?(false) }
        if !dragged { onPet?() }
    }
    override func rightMouseDown(with event: NSEvent) {
        if let menu = menu { NSMenu.popUpContextMenu(menu, with: event, for: self) }
    }
}

@MainActor private final class PetController: MacPetPort {
    let window: NSWindow
    let view: PetView
    private let bubble: NSTextField
    private let sprites: SpriteLibrary
    private var timer: Timer?
    private var frames: [SpriteFrame] = []
    private var elapsed: TimeInterval = 0
    private var lastTick = ProcessInfo.processInfo.systemUptime
    private var bubbleUntil: TimeInterval = 0
    private var returnToIdle = false
    private var actionName = "Idle"
    private var interactionName: String?
    private var dragging = false
    private var stopping = false

    init(sprites: SpriteLibrary, menu: NSMenu) throws {
        self.sprites = sprites
        guard let first = sprites.frames(named: "Idle")?.first else {
            throw CocoaError(.fileReadCorruptFile)
        }
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 220, height: 240),
                          styleMask: [.borderless], backing: .buffered, defer: false)
        window.isOpaque = false
        window.backgroundColor = .clear
        window.hasShadow = false
        window.sharingType = .readOnly
        window.level = .floating
        window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        window.isReleasedWhenClosed = false
        let root = NSView(frame: window.contentView!.bounds)
        view = PetView(frame: NSRect(x: 10, y: 0, width: 200, height: 200))
        view.sprite = first.image
        view.menu = menu
        root.addSubview(view)
        bubble = NSTextField(labelWithString: "")
        bubble.frame = NSRect(x: 10, y: 202, width: 200, height: 34)
        bubble.alignment = .center
        bubble.font = NSFont.systemFont(ofSize: 12)
        bubble.textColor = .darkGray
        bubble.wantsLayer = true
        bubble.layer?.backgroundColor = NSColor(white: 1, alpha: 0.94).cgColor
        bubble.layer?.cornerRadius = 10
        bubble.layer?.borderWidth = 1
        bubble.layer?.borderColor = NSColor(white: 0.85, alpha: 0.9).cgColor
        bubble.isHidden = true
        root.addSubview(bubble)
        view.onPet = { [weak self] in self?.pet() }
        view.onDraggingChanged = { [weak self] value in self?.dragging = value }
        window.contentView = root
        chooseAction("Idle")
        moveHome()
        window.orderFrontRegardless()
        showSpeech(sprites.profile.welcome, for: 5)
        timer = Timer.scheduledTimer(withTimeInterval: 0.05, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick() }
        }
    }

    func moveHome() {
        guard let screen = NSScreen.main ?? NSScreen.screens.first else { return }
        let area = screen.visibleFrame
        window.setFrameOrigin(NSPoint(x: area.maxX - window.frame.width - 24,
                                      y: area.minY + 24))
        window.orderFrontRegardless()
    }
    func chooseAction(_ name: String) {
        guard let next = sprites.frames(named: name) else { return }
        frames = next
        elapsed = 0
        returnToIdle = false
        interactionName = nil
        actionName = name
        view.sprite = next[0].image
    }
    func pet() {
        guard !stopping else { return }
        _ = startInteraction("Petted")
    }
    private func startInteraction(_ name: String) -> Bool {
        guard let next = sprites.frames(named: name, interaction: true) else { return false }
        frames = next
        elapsed = 0
        returnToIdle = true
        interactionName = name
        view.sprite = next[0].image
        if let line = sprites.profile.interactions[name]?.line {
            showSpeech(line, for: 3)
        }
        return true
    }
    func stop() { stopping = true; timer?.invalidate(); timer = nil }

    func snapshot() -> MacPetSnapshot {
        MacPetSnapshot(characterName: sprites.profile.characterName, action: actionName,
                       interaction: interactionName,
                       currentSpeech: bubble.isHidden ? nil : bubble.stringValue,
                       busy: dragging || interactionName != nil || !bubble.isHidden)
    }
    private func agentReady() -> MacToolCode? {
        if stopping { return .shuttingDown }
        if snapshot().busy { return .busy }
        return nil
    }
    func setAction(_ action: String) -> MacToolCode {
        if let code = agentReady() { return code }
        guard ["Idle", "Sit", "Lie", "Sleep"].contains(action),
              sprites.frames(named: action) != nil else { return .malformedArguments }
        chooseAction(action)
        return .applied
    }
    func playInteraction(_ name: String) -> MacToolCode {
        if let code = agentReady() { return code }
        guard ["Curious", "PlayYarn", "Pout", "Excited"].contains(name),
              startInteraction(name) else { return .malformedArguments }
        return .applied
    }
    func speak(_ text: String) -> MacToolCode {
        if let code = agentReady() { return code }
        showSpeech(text, for: 6)
        return .applied
    }

    private func showSpeech(_ text: String, for seconds: TimeInterval) {
        bubble.stringValue = text.replacingOccurrences(of: "\n", with: " ")
        bubble.isHidden = false
        bubbleUntil = ProcessInfo.processInfo.systemUptime + seconds
    }
    private func tick() {
        let now = ProcessInfo.processInfo.systemUptime
        let delta = min(max(now - lastTick, 0), 0.2)
        lastTick = now
        if now >= bubbleUntil { bubble.isHidden = true }
        guard !frames.isEmpty else { return }
        elapsed += delta
        let total = frames.reduce(0) { $0 + $1.duration }
        if elapsed >= total {
            if returnToIdle { chooseAction("Idle"); return }
            elapsed = elapsed.truncatingRemainder(dividingBy: total)
        }
        var time = elapsed
        for frame in frames {
            if time < frame.duration { view.sprite = frame.image; return }
            time -= frame.duration
        }
        view.sprite = frames.last?.image
    }
}

@MainActor private final class PetAppDelegate: NSObject, NSApplicationDelegate {
    private var controller: PetController?
    private var statusItem: NSStatusItem?
    private var agentUI: MacAgentUI?
    private var agentTask: Task<Void, Never>?

    func applicationDidFinishLaunching(_ notification: Notification) {
        let menu = NSMenu()
        menu.addItem(NSMenuItem(title: "摸摸玉子", action: #selector(pet), keyEquivalent: ""))
        menu.addItem(NSMenuItem(title: "安静坐下", action: #selector(sit), keyEquivalent: ""))
        menu.addItem(NSMenuItem(title: "睡觉", action: #selector(sleep), keyEquivalent: ""))
        menu.addItem(NSMenuItem(title: "恢复待机", action: #selector(idle), keyEquivalent: ""))
        menu.addItem(.separator())
        menu.addItem(NSMenuItem(title: "和玉子说话…", action: #selector(openAgent), keyEquivalent: ""))
        menu.addItem(NSMenuItem(title: "设置百炼 API Key…", action: #selector(setAPIKey), keyEquivalent: ""))
        menu.addItem(.separator())
        menu.addItem(NSMenuItem(title: "找回玉子", action: #selector(home), keyEquivalent: ""))
        menu.addItem(NSMenuItem(title: "退出玉子", action: #selector(quit), keyEquivalent: ""))
        for item in menu.items { item.target = self }
        do {
            guard let resources = Bundle.main.resourceURL else { throw CocoaError(.fileNoSuchFile) }
            let sprites = try SpriteLibrary(resources: resources)
            controller = try PetController(sprites: sprites, menu: menu)
            agentUI = MacAgentUI()
            agentUI?.onSend = { [weak self] text in self?.sendToAgent(text) }
            agentUI?.onCancel = { [weak self] in self?.agentTask?.cancel() }
            agentUI?.onSetKey = { [weak self] in self?.setAPIKey() }
            statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
            statusItem?.button?.title = "🐈 玉子"
            statusItem?.menu = menu
            NSApplication.shared.applicationIconImage = NSImage(contentsOf: resources.appendingPathComponent("AppIcon.png"))
        } catch {
            let alert = NSAlert()
            alert.messageText = "玉子无法启动"
            alert.informativeText = "请重新运行 macos/build.sh。\n\(error.localizedDescription)"
            alert.runModal()
            NSApplication.shared.terminate(nil)
        }
    }
    func applicationWillTerminate(_ notification: Notification) {
        agentTask?.cancel()
        controller?.stop()
    }
    @objc private func pet() { controller?.pet() }
    @objc private func sit() { controller?.chooseAction("Sit") }
    @objc private func sleep() { controller?.chooseAction("Sleep") }
    @objc private func idle() { controller?.chooseAction("Idle") }
    @objc private func home() { controller?.moveHome() }
    @objc private func quit() { NSApplication.shared.terminate(nil) }
    @objc private func openAgent() { agentUI?.show() }

    @objc private func setAPIKey() {
        NSApplication.shared.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = "设置百炼 API Key"
        alert.informativeText = "仅保存在本机钥匙串。请使用北京地域的百炼 API Key。"
        let field = NSSecureTextField(frame: NSRect(x: 0, y: 0, width: 300, height: 24))
        field.placeholderString = "API Key"
        alert.accessoryView = field
        alert.addButton(withTitle: "保存")
        alert.addButton(withTitle: "取消")
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        do {
            try MacAPIKeyStore.save(field.stringValue)
            agentUI?.showStatus("API Key 已存入钥匙串。")
        } catch {
            agentUI?.showStatus("API Key 保存失败：\(error.localizedDescription)")
        }
    }

    private func sendToAgent(_ input: String) {
        guard agentTask == nil, let controller, let agentUI else { return }
        let key: String
        do {
            guard let saved = try MacAPIKeyStore.load() else {
                agentUI.showStatus("请先通过菜单设置百炼 API Key。")
                return
            }
            key = saved
        } catch {
            agentUI.showStatus("无法读取钥匙串：\(error.localizedDescription)")
            return
        }
        let runtime = MacAgentRuntime(port: controller, model: MacQwenAdapter(apiKey: key))
        agentUI.setRunning(true)
        agentTask = Task { [weak self] in
            let result = await runtime.run(input)
            guard let self else { return }
            agentUI.showResult(result)
            agentUI.setRunning(false)
            self.agentTask = nil
        }
    }
}

@main struct TamagoApplication {
    static func main() {
        let app = NSApplication.shared
        let delegate = PetAppDelegate()
        app.delegate = delegate
        app.setActivationPolicy(.accessory)
        app.run()
    }
}
