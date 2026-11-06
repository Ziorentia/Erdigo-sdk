// swift-tools-version: 5.9
import PackageDescription

// The macOS SDK lives under macos/. The manifest stays at the repository root so
// that Swift Package Manager can consume the repository URL directly.
let package = Package(
    name: "erdigo-sdk",
    platforms: [.macOS(.v12)],
    products: [
        .library(name: "ErdigoSDK", targets: ["ErdigoSDK"]),
        .library(name: "ErdigoMockReceiver", targets: ["ErdigoMockReceiver"]),
        .executable(name: "erdigo-send", targets: ["erdigo-send"]),
    ],
    targets: [
        .target(name: "ErdigoCore", path: "macos/Sources/ErdigoCore"),
        .target(name: "ErdigoSDK", dependencies: ["ErdigoCore"], path: "macos/Sources/ErdigoSDK"),
        .target(name: "ErdigoMockReceiver", dependencies: ["ErdigoCore"], path: "macos/Sources/ErdigoMockReceiver"),
        .executableTarget(
            name: "erdigo-send",
            dependencies: ["ErdigoSDK", "ErdigoMockReceiver"],
            path: "macos/Sources/erdigo-send"
        ),
        .executableTarget(name: "erdigo-vectors", dependencies: ["ErdigoCore"], path: "macos/Tools/erdigo-vectors"),
        .executableTarget(name: "erdigo-example-quickstart", dependencies: ["ErdigoSDK"], path: "macos/Examples/quickstart"),
        .testTarget(
            name: "ErdigoTests",
            dependencies: ["ErdigoCore", "ErdigoSDK", "ErdigoMockReceiver"],
            path: "macos/Tests/ErdigoTests"
        ),
    ]
)
