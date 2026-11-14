import AppKit
import XCTest
@testable import LiteShotCore

final class GeometryTests: XCTestCase {
    func testRectFromCornersAnyDirection() {
        XCTAssertEqual(Geometry.rect(CGPoint(x: 50, y: 40), CGPoint(x: 10, y: 90)), CGRect(x: 10, y: 40, width: 40, height: 50))
    }

    func testCornerHandleWinsAndResizeFlips() {
        let r = CGRect(x: 100, y: 100, width: 200, height: 100)
        XCTAssertEqual(Geometry.handle(at: CGPoint(x: 302, y: 199), in: r, radius: 8), 4)
        XCTAssertNil(Geometry.handle(at: CGPoint(x: 200, y: 150), in: r, radius: 8))
        // Dragging the right edge past the left one flips the selection instead of going negative.
        XCTAssertEqual(Geometry.resize(r, handle: 3, to: CGPoint(x: 50, y: 0)), CGRect(x: 50, y: 100, width: 50, height: 100))
    }

    func testNormalizedBoxesComeOutTopLeft() {
        let area = CGRect(x: 10, y: 20, width: 100, height: 200)
        let r = Geometry.fromNormalized(CGRect(x: 0.5, y: 0.75, width: 0.25, height: 0.25), in: area)
        XCTAssertEqual(r, CGRect(x: 60, y: 20, width: 25, height: 50))
    }
}

final class PersonalInfoTests: XCTestCase {
    func found(_ text: String) -> [String] { PersonalInfo.ranges(in: text).map { String(text[$0]) } }

    func testFindsEmailsPhonesAndCards() {
        XCTAssertEqual(found("Mail jordan.reyes@example.com today"), ["jordan.reyes@example.com"])
        XCTAssertEqual(found("Call (555) 014-2290 after lunch"), ["(555) 014-2290"])
        XCTAssertEqual(found("Card 4111 1111 1111 1111 on file"), ["4111 1111 1111 1111"])
        XCTAssertEqual(found("SSN 078-05-1120"), ["078-05-1120"])
    }

    func testLeavesOrdinaryTextAndNonCardNumbersAlone() {
        XCTAssertEqual(found("Revenue grew 24% year over year"), [])
        XCTAssertEqual(found("Order 1234 5678 9012 3456"), [], "fails the Luhn check, so not a card")
    }

    func testKeywordsAnyCase() {
        let text = "Project Falcon update for ACME corp"
        let hits = PersonalInfo.ranges(in: text, keywords: ["falcon", "Acme Corp", "x"]).map { String(text[$0]) }
        XCTAssertEqual(hits, ["Falcon", "ACME corp"])
    }
}

final class TextStyleTests: XCTestCase {
    override class func setUp() {
        // The fonts the app bundles live with the Windows app's; register them from there.
        let fonts = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().appendingPathComponent("src/LiteShot/Fonts")
        TextStyle.registerBundledFonts(in: fonts)
    }

    func testBundledFontsLoad() {
        for f in TextStyle.fonts.compactMap(\.postScript) {
            XCTAssertNotNil(NSFont(name: f, size: 20), "\(f) is registered")
        }
    }

    func testLooksPadTheText() {
        func box(_ look: TextLook) -> CGRect {
            Annotation(.text("Ship it", at: CGPoint(x: 50, y: 50), size: 30), color: .systemOrange, width: 3, style: TextStyle(look: look)).bounds
        }
        XCTAssertGreaterThan(box(.label).width, box(.plain).width, "a label has room around the text")
        XCTAssertGreaterThan(box(.outline).height, box(.plain).height)
        let bold = Annotation(.text("Ship it", at: .zero, size: 30), color: .black, width: 3, style: TextStyle(bold: true)).bounds
        XCTAssertGreaterThanOrEqual(bold.width, box(.plain).width)
    }

    func testLabelInkContrasts() {
        XCTAssertEqual(Annotation.contrast(.white).usingColorSpace(.sRGB)!.redComponent, 0.11, accuracy: 0.01)
        XCTAssertEqual(Annotation.contrast(.black), .white)
    }
}

