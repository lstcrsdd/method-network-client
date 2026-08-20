import Foundation

/// Ревизия XPC-контракта. Поднимать при ЛЮБОМ изменении набора методов
/// `MethodVPNHelperProtocol` или их сигнатур.
///
/// Зачем: NSXPC молча отбрасывает сообщение с неизвестным селектором — reply-блок
/// не вызывается никогда, и приложение просто зависает до таймаута, не понимая
/// причины. Такое уже случалось: в системе оставался демон из старой сборки,
/// не знавший `setKillSwitch`, и подключение падало по таймауту 35 секунд.
/// Рукопожатие по ревизии ловит это сразу и говорит, что делать.
public let methodVPNHelperProtocolRevision = 2

/// Префикс строки версии, по которому приложение вычитывает ревизию из `getVersion`.
public let methodVPNHelperRevisionPrefix = "proto="

@objc(MethodVPNHelperProtocol)
public protocol MethodVPNHelperProtocol {
    func getVersion(withReply reply: @escaping (String) -> Void)
    func startTunnel(config: String, withReply reply: @escaping (Bool, String?) -> Void)
    func setKillSwitch(
        enabled: Bool,
        interface: String,
        allowedServerHosts: [String],
        withReply reply: @escaping (Bool, String?) -> Void
    )
    func stopTunnel(withReply reply: @escaping (Bool, String?) -> Void)
    func reconnectTunnel(withReply reply: @escaping (Bool, String?) -> Void)
    func isTunnelRunning(withReply reply: @escaping (Bool) -> Void)
}
