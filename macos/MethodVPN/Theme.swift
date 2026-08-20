import SwiftUI
import AppKit

// MARK: - Дизайн-токены

enum MethodTheme {

    /// Ширина боковых колонок — сайдбара и панели локаций. Одна константа на обе
    /// намеренно: заголовок окна центрируется по всему окну, а герой — по
    /// средней колонке, и при разной ширине боковин их оси расходятся на
    /// половину разницы. Было 200 и 280, кнопка уезжала влево на 40 пунктов.
    ///
    /// При минимальном окне 940 средней колонке остаётся 940 − 240·2 = 460 —
    /// безель героя (240) умещается с запасом.
    static let sideColumnWidth: CGFloat = 240
    // Фон
    static let background = Color(red: 0.04, green: 0.04, blue: 0.04)
    static let backgroundTop = Color(red: 0.051, green: 0.051, blue: 0.051)   // #0d0d0d
    static let backgroundBottom = Color(red: 0.024, green: 0.024, blue: 0.024) // #060606

    // Поверхности / стекло
    static let surface = Color.white.opacity(0.04)
    static let surfaceRaised = Color.white.opacity(0.06)
    static let glassStroke = Color.white.opacity(0.08)
    static let glassHighlight = Color.white.opacity(0.12)
    static let hover = Color.white.opacity(0.06)

    // Текст
    static let textPrimary = Color.white
    static let textSecondary = Color.white.opacity(0.42)
    static let textMuted = Color.white.opacity(0.28)

    // Акценты
    static let accent = Color(red: 0.18, green: 0.80, blue: 0.30)
    static let connected = Color(red: 0.16, green: 0.79, blue: 0.25)
    static let trafficRed = Color(red: 1, green: 0.37, blue: 0.34)
    static let trafficYellow = Color(red: 1, green: 0.74, blue: 0.18)
    static let trafficGreen = Color(red: 0.16, green: 0.79, blue: 0.25)

    /// Подложка окна. Раньше это была сплошная заливка градиентом — из-за неё
    /// окно было непрозрачным, и стеклу нечего было преломлять. Теперь системный
    /// материал пропускает обои, а тонкая затемняющая вуаль поверх держит
    /// контраст текста на светлых обоях.
    static var windowBackground: some View {
        ZStack {
            WindowMaterial(material: .underWindowBackground, blending: .behindWindow)
            Color.black.opacity(0.16)
        }
    }
}

// MARK: - Фон приложения

struct AppBackground: View {
    var accentGlow: Double = 0          // усиление центрального свечения (0…1)
    var tint: Color = .white

    var body: some View {
        ZStack {
            // Подложку рисует корень окна; здесь только ореол за героем, иначе
            // материал вложится в материал и окно снова станет глухим.

            // Мягкий ореол за героем: он даёт стеклу что преломлять и оживляет
            // центр экрана. Намеренно слабый — фон остаётся нейтральным.
            RadialGradient(
                colors: [Color.white.opacity(0.06 + accentGlow * 0.5), .clear],
                center: .init(x: 0.5, y: 0.34),
                startRadius: 0,
                endRadius: 420
            )
            .blur(radius: 50)
            .animation(.easeInOut(duration: 1.4), value: accentGlow)
        }
        .ignoresSafeArea()
    }
}

// MARK: - Liquid Glass

struct GlassPanel<Content: View>: View {
    var cornerRadius: CGFloat = 12
    @ViewBuilder var content: () -> Content

    var body: some View {
        content()
            .background {
                RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                    .fill(.ultraThinMaterial)
                    .overlay {
                        RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                            .stroke(MethodTheme.glassStroke, lineWidth: 0.5)
                    }
            }
    }
}

/// Матовая стеклянная карточка (Liquid Glass): размытие, светящийся верхний кант, мягкая тень.
struct GlassCard<Content: View>: View {
    var cornerRadius: CGFloat = 20
    var padding: CGFloat = 16
    @ViewBuilder var content: () -> Content

