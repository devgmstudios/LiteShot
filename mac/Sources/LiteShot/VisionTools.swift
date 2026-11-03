import CoreGraphics
import LiteShotCore
import Vision

/// Apple's on-device text recognition and face detection. Nothing leaves the Mac. Boxes come back in
/// Vision's normalized space (0…1, origin bottom left); `Geometry.fromNormalized` places them.
enum VisionTools {
    private static func recognize(_ image: CGImage) throws -> [VNRecognizedTextObservation] {
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .accurate
        request.usesLanguageCorrection = true
        try VNImageRequestHandler(cgImage: image, options: [:]).perform([request])
        return request.results ?? []
    }

    /// All the text, top to bottom, one recognized line per line.
    static func text(in image: CGImage) async throws -> String {
        try await Task.detached(priority: .userInitiated) {
            try recognize(image)
                .sorted { a, b in
                    abs(a.boundingBox.midY - b.boundingBox.midY) > 0.01 ? a.boundingBox.midY > b.boundingBox.midY : a.boundingBox.minX < b.boundingBox.minX
                }
                .compactMap { $0.topCandidates(1).first?.string }
                .joined(separator: "\n")
        }.value
    }

    /// Boxes around the personal details `PersonalInfo` finds in the recognized text.
    static func personalInfo(in image: CGImage, keywords: [String]) async throws -> [CGRect] {
        try await Task.detached(priority: .userInitiated) {
            var boxes: [CGRect] = []
            for line in try recognize(image) {
                guard let candidate = line.topCandidates(1).first else { continue }
                for range in PersonalInfo.ranges(in: candidate.string, keywords: keywords) {
                    if let box = try? candidate.boundingBox(for: range) { boxes.append(box.boundingBox) }
                }
            }
            return boxes
        }.value
    }

    static func faces(in image: CGImage) async throws -> [CGRect] {
        try await Task.detached(priority: .userInitiated) {
            let request = VNDetectFaceRectanglesRequest()
            try VNImageRequestHandler(cgImage: image, options: [:]).perform([request])
            return (request.results ?? []).map(\.boundingBox)
        }.value
    }
}
