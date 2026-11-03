import CoreGraphics

/// Selection maths, in view points with the origin at the top left (the overlay is a flipped view).
public enum Geometry {
    /// The rectangle two corners span, whichever way the drag went.
    public static func rect(_ a: CGPoint, _ b: CGPoint) -> CGRect {
        CGRect(x: min(a.x, b.x), y: min(a.y, b.y), width: abs(b.x - a.x), height: abs(b.y - a.y))
    }

    /// The eight resize handles, clockwise from the top-left corner.
    public static func handles(of r: CGRect) -> [CGPoint] {
        [
            CGPoint(x: r.minX, y: r.minY), CGPoint(x: r.midX, y: r.minY), CGPoint(x: r.maxX, y: r.minY),
            CGPoint(x: r.maxX, y: r.midY), CGPoint(x: r.maxX, y: r.maxY), CGPoint(x: r.midX, y: r.maxY),
            CGPoint(x: r.minX, y: r.maxY), CGPoint(x: r.minX, y: r.midY),
        ]
    }

    /// The handle under `p`, if any; corners win over edges where they overlap on a small selection.
    public static func handle(at p: CGPoint, in r: CGRect, radius: CGFloat) -> Int? {
        let order = [0, 2, 4, 6, 1, 3, 5, 7]
        let all = handles(of: r)
        return order.first { i in hypot(all[i].x - p.x, all[i].y - p.y) <= radius }
    }

    /// `r` with handle `handle` dragged to `p`. Dragging past the opposite edge flips the rectangle.
    public static func resize(_ r: CGRect, handle: Int, to p: CGPoint) -> CGRect {
        var minX = r.minX, minY = r.minY, maxX = r.maxX, maxY = r.maxY
        switch handle {
        case 0: minX = p.x; minY = p.y
        case 1: minY = p.y
        case 2: maxX = p.x; minY = p.y
        case 3: maxX = p.x
        case 4: maxX = p.x; maxY = p.y
        case 5: maxY = p.y
        case 6: minX = p.x; maxY = p.y
        case 7: minX = p.x
        default: break
        }
        return rect(CGPoint(x: minX, y: minY), CGPoint(x: maxX, y: maxY))
    }

    /// A rectangle in Vision's normalized space (origin bottom left) placed in `area`, top-left origin.
    public static func fromNormalized(_ box: CGRect, in area: CGRect) -> CGRect {
        CGRect(x: area.minX + box.minX * area.width,
               y: area.minY + (1 - box.maxY) * area.height,
               width: box.width * area.width,
               height: box.height * area.height)
    }
}