    var body: some View {
        content()
            .padding(padding)
            .background {
                ZStack {
                    RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                        .fill(.ultraThinMaterial)
                    RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                        .fill(Color.white.opacity(0.03))
                }
            }
            .overlay {
                RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                    .strokeBorder(
                        LinearGradient(colors: [Color.white.opacity(0.22), Color.white.opacity(0.04)],
                                       startPoint: .top, endPoint: .bottom),
                        lineWidth: 0.6)
            }
            .clipShape(RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
            .shadow(color: .black.opacity(0.32), radius: 18, y: 10)
    }
}

/// Карточка-группа для настроек (матовое стекло, строки внутри с разделителями).
struct GroupCard<Content: View>: View {
    @ViewBuilder var content: () -> Content
    var body: some View {
        VStack(spacing: 0) { content() }
            .background(
                ZStack {
                    RoundedRectangle(cornerRadius: 16, style: .continuous).fill(.ultraThinMaterial)
                    RoundedRectangle(cornerRadius: 16, style: .continuous).fill(Color.white.opacity(0.02))
                }
            )
            .overlay(
                RoundedRectangle(cornerRadius: 16, style: .continuous)
                    .strokeBorder(
                        LinearGradient(colors: [Color.white.opacity(0.16), Color.white.opacity(0.04)],
                                       startPoint: .top, endPoint: .bottom),
                        lineWidth: 0.6)
            )
            .clipShape(RoundedRectangle(cornerRadius: 16, style: .continuous))
            .shadow(color: .black.opacity(0.25), radius: 14, y: 8)
    }
}

struct RadialGlow: View {
    var intensity: Double
    var tint: Color = .white

    var body: some View {
        RadialGradient(
            colors: [tint.opacity(intensity), .clear],
            center: .center, startRadius: 0, endRadius: 300
        )
        .animation(.easeInOut(duration: 1.4), value: intensity)
        .allowsHitTesting(false)
    }
}

// MARK: - Пульс-кольца

struct PulseRingsView: View {
    var active: Bool
    var tint: Color = .white

    var body: some View {
        ZStack {
            PulseRing(delay: 0, tint: tint)
            PulseRing(delay: 1, tint: tint)
            PulseRing(delay: 2, tint: tint)
        }
        .frame(width: 240, height: 240)
        .opacity(active ? 1 : 0)
        .animation(.easeInOut(duration: 0.9), value: active)
        .allowsHitTesting(false)
    }
}

private struct PulseRing: View {
    let delay: Double
    var tint: Color
    @State private var scale: CGFloat = 0.7
    @State private var opacity: Double = 0.5

    var body: some View {
        Circle()
            .strokeBorder(tint.opacity(opacity), lineWidth: delay == 0 ? 1 : 0.5)
            .scaleEffect(scale)
            .onAppear { animate() }
            .onChange(of: delay) { _ in animate() }
    }

    private func animate() {
        scale = 0.7
        opacity = delay == 0 ? 0.18 : (delay == 1 ? 0.1 : 0.05)
        withAnimation(.easeOut(duration: 3).repeatForever(autoreverses: false).delay(delay)) {
            scale = 1.5
            opacity = 0
        }
    }
}

// MARK: - Анимированный орб вокруг кнопки подключения

/// Слои за кнопкой подключения. Читается как безель точного прибора:
/// кольцо рисок → трек → светящаяся дуга состояния → мягкое гало.
/// Риски дают ощущение измерительного инструмента — именно эта деталь
/// отличает «дорогой» интерфейс от просто тёмного с подсветкой.
struct ConnectAura: View {
    let isConnected: Bool
    let isConnecting: Bool

    @State private var breath = false
    @State private var spin = false

    // Размеры подобраны под МИНИМАЛЬНОЕ окно: на центральную панель остаётся
    // ~460pt (940 − сайдбар 200 − локации 280), минус отступы. Безель обязан
    // умещаться туда с запасом, иначе он упирается в края и обрезается.
    private let bezel: CGFloat = 240
    private let ring: CGFloat = 200

