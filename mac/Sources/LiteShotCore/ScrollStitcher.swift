import CoreGraphics
import Foundation

/// What became of a frame handed to `ScrollStitcher.add`.
public enum StitchStep {
    /// The page moved; the rows that scrolled into view were kept.
    case added
    /// Nothing moved: the end of the page, or the window didn't scroll.
    case same
    /// The frame couldn't be lined up with the one before (it scrolled too far, or the page changed).
    case lost
    /// The image reached its height limit; what fit was kept.
    case full
}

/// Joins the frames of a scrolling capture into one tall image, the same way as the Windows app. Each frame is lined up
/// with the one before by matching rows of pixels, and only the rows that scrolled into view are kept. Rows that stay
/// put while the page moves (a sticky header, a cookie bar) appear once; columns that stay put (a fixed side menu, an
/// inner scrollbar) are left out of the matching, and below the first frame the side panels are filled with their background.
public final class ScrollStitcher {
    public static let defaultMaxHeight = 30_000
    // Below this many moving rows, a change is a caret or a spinner, not a scroll.
    static let minMovingRows = 8
    // A shift counts when this share of the overlapping rows, and at least minMatchedRows of them, match exactly.
    static let minMatch: Float = 0.85
    static let minMatchedRows = 6
    // A column scrolls when at least this share of the moving rows changed in it.
    static let changedShare = 40
    // Columns with fewer colour changes from top to bottom (margins, a scrollbar) say nothing about the shift.
    static let minDetail = 6

    public let maxHeight: Int
    private var width = 0, height = 0, ignoreRight = 0
    private var first: [UInt32]?, previous: [UInt32]?
    private var cols: [Int] = []
    private var colsKnown = false
    private var bandLo = 0, bandHi = 0
    private var strips: [(pixels: [UInt32], start: Int, shift: Int, take: Int)] = []
    private var footerRows = Int.max

    public init(maxHeight: Int = ScrollStitcher.defaultMaxHeight) { self.maxHeight = maxHeight }

    public var frames: Int { first == nil ? 0 : strips.count + 1 }
    public var stitchedHeight: Int { first == nil ? 0 : min(maxHeight, height + strips.reduce(0) { $0 + $1.take }) }
    public private(set) var lastShift = 0
    public private(set) var header = 0
    public var footer: Int { footerRows == .max ? 0 : footerRows }

    /// Adds the next frame, which must be the size of the first. A frame that comes back `.same` or `.lost` changes nothing.
    public func add(_ frame: CGImage, expectedShift: Int = 0) -> StitchStep {
        let all = ScrollStitcher.pixels(frame)
        if first == nil {
            width = frame.width
            height = min(frame.height, maxHeight)
            // The page's scrollbar moves while the content scrolls; leave its strip out of the matching.
            ignoreRight = width >= 200 ? min(width / 10, 32) : 0
            cols = Array(0..<(width - ignoreRight))
            bandLo = 0
            bandHi = width - ignoreRight
            first = Array(all.prefix(width * height))
            previous = first
            return height >= frame.height ? .added : .full
        }
        guard frame.width == width, frame.height >= height, let prev = previous else { return .lost }
        if stitchedHeight >= maxHeight { return .full }

        let pixels = Array(all.prefix(width * height))
        var use = cols
        var a = print(prev, use), next = print(pixels, use)
        var (top, bottom) = unchanged(a, next)
        if height - top - bottom < ScrollStitcher.minMovingRows { return .same }
        var lo = bandLo, hi = bandHi
        if !colsKnown {
            // The second frame shows which columns scroll; line up those alone.
            let found = scrolling(prev, pixels, top, height - bottom)
            (lo, hi) = (found.lo, found.hi)
            if hi <= lo { return .same }
            use = found.detailed.isEmpty ? Array(lo..<hi) : found.detailed
            a = print(prev, use)
            next = print(pixels, use)
            (top, bottom) = unchanged(a, next)
            if height - top - bottom < ScrollStitcher.minMovingRows { return .same }
        }

        let shift = ScrollStitcher.findShift(a, next, top, height - bottom, expectedShift)
        if shift <= 0 { return .lost }
        cols = use
        colsKnown = true
        (bandLo, bandHi) = (lo, hi)

        // The footer can only shrink as frames come in, so keeping this frame from (footer + shift) rows above its
        // bottom covers whatever the final footer turns out to be.
        header = strips.isEmpty ? top : min(header, top)
        footerRows = min(footerRows, bottom)
        let start = max(0, height - footerRows - shift)
        let take = min(shift, maxHeight - stitchedHeight)
        strips.append((Array(pixels[(start * width)...]), start, shift, take))
        previous = pixels
        lastShift = shift
        return take < shift || stitchedHeight >= maxHeight ? .full : .added
    }