final class EditingTests: XCTestCase {
    func testClicksLandOnStrokesTextAndBlocks() {
        let box = Annotation(.rect(CGRect(x: 100, y: 100, width: 200, height: 100)), color: .red, width: 4)
        XCTAssertTrue(box.hit(CGPoint(x: 101, y: 150), tolerance: 4), "on the left edge")
        XCTAssertFalse(box.hit(CGPoint(x: 200, y: 150), tolerance: 4), "inside a box is free for other clicks")
        let arrow = Annotation(.arrow(CGPoint(x: 0, y: 0), CGPoint(x: 100, y: 100)), color: .red, width: 4)
        XCTAssertTrue(arrow.hit(CGPoint(x: 52, y: 48), tolerance: 4))
        XCTAssertFalse(arrow.hit(CGPoint(x: 80, y: 20), tolerance: 4))
        let ring = Annotation(.ellipse(CGRect(x: 0, y: 0, width: 200, height: 100)), color: .red, width: 4)
        XCTAssertTrue(ring.hit(CGPoint(x: 100, y: 1), tolerance: 4), "top of the ellipse")
        XCTAssertFalse(ring.hit(CGPoint(x: 100, y: 50), tolerance: 4), "its middle")
        let label = Annotation(.text("Hello", at: CGPoint(x: 10, y: 10), size: 20), color: .red, width: 3)
        XCTAssertTrue(label.hit(CGPoint(x: 20, y: 20), tolerance: 2))
    }

    func testMovingKeepsTheShape() {
        let pen = Annotation(.pen([CGPoint(x: 0, y: 0), CGPoint(x: 10, y: 5)]), color: .red, width: 3).moved(by: CGSize(width: 5, height: -2))
        guard case let .pen(ps) = pen.kind else { return XCTFail("still a pen") }
        XCTAssertEqual(ps, [CGPoint(x: 5, y: -2), CGPoint(x: 15, y: 3)])
        let blur = Annotation(.pixelate(CGRect(x: 1, y: 2, width: 3, height: 4), oval: true), color: .red, width: 3).moved(by: CGSize(width: 10, height: 10))
        guard case let .pixelate(r, oval) = blur.kind else { return XCTFail("still pixelated") }
        XCTAssertEqual(r, CGRect(x: 11, y: 12, width: 3, height: 4))
        XCTAssertTrue(oval)
    }
}

final class TextFinderTests: XCTestCase {
    func found(_ query: String, in line: String) -> [String] { TextFinder.ranges(of: query, in: line).map { String(line[$0]) } }

    func testCaseAccentsAndRepeats() {
        let line = "Revenue grew 24% year over YEAR, at the Café"
        XCTAssertEqual(found("year", in: line), ["year", "YEAR"])
        XCTAssertEqual(found("cafe", in: line), ["Café"])
        XCTAssertEqual(found("  revenue   GREW ", in: line), ["Revenue grew"])
    }

    func testDoubledSpacesInTheLineStillMatch() {
        XCTAssertEqual(found("glass interface", in: "the glass  interface refresh"), ["glass  interface"])
    }

    func testNothingToFind() {
        XCTAssertEqual(found("   ", in: "anything"), [])
        XCTAssertEqual(found("growth", in: "Revenue grew"), [])
    }

    func testReadingOrder() {
        let a = CGRect(x: 200, y: 10, width: 40, height: 20), b = CGRect(x: 20, y: 12, width: 40, height: 20)
        let c = CGRect(x: 10, y: 60, width: 40, height: 20)
        XCTAssertEqual(TextFinder.readingOrder([c, a, b]), [b, a, c])
    }
}

final class RelatedWordsTests: XCTestCase {
    static let table: RelatedWords? = {
        let url = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("Sources/LiteShotCore/Resources/related-words.gz")
        return (try? Data(contentsOf: url)).flatMap { RelatedWords(gzip: $0) }
    }()

    func testKindsOfFish() throws {
        let words = try XCTUnwrap(Self.table, "the table loads")
        let fish = try XCTUnwrap(words.matcher(for: "fish"))
        XCTAssertTrue(fish("Salmon,") && fish("trout") && fish("TUNA"))
        XCTAssertFalse(fish("report") || fish("grew"))
        XCTAssertTrue(try XCTUnwrap(words.matcher(for: "salmon"))("fish"), "fish is just broader than salmon")
        XCTAssertNil(words.matcher(for: "thing"))
        XCTAssertNil(words.matcher(for: "zzqx"))
    }

    func testRelatedRangesInALine() throws {
        let words = try XCTUnwrap(Self.table)
        let line = "Tonight: grilled salmon and trout tacos."
        XCTAssertEqual(TextFinder.relatedRanges(of: "fish", in: line, words: words).map { String(line[$0]) }, ["salmon", "trout"])
        XCTAssertEqual(TextFinder.relatedRanges(of: "grilled fish", in: line, words: words), [])
    }
}