    var body: some View {
        ZStack {
            // Дышащее гало. Намеренно слабое: цвет должен читаться как акцент,
            // а не заливать сцену — иначе безель тонет и вид становится «неоновым».
            Circle()
                .fill(RadialGradient(colors: [MethodTheme.connected.opacity(0.16), .clear],
                                     center: .center, startRadius: 32, endRadius: 150))
                .frame(width: 300, height: 300)
                .scaleEffect(breath ? 1.05 : 0.93)
                .opacity(isConnected ? 1 : 0)
                .blur(radius: 32)

            TickBezel(active: isConnected)
                .frame(width: bezel, height: bezel)

            // Постоянный трек: кольцо видно всегда, а не только при подключении.
            Circle()
                .strokeBorder(Color.white.opacity(0.055), lineWidth: 1)
                .frame(width: ring, height: ring)

            // Светящееся кольцо с вращающимся бликом (подключено).
            Circle()
                .strokeBorder(
                    AngularGradient(
                        gradient: Gradient(colors: [
                            MethodTheme.connected.opacity(0),
                            MethodTheme.connected.opacity(0.9),
                            MethodTheme.connected.opacity(0.12),
                            MethodTheme.connected.opacity(0),
                        ]),
                        center: .center),
                    lineWidth: 1.6)
                .frame(width: ring, height: ring)
                .rotationEffect(.degrees(spin ? 360 : 0))
                .opacity(isConnected ? 1 : 0)
                .shadow(color: MethodTheme.connected.opacity(0.45), radius: 9)

            // Дуга подключения.
            Circle()
                .trim(from: 0, to: 0.16)
                .stroke(MethodTheme.trafficYellow.opacity(0.95),
                        style: StrokeStyle(lineWidth: 2, lineCap: .round))
                .frame(width: ring, height: ring)
                .rotationEffect(.degrees(spin ? 360 : 0))
                .opacity(isConnecting ? 1 : 0)

            PulseRingsView(active: isConnected, tint: MethodTheme.connected)
        }
        .frame(width: 300, height: 300)
        .allowsHitTesting(false)
        .animation(.easeInOut(duration: 0.7), value: isConnected)
        .animation(.easeInOut(duration: 0.4), value: isConnecting)
        .onAppear {
            withAnimation(.easeInOut(duration: 3.4).repeatForever(autoreverses: true)) { breath = true }
            withAnimation(.linear(duration: 7).repeatForever(autoreverses: false)) { spin = true }
        }
    }
}

/// Кольцо рисок вокруг кнопки: каждая шестая — длинная и ярче.
private struct TickBezel: View {
    let active: Bool

    var body: some View {
        Canvas { context, size in
            let center = CGPoint(x: size.width / 2, y: size.height / 2)
            let outer = min(size.width, size.height) / 2
            let count = 72
            for i in 0..<count {
                let angle = (Double(i) / Double(count)) * 2 * .pi - .pi / 2
                let isMajor = i % 6 == 0
                let inner = outer - (isMajor ? 9 : 5)
                var path = Path()
                path.move(to: CGPoint(x: center.x + cos(angle) * inner,
                                      y: center.y + sin(angle) * inner))
                path.addLine(to: CGPoint(x: center.x + cos(angle) * outer,
                                         y: center.y + sin(angle) * outer))
                let opacity = (isMajor ? 0.20 : 0.09) + (active ? 0.14 : 0)
                context.stroke(path,
                               with: .color(.white.opacity(opacity)),
                               lineWidth: isMajor ? 1.2 : 0.8)
            }
        }
        .allowsHitTesting(false)
    }
}

// MARK: - Плёночное зерно

/// Однократно генерируемая текстура шума. Тайлится по всему окну с очень
/// низкой непрозрачностью: убирает полосы (banding) в больших градиентах
/// и снимает «пластиковость» плоских тёмных заливок.
enum GrainTexture {
    static let shared: CGImage? = make(side: 128)

    private static func make(side: Int) -> CGImage? {
        let bytesPerPixel = 4
        var pixels = [UInt8](repeating: 0, count: side * side * bytesPerPixel)
        for i in stride(from: 0, to: pixels.count, by: bytesPerPixel) {
            let v = UInt8.random(in: 0...255)
            pixels[i] = v
            pixels[i + 1] = v
            pixels[i + 2] = v
            pixels[i + 3] = 255
        }
        return pixels.withUnsafeMutableBytes { raw -> CGImage? in
            guard let base = raw.baseAddress,
                  let context = CGContext(
                    data: base,
                    width: side,
                    height: side,
                    bitsPerComponent: 8,
                    bytesPerRow: side * bytesPerPixel,
                    space: CGColorSpaceCreateDeviceRGB(),
                    bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
                  ) else { return nil }
            return context.makeImage()
        }
    }
}

struct GrainOverlay: View {
    var opacity: Double = 0.032

    var body: some View {
        GeometryReader { geo in
            if let grain = GrainTexture.shared {
                Image(decorative: grain, scale: 1)
                    .resizable(resizingMode: .tile)
                    .frame(width: geo.size.width, height: geo.size.height)
                    .blendMode(.overlay)
                    .opacity(opacity)
            }
        }
        .allowsHitTesting(false)
    }
}

/// Виньетка: слегка притемняет углы, собирая внимание к центру экрана.
struct VignetteOverlay: View {
    var strength: Double = 0.35

