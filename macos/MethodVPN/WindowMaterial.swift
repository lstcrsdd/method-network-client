import SwiftUI
import AppKit

/// Системная подложка окна: размывает то, что ЗА окном (обои, другие окна).
///
/// Без неё вся затея с Liquid Glass не работает. Стекло — это линза: оно
/// преломляет то, что под ним. Если под ним сплошная чёрная заливка, преломлять
/// нечего, и `glassEffect` выглядит обычным тёмным прямоугольником — ровно то,
/// что и получалось: окно смотрелось плоским тёмным приложением из 2015-го.
///
/// `.behindWindow` заставляет систему брать содержимое позади окна; `NSWindow`
/// у нас уже `isOpaque = false` с прозрачным фоном, так что материал виден.
struct WindowMaterial: NSViewRepresentable {
    var material: NSVisualEffectView.Material = .underWindowBackground
    var blending: NSVisualEffectView.BlendingMode = .behindWindow

    func makeNSView(context: Context) -> NSVisualEffectView {
        let view = NSVisualEffectView()
        view.material = material
        view.blendingMode = blending
        view.state = .active          // активен и когда окно неактивно
        view.isEmphasized = true
        return view
    }

    func updateNSView(_ view: NSVisualEffectView, context: Context) {
        view.material = material
        view.blendingMode = blending
    }
}
