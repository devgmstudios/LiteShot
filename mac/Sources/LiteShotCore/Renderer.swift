import AppKit

/// Turns a selection of the frozen screen, with its annotations, into the final image at full pixel resolution.
public enum Renderer {
    public static func render(screen: CGImage, screenSize: CGSize, selection: CGRect, annotations: [Annotation]) -> CGImage? {
        guard screenSize.width > 0, selection.width >= 1, selection.height >= 1 else { return nil }
        let s = CGFloat(screen.width) / screenSize.width
        let w = max(1, Int((selection.width * s).rounded())), h = max(1, Int((selection.height * s).rounded()))
        guard let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue)
        else { return nil }
        // Top-left origin, in screen points, like the overlay view.
        ctx.translateBy(x: 0, y: CGFloat(h))
        ctx.scaleBy(x: s, y: -s)
        ctx.translateBy(x: -selection.minX, y: -selection.minY)
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(cgContext: ctx, flipped: true)
        NSGraphicsContext.current?.imageInterpolation = .none
        NSImage(cgImage: screen, size: screenSize).draw(in: CGRect(origin: .zero, size: screenSize), from: .zero,
                                                        operation: .copy, fraction: 1, respectFlipped: true, hints: nil)
        NSGraphicsContext.current?.imageInterpolation = .high
        for a in annotations { a.draw(screen: screen, screenSize: screenSize) }
        NSGraphicsContext.restoreGraphicsState()
        return ctx.makeImage()
    }

    public static func png(_ image: CGImage) -> Data? {
        NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:])
    }
}