    /// The stitched image: the first frame down to the footer, each frame's new rows, then the last footer.
    public func compose() -> CGImage? {
        guard let first else { return nil }
        let f = footer, total = stitchedHeight
        var out = [UInt32](repeating: 0, count: width * total)
        var y = 0
        func copy(_ source: [UInt32], _ fromRow: Int, _ rows: Int) {
            var r = 0
            while r < rows, y < total {
                let s = (fromRow + r) * width
                out.replaceSubrange((y * width)..<((y + 1) * width), with: source[s..<(s + width)])
                r += 1
                y += 1
            }
        }
        copy(first, 0, height - f)
        for s in strips { copy(s.pixels, height - f - s.shift - s.start, s.take) }
        if let last = strips.last { copy(last.pixels, height - f - last.start, f) } else { copy(first, height - f, f) }
        // Under the first frame, a fixed side panel would show its own rows again: fill it with its background.
        let n = width - ignoreRight
        let left = busy(first, 0, bandLo, height - f), right = busy(first, bandHi, n, height - f)
        if left || right {
            var row = [UInt32](repeating: 0, count: width)
            for c in 0..<n where (left && c < bandLo) || (right && c >= bandHi) { row[c] = background(first, c, height - f) }
            for yy in max(0, height - f)..<max(0, total - f) {
                if left { for c in 0..<bandLo { out[yy * width + c] = row[c] } }
                if right { for c in bandHi..<n { out[yy * width + c] = row[c] } }
            }
        }
        return ScrollStitcher.image(out, width: width, height: total)
    }

    // MARK: - matching

    struct RowPrint {
        var hashes: [UInt64]
        var flat: [Bool]
    }

    private func print(_ pixels: [UInt32], _ columns: [Int]) -> RowPrint {
        var hashes = [UInt64](repeating: 0, count: height), flat = [Bool](repeating: false, count: height)
        for r in 0..<height {
            let o = r * width
            var h: UInt64 = 14_695_981_039_346_656_037
            let firstPixel = pixels[o + columns[0]]
            var same = true
            for x in columns {
                let p = pixels[o + x]
                h = (h ^ UInt64(p)) &* 1_099_511_628_211
                if p != firstPixel { same = false }
            }
            hashes[r] = h
            flat[r] = same
        }
        return RowPrint(hashes: hashes, flat: flat)
    }

    private func unchanged(_ a: RowPrint, _ b: RowPrint) -> (Int, Int) {
        var top = 0
        while top < height, a.hashes[top] == b.hashes[top] { top += 1 }
        var bottom = 0
        while bottom < height - top, a.hashes[height - 1 - bottom] == b.hashes[height - 1 - bottom] { bottom += 1 }
        return (top, bottom)
    }

