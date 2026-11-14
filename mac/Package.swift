// swift-tools-version:5.9
// LiteShot for Mac: a menu bar app. `swift build` builds it, `swift test` tests the core, and
// scripts/bundle.sh wraps the binary into LiteShot.app.
import PackageDescription

let package = Package(
    name: "LiteShot",
    platforms: [.macOS(.v14)],
    products: [
        .executable(name: "LiteShot", targets: ["LiteShot"]),
    ],
    targets: [
        // Drawing, geometry and text rules: everything that doesn't need a screen, so it can be tested.
        // Resources/related-words.gz is copied into the app bundle by scripts/bundle.sh, not built in.
        .target(name: "LiteShotCore", exclude: ["Resources"]),
        .executableTarget(name: "LiteShot", dependencies: ["LiteShotCore"]),
        .testTarget(name: "LiteShotCoreTests", dependencies: ["LiteShotCore"]),
    ]
)