    var body: some View {
        RadialGradient(
            colors: [.clear, .black.opacity(strength)],
            center: .center,
            startRadius: 280,
            endRadius: 760
        )
        .allowsHitTesting(false)
        .blendMode(.multiply)
    }
}

/// Индикатор качества канала «палочками» — читается быстрее, чем число в мс.
struct SignalBars: View {
    let ms: Int
    var height: CGFloat = 11

    private var level: Int {
        switch ms {
        case ..<60:  return 4
        case ..<110: return 3
        case ..<180: return 2
        default:     return 1
        }
    }

    private var tint: Color {
        switch level {
        case 4, 3: return MethodTheme.connected
        case 2:    return MethodTheme.trafficYellow
        default:   return MethodTheme.trafficRed
        }
    }

    var body: some View {
        HStack(alignment: .bottom, spacing: 1.5) {
            ForEach(1...4, id: \.self) { index in
                RoundedRectangle(cornerRadius: 0.8, style: .continuous)
                    .fill(index <= level ? tint.opacity(0.95) : Color.white.opacity(0.13))
                    .frame(width: 2.4, height: height * (0.4 + 0.2 * CGFloat(index - 1)))
            }
        }
        .frame(height: height, alignment: .bottom)
        .accessibilityLabel("Задержка \(ms) миллисекунд")
    }
}

// MARK: - Кнопка подключения

struct ConnectButton: View {
    let title: String
    let isConnected: Bool
    let isConnecting: Bool
    let action: () -> Void

    @State private var hovering = false

    /// Круглая — форма согласована с безелем вокруг. Капсула внутри кольца
    /// читалась как конфликт форм; круг превращает узел в цельный «пульт».
    private let diameter: CGFloat = 136

    var body: some View {
        Button(action: action) {
            ZStack {
                Circle().fill(.ultraThinMaterial)

                // Тело кнопки — вертикальный градиент, а не плоская заливка:
                // сверху светлее, снизу темнее, как у выпуклой поверхности.
                Circle().fill(
                    LinearGradient(
                        colors: [buttonFillTop, buttonFillBottom],
                        startPoint: .top, endPoint: .bottom
                    )
                )

                // Верхний блик: свет падает сверху, как на физической кнопке.
                Circle()
                    .fill(
                        LinearGradient(
                            colors: [Color.white.opacity(isConnected ? 0.20 : 0.11), .clear],
                            startPoint: .top, endPoint: .center
                        )
                    )
                    .padding(0.5)
                    .blendMode(.plusLighter)
                    .allowsHitTesting(false)

                // Внутренняя тень по нижней кромке — даёт ощущение толщины.
                Circle()
                    .strokeBorder(
                        LinearGradient(
                            colors: [.clear, .black.opacity(0.45)],
                            startPoint: .center, endPoint: .bottom
                        ),
                        lineWidth: 3
                    )
                    .blur(radius: 2.5)
                    .allowsHitTesting(false)

                Circle().strokeBorder(borderColor, lineWidth: 0.8)

                VStack(spacing: 8) {
                    Image(systemName: "power")
                        .font(.system(size: 27, weight: .light))
                        .foregroundStyle(glyphColor)
                        .shadow(color: glyphGlow, radius: 11)
                    // Подпись — всегда нейтральная. Зелёным по зелёному падает
                    // контраст; акцент несут символ питания и кольцо.
                    Text(title.uppercased())
                        .font(.system(size: 9, weight: .semibold))
                        .kerning(1.3)
                        .foregroundStyle(Color.white.opacity(0.72))
                        .contentTransition(.opacity)
                        .lineLimit(1)
                        .minimumScaleFactor(0.7)
                        .frame(width: diameter - 28)
                }
            }
            .frame(width: diameter, height: diameter)
            .shadow(color: shadowColor, radius: isConnected ? 34 : (hovering ? 20 : 14), y: 8)
            .scaleEffect(hovering ? 1.025 : 1)
            .contentShape(Circle())
            .animation(.easeInOut(duration: 0.35), value: isConnected)
            .animation(.spring(response: 0.3, dampingFraction: 0.72), value: hovering)
        }
        .buttonStyle(PressScale())
        .onHover { hovering = $0 }
        .accessibilityLabel(title)
    }