    static func findShift(_ a: RowPrint, _ b: RowPrint, _ lo: Int, _ hi: Int, _ expected: Int) -> Int {
        var best = 0, bestMatched = 0
        var bestShare: Float = 0
        guard hi - lo > 1 else { return 0 }
        for d in 1..<(hi - lo) {
            var matched = 0, counted = 0
            var i = lo
            while i < hi - d {
                // Rows of one flat colour match anywhere, so they say nothing about the shift.
                if !(b.flat[i] && a.flat[i + d]) {
                    counted += 1
                    if b.hashes[i] == a.hashes[i + d] { matched += 1 }
                }
                i += 1
            }
            if matched < minMatchedRows { continue }
            let share = Float(matched) / Float(counted)
            if share < minMatch { continue }
            let better = best == 0
                || (expected > 0 && abs(share - bestShare) < 0.02
                    ? abs(d - expected) < abs(best - expected)
                    : share > bestShare + 0.0001 || (abs(share - bestShare) <= 0.0001 && matched > bestMatched))
            if better { best = d; bestShare = share; bestMatched = matched }
        }
        return best
    }

    private func scrolling(_ a: [UInt32], _ b: [UInt32], _ lo: Int, _ hi: Int) -> (detailed: [Int], lo: Int, hi: Int) {
        let n = width - ignoreRight
        var changed = [Int](repeating: 0, count: n), detail = [Int](repeating: 0, count: n)
        for r in lo..<hi {
            let o = r * width
            for x in 0..<n {
                if a[o + x] != b[o + x] { changed[x] += 1 }
                if r > lo, a[o + x] != a[o - width + x] || b[o + x] != b[o - width + x] { detail[x] += 1 }
            }
        }
        let need = max(ScrollStitcher.minMovingRows, (hi - lo) / ScrollStitcher.changedShare)
        guard var firstCol = changed.firstIndex(where: { $0 >= need }), var lastCol = changed.lastIndex(where: { $0 >= need }) else { return ([], 0, 0) }
        // Out to the edge of what moved at all, but not across a still gap into a side panel.
        while firstCol > 0, changed[firstCol - 1] > 0 { firstCol -= 1 }
        while lastCol < n - 1, changed[lastCol + 1] > 0 { lastCol += 1 }
        let detailed = (firstCol...lastCol).filter { changed[$0] >= need && detail[$0] > ScrollStitcher.minDetail }
        return (detailed, firstCol, lastCol + 1)
    }

    private func busy(_ first: [UInt32], _ from: Int, _ to: Int, _ bottom: Int) -> Bool {
        guard from < to, header + 1 < bottom else { return false }
        for c in from..<to {
            var changes = 0
            for r in (header + 1)..<bottom where first[r * width + c] != first[(r - 1) * width + c] {
                changes += 1
                if changes > ScrollStitcher.minDetail { return true }
            }
        }
        return false
    }

    private func background(_ first: [UInt32], _ c: Int, _ bottom: Int) -> UInt32 {
        var counts: [UInt32: Int] = [:]
        if header < bottom { for r in header..<bottom { counts[first[r * width + c], default: 0] += 1 } }
        return counts.max { $0.value < $1.value }?.key ?? first[c]
    }

    // MARK: - pixels

    /// A frame's pixels as 32-bit BGRA words, row by row from the top.
    public static func pixels(_ image: CGImage) -> [UInt32] {
        let w = image.width, h = image.height
        var data = [UInt32](repeating: 0, count: w * h)
        data.withUnsafeMutableBytes { raw in
            guard let ctx = CGContext(data: raw.baseAddress, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
                                      space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                      bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue) else { return }
            ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        }
        return data
    }

    static func image(_ pixels: [UInt32], width: Int, height: Int) -> CGImage? {
        var copy = pixels
        return copy.withUnsafeMutableBytes { raw -> CGImage? in
            guard let ctx = CGContext(data: raw.baseAddress, width: width, height: height, bitsPerComponent: 8, bytesPerRow: width * 4,
                                      space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                      bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue) else { return nil }
            return ctx.makeImage()
        }
    }

    /// True when two captures of the same area show the same pixels (the page has stopped moving).
    public static func samePixels(_ a: CGImage, _ b: CGImage) -> Bool {
        a.width == b.width && a.height == b.height && pixels(a) == pixels(b)
    }
}
