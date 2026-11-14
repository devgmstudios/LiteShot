import AppKit

public enum Tool: String, CaseIterable {
    case pen, line, arrow, rect, ellipse, marker, text, counter, pixelate
}

/// One thing drawn on a capture, in view points (top-left origin). It draws itself into the current
/// NSGraphicsContext, which must be flipped: the overlay view and `Renderer` both set one up.
public struct Annotation {
    public enum Kind {
        case pen([CGPoint])
        case line(CGPoint, CGPoint)
        case arrow(CGPoint, CGPoint)
        case rect(CGRect)
        case ellipse(CGRect)
        case marker([CGPoint])
        case text(String, at: CGPoint, size: CGFloat)
        case counter(Int, at: CGPoint)
        /// Pixelated blocks over an area; an oval one covers a face.
        case pixelate(CGRect, oval: Bool)
    }

    public var kind: Kind
    public var color: NSColor
    public var width: CGFloat
    /// For text: font, bold, italic, alignment and look.
    public var style = TextStyle()

    public init(_ kind: Kind, color: NSColor, width: CGFloat, style: TextStyle = TextStyle()) {
        self.kind = kind
        self.color = color
        self.width = width
        self.style = style
    }

    public static func font(size: CGFloat) -> NSFont { .systemFont(ofSize: size, weight: .semibold) }

    var counterRadius: CGFloat { max(12, width * 4) }

    /// Roughly what the annotation covers, for redrawing and for the text caret.
    public var bounds: CGRect {
        switch kind {
        case .pen(let ps), .marker(let ps):
            guard let f = ps.first else { return .null }
            return ps.reduce(CGRect(origin: f, size: .zero)) { $0.union(CGRect(origin: $1, size: .zero)) }
        case .line(let a, let b), .arrow(let a, let b): return Geometry.rect(a, b)
        case .rect(let r), .ellipse(let r), .pixelate(let r, _): return r
        case .text(let s, let at, let size):
            return textBounds(s, at: at, size: size)
        case .counter(_, let at):
            let r = counterRadius
            return CGRect(x: at.x - r, y: at.y - r, width: r * 2, height: r * 2)
        }
    }

    /// Whether a click at `p` lands on the drawing: on the stroke of lines, boxes and circles (so a click inside a box
    /// can still start something else), anywhere on text, numbers and pixelation.
    public func hit(_ p: CGPoint, tolerance: CGFloat) -> Bool {
        let reach = width / 2 + tolerance
        switch kind {
        case .pen(let ps): return Annotation.near(p, ps, reach)
        case .marker(let ps): return Annotation.near(p, ps, max(14, width * 5) / 2 + tolerance)
        case .line(let a, let b), .arrow(let a, let b): return Annotation.near(p, [a, b], reach)
        case .rect(let r):
            return r.insetBy(dx: -reach, dy: -reach).contains(p) && !r.insetBy(dx: reach, dy: reach).contains(p)
        case .ellipse(let r):
            guard r.width > 0, r.height > 0 else { return false }
            // How far out the point is, in units of the radius, compared at the ellipse's edge.
            let dx = (p.x - r.midX) / (r.width / 2), dy = (p.y - r.midY) / (r.height / 2)
            let d = (dx * dx + dy * dy).squareRoot()
            return abs(d - 1) * min(r.width, r.height) / 2 <= reach
        case .text, .counter, .pixelate:
            return bounds.insetBy(dx: -tolerance, dy: -tolerance).contains(p)
        }
    }

    /// The same drawing shifted by `d`.
    public func moved(by d: CGSize) -> Annotation {
        func m(_ p: CGPoint) -> CGPoint { CGPoint(x: p.x + d.width, y: p.y + d.height) }
        var copy = self
        switch kind {
        case .pen(let ps): copy.kind = .pen(ps.map(m))
        case .marker(let ps): copy.kind = .marker(ps.map(m))
        case .line(let a, let b): copy.kind = .line(m(a), m(b))
        case .arrow(let a, let b): copy.kind = .arrow(m(a), m(b))
        case .rect(let r): copy.kind = .rect(r.offsetBy(dx: d.width, dy: d.height))
        case .ellipse(let r): copy.kind = .ellipse(r.offsetBy(dx: d.width, dy: d.height))
        case .text(let s, let at, let size): copy.kind = .text(s, at: m(at), size: size)
        case .counter(let n, let at): copy.kind = .counter(n, at: m(at))
        case .pixelate(let r, let oval): copy.kind = .pixelate(r.offsetBy(dx: d.width, dy: d.height), oval: oval)
        }
        return copy
    }

    /// Whether `p` is within `reach` of the polyline through `ps`.
    static func near(_ p: CGPoint, _ ps: [CGPoint], _ reach: CGFloat) -> Bool {
        guard let first = ps.first else { return false }
        if ps.count == 1 { return hypot(p.x - first.x, p.y - first.y) <= reach }
        for (a, b) in zip(ps, ps.dropFirst()) {
            let dx = b.x - a.x, dy = b.y - a.y, l2 = dx * dx + dy * dy
            let t = l2 == 0 ? 0 : max(0, min(1, ((p.x - a.x) * dx + (p.y - a.y) * dy) / l2))
            if hypot(p.x - (a.x + t * dx), p.y - (a.y + t * dy)) <= reach { return true }
        }
        return false
    }

