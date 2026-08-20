import Foundation

PacketFilterKillSwitch.methodClient.recoverAfterUnexpectedExit()
let delegate = HelperDelegate()
let listener = NSXPCListener(machServiceName: "network.method.client.helper")
listener.delegate = delegate
listener.resume()

print("Method Helper started and listening on XPC")
RunLoop.main.run()
