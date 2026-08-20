import AppKit
import Combine
import Foundation
import Network

/// Следит за сном, пробуждением и сменой сети → переподключает VPN.
final class ReconnectManager {
    var onReconnect: (() -> Void)?

    private var pathMonitor: NWPathMonitor?
    private var lastPathSatisfied = true
    private var observers: [NSObjectProtocol] = []

    func start() {
        stop()

        let monitor = NWPathMonitor()
        monitor.pathUpdateHandler = { [weak self] path in
            guard let self else { return }
            let ok = path.status == .satisfied
            if self.lastPathSatisfied == false && ok {
                DispatchQueue.main.async { self.onReconnect?() }
            }
            self.lastPathSatisfied = ok
        }
        monitor.start(queue: DispatchQueue(label: "network.method.vpn.path"))
        pathMonitor = monitor

        let center = NSWorkspace.shared.notificationCenter
        observers.append(center.addObserver(
            forName: NSWorkspace.didWakeNotification, object: nil, queue: .main
        ) { [weak self] _ in self?.onReconnect?() })

        observers.append(center.addObserver(
            forName: NSWorkspace.screensDidWakeNotification, object: nil, queue: .main
        ) { [weak self] _ in self?.onReconnect?() })
    }

    func stop() {
        pathMonitor?.cancel()
        pathMonitor = nil
        let center = NSWorkspace.shared.notificationCenter
        for token in observers { center.removeObserver(token) }
        observers.removeAll()
    }

    deinit { stop() }
}