    /// Draws into the current (flipped) graphics context. `screen` is the frozen capture, whose size in
    /// points is `screenSize`; pixelation reads from it.
    public func draw(screen: CGImage, screenSize: CGSize) {
        color.setStroke()
        color.setFill()
        switch kind {
        case .pen(let ps):
            stroke(ps, width: width)
        case .marker(let ps):
            color.withAlphaComponent(0.42).setStroke()
            stroke(ps, width: max(14, width * 5), cap: .butt)
        case .line(let a, let b):
            stroke([a, b], width: width)
        case .arrow(let a, let b):
            drawArrow(from: a, to: b)
        case .rect(let r):
            let p = NSBezierPath(roundedRect: r, xRadius: 2, yRadius: 2)
            p.lineWidth = width
            p.lineJoinStyle = .round
            p.stroke()
        case .ellipse(let r):
            let p = NSBezierPath(ovalIn: r)
            p.lineWidth = width
            p.stroke()
        case .text(let s, let at, let size):
            drawText(s, at: at, size: size)
        case .counter(let n, let at):
            let r = counterRadius
            NSBezierPath(ovalIn: CGRect(x: at.x - r, y: at.y - r, width: r * 2, height: r * 2)).fill()
            let label = "\(n)" as NSString
            let attrs: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: r * 1.1, weight: .bold),
                                                        .foregroundColor: NSColor.white]
            let size = label.size(withAttributes: attrs)
            label.draw(at: CGPoint(x: at.x - size.width / 2, y: at.y - size.height / 2), withAttributes: attrs)
        case .pixelate(let r, let oval):
            Pixelate.draw(screen: screen, screenSize: screenSize, rect: r, oval: oval)
        }
    }

    private func stroke(_ ps: [CGPoint], width: CGFloat, cap: NSBezierPath.LineCapStyle = .round) {
        guard let first = ps.first else { return }
        let p = NSBezierPath()
        p.move(to: first)
        if ps.count == 1 { p.line(to: CGPoint(x: first.x + 0.01, y: first.y)) }
        for q in ps.dropFirst() { p.line(to: q) }
        p.lineWidth = width
        p.lineCapStyle = cap
        p.lineJoinStyle = .round
        p.stroke()
    }

    private func drawArrow(from a: CGPoint, to b: CGPoint) {
        let angle = atan2(b.y - a.y, b.x - a.x)
        let head = max(14, width * 4.2)
        let spread: CGFloat = .pi / 7
        // The shaft stops inside the head so its round cap doesn't poke through the tip.
        let shaftEnd = CGPoint(x: b.x - cos(angle) * head * 0.6, y: b.y - sin(angle) * head * 0.6)
        stroke([a, shaftEnd], width: width)
        let p = NSBezierPath()
        p.move(to: b)
        p.line(to: CGPoint(x: b.x - cos(angle - spread) * head, y: b.y - sin(angle - spread) * head))
        p.line(to: CGPoint(x: b.x - cos(angle + spread) * head, y: b.y - sin(angle + spread) * head))
        p.close()
        p.lineJoinStyle = .round
        p.lineWidth = max(1, width * 0.5)
        p.fill()
        p.stroke()
    }
}

/// Coarse blocks of the frozen screen over an area: the pixelate tool, and the blur on faces and personal info.
public enum Pixelate {
    public static func draw(screen: CGImage, screenSize: CGSize, rect: CGRect, oval: Bool) {
        guard rect.width >= 1, rect.height >= 1, screenSize.width > 0 else { return }
        let s = CGFloat(screen.width) / screenSize.width
        let full = CGRect(x: 0, y: 0, width: screen.width, height: screen.height)
        let px = CGRect(x: rect.minX * s, y: rect.minY * s, width: rect.width * s, height: rect.height * s).integral.intersection(full)
        guard !px.isEmpty, let crop = screen.cropping(to: px) else { return }
        // About 12-point blocks, never fewer than three across the shorter side.
        let block = max(4, min(12 * s, min(px.width, px.height) / 3))
        let w = max(1, Int(px.width / block)), h = max(1, Int(px.height / block))
        guard let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue)
        else { return }
        ctx.interpolationQuality = .medium
        ctx.draw(crop, in: CGRect(x: 0, y: 0, width: w, height: h))
        guard let small = ctx.makeImage(), let gc = NSGraphicsContext.current else { return }
        let target = CGRect(x: px.minX / s, y: px.minY / s, width: px.width / s, height: px.height / s)
        gc.saveGraphicsState()
        if oval { NSBezierPath(ovalIn: target).addClip() }
        gc.imageInterpolation = .none
        NSImage(cgImage: small, size: target.size).draw(in: target, from: .zero, operation: .sourceOver,
                                                        fraction: 1, respectFlipped: true, hints: nil)
        gc.restoreGraphicsState()
    }
}