final class RendererTests: XCTestCase {
    /// A solid image of `size` pixels.
    func solid(_ color: NSColor, size: Int) -> CGImage {
        let ctx = CGContext(data: nil, width: size, height: size, bitsPerComponent: 8, bytesPerRow: 0,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!,
                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        ctx.setFillColor(color.cgColor)
        ctx.fill(CGRect(x: 0, y: 0, width: size, height: size))
        return ctx.makeImage()!
    }

    /// RGBA of the pixel at (x, y), top-left origin.
    func pixel(_ image: CGImage, _ x: Int, _ y: Int) -> (r: UInt8, g: UInt8, b: UInt8) {
        let w = image.width, h = image.height
        var data = [UInt8](repeating: 0, count: w * h * 4)
        let ctx = CGContext(data: &data, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!,
                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        let i = (y * w + x) * 4
        return (data[i], data[i + 1], data[i + 2])
    }

    func testSelectionComesOutAtFullResolution() throws {
        // A 200-pixel screen shown at 100 points: a Retina display.
        let screen = solid(.red, size: 200)
        let out = try XCTUnwrap(Renderer.render(screen: screen, screenSize: CGSize(width: 100, height: 100),
                                                selection: CGRect(x: 10, y: 10, width: 30, height: 20), annotations: []))
        XCTAssertEqual(out.width, 60)
        XCTAssertEqual(out.height, 40)
        let p = pixel(out, 30, 20)
        XCTAssertGreaterThan(p.r, 200)
        XCTAssertLessThan(p.g, 40)
    }

    func testAnnotationsLandWhereTheyWereDrawn() throws {
        let screen = solid(.white, size: 100)
        let box = Annotation(.rect(CGRect(x: 20, y: 20, width: 40, height: 40)), color: .systemBlue, width: 4)
        let out = try XCTUnwrap(Renderer.render(screen: screen, screenSize: CGSize(width: 100, height: 100),
                                                selection: CGRect(x: 0, y: 0, width: 100, height: 100), annotations: [box]))
        let edge = pixel(out, 20, 40), inside = pixel(out, 40, 40)
        XCTAssertGreaterThan(edge.b, 150)
        XCTAssertLessThan(edge.r, 120, "the box's left edge is blue")
        XCTAssertGreaterThan(inside.r, 240, "inside stays white")
    }

    func testTextIsUprightAndNearItsPoint() throws {
        let screen = solid(.white, size: 200)
        let label = Annotation(.text("Hi", at: CGPoint(x: 10, y: 10), size: 60), color: .black, width: 3)
        let out = try XCTUnwrap(Renderer.render(screen: screen, screenSize: CGSize(width: 200, height: 200),
                                                selection: CGRect(x: 0, y: 0, width: 200, height: 200), annotations: [label]))
        // Ink in the top half, where the text starts, and none in the bottom half.
        func ink(_ rows: Range<Int>) -> Int {
            var n = 0
            for y in stride(from: rows.lowerBound, to: rows.upperBound, by: 4) {
                for x in stride(from: 0, to: 200, by: 4) where pixel(out, x, y).r < 100 { n += 1 }
            }
            return n
        }
        XCTAssertGreaterThan(ink(0..<100), 10)
        XCTAssertEqual(ink(110..<200), 0)
    }

    func testPixelateChangesTheArea() throws {
        // Stripes one pixel wide average out to grey under pixelation.
        let size = 64
        let ctx = CGContext(data: nil, width: size, height: size, bitsPerComponent: 8, bytesPerRow: 0,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        for x in 0..<size {
            ctx.setFillColor(x % 2 == 0 ? NSColor.black.cgColor : NSColor.white.cgColor)
            ctx.fill(CGRect(x: x, y: 0, width: 1, height: size))
        }
        let stripes = ctx.makeImage()!
        let blur = Annotation(.pixelate(CGRect(x: 0, y: 0, width: 64, height: 64), oval: false), color: .black, width: 3)
        let out = try XCTUnwrap(Renderer.render(screen: stripes, screenSize: CGSize(width: 64, height: 64),
                                                selection: CGRect(x: 0, y: 0, width: 64, height: 64), annotations: [blur]))
        let a = pixel(out, 10, 10).r, b = pixel(out, 11, 10).r
        XCTAssertLessThan(abs(Int(a) - Int(b)), 40, "neighbouring stripes look alike once pixelated")
    }
}
