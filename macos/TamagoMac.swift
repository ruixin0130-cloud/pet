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

    init(resources: URL) throws {
        let pet = resources.appendingPathComponent("tamago", isDirectory: true)
        manifest = try JSONDecoder().decode(PetManifest.self,
            from: Data(contentsOf: pet.appendingPathComponent("manifest.json")))
        profile = try JSONDecoder().decode(PetProfile.self,
            from: Data(contentsOf: pet.appendingPathComponent("profile.json")))
        var loaded: [String: CGImage] = [:]
        for (key, filename) in manifest.images {
            guard !filename.contains("/") && !filename.contains("\\") && !filename.contains("..") else {
                throw CocoaError(.fileReadInvalidFileName)
            }
            let url = pet.appendingPathComponent(filename)
            guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
                  let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else {
                throw CocoaError(.fileReadCorruptFile)
            }
            loaded[key] = image
        }
        atlases = loaded
    }

    func frames(named name: String, interaction: Bool = false) -> [SpriteFrame]? {
        let key = (interaction ? "interaction:" : "action:") + name
        if let cached = cache[key] { return cached }
        guard let clip = (interaction ? manifest.interactions : manifest.actions)[name] else { return nil }
        var result: [SpriteFrame] = []
        for frame in clip.frames {
            guard frame.rect.count == 4, frame.time > 0,
                  let atlas = atlases[frame.image] else { return nil }
            let values = frame.rect
            guard values[0] >= 0, values[1] >= 0, values[2] > 0, values[3] > 0,
                  values[0] + values[2] <= atlas.width,
                  values[1] + values[3] <= atlas.height else { return nil }
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
        if abs(dx) + abs(dy) > 4 { dragged = true }
        window?.setFrameOrigin(NSPoint(x: windowStart.x + dx, y: windowStart.y + dy))
    }
    override func mouseUp(with event: NSEvent) {
        if !dragged { onPet?() }
    }
    override func rightMouseDown(with event: NSEvent) {
        if let menu = menu { NSMenu.popUpContextMenu(menu, with: event, for: self) }
    }
}

private final class PetController {
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
        window.contentView = root
        setAction("Idle")
        moveHome()
        window.orderFrontRegardless()
        speak(sprites.profile.welcome, for: 5)
        timer = Timer.scheduledTimer(withTimeInterval: 0.05, repeats: true) { [weak self] _ in
            self?.tick()
        }
    }

    func moveHome() {
        guard let screen = NSScreen.main ?? NSScreen.screens.first else { return }
        let area = screen.visibleFrame
        window.setFrameOrigin(NSPoint(x: area.maxX - window.frame.width - 24,
                                      y: area.minY + 24))
        window.orderFrontRegardless()
    }
    func setAction(_ name: String) {
        guard let next = sprites.frames(named: name) else { return }
        frames = next
        elapsed = 0
        returnToIdle = false
        view.sprite = next[0].image
    }
    func pet() {
        guard let next = sprites.frames(named: "Petted", interaction: true) else { return }
        frames = next
        elapsed = 0
        returnToIdle = true
        view.sprite = next[0].image
        speak(sprites.profile.interactions["Petted"]?.line ?? "呼噜呼噜～", for: 3)
    }
    func stop() { timer?.invalidate(); timer = nil }

    private func speak(_ text: String, for seconds: TimeInterval) {
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
            if returnToIdle { setAction("Idle"); return }
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

private final class PetAppDelegate: NSObject, NSApplicationDelegate {
    private var controller: PetController?
    private var statusItem: NSStatusItem?

    func applicationDidFinishLaunching(_ notification: Notification) {
        let menu = NSMenu()
        menu.addItem(NSMenuItem(title: "摸摸玉子", action: #selector(pet), keyEquivalent: ""))
        menu.addItem(NSMenuItem(title: "安静坐下", action: #selector(sit), keyEquivalent: ""))
        menu.addItem(NSMenuItem(title: "睡觉", action: #selector(sleep), keyEquivalent: ""))
        menu.addItem(NSMenuItem(title: "恢复待机", action: #selector(idle), keyEquivalent: ""))
        menu.addItem(.separator())
        menu.addItem(NSMenuItem(title: "找回玉子", action: #selector(home), keyEquivalent: ""))
        menu.addItem(NSMenuItem(title: "退出玉子", action: #selector(quit), keyEquivalent: ""))
        for item in menu.items { item.target = self }
        do {
            guard let resources = Bundle.main.resourceURL else { throw CocoaError(.fileNoSuchFile) }
            let sprites = try SpriteLibrary(resources: resources)
            controller = try PetController(sprites: sprites, menu: menu)
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
    func applicationWillTerminate(_ notification: Notification) { controller?.stop() }
    @objc private func pet() { controller?.pet() }
    @objc private func sit() { controller?.setAction("Sit") }
    @objc private func sleep() { controller?.setAction("Sleep") }
    @objc private func idle() { controller?.setAction("Idle") }
    @objc private func home() { controller?.moveHome() }
    @objc private func quit() { NSApplication.shared.terminate(nil) }
}

let app = NSApplication.shared
private let delegate = PetAppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