    // Корпус кнопки остаётся нейтральным и в подключённом состоянии —
    // лишь едва теплеет. Цветом «кричит» только глиф и кольцо вокруг.
    private var buttonFillTop: Color {
        if isConnected { return MethodTheme.connected.opacity(hovering ? 0.115 : 0.085) }
        return Color.white.opacity(hovering ? 0.115 : 0.085)
    }
    private var buttonFillBottom: Color {
        if isConnected { return MethodTheme.connected.opacity(0.022) }
        return Color.white.opacity(hovering ? 0.03 : 0.018)
    }
    private var glyphColor: Color {
        if isConnected { return MethodTheme.connected }
        if isConnecting { return MethodTheme.trafficYellow }
        return Color.white.opacity(0.88)
    }
    private var glyphGlow: Color {
        isConnected ? MethodTheme.connected.opacity(0.5) : .clear
    }
    private var borderColor: Color {
        isConnected ? MethodTheme.connected.opacity(0.28) : Color.white.opacity(0.15)
    }
    private var shadowColor: Color {
        isConnected ? MethodTheme.connected.opacity(0.16) : Color.black.opacity(0.45)
    }
}

// MARK: - Стиль «нажатия» (лёгкое сжатие)

struct PressScale: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .scaleEffect(configuration.isPressed ? 0.97 : 1)
            .opacity(configuration.isPressed ? 0.9 : 1)
            .animation(.spring(response: 0.25, dampingFraction: 0.6), value: configuration.isPressed)
    }
}

// MARK: - Переключатель (премиум)

struct MethodToggle: View {
    @Binding var isOn: Bool

    var body: some View {
        ZStack(alignment: isOn ? .trailing : .leading) {
            Capsule()
                .fill(isOn ? AnyShapeStyle(MethodTheme.connected) : AnyShapeStyle(Color.white.opacity(0.12)))
            Circle()
                .fill(.white)
                .padding(4)
                .shadow(color: .black.opacity(0.35), radius: 2, y: 1)
        }
        .frame(width: 46, height: 28)
        .animation(.spring(response: 0.32, dampingFraction: 0.72), value: isOn)
        .contentShape(Capsule())
        .onTapGesture { isOn.toggle() }
    }
}

// MARK: - Строки настроек

/// Заголовок секции (мелкий капс).
struct SectionLabel: View {
    let text: String
    var body: some View {
        Text(text.uppercased())
            .font(.system(size: 10, weight: .medium))
            .kerning(0.9)
            .foregroundStyle(MethodTheme.textMuted)
            .padding(.horizontal, 4)
            .padding(.bottom, 8)
    }
}

/// Универсальная строка в карточке настроек.
struct SettingsRow<Trailing: View>: View {
    let title: String
    var subtitle: String? = nil
    var showDivider: Bool = true
    @ViewBuilder var trailing: () -> Trailing

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 12) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(title)
                        .font(.system(size: 14))
                        .foregroundStyle(Color.white.opacity(0.92))
                    if let subtitle {
                        Text(subtitle)
                            .font(.system(size: 12))
                            .foregroundStyle(MethodTheme.textSecondary)
                    }
                }
                Spacer(minLength: 12)
                trailing()
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 13)

            if showDivider {
                Rectangle().fill(Color.white.opacity(0.06)).frame(height: 0.5)
                    .padding(.leading, 16)
            }
        }
    }
}

// MARK: - Заголовок окна

struct TitleBarView: View {
    var body: some View {
        Text("Method Network")
            .font(.system(size: 13, weight: .medium))
            .foregroundStyle(MethodTheme.textSecondary)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .frame(height: 40)
            .background(WindowDragArea().background(Color.white.opacity(0.02)))
            .overlay(alignment: .bottom) {
                Rectangle().fill(MethodTheme.glassStroke).frame(height: 0.5)
            }
    }
}

/// Делает область перетаскиваемой как заголовок окна (компенсирует hiddenTitleBar).
struct WindowDragArea: NSViewRepresentable {
    func makeNSView(context: Context) -> NSView { DragView() }
    func updateNSView(_ nsView: NSView, context: Context) {}

    private final class DragView: NSView {
        override func mouseDown(with event: NSEvent) {
            window?.performDrag(with: event)
        }
    }
}
